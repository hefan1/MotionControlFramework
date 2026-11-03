using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.Preparation;
using MotionControl.Framework.Contracts.Clearance;

namespace MotionControl.Framework.Runtime.Machine
{
    /// <summary>清料占用整机准备状态与急停监控，但清料结束不赋予工位准备资格。</summary>
    public sealed partial class MachineRuntime
    {
        private Action _beforeClearance;
        private Action _clearanceActionsObserved;
        private Action<string> _clearanceInspection;
        private bool _clearing;
        private IControlledClearanceReservation _clearanceReservation;
        private ControlledClearanceExecutor _clearanceExecutor;
        private ControlledClearancePlan _clearancePlan;
        private ControlledClearanceProfile _clearanceProfile;
        private IReadOnlyDictionary<string, ControlledClearanceProfile> _clearanceProfiles =
            new Dictionary<string, ControlledClearanceProfile>(StringComparer.OrdinalIgnoreCase);
        private bool _clearanceStopRequested;
        private int _clearanceStopRequests;
        private Task _clearanceCancellationTask = Task.CompletedTask;
        private CancellationTokenRegistration _clearanceExternalCancellation;

        /// <summary>清料正在执行、停止或仍有未确认收尾时为真。</summary>
        public bool IsClearing { get { lock (_preparationSync) return _clearing; } }

        /// <summary>内部观察：完整清料任务、停止请求、取消回调及设备原始调用均已退出。</summary>
        internal bool IsClearanceStopSettled
        {
            get
            {
                lock (_preparationSync)
                    return _clearing && _preparationStopRequests == 0 && _clearanceStopRequests == 0
                        && _preparationTask.IsCompleted && _preparationStopTask.IsCompleted
                        && _clearanceCancellationTask.IsCompleted && _clearanceExecutor != null
                        && _clearanceExecutor.InFlightSettlement.IsCompleted;
            }
        }

        /// <summary>可观察完整清料任务；不能以任务完成代替未确认现场的处理。</summary>
        public Task ClearanceCompletion { get { lock (_preparationSync) return _preparationTask; } }

        /// <summary>连接前由设备项目注册固定清料配置；连接后不能更换计划或恢复条件。</summary>
        public void ConfigureControlledClearanceProfiles(IEnumerable<ControlledClearanceProfile> profiles)
        {
            if (profiles == null) throw new ArgumentNullException(nameof(profiles));
            var declared = new Dictionary<string, ControlledClearanceProfile>(StringComparer.OrdinalIgnoreCase);
            foreach (ControlledClearanceProfile profile in profiles)
            {
                if (profile == null || declared.ContainsKey(profile.Id))
                    throw new ArgumentException("清料配置不能包含空项或重复编号。", nameof(profiles));
                declared.Add(profile.Id, profile);
            }
            if (!_commandLock.Wait(0)) throw new InvalidOperationException("设备命令正在执行，不能更新清料配置。");
            try
            {
                if (State != MachineRunState.Disconnected || IsPreparing)
                    throw new InvalidOperationException("只能在控制器断开且没有清料会话时注册清料配置。");
                _clearanceProfiles = new System.Collections.ObjectModel.ReadOnlyDictionary<string, ControlledClearanceProfile>(declared);
            }
            finally { _commandLock.Release(); }
        }

        /// <summary>连接前配置独立清料证据；不得使用普通准备记录覆盖原会话。</summary>
        public void SetClearanceRecordCallbacks(Action before, Action actionsObserved, Action<string> inspection = null)
        {
            if (State != MachineRunState.Disconnected) throw new InvalidOperationException("只能在连接前配置清料记录。");
            if (before == null || actionsObserved == null) throw new ArgumentException("清料记录回调必须成对提供。");
            _beforeClearance = before;
            _clearanceActionsObserved = actionsObserved;
            _clearanceInspection = inspection;
        }

