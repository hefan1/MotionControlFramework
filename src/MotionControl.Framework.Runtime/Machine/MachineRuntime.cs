using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Contracts.Safety;
using MotionControl.Framework.Runtime.FlowScheduling;

namespace MotionControl.Framework.Runtime.Machine
{
    /// <summary>
    /// 管理一台设备的卡连接、复位、启动、暂停和急停监控。各工位流程由设备项目提供；
    /// 没有流程时可以连接控制卡进行手动调试，但不能启动自动运行。
    /// </summary>
    public sealed class MachineRuntime
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

        /// <summary>请求暂停后等待流程退出当前步骤的最长时间。</summary>
        private readonly TimeSpan _pauseTimeout;

        /// <summary>运行时轮询急停输入时两次读取之间的间隔。</summary>
        private readonly TimeSpan _safetyPollInterval;

        /// <summary>串行化连接、复位、启动、暂停和断开等整机命令。</summary>
        private readonly SemaphoreSlim _commandLock = new SemaphoreSlim(1, 1);

        /// <summary>当前仍在执行的手动轴或 IO 操作数量，防止其间启动、复位或断卡。</summary>
        private int _activeManualOperationCount;

        /// <summary>保护整机状态及最近错误，供不同线程安全读取和更新。</summary>
        private readonly object _statusLock = new object();

        /// <summary>当前整机运行状态。</summary>
        private MachineRunState _state;

        /// <summary>最近一次整机操作或流程故障；无错误时为 null。</summary>
        private Exception _lastError;

        /// <summary>观察并回收当前整机流程任务的后台任务。</summary>
        private Task _flowObserverTask = Task.CompletedTask;

        /// <summary>周期监控急停输入的后台任务。</summary>
        private Task _safetyMonitorTask = Task.CompletedTask;

        /// <summary>取消当前急停监控任务的令牌源。</summary>
        private CancellationTokenSource _safetyMonitorCancellation;

        /// <summary>创建单机运行上下文；可选设置启动前初始化流程、暂停时限和急停轮询间隔。</summary>
        public MachineRuntime(
            MotionCardGroup motionCards,
            IEnumerable<IStationFlow> stationFlows,
            IMachineAlarmService alarmService,
            IMachineSafetyStatus safetyStatus,
            TimeSpan? pauseTimeout = null,
            TimeSpan? safetyPollInterval = null,
            IEquipmentInitializationFlow equipmentInitializationFlow = null)
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
            _alarmService = alarmService;
            _safetyStatus = safetyStatus;
            _pauseTimeout = pauseTimeout ?? DefaultPauseTimeout;
            _safetyPollInterval = safetyPollInterval ?? DefaultSafetyPollInterval;
            _state = MachineRunState.Disconnected;

