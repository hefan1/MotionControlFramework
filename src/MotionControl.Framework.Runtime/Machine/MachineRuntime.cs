using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Runtime.FlowScheduling;

namespace MotionControl.Framework.Runtime.Machine
{
    /// <summary>
    /// 管理一台设备的卡连接、复位、启动和暂停。各工位流程由设备项目提供，
    /// 暂停后保留工位当前步骤，复位成功后才能再次启动。
    /// </summary>
    public sealed class MachineRuntime
    {
        private static readonly TimeSpan DefaultPauseTimeout = TimeSpan.FromSeconds(30);

        private readonly MotionCardGroup _motionCards;
        private readonly StationFlowScheduler _flowScheduler;
        private readonly IMachineAlarmService _alarmService;
        private readonly TimeSpan _pauseTimeout;
        private readonly SemaphoreSlim _commandLock = new SemaphoreSlim(1, 1);
        private readonly object _statusLock = new object();

        private MachineRunState _state;
        private Exception _lastError;
        private Task _flowObserverTask = Task.CompletedTask;

        /// <summary>创建单机运行上下文，并设置流程暂停等待时限。</summary>
        public MachineRuntime(
            MotionCardGroup motionCards,
            IEnumerable<IStationFlow> stationFlows,
            IMachineAlarmService alarmService,
            TimeSpan? pauseTimeout = null)
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

            if (pauseTimeout.HasValue && pauseTimeout.Value <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(pauseTimeout), "暂停等待时限必须大于 0。");
            }

            _motionCards = motionCards;
            _flowScheduler = new StationFlowScheduler(stationFlows);
            _alarmService = alarmService;
            _pauseTimeout = pauseTimeout ?? DefaultPauseTimeout;
            _state = MachineRunState.Disconnected;

            _alarmService.StopAlarmRaised += OnStopAlarmRaised;
            _alarmService.AlarmsChanged += OnAlarmsChanged;
        }

        /// <summary>供界面读取；状态变化可能发生在流程后台线程。</summary>
        public event EventHandler StatusChanged;

        /// <summary>报警列表变化时通知界面刷新。</summary>
        public event EventHandler AlarmsChanged;

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

        public MotionCardGroup MotionCards
        {
            get { return _motionCards; }
        }

        public IMachineAlarmService AlarmService
        {
            get { return _alarmService; }
        }

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

        /// <summary>所有工位流程退出后断开运动卡连接。</summary>
        public async Task DisconnectAsync(CancellationToken cancellationToken)
        {
            await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (State == MachineRunState.Running || State == MachineRunState.Pausing || _flowScheduler.IsRunning)
                {
                    throw new InvalidOperationException("工位流程仍在运行或等待停止，不能断开控制卡。");
                }

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

        /// <summary>复位运动轴、设备流程和停机报警，成功后允许启动。</summary>
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

                try
                {
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

        /// <summary>从各工位保留的当前步骤启动设备流程。</summary>
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                EnsureState(MachineRunState.Ready, "启动前必须先成功复位设备。");
                try
                {
                    SetRunningIfNoStopAlarm();
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
            await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var currentState = State;
                if (currentState != MachineRunState.Running
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
