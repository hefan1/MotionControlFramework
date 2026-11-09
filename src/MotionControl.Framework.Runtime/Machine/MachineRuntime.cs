using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.IO;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Contracts.Safety;
using MotionControl.Framework.Runtime.FlowScheduling;

namespace MotionControl.Framework.Runtime.Machine
{
    /// <summary>
    /// 管理一台设备的卡连接、复位、启动、暂停和急停监控。各工位流程由设备项目提供；
    /// 没有流程时可以连接控制卡进行手动调试，但不能启动自动运行。
    /// </summary>
    public sealed partial class MachineRuntime
    {
        /// <summary>暂停时默认最多等待工位流程 30 秒退出当前步骤。</summary>
        private static readonly TimeSpan DefaultPauseTimeout = TimeSpan.FromSeconds(30);

        /// <summary>整机运行期间默认每 100 毫秒读取一次急停输入。</summary>
        private static readonly TimeSpan DefaultSafetyPollInterval = TimeSpan.FromMilliseconds(100);

        /// <summary>整机急停输入触发或读取失败时使用的停机报警定义。</summary>
        private static readonly MachineAlarmDefinition EmergencyStopAlarm = new MachineAlarmDefinition(
            "EMERGENCY_STOP",
            "急停输入已触发或无法读取",
            MachineAlarmKind.StopMachine);

        /// <summary>本设备配置的控制卡集合及逻辑轴入口。</summary>
        private readonly MotionCardGroup _motionCards;

        /// <summary>负责并行调度本设备的各工位流程。</summary>
        private readonly MachineFlowScheduler _flowScheduler;

        /// <summary>保存、查询和复位设备报警的服务。</summary>
        private readonly IMachineAlarmService _alarmService;

        /// <summary>提供急停回路状态的读取入口。</summary>
        private readonly IMachineSafetyStatus _safetyStatus;

        /// <summary>读取现场启动、停止和复位按钮逻辑 DI 的服务。</summary>
        private readonly ILogicalIo _controlInputIo;

        /// <summary>保护现场按钮映射名称，供配置页和监控线程安全读取。</summary>
        private readonly object _controlInputLock = new object();

        /// <summary>现场启动按钮对应的逻辑 DI 名称。</summary>
        private string _startInputName = string.Empty;

        /// <summary>现场停止按钮对应的逻辑 DI 名称；按设备约定作为暂停输入。</summary>
        private string _pauseInputName = string.Empty;

        /// <summary>现场复位按钮对应的逻辑 DI 名称。</summary>
        private string _resetInputName = string.Empty;

        /// <summary>现场按钮状态监控后台任务。</summary>
        private Task _controlInputMonitorTask = Task.CompletedTask;

        /// <summary>取消现场按钮状态监控及其发起的整机命令。</summary>
        private CancellationTokenSource _controlInputMonitorCancellation;

        /// <summary>请求暂停后等待流程退出当前步骤的最长时间。</summary>
        private readonly TimeSpan _pauseTimeout;

        /// <summary>运行时轮询急停输入时两次读取之间的间隔。</summary>
        private readonly TimeSpan _safetyPollInterval;

        /// <summary>串行化连接、复位、启动、暂停和断开等整机命令。</summary>
        private readonly SemaphoreSlim _commandLock = new SemaphoreSlim(1, 1);

        /// <summary>当前仍在执行的手动轴或 IO 操作数量，防止其间启动、复位或断卡。</summary>
        private int _activeManualOperationCount;

        /// <summary>保护当前执行入口，供界面线程和命令线程安全读取。</summary>
        private readonly object _controlOwnerLock = new object();

        /// <summary>当前独占设备命令权的执行入口；同一时刻只允许一个入口启动设备。</summary>
        private MachineControlOwner _controlOwner;
        /// <summary>每次执行入口或安全输入配置更换时递增，识别切换后又返回的旧读取。</summary>
        private long _safetyObservationVersion;

        /// <summary>控制权及安全输入配置版本；在线变更进行中时返回负值。</summary>
        public long SafetyObservationVersion { get { lock (_readSessionLock) return _onlineSafetyInputsChanging ? -1 : _safetyObservationVersion; } }

        /// <summary>在线映射变更期间拒绝观察查询，完成后由新轮次重读。</summary>
        private bool _onlineSafetyInputsChanging;
        /// <summary>当前任务会话的唯一所有者。</summary>
        private object _taskEngineOwner;
        /// <summary>正式绑定的 owner 只允许通过已校验收尾的设备释放路径归还。</summary>
        private bool _boundTaskOwner;
        /// <summary>兼容未传所有者的旧调用。</summary>
        private readonly object _legacyTaskEngineOwner = new object();
        /// <summary>任务会话接管之前的入口。</summary>
        private MachineControlOwner _ownerBeforeTaskEngine;

        /// <summary>保护整机状态及最近错误，供不同线程安全读取和更新。</summary>
        private readonly object _statusLock = new object();

        /// <summary>当前整机运行状态。</summary>
        private MachineRunState _state;

        /// <summary>最近一次整机操作或流程故障；无错误时为 null。</summary>
        private Exception _lastError;

        /// <summary>观察并回收当前整机流程任务的后台任务。</summary>
        private Task _flowObserverTask = Task.CompletedTask;
        /// <summary>使连接或控制权切换与页面急停报警的提交互斥；驱动异步读取始终在锁外。</summary>
        private readonly object _readSessionLock = new object();

        /// <summary>当前控制卡连接周期的唯一标识；断卡后为空，重连会产生新标识。</summary>
        private Guid? _connectionSessionId;