        /// <summary>内部验证入口；设备批准计划、持续条件及最终恢复契约完成前不得接入正式 Host。</summary>
        internal async Task StartControlledClearanceAsync(string profileId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(profileId)) throw new ArgumentException("清料配置编号不能为空。", nameof(profileId));
            await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            IControlledClearanceReservation reservation = null;
            ControlledClearanceExecutor executor = null;
            ControlledClearanceProfile profile = null;
            bool evidenceEstablished = false;
            try
            {
                if (!_clearanceProfiles.TryGetValue(profileId, out profile))
                    throw new InvalidOperationException("设备未注册该清料配置，不能执行：" + profileId);
                ControlledClearancePlan plan = profile.Plan;
                foreach (string axis in profile.RequiredResources.Where(item => item.StartsWith("Axis:", StringComparison.OrdinalIgnoreCase)))
                    _motionCards.GetAxis(axis.Substring(5));
                EnsureNoPreparationInProgress("启动清料");
                EnsureRecoveryTrusted();
                EnsureOneOfStates(new[] { MachineRunState.Ready, MachineRunState.ResetRequired, MachineRunState.Paused },
                    "当前设备状态不能清料；请先停止生产并处理报警。");
                if (_flowScheduler.IsRunning || !_flowScheduler.IsSettlementConfirmed)
                    throw new InvalidOperationException("旧工位仍运行或停止尚未确认，不能清料。");
                if (_flowObserverTask == null || _flowObserverTask.Status != TaskStatus.RanToCompletion)
                    throw new InvalidOperationException("旧工位结束状态尚未确认，不能清料。");
                var alarmDrain = _alarmService as IMachineStopAlarmNotificationDrain;
                if (alarmDrain == null) throw new InvalidOperationException("报警交接状态不可确认，不能清料。");
                Task notifications = alarmDrain.WaitForStopNotificationDispatchAsync();
                if (notifications == null || notifications.Status != TaskStatus.RanToCompletion)
                    throw new InvalidOperationException("旧报警通知尚未完成交接，不能清料。");
                EnsureNoManualOperationsInProgress("启动清料");
                if (!_motionCards.IsConnected || _controlInputIo == null) throw new InvalidOperationException("控制器或逻辑 IO 不可用，不能清料。");
                if (_beforeClearance == null || _clearanceActionsObserved == null) throw new InvalidOperationException("清料持久记录未配置，不能动作。");
                executor = new ControlledClearanceExecutor(_motionCards.Axes.ToDictionary(axis => axis.Name, StringComparer.OrdinalIgnoreCase), _controlInputIo);
                if (!IsEmergencyStopMonitoringConfigured()) throw new InvalidOperationException("清料必须配置有效急停输入。");
                if (await UpdateEmergencyStopAlarmAsync(cancellationToken).ConfigureAwait(false)) throw new InvalidOperationException("急停输入有效，不能清料。");
                _alarmService.EnsureStopAlarmConditionsRecovered();
                if (_alarmService.HasLatchedStopAlarm) throw new InvalidOperationException("停机报警尚未复位，不能清料。");
                await EnsurePreparationAxesStoppedAsync(cancellationToken, true).ConfigureAwait(false);

                IControlledClearanceResources resourceOwner = null;
                lock (_controlOwnerLock)
                {
                    if (_taskEngineOwner != null)
                        resourceOwner = _taskEngineOwner as IControlledClearanceResources
                            ?? throw new InvalidOperationException("当前任务入口不支持清料资源保护。");
                }
                // 先持久保存清料活动，再申请包含原隔离的完整资源；失败一律保留记录。
                RecoveryBlockReason = "清料已改变或可能改变现场，需核查资源和恢复记录后重新准备。";
                _beforeClearance();
                evidenceEstablished = true;
                if (resourceOwner != null)
                {
                    reservation = resourceOwner.ReserveClearance(profile.RequiredResources);
                    if (reservation == null) throw new InvalidOperationException("任务入口未返回清料资源凭证。");
                    Task resourceReady = reservation.Ready;
                    if (resourceReady == null) throw new InvalidOperationException("清料资源凭证未提供持久化任务。");
                    if (!ReferenceEquals(await Task.WhenAny(resourceReady, Task.Delay(_pauseTimeout, cancellationToken)).ConfigureAwait(false), resourceReady))
                        throw new TimeoutException("清料资源记录尚未完成，设备动作未启动。");
                    await resourceReady.ConfigureAwait(false);
                }
                cancellationToken.ThrowIfCancellationRequested();
                var gate = _alarmService as IMachineAlarmStartGate;
                if (gate == null) throw new InvalidOperationException("报警服务不支持原子检查清料许可。");
                var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                CancellationTokenSource runCancellation = null;
                lock (_statusLock)
                {
                    if (_state != MachineRunState.Ready && _state != MachineRunState.ResetRequired && _state != MachineRunState.Paused)
                        throw new InvalidOperationException("设备状态已改变，不能启动清料。");
                    gate.ExecuteWhenNoStopAlarm(() =>
                    {
                        lock (_preparationSync)
                        {
                            _clearing = true;
                            _preparing = true;
                            _preparationStopUnconfirmed = false;
                            _clearanceStopRequested = false;
                            _clearanceCancellationTask = Task.CompletedTask;
                            _preparationRecordReusable = false;
                            _clearanceReservation = reservation;
                            _clearanceExecutor = executor;
                            _clearancePlan = plan;
                            _clearanceProfile = profile;
                            runCancellation = _preparationCancellation = new CancellationTokenSource();
                            _preparationTask = Task.Run(async () => { await start.Task.ConfigureAwait(false); await RunControlledClearanceAsync(plan, executor, reservation, runCancellation).ConfigureAwait(false); });
                        }
                        _state = MachineRunState.Preparing;
                        _lastError = null;
                    });
                }
                NotifyClearanceStatusChanged();
                _clearanceExternalCancellation = cancellationToken.Register(() => _ = StopClearanceAfterExternalCancellationAsync(runCancellation));
                try { StartEmergencyStopMonitor(); }
                catch (Exception error)
                {
                    lock (_preparationSync)
                    {
                        _preparationStopUnconfirmed = true;
                        _clearanceStopRequested = true;
                        _clearanceCancellationTask = Task.Run(() => runCancellation.Cancel());
                    }
                    start.TrySetException(error);
                    SetClearanceStatus(MachineRunState.Alarmed, error);
                    throw;
                }
                start.TrySetResult(true);
            }
            catch (Exception error)
            {
                Exception failure = PreserveClearanceFailure(reservation, error, "清料未能安全启动");
                if (evidenceEstablished && !IsClearing)
                {
                    var canceled = new CancellationTokenSource();
                    canceled.Cancel();
                    Task stopped = Task.FromException(failure);
                    _ = stopped.ContinueWith(task => { var ignored = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                    lock (_preparationSync)
                    {
                        _clearing = true;
                        _preparing = true;
                        _preparationStopUnconfirmed = true;
                        _clearanceStopRequested = true;
                        _clearanceReservation = reservation;
                        _clearanceExecutor = executor;
                        _clearancePlan = profile.Plan;
                        _clearanceProfile = profile;
                        _preparationCancellation = canceled;
                        _preparationTask = stopped;
                        _clearanceCancellationTask = Task.CompletedTask;
                    }
                    SetClearanceStatus(MachineRunState.Alarmed, failure);
                }
                else if (!ReferenceEquals(failure, error)) SetClearanceStatus(MachineRunState.Alarmed, failure);
                if (ReferenceEquals(failure, error)) throw;
                throw failure;
            }
            finally { _commandLock.Release(); }
        }

        /// <summary>请求清料停止并等待停轴和后台任务；超时继续占用设备。</summary>
        public Task StopControlledClearanceAsync() { return StopControlledClearanceAsync(null); }

        /// <summary>报警线程只发起清料停止，停止失败保留在设备报警状态中。</summary>
        private async Task StopClearanceAfterAlarmAsync()
        {
            try { await StopControlledClearanceAsync().ConfigureAwait(false); }
            catch (Exception error) { SetClearanceStatus(MachineRunState.Alarmed, error); }
        }

        /// <summary>外部取消与本会话绑定，不能让旧回调停止后续清料。</summary>
        private async Task StopClearanceAfterExternalCancellationAsync(CancellationTokenSource expected)
        {
            try { await StopControlledClearanceAsync(expected).ConfigureAwait(false); }
            catch (Exception error) { SetClearanceStatus(MachineRunState.Alarmed, error); }
        }

        private async Task StopControlledClearanceAsync(CancellationTokenSource expected)
        {
            Task operation;
            Task cancellationTask;
            lock (_preparationSync)
            {
                if (!_clearing) return;
                if (expected != null && !ReferenceEquals(expected, _preparationCancellation)) return;
                operation = _preparationTask;
                _clearanceStopRequested = true;
                _clearanceStopRequests++;
                if (_clearanceCancellationTask.IsCompleted && _preparationCancellation != null
                    && !_preparationCancellation.IsCancellationRequested)
                {
                    CancellationTokenSource target = _preparationCancellation;
                    // 回调可以重入运行时或阻塞；不在状态锁内执行，保留任务防止迟到影响新会话。
                    _clearanceCancellationTask = Task.Run(() => target.Cancel());
                }
                cancellationTask = _clearanceCancellationTask;
            }
            try
            {
                await StopPreparationAxesAsync().ConfigureAwait(false);
                Task settlement = Task.WhenAll(operation, cancellationTask);
                if (!ReferenceEquals(await Task.WhenAny(settlement, Task.Delay(_pauseTimeout)).ConfigureAwait(false), settlement))
                    throw new TimeoutException("清料任务仍在执行或收尾，设备继续保持隔离。");
                await settlement.ConfigureAwait(false);
            }
            catch (Exception error)
            {
                IControlledClearanceReservation reservation;
                lock (_preparationSync)
                {
                    _preparationStopUnconfirmed = true;
                    reservation = _clearanceReservation;
                }
                Exception failure = PreserveClearanceFailure(reservation, error, "清料停止未确认");
                SetClearanceStatus(MachineRunState.Alarmed, failure);
                if (ReferenceEquals(failure, error)) throw;
                throw failure;
            }
            finally { lock (_preparationSync) _clearanceStopRequests--; }
        }

        /// <summary>由 Runtime 持有取消、动作、停轴与证据直至完整结算。</summary>
        private async Task RunControlledClearanceAsync(ControlledClearancePlan plan, ControlledClearanceExecutor executor,
            IControlledClearanceReservation reservation, CancellationTokenSource cancellation)
        {
            Exception failure = null;
            try
            {
                await executor.ExecuteAsync(plan, cancellation.Token).ConfigureAwait(false);
                cancellation.Token.ThrowIfCancellationRequested();
                if (executor.HasUnsettledAction) throw new InvalidOperationException("清料动作仍有未确认收尾。");
                await EnsurePreparationAxesStoppedAsync(CancellationToken.None, true).ConfigureAwait(false);
                cancellation.Token.ThrowIfCancellationRequested();
                if (_alarmService.HasLatchedStopAlarm) throw new InvalidOperationException("清料结束时存在停机报警。");
            }
            catch (Exception error)
            {
                failure = PreserveClearanceFailure(reservation, error, "清料失败或中断");
                try { await StopPreparationAxesAsync().ConfigureAwait(false); }
                catch (Exception stopError) { failure = new AggregateException("清料停止未确认。", failure, stopError); }
            }
            await _commandLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (failure == null && cancellation.IsCancellationRequested)
                    failure = new OperationCanceledException("清料完成确认前收到停止请求。");
                if (failure == null && (_alarmService.HasLatchedStopAlarm || State == MachineRunState.Alarmed))
                    failure = new InvalidOperationException("清料结束时设备处于报警状态。");
                if (failure == null)
                {
                    try { _clearanceActionsObserved(); }
                    catch (Exception recordError) { failure = new InvalidOperationException("清料结束记录保存失败。", recordError); }
                }
                try { await StopEmergencyStopMonitorAsync().ConfigureAwait(false); }
                catch (Exception monitorError) { failure = failure == null ? monitorError : new AggregateException(failure, monitorError); }
                if (failure == null && cancellation.IsCancellationRequested)
                    failure = new OperationCanceledException("清料结束记录保存期间收到停止请求。");
                if (failure == null && (_alarmService.HasLatchedStopAlarm || State == MachineRunState.Alarmed))
                    failure = new InvalidOperationException("清料结束记录保存期间出现停机报警。");
                if (failure == null)
                {
                    try
                    {
                        var gate = _alarmService as IMachineAlarmStartGate;
                        if (gate == null) throw new InvalidOperationException("报警服务不支持原子检查清料结束状态。");
                        lock (_statusLock)
                        {
                            gate.ExecuteWhenNoStopAlarm(() =>
                            {
                                lock (_preparationSync)
                                {
                                    cancellation.Token.ThrowIfCancellationRequested();
                                    if (_clearanceStopRequested) throw new OperationCanceledException("清料完成前收到停止请求。");
                                    reservation?.Complete();
                                    _clearing = false;
                                    _preparing = false;
                                    _preparationCancellation = null;
                                    _clearanceReservation = null;
                                    _clearanceExecutor = null;
                                    _clearancePlan = null;
                                    _clearanceProfile = null;
                                    _state = MachineRunState.ResetRequired;
                                    _lastError = null;
                                }
                            });
                        }
                        InvalidateClearancePreparations("清料改变了现场，请核实并重新准备。");
                        NotifyClearanceStatusChanged();
                    }
                    catch (Exception error) { failure = error; }
                }
                if (failure != null)
                {
                    failure = PreserveClearanceFailure(reservation, failure, "清料收尾未确认");
                    lock (_preparationSync) _preparationStopUnconfirmed = true;
                    SetClearanceStatus(MachineRunState.Alarmed, failure);
                }
            }
            finally
            {
                _commandLock.Release();
                if (failure == null) { _clearanceExternalCancellation.Dispose(); cancellation.Dispose(); }
            }
            NotifyClearancePreparationChanged();
            if (failure != null) throw new InvalidOperationException("清料未完成，设备继续保持隔离：" + failure.Message, failure);
        }

