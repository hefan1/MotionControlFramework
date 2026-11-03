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
        /// <summary>当前任务会话的唯一所有者。</summary>
        private object _taskEngineOwner;
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
        /// <summary>当前控制卡连接周期的唯一标识；断卡后为空，重连会产生新标识。</summary>
        private Guid? _connectionSessionId;

        /// <summary>读取本次控制卡连接周期标识；同一 Runtime 对象重连也会改变。</summary>
        public Guid? ConnectionSessionId { get { lock (_statusLock) return _connectionSessionId; } }

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

        /// <summary>恢复记录不可信时拦截自动生产和工位准备；不影响手动排查。</summary>
        public string RecoveryBlockReason { get; private set; }

        /// <summary>连接前安装恢复校验结果，不能在设备运行期间更改此门禁。</summary>
        public void SetRecoveryBlockReason(string reason)
        {
            if (State != MachineRunState.Disconnected) throw new InvalidOperationException("只能在连接前设置恢复检查结果。");
            RecoveryBlockReason = string.IsNullOrWhiteSpace(reason) ? null : reason;
        }

        /// <summary>恢复数据不可信时禁止开始自动动作。</summary>
        private void EnsureRecoveryTrusted()
        { if (RecoveryBlockReason != null) throw new InvalidOperationException("设备恢复信息未确认，不能准备或启动：" + RecoveryBlockReason); }

        /// <summary>读取自动启动拦截原因，供操作页在启动前显示具体就绪条件。</summary>
        public async Task<string> GetAutomaticStartBlockReasonAsync(CancellationToken cancellationToken)
        {
            if (RecoveryBlockReason != null) return "设备恢复信息未确认，不能准备或启动：" + RecoveryBlockReason;
            if (State != MachineRunState.Ready)
            {
                return null;
            }

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
                if (await UpdateEmergencyStopAlarmAsync(cancellationToken).ConfigureAwait(false))
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
                return null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception readinessError)
            {
                return readinessError.Message;
            }
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
                EnsureState(MachineRunState.Disconnected, "只有断开状态的设备可以连接运动控制卡。");
                try
                {
                    InvalidatePreparationRecordReuse();
                    _flowScheduler.ResetForNewConnection();
                    InvalidatePreparations("控制器已重新连接，请重新确认工位准备。");
                    await _motionCards.ConnectAsync(cancellationToken).ConfigureAwait(false);
                    lock (_statusLock) _connectionSessionId = Guid.NewGuid();
                    SetStatus(MachineRunState.ResetRequired, null);
                    StartControlInputMonitor();
                }
                catch (Exception connectionError)
                {
                    lock (_statusLock) _connectionSessionId = null;
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
                EnsureNoPreparationInProgress("断开控制卡");
                if (State == MachineRunState.Running || State == MachineRunState.Pausing || _flowScheduler.IsRunning)
                {
                    throw new InvalidOperationException("工位流程仍在运行或等待停止，不能断开控制卡。");
                }

                lock (_controlOwnerLock)
                {
                    if (_taskEngineOwner != null)
                        throw new InvalidOperationException("任务执行会话仍占用设备；请先在任务运行页停止并关闭会话，再断开控制卡。");
                }

                EnsureNoManualOperationsInProgress("断开运动控制卡");
                InvalidatePreparationRecordReuse();
                lock (_statusLock) _connectionSessionId = null;

                try
                {
                    await StopControlInputMonitorAsync().ConfigureAwait(false);
                    await _motionCards.DisconnectAsync(cancellationToken).ConfigureAwait(false);
                    InvalidatePreparations("控制器已断开，准备状态无效。");
                    SetStatus(MachineRunState.Disconnected, null);
                }
                catch (Exception disconnectError)
                {
                    SetStatus(MachineRunState.Alarmed, disconnectError);
                    // 断卡失败后仍有卡连接时，恢复现场按钮轮询；重新建立输入基线，
                    // 不把断卡期间已经有效的电平误当成新的按钮上升沿。
                    if (_motionCards.Cards.Any(card => card.IsConnected))
                    {
                        try
                        {
                            StartControlInputMonitor();
                        }
                        catch (Exception monitorError)
                        {
                            throw new AggregateException("断卡失败，且现场按钮监控恢复失败，请检查现场。",
                                disconnectError, monitorError);
                        }
                    }
                    throw;
                }
            }
            finally
            {
                _commandLock.Release();
            }
        }

        /// <summary>确认所有轴停稳且安全条件恢复后，复位运动轴、设备流程和停机报警。</summary>
        public async Task ResetAsync(CancellationToken cancellationToken)
        {
            await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                EnsureNoPreparationInProgress("报警复位");
                EnsureOneOfStates(
                    new[] { MachineRunState.ResetRequired, MachineRunState.Paused, MachineRunState.Alarmed },
                    "设备当前状态不允许复位。");

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

                        if (axisStatus.Moving)
                        {
                            throw new InvalidOperationException(
                                "逻辑轴 '" + axis.Name + "' 仍在运动，停止所有轴后再执行整机复位。");
                        }
                    }

                    await UpdateEmergencyStopAlarmAsync(cancellationToken).ConfigureAwait(false);
                    _alarmService.EnsureStopAlarmConditionsRecovered();

                    foreach (var axis in _motionCards.Axes)
                    {
                        try
                        {
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

                    _alarmService.ResetStopAlarms();
                    SetReadyIfNoStopAlarm();
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
        public async Task AcquireTaskEngineControlAsync(object owner, CancellationToken cancellationToken)
        {
            if (owner == null) throw new ArgumentNullException(nameof(owner));
            await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                EnsureNoPreparationInProgress("切换任务执行入口");
                EnsureNoManualOperationsInProgress("切换任务执行入口");
                lock (_controlOwnerLock)
                {
                    if (_taskEngineOwner != null)
                    {
                        if (ReferenceEquals(_taskEngineOwner, owner)) return;
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
                    _controlOwner = MachineControlOwner.TaskEngine;
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
            if (owner == null) throw new ArgumentNullException(nameof(owner));
            if (!_commandLock.Wait(0)) throw new InvalidOperationException("设备命令仍在执行，不能关闭任务会话。");
            try
            {
                EnsureNoPreparationInProgress("关闭任务会话");
                lock (_controlOwnerLock)
                {
                    if (_controlOwner != MachineControlOwner.TaskEngine)
                    {
                        return;
                    }

                    if (!ReferenceEquals(_taskEngineOwner, owner))
                        throw new InvalidOperationException("任务执行入口的所有者不匹配，不能归还。");

                    var currentState = State;
                    if (currentState == MachineRunState.Running || currentState == MachineRunState.Pausing)
                    {
                        throw new InvalidOperationException(
                            "设备正在运行，不能归还任务执行入口；请先停止任务并确认所有轴已停止。当前状态："
                            + currentState + "。");
                    }

                    _controlOwner = _ownerBeforeTaskEngine;
                    _taskEngineOwner = null;
                }
            }
            finally { _commandLock.Release(); }
        }

        /// <summary>执行可选设备初始化后，从各工位保留的当前步骤启动设备流程。</summary>
        public async Task StartAsync(CancellationToken cancellationToken)
        {
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

                EnsureState(MachineRunState.Ready, "启动前必须先成功复位设备。");
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
                    if (!IsStationFlowSettlementConfirmed)
                        throw new InvalidOperationException("上一次工位流程或停机报警尚未确认收尾，不能启动。");
                    RecordBeforeAutomaticRun();
                    SetRunningIfNoStopAlarm();
                    StartEmergencyStopMonitor();
                    var startGate = _alarmService as IMachineAlarmStartGate;
                    if (startGate == null)
                        throw new InvalidOperationException("报警服务不支持原子检查工位流程启动许可。");
                    var activation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    Task runTask = null;
                    try
                    {
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
            lock (_statusLock)
            {
                _state = state;
                _lastError = error;
            }

            RaiseStatusChanged();
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