        /// <summary>读取本次控制卡连接周期标识；同一 Runtime 对象重连也会改变。</summary>
        public Guid? ConnectionSessionId { get { lock (_readSessionLock) return _connectionSessionId; } }

        /// <summary>控制器丢失后立即作废旧连接周期；不停止设备、不清除现场或恢复证据。</summary>
        public void InvalidateConnectionSessionOnLoss()
        {
            lock (_readSessionLock)
            {
                _connectionSessionId = null;
                _safetyObservationVersion++;
                InvalidatePreparationRecordReuse();
            }
        }

        /// <summary>判断提交时的连接是否仍有效；这里只检查连接反馈，不采集轴或 IO。</summary>
        public bool IsConnectionSessionCurrent(Guid? session)
        {
            return session.HasValue && ConnectionSessionId == session && _motionCards.IsConnected
                && State != MachineRunState.Disconnected;
        }

        /// <summary>阻止已失效连接发布下一步动作或成功状态；调用方不得在状态或报警锁内检查驱动连接。</summary>
        private void EnsureConnectionSessionCurrent(Guid? session, string operation)
        {
            if (!IsConnectionSessionCurrent(session))
                throw new InvalidOperationException("控制器连接会话未确认或已变化，不能" + operation + "。");
        }

        /// <summary>当前 C# 工位流程及报警交接均正常收尾；只用于同连接会话恢复，不能证明物理安全。</summary>
        public bool IsStationFlowSettlementConfirmed
        {
            get
            {
                if (ConnectionSessionId == null || !_motionCards.IsConnected || ControlOwner != MachineControlOwner.StationFlows
                    || State != MachineRunState.Ready || !_flowScheduler.IsSettlementConfirmed)
                    return false;
                Task observer = _flowObserverTask;
                if (observer == null || observer.Status != TaskStatus.RanToCompletion) return false;
                var drain = _alarmService as IMachineStopAlarmNotificationDrain;
                if (drain == null) return false;
                try
                {
                    Task notification = drain.WaitForStopNotificationDispatchAsync();
                    if (notification == null) return false;
                    if (notification.IsFaulted) { var ignored = notification.Exception; return false; }
                    return notification.Status == TaskStatus.RanToCompletion;
                }
                catch (Exception error)
                {
                    System.Diagnostics.Trace.TraceError("核查工位报警收尾失败：" + error);
                    return false;
                }
            }
        }

        /// <summary>断卡后检查本运行时可观察的流程、监控和报警通知是否均已收尾。</summary>
        public bool IsAlarmHandoffSettled
        {
            get
            {
                if (State != MachineRunState.Disconnected || _flowScheduler.IsRunning || !_flowScheduler.IsSettlementConfirmed
                    || IsPreparing || IsClearing || IsTaskEngineControlClaimed || _alarmService.HasLatchedStopAlarm)
                    return false;
                if (_flowObserverTask == null || _flowObserverTask.Status != TaskStatus.RanToCompletion
                    || _safetyMonitorTask == null || !_safetyMonitorTask.IsCompleted || _safetyMonitorTask.IsFaulted
                    || _controlInputMonitorTask == null || !_controlInputMonitorTask.IsCompleted || _controlInputMonitorTask.IsFaulted)
                    return false;
                var drain = _alarmService as IMachineStopAlarmNotificationDrain;
                if (drain == null) return false;
                try
                {
                    Task pending = drain.WaitForStopNotificationDispatchAsync();
                    if (pending == null) return false;
                    if (pending.IsFaulted) { var ignored = pending.Exception; return false; }
                    return pending.Status == TaskStatus.RanToCompletion && !_alarmService.HasLatchedStopAlarm;
                }
                catch (Exception error)
                {
                    System.Diagnostics.Trace.TraceError("核查报警交接失败：" + error);
                    return false;
                }
            }
        }

        /// <summary>周期监控急停输入的后台任务。</summary>
        private Task _safetyMonitorTask = Task.CompletedTask;

        /// <summary>取消当前急停监控任务的令牌源。</summary>
        private CancellationTokenSource _safetyMonitorCancellation;

        /// <summary>创建单机运行上下文；可选接入现场按钮 DI、启动前初始化流程和监控间隔。</summary>
        public MachineRuntime(
            MotionCardGroup motionCards,
            IEnumerable<IStationFlow> stationFlows,
            IMachineAlarmService alarmService,
            IMachineSafetyStatus safetyStatus,
            TimeSpan? pauseTimeout = null,
            TimeSpan? safetyPollInterval = null,
            IEquipmentInitializationFlow equipmentInitializationFlow = null,
            ILogicalIo controlInputIo = null,
            string startInputName = null,
            string pauseInputName = null,
            string resetInputName = null,
            MachineControlOwner? controlOwner = null)
        {
            if (motionCards == null)
            {
                throw new ArgumentNullException(nameof(motionCards));
            }

            if (stationFlows == null)
            {
                throw new ArgumentNullException(nameof(stationFlows));
            }

            if (alarmService == null)
            {
                throw new ArgumentNullException(nameof(alarmService));
            }

            if (safetyStatus == null)
            {
                throw new ArgumentNullException(nameof(safetyStatus), "运行时必须接入急停状态来源。");
            }

            if (pauseTimeout.HasValue && pauseTimeout.Value <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(pauseTimeout), "暂停等待时限必须大于 0。");
            }