        /// <summary>不能吞掉隔离锁存错误；原故障与锁存故障都保留供排查。</summary>
        private static Exception PreserveClearanceFailure(IControlledClearanceReservation reservation, Exception error, string context)
        {
            if (reservation == null) return error;
            try { reservation.MarkUnconfirmed(context + "：" + error.Message); return error; }
            catch (Exception isolationError)
            { return new AggregateException(context + "，资源隔离锁存也失败。", error, isolationError); }
        }

        /// <summary>清料状态已经提交后，单个界面订阅者异常不能阻断安全流程或伪造回滚。</summary>
        private void NotifyClearanceStatusChanged()
        {
            var handlers = StatusChanged;
            if (handlers == null) return;
            foreach (EventHandler handler in handlers.GetInvocationList())
                try { handler(this, EventArgs.Empty); }
                catch (Exception error) { System.Diagnostics.Trace.TraceError("清料状态通知失败：" + error); }
        }

        /// <summary>清料准备状态通知隔离单个订阅者故障，其余订阅者仍可收到更新。</summary>
        private void NotifyClearancePreparationChanged()
        {
            var handlers = PreparationChanged;
            if (handlers == null) return;
            foreach (EventHandler handler in handlers.GetInvocationList())
                try { handler(this, EventArgs.Empty); }
                catch (Exception error) { System.Diagnostics.Trace.TraceError("清料准备状态通知失败：" + error); }
        }

        /// <summary>更新清料状态，再独立通知订阅者。</summary>
        private void SetClearanceStatus(MachineRunState state, Exception error)
        {
            lock (_statusLock) { _state = state; _lastError = error; }
            NotifyClearanceStatusChanged();
        }

        /// <summary>清料改变现场后撤销准备资格；通知失败不影响已写入的准备状态。</summary>
        private void InvalidateClearancePreparations(string reason)
        {
            lock (_preparationSync)
                foreach (IStationPreparation item in _preparations)
                    _preparationStates[item.StationId] = new StationPreparationSnapshot(item.StationId,
                        item.StationName, StationPreparationState.NotPrepared, "需要重新准备", reason);
            NotifyClearancePreparationChanged();
        }
    }
}