            _alarmService.StopAlarmRaised += OnStopAlarmRaised;
            _alarmService.AlarmsChanged += OnAlarmsChanged;
        }

        /// <summary>供界面读取；状态变化可能发生在流程后台线程。</summary>
        public event EventHandler StatusChanged;

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

        /// <summary>返回本设备使用的报警服务。</summary>
        public IMachineAlarmService AlarmService
        {
            get { return _alarmService; }
        }

        /// <summary>检查手动操作条件并刷新急停状态，返回可供界面本地化显示的阻止原因。</summary>
        public async Task<ManualOperationBlockReason> GetManualOperationBlockReasonAsync(
            CancellationToken cancellationToken)
        {
            ManualOperationBlockReason stateReason = GetManualOperationStateBlockReason();
            if (stateReason != ManualOperationBlockReason.None)
            {
                return stateReason;
            }

            bool emergencyStopActive;
            try
            {
                emergencyStopActive = await UpdateEmergencyStopAlarmAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return ManualOperationBlockReason.EmergencyStopStatusUnavailable;
            }

            if (emergencyStopActive)
            {
                return ManualOperationBlockReason.EmergencyStopInputActive;
            }

            bool emergencyStopAlarmExists = _alarmService.Alarms.Any(
                alarm => string.Equals(alarm.Definition.Code, "EMERGENCY_STOP", StringComparison.OrdinalIgnoreCase));
            if (emergencyStopAlarmExists)
            {
                return ManualOperationBlockReason.EmergencyStopAlarmNotReset;
            }

            return GetManualOperationStateBlockReason();
        }

        /// <summary>检查联锁并执行一项手动操作；操作结束前阻止整机启动、复位和断卡。</summary>
        public async Task<ManualOperationBlockReason> RunManualOperationAsync(
            Func<Task> operation,
            CancellationToken cancellationToken)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            ManualOperationBlockReason blockReason = await BeginManualOperationAsync(cancellationToken)
                .ConfigureAwait(true);
            if (blockReason != ManualOperationBlockReason.None)
            {
                return blockReason;
            }

            try
            {
                await operation().ConfigureAwait(false);
                return ManualOperationBlockReason.None;
            }
            finally
            {
                EndManualOperation();
            }
        }

        /// <summary>
        /// 执行不启动运动、不写入 DO 的手动恢复或示教操作。
        /// 急停状态可以有效、未复位或暂时不可读，但控制卡必须连接且自动流程必须停止。
        /// </summary>
        public async Task<ManualOperationBlockReason> RunManualRecoveryOperationAsync(
            Func<Task> operation,
            CancellationToken cancellationToken)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            ManualOperationBlockReason blockReason = await BeginManualRecoveryOperationAsync(cancellationToken)
                .ConfigureAwait(true);
            if (blockReason != ManualOperationBlockReason.None)
            {
                return blockReason;
            }

            try
            {
                await operation().ConfigureAwait(false);
                return ManualOperationBlockReason.None;
            }
            finally
            {
                EndManualOperation();
            }
        }

        /// <summary>急停输入已恢复但整机急停报警仍锁存时，允许单独复位轴驱动报警。</summary>
        public async Task<ManualOperationBlockReason> RunManualAxisAlarmResetAsync(
            Func<Task> resetOperation,
            CancellationToken cancellationToken)
        {
            if (resetOperation == null)
            {
                throw new ArgumentNullException(nameof(resetOperation));
            }

            ManualOperationBlockReason blockReason = await BeginManualAxisAlarmResetAsync(cancellationToken)
                .ConfigureAwait(true);
            if (blockReason != ManualOperationBlockReason.None)
            {
                return blockReason;
            }

            try
            {
                await resetOperation().ConfigureAwait(false);
                return ManualOperationBlockReason.None;
            }
            finally
            {
                EndManualOperation();
            }
        }

        /// <summary>执行手动受控停止；停止不受急停联锁阻挡，但自动流程期间仍需整机暂停。</summary>
        public async Task<ManualOperationBlockReason> RunManualStopAsync(
            Func<Task> stopOperation,
            CancellationToken cancellationToken)
        {
            if (stopOperation == null)
            {
                throw new ArgumentNullException(nameof(stopOperation));
            }

            ManualOperationBlockReason blockReason = await BeginManualRecoveryOperationAsync(cancellationToken)
                .ConfigureAwait(true);
            if (blockReason != ManualOperationBlockReason.None)
            {
                return blockReason;
            }

            try
            {
                await stopOperation().ConfigureAwait(false);
                return ManualOperationBlockReason.None;
            }
            finally
            {
                EndManualOperation();
            }
        }

        /// <summary>返回本设备配置的工位流程只读列表。</summary>
        public IReadOnlyList<IStationFlow> StationFlows
        {
            get { return _flowScheduler.StationFlows; }
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
                    _flowScheduler.ResetForNewConnection();
                    await _motionCards.ConnectAsync(cancellationToken).ConfigureAwait(false);
                    SetStatus(MachineRunState.ResetRequired, null);
                }
                catch (Exception connectionError)
                {
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
                if (State == MachineRunState.Running || State == MachineRunState.Pausing || _flowScheduler.IsRunning)
                {
                    throw new InvalidOperationException("工位流程仍在运行或等待停止，不能断开控制卡。");
                }

                EnsureNoManualOperationsInProgress("断开运动控制卡");

                try
                {
                    await _motionCards.DisconnectAsync(cancellationToken).ConfigureAwait(false);
                    SetStatus(MachineRunState.Disconnected, null);
                }
                catch (Exception disconnectError)
                {
                    SetStatus(MachineRunState.Alarmed, disconnectError);
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

        /// <summary>执行可选设备初始化后，从各工位保留的当前步骤启动设备流程。</summary>
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_flowScheduler.StationFlows.Count == 0)
                {
                    throw new InvalidOperationException("尚未配置工位流程，不能启动自动运行。");
                }

                EnsureState(MachineRunState.Ready, "启动前必须先成功复位设备。");
                EnsureNoManualOperationsInProgress("启动自动流程");
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
                    SetRunningIfNoStopAlarm();
                    StartEmergencyStopMonitor();
                    var runTask = _flowScheduler.Start();
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
                    SetStatus(MachineRunState.Alarmed, startError);
                    throw;
                }
            }
            finally
            {
                _commandLock.Release();
            }
        }

        /// <summary>读取急停输入并更新停机报警条件；未配置、映射无效或读取失败时按安全策略闭锁。</summary>
        private async Task<bool> UpdateEmergencyStopAlarmAsync(CancellationToken cancellationToken)
        {
            bool emergencyStopActive;
            try
            {
                emergencyStopActive = await _safetyStatus
                    .ReadEmergencyStopActiveAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception readError)
            {
                _alarmService.ReportCondition(
                    EmergencyStopAlarm,
                    "整机急停输入",
                    true,
                    "急停 DI 未配置、映射无效或读取失败，按安全策略禁止动作。原因：" + readError.Message);
                throw new InvalidOperationException("急停输入不可用，已按安全策略禁止动作。", readError);
            }

            _alarmService.ReportCondition(
                EmergencyStopAlarm,
                "整机急停输入",
                emergencyStopActive,
                emergencyStopActive ? "急停输入有效，请释放急停并检查安全回路。" : "急停输入已恢复。");
            return emergencyStopActive;
        }

        /// <summary>启动流程期间周期读取急停输入，触发时借助停机报警暂停整机流程。</summary>
        private void StartEmergencyStopMonitor()
        {
            if (_safetyMonitorTask != null && !_safetyMonitorTask.IsCompleted)
            {
                throw new InvalidOperationException("上一次急停状态监控尚未结束，不能重复启动设备。");
            }

            _safetyMonitorCancellation = new CancellationTokenSource();
            _safetyMonitorTask = MonitorEmergencyStopAsync(_safetyMonitorCancellation.Token);
        }

        /// <summary>在整机运行期间监控急停输入；读取异常时也触发停机报警。</summary>
        private async Task MonitorEmergencyStopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (State == MachineRunState.Running)
                {
                    await Task.Delay(_safetyPollInterval, cancellationToken).ConfigureAwait(false);
                    if (State != MachineRunState.Running)
                    {
                        return;
                    }

                    try
                    {
                        var active = await _safetyStatus
                            .ReadEmergencyStopActiveAsync(cancellationToken)
                            .ConfigureAwait(false);
                        _alarmService.ReportCondition(
                            EmergencyStopAlarm,
                            "整机急停输入",
                            active,
                            active ? "运行期间检测到急停输入有效。" : "急停输入已恢复。");
                        if (active)
                        {
                            return;
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception readError)
                    {
                        _alarmService.ReportCondition(
                            EmergencyStopAlarm,
                            "整机急停输入",
                            true,
                            "运行期间急停 DI 未配置、映射无效或读取失败，按安全策略停机。原因：" + readError.Message);
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 正常暂停时取消轮询，不把监控取消当作设备故障。
            }
        }

        /// <summary>整机流程退出后取消并回收急停状态监控任务。</summary>
        private async Task StopEmergencyStopMonitorAsync()
        {
            var cancellation = _safetyMonitorCancellation;
            if (cancellation == null)
            {
                return;
            }

            cancellation.Cancel();
            try
            {
                await _safetyMonitorTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 监控任务因整机暂停而取消，属于正常结束。
            }
            finally
            {
                if (ReferenceEquals(_safetyMonitorCancellation, cancellation))
                {
                    _safetyMonitorCancellation = null;
                    cancellation.Dispose();
                }
            }
        }

        /// <summary>请求各工位暂停，并在时限内等待所有流程退出当前步骤。</summary>
        public async Task PauseAsync(CancellationToken cancellationToken)
        {
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

        /// <summary>观察整机流程结束或故障，并更新设备运行状态。</summary>
        private async Task ObserveFlowRunAsync(Task runTask)
        {
            try
            {
                await runTask.ConfigureAwait(false);
                if (State == MachineRunState.Running)
                {
                    SetStatus(
                        MachineRunState.Alarmed,
                        new InvalidOperationException("整机运行期间工位流程调度意外结束。"));
                }
                else if (State == MachineRunState.Pausing)
                {
                    SetPausedOrAlarmed();
                }
            }
            catch (Exception flowError)
            {
                if (State == MachineRunState.Running || State == MachineRunState.Pausing)
                {
                    if (_alarmService.HasLatchedStopAlarm)
                    {
                        SetStatus(
                            MachineRunState.Alarmed,
                            CreateStopAlarmError(GetFirstStopAlarm(), flowError));
                        return;
                    }

                    SetStatus(
                        MachineRunState.Alarmed,
                        new InvalidOperationException("工位流程发生错误，整机已暂停。", flowError));
                }
            }
            finally
            {
                await StopEmergencyStopMonitorAsync().ConfigureAwait(false);
            }
        }

        /// <summary>收到停机报警后立即请求所有工位流程暂停。</summary>
        private void OnStopAlarmRaised(object sender, MachineAlarmRaisedEventArgs eventArgs)
        {
            if (!_alarmService.HasLatchedStopAlarm)
            {
                return;
            }

            var alarmError = CreateStopAlarmError(eventArgs.Alarm, null);
            var shouldPauseFlows = false;

            lock (_statusLock)
            {
                if (_state == MachineRunState.Disconnected)
                {
                    return;
                }

                if (_state == MachineRunState.Running || _state == MachineRunState.Pausing)
                {
                    _state = MachineRunState.Pausing;
                    shouldPauseFlows = true;
                }
                else
                {
                    _state = MachineRunState.Alarmed;
                }

                _lastError = alarmError;
            }

            Exception pauseRequestError = null;
            if (shouldPauseFlows)
            {
                try
                {
                    _flowScheduler.RequestPause();
                }
                catch (Exception pauseError)
                {
                    pauseRequestError = new InvalidOperationException(
                        "停机报警触发，但请求工位流程暂停时发生错误。",
                        pauseError);
                    lock (_statusLock)
                    {
                        _lastError = pauseRequestError;
                    }
                }
            }

            RaiseStatusChanged();

            if (shouldPauseFlows)
            {
                _ = MarkPauseTimeoutIfStillRunningAsync();
            }

            if (pauseRequestError != null)
            {
                throw pauseRequestError;
            }
        }

        /// <summary>停机报警触发后，若工位超过暂停时限仍未退出，则把整机状态置为报警。</summary>
        private async Task MarkPauseTimeoutIfStillRunningAsync()
        {
            await Task.Delay(_pauseTimeout).ConfigureAwait(false);
            if (!_flowScheduler.IsRunning)
            {
                return;
            }

            lock (_statusLock)
            {
                if (_state != MachineRunState.Pausing)
                {
                    return;
                }

                _state = MachineRunState.Alarmed;
                _lastError = new TimeoutException(
                    "停机报警后等待工位流程退出超时；工位仍在后台运行，不能复位或断开运动控制卡。");
            }

            RaiseStatusChanged();
        }

        /// <summary>将报警服务的变化事件转发给界面。</summary>
        private void OnAlarmsChanged(object sender, EventArgs eventArgs)
        {
            var alarmsChanged = AlarmsChanged;
            if (alarmsChanged != null)
            {
                alarmsChanged(this, EventArgs.Empty);
            }
        }

        /// <summary>检查启动条件并把设备状态切换到运行或报警。</summary>
        private void SetRunningIfNoStopAlarm()
        {
            var hasStopAlarm = false;

            lock (_statusLock)
            {
                if (_state != MachineRunState.Ready)
                {
                    throw new InvalidOperationException(
                        "启动前必须先成功复位设备。当前状态：" + _state + "。");
                }

                if (_alarmService.HasLatchedStopAlarm)
                {
                    hasStopAlarm = true;
                    _state = MachineRunState.Alarmed;
                    _lastError = CreateStopAlarmError(GetFirstStopAlarm(), null);
                }
                else
                {
                    _state = MachineRunState.Running;
                    _lastError = null;
                }
            }

            RaiseStatusChanged();

            if (hasStopAlarm)
            {
                throw new InvalidOperationException("存在尚未复位的停机报警，设备不能启动。");
            }
        }

        /// <summary>无未复位停机报警时把设备状态切换到就绪。</summary>
        private void SetReadyIfNoStopAlarm()
        {
            var hasStopAlarm = false;

            lock (_statusLock)
            {
                if (_alarmService.HasLatchedStopAlarm)
                {
                    hasStopAlarm = true;
                    _state = MachineRunState.Alarmed;
                    _lastError = CreateStopAlarmError(GetFirstStopAlarm(), null);
                }
                else
                {
                    _state = MachineRunState.Ready;
                    _lastError = null;
                }
            }

            RaiseStatusChanged();

            if (hasStopAlarm)
            {
                throw new InvalidOperationException("复位期间出现新的停机报警，设备仍处于报警状态。");
            }
        }

        /// <summary>流程暂停完成后，根据停机报警状态更新设备状态。</summary>
        private void SetPausedOrAlarmed()
        {
            lock (_statusLock)
            {
                if (_state != MachineRunState.Pausing)
                {
                    return;
                }

                if (_alarmService.HasLatchedStopAlarm)
                {
                    _state = MachineRunState.Alarmed;
                    _lastError = CreateStopAlarmError(GetFirstStopAlarm(), null);
                }
                else
                {
                    _state = MachineRunState.Paused;
                    _lastError = null;
                }
            }

            RaiseStatusChanged();
        }

        /// <summary>返回当前最先找到的停机报警记录。</summary>
        private MachineAlarmRecord GetFirstStopAlarm()
        {
            return _alarmService.Alarms.FirstOrDefault(
                alarm => alarm.Definition.Kind == MachineAlarmKind.StopMachine);
        }

        /// <summary>根据停机报警记录创建带设备定位信息的异常。</summary>
        private static Exception CreateStopAlarmError(MachineAlarmRecord alarm, Exception innerError)
        {
            if (alarm == null)
            {
                return new InvalidOperationException("存在尚未复位的停机报警，整机不能继续运行。", innerError);
            }

            var message = "停机报警 '" + alarm.SourceName + "/" + alarm.Definition.Code
                + "'：" + alarm.Definition.Message + "。整机已请求暂停。";
            if (!string.IsNullOrWhiteSpace(alarm.Detail))
            {
                message += " " + alarm.Detail;
            }

            return new InvalidOperationException(message, innerError);
        }

        /// <summary>检查设备是否处于指定状态，不符合时报告当前状态。</summary>
        private void EnsureState(MachineRunState requiredState, string errorMessage)
        {
            if (State != requiredState)
            {
                throw new InvalidOperationException(errorMessage + " 当前状态：" + State + "。");
            }
        }

        /// <summary>根据整机状态返回当前是否允许手动轴或 IO 操作。</summary>
        private ManualOperationBlockReason GetManualOperationStateBlockReason()
        {
            MachineRunState state = State;
            if (state == MachineRunState.Disconnected)
            {
                return ManualOperationBlockReason.MotionCardDisconnected;
            }

            if (state == MachineRunState.Running || state == MachineRunState.Pausing)
            {
                return ManualOperationBlockReason.AutomaticFlowActive;
            }

            return ManualOperationBlockReason.None;
        }

        /// <summary>在整机命令锁内检查手动联锁并登记正在执行的手动操作。</summary>
        private async Task<ManualOperationBlockReason> BeginManualOperationAsync(
            CancellationToken cancellationToken)
        {
            await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ManualOperationBlockReason blockReason = await GetManualOperationBlockReasonAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (blockReason == ManualOperationBlockReason.None)
                {
                    Interlocked.Increment(ref _activeManualOperationCount);
                }

                return blockReason;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        /// <summary>在整机命令锁内登记恢复操作；拒绝未连接或自动运行状态，但不检查急停锁存。</summary>
        private async Task<ManualOperationBlockReason> BeginManualRecoveryOperationAsync(CancellationToken cancellationToken)
        {
            await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ManualOperationBlockReason blockReason = GetManualOperationStateBlockReason();
                if (blockReason == ManualOperationBlockReason.None)
                {
                    Interlocked.Increment(ref _activeManualOperationCount);
                }

                return blockReason;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        /// <summary>急停输入恢复后，允许复位轴报警；急停仍有效或无法读取时继续阻止复位。</summary>
        private async Task<ManualOperationBlockReason> BeginManualAxisAlarmResetAsync(
            CancellationToken cancellationToken)
        {
            await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ManualOperationBlockReason blockReason = await GetManualOperationBlockReasonAsync(cancellationToken)
                    .ConfigureAwait(false);

                // 急停输入已经恢复；仅整机急停报警尚未复位时，允许先处理轴驱动报警。
                if (blockReason == ManualOperationBlockReason.EmergencyStopAlarmNotReset)
                {
                    blockReason = ManualOperationBlockReason.None;
                }

                if (blockReason == ManualOperationBlockReason.None)
                {
                    Interlocked.Increment(ref _activeManualOperationCount);
                }

                return blockReason;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        /// <summary>手动操作结束时减少运行计数，允许后续整机命令继续执行。</summary>
        private void EndManualOperation()
        {
            Interlocked.Decrement(ref _activeManualOperationCount);
        }

        /// <summary>启动、复位或断卡前拒绝仍有手动操作未完成的情况。</summary>
        private void EnsureNoManualOperationsInProgress(string operationName)
        {
            if (Interlocked.CompareExchange(ref _activeManualOperationCount, 0, 0) > 0)
            {
                throw new InvalidOperationException(
                    "手动轴或 IO 操作仍在执行，不能" + operationName + "；请先等待动作完成或受控停止。");
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