            if (safetyPollInterval.HasValue && safetyPollInterval.Value <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(safetyPollInterval), "急停状态轮询间隔必须大于 0。");
            }

            _motionCards = motionCards;
            _flowScheduler = new MachineFlowScheduler(
                motionCards,
                stationFlows,
                equipmentInitializationFlow);
            _controlOwner = controlOwner
                ?? (_flowScheduler.StationFlows.Count > 0
                    ? MachineControlOwner.StationFlows
                    : MachineControlOwner.None);
            _alarmService = alarmService;
            _safetyStatus = safetyStatus;
            _controlInputIo = controlInputIo;
            UpdateControlInputNames(startInputName, pauseInputName, resetInputName);
            _pauseTimeout = pauseTimeout ?? DefaultPauseTimeout;
            _safetyPollInterval = safetyPollInterval ?? DefaultSafetyPollInterval;
            _state = MachineRunState.Disconnected;

            _alarmService.StopAlarmRaised += OnStopAlarmRaised;
            _alarmService.AlarmsChanged += OnAlarmsChanged;
        }

        /// <summary>供界面读取；状态变化可能发生在流程后台线程。</summary>
        public event EventHandler StatusChanged;

        /// <summary>Host 提供的运行前持久标记；失败必须阻止进入生产，不执行设备动作。</summary>
        private Action _beforeAutomaticRun;

        /// <summary>连接前接入运行前持久标记；为空时保持未配置持久化的旧设备行为。</summary>
        public void SetBeforeAutomaticRun(Action action)
        {
            if (State != MachineRunState.Disconnected) throw new InvalidOperationException("只能在连接前设置生产运行记录。");
            _beforeAutomaticRun = action;
        }

        /// <summary>在发出自动动作之前持久化运行标记；错误向调用者报告并阻止启动。</summary>
        private void RecordBeforeAutomaticRun() { _beforeAutomaticRun?.Invoke(); }

        /// <summary>报警列表变化时通知界面刷新。</summary>
        public event EventHandler AlarmsChanged;

        /// <summary>返回受锁保护的当前整机状态。</summary>
        public MachineRunState State
        {
            get
            {
                lock (_statusLock)
                {
                    return _state;
                }
            }
        }

        /// <summary>最近一次整机操作失败或停机报警的异常信息。</summary>
        public Exception LastError
        {
            get
            {
                lock (_statusLock)
                {
                    return _lastError;
                }
            }
        }

        /// <summary>返回本设备使用的运动卡集合和逻辑轴入口。</summary>
        public MotionCardGroup MotionCards
        {
            get { return _motionCards; }
        }

        /// <summary>
        /// 返回当前独占设备命令权的执行入口。配置了 C# 工位流程时由工位流程独占；
        /// 没有工位流程时为空，可交给任务执行入口。
        /// </summary>
        public MachineControlOwner ControlOwner
        {
            get
            {
                lock (_controlOwnerLock)
                {
                    return _controlOwner;
                }
            }
        }

        /// <summary>返回是否有任务会话实际接管设备，不受配置中的默认入口名称影响。</summary>
        public bool IsTaskEngineControlClaimed
        {
            get
            {
                lock (_controlOwnerLock) return _taskEngineOwner != null;
            }
        }

        /// <summary>读取当前任务资源保护状态；未知实现按未知处理，不能视为空闲。</summary>
        public TaskResourceRecoveryState GetTaskResourceRecoveryState()
        {
            object owner;
            lock (_controlOwnerLock) owner = _taskEngineOwner;
            if (owner == null) return TaskResourceRecoveryState.Clear;
            var guard = owner as ITaskResourceRecoveryState;
            if (guard == null) return TaskResourceRecoveryState.Unknown;
            try
            {
                TaskResourceRecoveryState state = guard.GetResourceRecoveryState();
                return Enum.IsDefined(typeof(TaskResourceRecoveryState), state) ? state : TaskResourceRecoveryState.Unknown;
            }
            catch (Exception error)
            {
                System.Diagnostics.Trace.TraceError("读取任务资源状态失败，保留恢复保护：" + error);
                return TaskResourceRecoveryState.Unknown;
            }
        }

        /// <summary>返回本设备使用的报警服务。</summary>
        public IMachineAlarmService AlarmService
        {
            get { return _alarmService; }
        }

        /// <summary>返回本设备配置的工位流程只读列表。</summary>
        public IReadOnlyList<IStationFlow> StationFlows
        {
            get { return _flowScheduler.StationFlows; }
        }

        /// <summary>返回启动初始化是否完成，可用于暂停恢复文件保存状态。</summary>
        public bool EquipmentInitializationCompleted
        {
            get { return _flowScheduler.EquipmentInitializationCompleted; }
        }

        /// <summary>返回当前连接周期是否仍有启动初始化待执行。</summary>
        public bool HasPendingEquipmentInitialization
        {
            get { return _flowScheduler.HasPendingInitialization; }
        }

        /// <summary>设备流程和资源恢复的独立门禁。</summary>
        private string _recoveryBlockReason;

        /// <summary>跨连接报警状态不可信时的独立门禁。</summary>
        private string _alarmContinuityBlockReason;

        /// <summary>恢复记录或报警交接不可信时向操作员显示所有原因，不影响手动排查。</summary>
        public string RecoveryBlockReason
        {
            get
            {
                string alarmReason = Volatile.Read(ref _alarmContinuityBlockReason);
                if (_recoveryBlockReason == null) return alarmReason;
                return alarmReason == null ? _recoveryBlockReason : _recoveryBlockReason + "；" + alarmReason;
            }
            private set { _recoveryBlockReason = value; }
        }

        /// <summary>连接前安装报警交接检查结果，不允许普通复位将未知当作条件已恢复。</summary>
        public void SetAlarmContinuityBlockReason(string reason)
        {
            if (State != MachineRunState.Disconnected) throw new InvalidOperationException("只能在连接前设置报警交接检查结果。");
            _alarmContinuityBlockReason = string.IsNullOrWhiteSpace(reason) ? null : reason;
        }

        /// <summary>旧来源迟到或连接失效时锁定当前会话；此方法不会解除已有的报警交接门禁。</summary>
        public void MarkAlarmContinuityUnconfirmed(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("报警交接原因不能为空。", nameof(reason));
            Interlocked.CompareExchange(ref _alarmContinuityBlockReason, reason, null);
        }

        /// <summary>未知报警来源须先由设备项目核查，普通报警复位不能代替核查。</summary>
        private void EnsureAlarmContinuityTrusted()
        {
            string reason = Volatile.Read(ref _alarmContinuityBlockReason);
            if (reason != null)
                throw new InvalidOperationException("旧报警交接尚未确认，不能复位：" + reason);
        }

        /// <summary>连接前安装恢复校验结果，不能在设备运行期间更改此门禁。</summary>
        public void SetRecoveryBlockReason(string reason)
        {
            if (State != MachineRunState.Disconnected) throw new InvalidOperationException("只能在连接前设置恢复检查结果。");
            RecoveryBlockReason = string.IsNullOrWhiteSpace(reason) ? null : reason;
        }

        /// <summary>恢复数据不可信时禁止开始自动动作。</summary>
        private void EnsureRecoveryTrusted()
        {
            if (IsShutdownInProgress) throw new InvalidOperationException("设备正在安全退出，不能准备或启动。");
            if (RecoveryBlockReason != null) throw new InvalidOperationException("设备恢复信息未确认，不能准备或启动：" + RecoveryBlockReason);
        }

        /// <summary>读取自动启动拦截原因，供操作页在启动前显示具体就绪条件。</summary>
        public async Task<string> GetAutomaticStartBlockReasonAsync(CancellationToken cancellationToken)
        {
            if (ProductionStopReviewRequired) return "生产已停止，旧工步尚未明确核查。";
            if (RecoveryBlockReason != null) return "设备恢复信息未确认，不能准备或启动：" + RecoveryBlockReason;
            Guid? session = ConnectionSessionId;
            long observationVersion = SafetyObservationVersion;
            if (!IsStationReadinessCurrent(session, observationVersion)) return ReadinessContextChanged;

            if (_flowScheduler.StationFlows.Count == 0)
            {
                return "尚未配置自动工位，不能启动自动流程。";
            }

            if (!IsEmergencyStopMonitoringConfigured())
            {
                return "软件急停状态 DI 尚未配置，不能启动自动流程。请先在安全联锁页选择 DI。";
            }

            if (Interlocked.CompareExchange(ref _activeManualOperationCount, 0, 0) > 0)
            {
                return "仍有手动动作执行，请等待动作完成后再启动自动流程。";
            }

            try
            {
                if (await UpdateEmergencyStopAlarmAsync(cancellationToken, session, observationVersion).ConfigureAwait(false))
                {
                    return "急停输入仍处于触发状态，设备不能启动。";
                }

                string preparationReason = GetPreparationStartBlockReason();
                if (preparationReason != null) return preparationReason;

                await _flowScheduler
                    .EnsureAxesReadyForAutomaticStartAsync(
                        _flowScheduler.HasPendingInitialization,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!IsStationReadinessCurrent(session, observationVersion) || !IsEmergencyStopMonitoringConfigured()
                    || IsShutdownInProgress || ProductionStopReviewRequired || RecoveryBlockReason != null)
                    return ReadinessContextChanged;
                if (Volatile.Read(ref _activeManualOperationCount) > 0)
                    return "仍有手动动作执行，请等待动作完成后再启动自动流程。";
                return GetPreparationStartBlockReason();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception readinessError)
            {
                return string.IsNullOrWhiteSpace(readinessError.Message) ? "无法确认自动启动条件。" : readinessError.Message;
            }
        }

        /// <summary>原读取上下文失效时的保守提示。</summary>
        private const string ReadinessContextChanged = "设备状态、连接或执行入口已变化，自动启动条件待重新确认。";

        /// <summary>检查读数仍属于当前已连接、就绪的工位控制入口。</summary>
        public bool IsStationReadinessCurrent(Guid? session, long observationVersion)
        {
            return State == MachineRunState.Ready && ControlOwner == MachineControlOwner.StationFlows
                && IsReadConnectionCurrent(session, observationVersion);
        }

        /// <summary>核对只读联锁查询的卡连接与独占入口变更次数。</summary>
        private bool IsReadConnectionCurrent(Guid? session, long observationVersion)
        {
            return observationVersion >= 0 && session.HasValue && ConnectionSessionId == session && SafetyObservationVersion == observationVersion
                && _motionCards.IsConnected;
        }

        /// <summary>连接控制卡前载入上次初始化完成标记，供设备流程重连后检查现场状态。</summary>
        public void RequestInitializationRecovery(bool initializationCompletedBeforeRestart)
        {
            EnsureState(MachineRunState.Disconnected, "只能在连接控制卡前设置初始化恢复状态。");
            _flowScheduler.RequestInitializationRecovery(initializationCompletedBeforeRestart);
        }

        /// <summary>按设备配置连接所有运动卡，并进入等待复位状态。</summary>
        public async Task ConnectAsync(CancellationToken cancellationToken)
        {
            await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (IsShutdownInProgress) throw new InvalidOperationException("设备正在安全退出，不能重新连接。");
                EnsureState(MachineRunState.Disconnected, "只有断开状态的设备可以连接运动控制卡。");
                try
                {
                    InvalidatePreparationRecordReuse();
                    _flowScheduler.ResetForNewConnection();
                    InvalidatePreparations("控制器已重新连接，请重新确认工位准备。");
                    await _motionCards.ConnectAsync(cancellationToken).ConfigureAwait(false);
                    lock (_readSessionLock) _connectionSessionId = Guid.NewGuid();
                    SetStatus(MachineRunState.ResetRequired, null);
                    StartControlInputMonitor();
                }
                catch (Exception connectionError)
                {
                    lock (_readSessionLock) _connectionSessionId = null;
                    SetStatus(MachineRunState.Disconnected, connectionError);
                    throw;
                }
            }
            finally
            {
                _commandLock.Release();
            }
        }

        /// <summary>所有工位流程退出且各轴停稳后断开运动卡连接。</summary>
        public async Task DisconnectAsync(CancellationToken cancellationToken)
        {
            await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                EnsureShutdownOutputReadsSettled();
                EnsureStopFeedbackReadSettled();
                EnsureNoPreparationInProgress("断开控制卡");
                if (State == MachineRunState.Running || State == MachineRunState.Pausing || _flowScheduler.IsRunning)
                {
                    throw new InvalidOperationException("工位流程仍在运行或等待停止，不能断开控制卡。");
                }

                lock (_controlOwnerLock)
                {
                    if (_taskEngineOwner != null && !_shutdownSealed)
                        throw new InvalidOperationException("任务执行会话仍占用设备；请先在任务运行页停止并关闭会话，再断开控制卡。");
                }

                EnsureNoManualOperationsInProgress("断开运动控制卡");
                InvalidatePreparationRecordReuse();
                lock (_readSessionLock) _connectionSessionId = null;

                try
                {
                    InvalidatePreparationSnapshots("控制器连接会话已失效，准备状态需重新确认。");
                    await StopControlInputMonitorAsync().ConfigureAwait(false);
                    await _motionCards.DisconnectAsync(cancellationToken).ConfigureAwait(false);
                    SetDisconnectStatus(MachineRunState.Disconnected, null);
                }
                catch (Exception disconnectError)
                {
                    SetDisconnectStatus(MachineRunState.Alarmed, disconnectError);
                    // 断卡失败后仍有卡连接时，恢复现场按钮轮询；重新建立输入基线，
                    // 不把断卡期间已经有效的电平误当成新的按钮上升沿。
                    try
                    {
                        if (_motionCards.Cards.Any(card => card.IsConnected)) StartControlInputMonitor();
                    }
                    catch (Exception monitorError)
                    {
                        throw new AggregateException("断卡失败，且无法确认剩余连接或恢复现场按钮监控，请检查现场。",
                            disconnectError, monitorError);
                    }
                    throw;
                }
                finally
                {
                    // 准备订阅者不参与驱动断卡，其异常不能改变断卡结果或覆盖驱动异常。
                    NotifyDisconnectedSubscribers(PreparationChanged, "准备状态");
                }
            }
            finally
            {
                _commandLock.Release();
            }
        }

        /// <summary>正常复位不会代替停止后的现场核查。</summary>
        public Task ResetAsync(CancellationToken cancellationToken)
        {
            return ResetAsync(cancellationToken, false);
        }

        /// <summary>确认停稳并复位报警；仅调用方已明确核查旧工步时允许解除停止门禁。</summary>
        public async Task ResetAsync(CancellationToken cancellationToken, bool confirmedStoppedProduction)
        {
            Guid? session = ConnectionSessionId;
            await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (IsShutdownInProgress) throw new InvalidOperationException("设备正在安全退出，不能复位或重新运行。");
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsConnectionSessionCurrent(session))
                    throw new InvalidOperationException("控制器连接会话未确认或已变化，不能复位设备。");
                EnsureAlarmContinuityTrusted();
                EnsureNoPreparationInProgress("报警复位");
                EnsureOneOfStates(
                    new[] { MachineRunState.ResetRequired, MachineRunState.Paused, MachineRunState.Stopped, MachineRunState.Alarmed },
                    "设备当前状态不允许复位。");

                if (ProductionStopReviewRequired && !confirmedStoppedProduction)
                    throw new InvalidOperationException("生产已停止，旧工步仍保留；请先通过顶部报警复位入口核查并明确确认。");
                EnsureStopFeedbackReadSettled();

                if (ProductionStopReviewRequired && !_flowScheduler.IsSettlementConfirmed)
                    throw new InvalidOperationException("工位停止收尾尚未确认，不能复位或继续生产。");

                if (_flowScheduler.IsRunning)
                {
                    throw new InvalidOperationException("工位流程仍在后台运行，不能复位；请先等待流程退出或再次请求暂停。");
                }

                EnsureNoManualOperationsInProgress("复位设备");

                try
                {
                    foreach (var axis in _motionCards.Axes)
                    {
                        AxisStatus axisStatus;
                        try
                        {
                            axisStatus = await axis.ReadStatusAsync(cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception statusError)
                        {
                            throw new InvalidOperationException(
                                "整机复位前读取逻辑轴 '" + axis.Name + "' 状态失败，不能复位。",
                                statusError);
                        }

                        if (!AxisStopFeedback.IsConfirmedStopped(axisStatus))
                        {
                            throw new InvalidOperationException(
                                "逻辑轴 '" + axis.Name + "' 仍在运动或速度状态未知，确认所有轴停稳后再执行整机复位。");
                        }
                    }

                    await UpdateEmergencyStopAlarmAsync(cancellationToken).ConfigureAwait(false);
                    EnsureAlarmContinuityTrusted();
                    _alarmService.EnsureStopAlarmConditionsRecovered();

                    foreach (var axis in _motionCards.Axes)
                    {
                        try
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            EnsureAlarmContinuityTrusted();
                            if (!IsConnectionSessionCurrent(session)) throw new InvalidOperationException("控制器连接会话已失效，不能复位轴。");
                            await axis.ResetAlarmAsync(cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception axisError)
                        {
                            throw new InvalidOperationException(
                                "逻辑轴 '" + axis.Name + "' 报警复位失败。",
                                axisError);
                        }
                    }

                    try
                    {
                        EnsureConnectionSessionCurrent(session, "复位设备初始化流程");
                        EnsureAlarmContinuityTrusted();
                        await _flowScheduler
                            .ResetEquipmentInitializationAsync(cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception initializationError)
                    {
                        throw new InvalidOperationException("设备启动前初始化流程复位失败。", initializationError);
                    }

                    foreach (var flow in _flowScheduler.StationFlows)
                    {
                        try
                        {
                            EnsureConnectionSessionCurrent(session, "复位工位");
                            EnsureAlarmContinuityTrusted();
                            await flow.ResetAsync(cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception flowError)
                        {
                            throw new InvalidOperationException(
                                "工位 '" + flow.StationName + "' 复位失败。",
                                flowError);
                        }
                    }

                    EnsureConnectionSessionCurrent(session, "复位停机报警");
                    EnsureAlarmContinuityTrusted();
                    _alarmService.ResetStopAlarms();
                    EnsureConnectionSessionCurrent(session, "确认设备复位完成");
                    SetReadyIfNoStopAlarm(confirmedStoppedProduction);
                }
                catch (Exception resetError)
                {
                    SetStatus(MachineRunState.Alarmed, resetError);
                    throw;
                }
            }
            finally
            {
                _commandLock.Release();
            }
        }

        /// <summary>
        /// 请求由图形化任务定义入口接管设备命令权；工位流程必须空闲，且不能被其他任务会话占用。
        /// </summary>
        public Task AcquireTaskEngineControlAsync(CancellationToken cancellationToken)
        {
            return AcquireTaskEngineControlAsync(_legacyTaskEngineOwner, cancellationToken);
        }

        /// <summary>以明确会话身份申请任务控制权，防止多个任务设备同时接管。</summary>
        public Task AcquireTaskEngineControlAsync(object owner, CancellationToken cancellationToken)
        {
            return AcquireTaskEngineControlAsync(owner, cancellationToken, null);
        }

        /// <summary>正式任务设备在命令锁内核对装载前的连接标识，旧连接不能接管或重用控制权。</summary>
        internal async Task AcquireTaskEngineControlAsync(object owner, CancellationToken cancellationToken, Guid? expectedSession)
        {
            if (owner == null) throw new ArgumentNullException(nameof(owner));
            await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (IsShutdownInProgress) throw new InvalidOperationException("设备正在安全退出，不能接管或重新运行。");
                EnsureStopFeedbackReadSettled();
                EnsureNoPreparationInProgress("切换任务执行入口");
                EnsureNoManualOperationsInProgress("切换任务执行入口");
                if (expectedSession.HasValue) EnsureConnectionSessionCurrent(expectedSession, "接管任务设备");
                lock (_readSessionLock)
                lock (_controlOwnerLock)
                {
                    if (_taskEngineOwner != null)
                    {
                        if (ReferenceEquals(_taskEngineOwner, owner))
                        {
                            if (expectedSession.HasValue) _boundTaskOwner = true;
                            return;
                        }
                        throw new InvalidOperationException("任务执行入口已被其他会话占用。");
                    }

                    // 互斥看的是"谁正在控制设备"，不是"配置了哪种入口"。
                    // 工位流程定义了但没在跑时允许切换，切换后 StartAsync 会因入口不是工位流程而拒绝。
                    if (_flowScheduler.IsRunning)
                    {
                        throw new InvalidOperationException(
                            "C# 工位流程正在运行，任务执行入口不能同时启动；请先暂停并确认所有轴已停止。");
                    }

                    var currentState = State;
                    if (currentState == MachineRunState.Running || currentState == MachineRunState.Pausing)
                    {
                        throw new InvalidOperationException(
                            "设备正在运行，不能切换执行入口；请先暂停并确认所有轴已停止。当前状态："
                            + currentState + "。");
                    }

                    _ownerBeforeTaskEngine = _controlOwner;
                    _taskEngineOwner = owner;
                    _boundTaskOwner = expectedSession.HasValue;
                    _controlOwner = MachineControlOwner.TaskEngine;
                    _safetyObservationVersion++;
                }
            }
            finally
            {
                _commandLock.Release();
            }
        }

        /// <summary>
        /// 归还任务执行入口并恢复接管前的入口；设备运行中拒绝归还。
        /// </summary>
        public void ReleaseTaskEngineControl()
        {
            ReleaseTaskEngineControl(_legacyTaskEngineOwner);
        }

        /// <summary>只允许持有控制权的任务会话归还入口。</summary>
        public void ReleaseTaskEngineControl(object owner)
        {
            ReleaseTaskEngineControl(owner, throughBoundDevice: false);
        }

        /// <summary>正式绑定设备已冻结新登记并检查真实收尾之后，才允许释放其 Runtime 控制权。</summary>
        internal void ReleaseTaskEngineControl(object owner, bool throughBoundDevice)
        {
            if (owner == null) throw new ArgumentNullException(nameof(owner));
            if (!_commandLock.Wait(0)) throw new InvalidOperationException("设备命令仍在执行，不能关闭任务会话。");
            try
            {
                if (_shutdownSealed) throw new InvalidOperationException("设备已封存退出交接，不能提前释放任务控制权。");
                EnsureStopFeedbackReadSettled();
                EnsureNoPreparationInProgress("关闭任务会话");
                lock (_readSessionLock)
                lock (_controlOwnerLock)
                {
                    if (_controlOwner != MachineControlOwner.TaskEngine)
                    {
                        return;
                    }

                    if (!ReferenceEquals(_taskEngineOwner, owner))
                        throw new InvalidOperationException("任务执行入口的所有者不匹配，不能归还。");
                    if (_boundTaskOwner && !throughBoundDevice)
                        throw new InvalidOperationException("正式任务设备须先完成动作收尾，再由持有设备归还控制权。");

                    var currentState = State;
                    if (currentState == MachineRunState.Running || currentState == MachineRunState.Pausing)
                    {
                        throw new InvalidOperationException(
                            "设备正在运行，不能归还任务执行入口；请先停止任务并确认所有轴已停止。当前状态："
                            + currentState + "。");
                    }

                    _controlOwner = _ownerBeforeTaskEngine;
                    _safetyObservationVersion++;
                    _taskEngineOwner = null;
                    _boundTaskOwner = false;
                }
            }
            finally { _commandLock.Release(); }
        }

        /// <summary>执行可选设备初始化后，从各工位保留的当前步骤启动设备流程。</summary>
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            Guid? session = ConnectionSessionId;
            await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (ControlOwner != MachineControlOwner.StationFlows)
                {
                    throw new InvalidOperationException(
                        "当前设备的执行入口是 " + ControlOwner + "，不能通过工位流程启动；同一时刻只允许一个入口控制设备。");
                }

                if (_flowScheduler.StationFlows.Count == 0)
                {
                    throw new InvalidOperationException("尚未配置工位流程，不能启动自动运行。");
                }

                if (!IsConnectionSessionCurrent(session))
                    throw new InvalidOperationException("控制器连接会话未确认或已变化，不能启动工位。");
                EnsureState(MachineRunState.Ready, "启动前必须先成功复位设备。");
                if (ProductionStopReviewRequired) throw new InvalidOperationException("生产已停止，旧工步尚未明确核查，不能启动。");
                EnsureRecoveryTrusted();
                await EnsurePreparationsReadyForStartAsync(cancellationToken).ConfigureAwait(false);
                EnsureNoManualOperationsInProgress("启动自动流程");
                if (!IsEmergencyStopMonitoringConfigured())
                {
                    throw new InvalidOperationException(
                        "软件急停状态 DI 尚未配置，不能启动自动流程。请先在安全联锁页选择 DI；手动调试仍可使用。设备安全停机仍须由硬件安全回路完成。");
                }

                if (!_flowScheduler.HasPendingInitialization)
                {
                    await _flowScheduler
                        .EnsureRequiredStationAxesHomedForAutomaticStartAsync(cancellationToken)
                        .ConfigureAwait(false);
                }

                try
                {
                    if (await UpdateEmergencyStopAlarmAsync(cancellationToken).ConfigureAwait(false))
                    {
                        throw new InvalidOperationException("急停输入仍处于触发状态，设备不能启动。");
                    }

                    await _flowScheduler
                        .EnsureAxesReadyForAutomaticStartAsync(
                            _flowScheduler.HasPendingInitialization,
                            cancellationToken)
                        .ConfigureAwait(false);
                    Task previousObserver = _flowObserverTask;
                    if (await Task.WhenAny(previousObserver, Task.Delay(_pauseTimeout, cancellationToken)).ConfigureAwait(false) != previousObserver)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        throw new TimeoutException("上一次工位状态与监控仍在收尾，不能启动。");
                    }
                    await previousObserver.ConfigureAwait(false);
                    if (!IsConnectionSessionCurrent(session) || !IsStationFlowSettlementConfirmed)
                        throw new InvalidOperationException("上一次工位流程或停机报警尚未确认收尾，不能启动。");
                    RecordBeforeAutomaticRun();
                    EnsureConnectionSessionCurrent(session, "启动工位");
                    SetRunningIfNoStopAlarm();
                    StartEmergencyStopMonitor();
                    var startGate = _alarmService as IMachineAlarmStartGate;
                    if (startGate == null)
                        throw new InvalidOperationException("报警服务不支持原子检查工位流程启动许可。");
                    var activation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    Task runTask = null;
                    try
                    {
                        EnsureConnectionSessionCurrent(session, "发放工位启动许可");
                        // 仅将可取消调度和异步内存许可一同提交；不会在报警锁线程调用设备。
                        startGate.ExecuteWhenNoStopAlarm(() =>
                        {
                            runTask = _flowScheduler.Start(activation.Task);
                            activation.TrySetResult(true);
                        });
                    }
                    finally { if (runTask == null) activation.TrySetResult(true); }
                    MarkPreparationProductionStarted();
                    _flowObserverTask = ObserveFlowRunAsync(runTask);

                    if (_alarmService.HasLatchedStopAlarm)
                    {
                        var stopAlarm = _alarmService.Alarms.FirstOrDefault(
                            alarm => alarm.Definition.Kind == MachineAlarmKind.StopMachine);
                        if (stopAlarm != null)
                        {
                            OnStopAlarmRaised(this, new MachineAlarmRaisedEventArgs(stopAlarm));
                        }
                    }
                }
                catch (Exception startError)
                {
                    await StopEmergencyStopMonitorAsync().ConfigureAwait(false);
                    if (startError is StationAxesNotHomedException)
                    {
                        throw;
                    }

                    SetStatus(MachineRunState.Alarmed, startError);
                    throw;
                }
            }
            finally
            {
                _commandLock.Release();
            }
        }

        /// <summary>请求各工位暂停，并在时限内等待所有流程退出当前步骤。</summary>
        public async Task PauseAsync(CancellationToken cancellationToken)
        {
            if (IsPreparing)
            {
                await StopPreparationAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            if (ControlOwner == MachineControlOwner.TaskEngine)
            {
                // 捕获启动暂停时的设备身份；异步停止期间不得误用后来接管的新任务会话。
                Guid? session = ConnectionSessionId;
                object taskOwner;
                lock (_controlOwnerLock) taskOwner = _taskEngineOwner;
                IAutomaticProductionPause taskPause = taskOwner as IAutomaticProductionPause;
                if (taskPause == null)
                    throw new InvalidOperationException("任务执行入口未提供受控暂停能力，不能确认生产暂停。");
                if (!session.HasValue)
                    throw new InvalidOperationException("任务控制器连接会话已失效，不能确认生产暂停。");
                EnsureTaskDeviceBinding(session, taskOwner);
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await taskPause.PauseAutomaticProductionAsync().ConfigureAwait(false);
                    await FinishTaskEngineRunAsync(session.Value, taskOwner, false, null).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    try { await FinishTaskEngineRunAsync(session.Value, taskOwner, true, error).ConfigureAwait(false); }
                    catch (Exception finishError)
                    {
                        throw new AggregateException("任务暂停失败且当前连接的状态收尾未确认。", error, finishError);
                    }
                    throw;
                }
                return;
            }
            await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var currentState = State;
                if (currentState != MachineRunState.Running
                    && currentState != MachineRunState.Pausing
                    && !(currentState == MachineRunState.Alarmed && _flowScheduler.IsRunning))
                {
                    throw new InvalidOperationException(
                        "只有运行中的设备或仍有流程任务的报警设备可以请求暂停。当前状态："
                        + currentState + "。");
                }

                if (currentState == MachineRunState.Running)
                {
                    SetStatus(MachineRunState.Pausing, null);
                }

                try
                {
                    await _flowScheduler.PauseAsync(_pauseTimeout, cancellationToken).ConfigureAwait(false);
                    await _flowObserverTask.ConfigureAwait(false);
                    SetPausedOrAlarmed();
                }
                catch (Exception pauseError)
                {
                    SetStatus(MachineRunState.Alarmed, pauseError);
                    throw;
                }
            }
            finally
            {
                _commandLock.Release();
            }
        }

        /// <summary>检查设备是否处于指定状态，不符合时报告当前状态。</summary>
        private void EnsureState(MachineRunState requiredState, string errorMessage)
        {
            if (State != requiredState)
            {
                throw new InvalidOperationException(errorMessage + " 当前状态：" + State + "。");
            }
        }

        /// <summary>检查设备是否处于允许的一组状态之一。</summary>
        private void EnsureOneOfStates(IReadOnlyCollection<MachineRunState> allowedStates, string errorMessage)
        {
            if (!allowedStates.Contains(State))
            {
                throw new InvalidOperationException(errorMessage + " 当前状态：" + State + "。");
            }
        }

        /// <summary>线程安全地更新设备状态和最近错误，并通知界面。</summary>
        private void SetStatus(MachineRunState state, Exception error)
        {
            UpdateStatusFields(state, error);
            RaiseStatusChanged();
        }

        /// <summary>先提交驱动断卡的真实结果，再独立通知每个订阅者；界面异常只记录诊断。</summary>
        private void SetDisconnectStatus(MachineRunState state, Exception error)
        {
            UpdateStatusFields(state, error);
            NotifyDisconnectedSubscribers(StatusChanged, "断卡状态");
        }

        /// <summary>仅在断卡尝试结束后逐个通知订阅者，其他状态变更保留原有派发行为。</summary>
        private void NotifyDisconnectedSubscribers(EventHandler handlers, string source)
        {
            if (handlers == null) return;
            foreach (EventHandler handler in handlers.GetInvocationList())
            {
                try { handler(this, EventArgs.Empty); }
                catch (Exception notificationError)
                {
                    System.Diagnostics.Trace.TraceError(source + "订阅者处理失败：" + notificationError);
                }
            }
        }

        /// <summary>在同一状态锁内更新整机状态及对应错误，不调用外部代码。</summary>
        private void UpdateStatusFields(MachineRunState state, Exception error)
        {
            lock (_statusLock)
            {
                _state = state;
                _lastError = error;
            }

        }

        /// <summary>通知订阅者设备状态已变化。</summary>
        private void RaiseStatusChanged()
        {
            var statusChanged = StatusChanged;
            if (statusChanged != null)
            {
                statusChanged(this, EventArgs.Empty);
            }
        }
    }
}
