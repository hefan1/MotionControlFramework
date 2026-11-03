using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Device;
using MotionControl.TaskEngine.Device.InMemory;
using MotionControl.TaskEngine.Execution;
using MotionControl.TaskEngine.Resources;

namespace MotionControl.TaskEngine.Machine
{
    /// <summary>复位、启动、暂停、终止与故障锁存的具体顺序。</summary>
    public sealed partial class TaskMachineController
    {
        /// <summary>等待暂停收尾的界面操作时限；超时只报告未确认，绝不当作轴已停稳。</summary>
        private static readonly TimeSpan PauseSettlementTimeout = TimeSpan.FromSeconds(12);
        /// <summary>在 Runtime 清除轴报警前检查任务侧复位条件，不改变运行状态。</summary>
        public void ValidateResetPrerequisites()
        {
            AlarmService.EnsureStopAlarmConditionsRecovered();
            lock (_sync) ValidateResetConditions(false);
        }

        /// <summary>在线程池检查任务复位条件，避免读取轴状态阻塞 WPF 界面线程。</summary>
        public Task ValidateResetPrerequisitesAsync()
        {
            return Task.Run(() => ValidateResetPrerequisites());
        }

        /// <summary>检查停机条件和轴报警后进入就绪；不清报警、不重置工位步骤、不自动运行。</summary>
        public void Reset()
        {
            AlarmService.EnsureStopAlarmConditionsRecovered();
            lock (_sync)
            {
                ValidateResetConditions(true);
            }
            AlarmService.ResetStopAlarms();
            lock (_sync)
            {
                // 报警复位可能失败或与新报警并发；只能在全部检查完成后发布就绪。
                AlarmService.EnsureStopAlarmConditionsRecovered();
                ValidateResetConditions(true);
                _faults.Clear();
                _state = TaskMachineState.Ready;
            }
        }

        /// <summary>检查任务复位条件；Runtime 复位前允许仍待清除的轴报警存在。</summary>
        private void ValidateResetConditions(bool requireAxisAlarmsCleared)
        {
            RequireOpen();
            if (_preparationActive) throw new InvalidOperationException("工位准备正在执行，请先停止回零再复位。");
            if (_state == TaskMachineState.Running || _state == TaskMachineState.Pausing)
                throw new InvalidOperationException("请先暂停整机并确认所有轴已停稳，再复位。");
            if (_device.ReadResources().Any(resource => resource.State == TaskResourceState.Blocked))
                throw new InvalidOperationException("存在待确认区域，请先核实并显式恢复资源。");
            if (HasUnsettledPauseWork())
                throw new InvalidOperationException("任务仍有未完成的设备操作或资源记录，不能确认安全；请等待收尾并检查现场。");
            if (_activeStopAlarms.Count != 0) throw new InvalidOperationException("停机条件仍有效：" + string.Join("；", _activeStopAlarms.Values));
            var axesRequiringHome = new HashSet<string>(GetAxesRequiringPreHome(), StringComparer.OrdinalIgnoreCase);
            foreach (string name in _device.Catalog.Axes.Keys)
            {
                TaskAxisState axis = _device.ReadAxis(name);
                if (requireAxisAlarmsCleared && !string.IsNullOrWhiteSpace(axis.Alarm))
                    throw new InvalidOperationException("轴报警尚未解除：" + name + "，" + axis.Alarm);
                if (!(_device is InMemoryTaskDevice) && axesRequiringHome.Contains(name))
                {
                    // Runtime 随后会清除轴报警；报警存在时先不以伺服状态阻止该清除步骤。
                    if (!axis.Enabled && string.IsNullOrWhiteSpace(axis.Alarm))
                        throw new InvalidOperationException("任务使用的轴尚未使能：" + name + "。请先结束任务会话；如有报警，先处理原因并复位，再到轴手动页面使能，最后重新读取任务。");
                    if (!axis.Homed) throw new InvalidOperationException("任务使用的轴尚未回零：" + name + "。请在轴手动页面回零后再复位任务。");
                }
            }
            if (AllStations().Any(station => station.Unrecoverable))
                throw new InvalidOperationException("执行器已终止，无法证明当前步骤可以恢复；请终止并核实生产状态。");
        }

        /// <summary>在线程池执行同步轴状态读取，避免任务复位阻塞 WPF 界面线程。</summary>
        public Task ResetAsync()
        {
            return Task.Run(() => Reset());
        }

        /// <summary>只从复位后的就绪启动；首次创建工位，后续继续原节点和原终点。</summary>
        public void Start()
        {
            lock (_sync)
            {
                RequireOpen();
                if (_state != TaskMachineState.Ready) throw new InvalidOperationException("必须先复位到就绪状态再启动。");
                if (HasUnsettledPauseWork())
                    throw new InvalidOperationException("任务停止或故障处理仍未确认收尾，不能启动。");
                EnsurePreparationCompletedForStart();
                var startGate = AlarmService as IMachineAlarmStartGate;
                if (startGate == null)
                    throw new InvalidOperationException("报警服务不支持原子检查并发放启动许可，不能启动任务。");
                // 只在报警服务锁内提交内存许可，不访问设备或等待后台任务。
                startGate.ExecuteWhenNoStopAlarm(CommitStart);
            }
        }

        /// <summary>原子门禁内提交运行许可；调用方已经持有整机锁和报警启动门禁。</summary>
        private void CommitStart()
        {
            _state = TaskMachineState.Running;
            foreach (StationExecutionControl station in AllStations())
            {
                station.CurrentRun?.Resume();
                station.RecoveryPermit?.TrySetResult(true);
                station.RecoveryPermit = null;
            }
            _runningPermit.TrySetResult(true);
            if (!_workersStarted)
            {
                _workersStarted = true;
                if (_initialization != null) _workers.Add(Task.Run(() => RunInitializationAsync(_lifetime.Token)));
                foreach (StationExecutionControl station in _stations.Where(item => item.Configuration.Enabled))
                    _workers.Add(Task.Run(() => RunStationAsync(station, _lifetime.Token)));
            }
        }

        /// <summary>兼容内存工作台的同步暂停；真实设备页面应使用等待轴停稳的 PauseAsync。</summary>
        public void Pause()
        {
            if (!(_device is InMemoryTaskDevice))
                throw new InvalidOperationException("真实设备暂停必须调用 PauseAsync，并等待所有轴受控停稳。");
            lock (_sync)
            {
                RequireOpen();
                if (_state == TaskMachineState.Faulted) return;
                if (_state != TaskMachineState.Running) throw new InvalidOperationException("只有自动运行状态可以暂停。");
                PauseAll(TaskMachineState.Paused);
            }

            // 兼容内存工作台的同步暂停；正式设备页面使用 PauseAsync 等待停稳。
        }

        /// <summary>暂停整机并等待所有当前运动轴受控停止后再返回。</summary>
        public async Task PauseAsync()
        {
            TaskRun[] runs;
            lock (_sync)
            {
                RequireOpen();
                if (_state == TaskMachineState.Faulted) return;
                if (_state != TaskMachineState.Running) throw new InvalidOperationException("只有自动运行状态可以暂停。");
                runs = PauseAll(TaskMachineState.Pausing);
                BeginPauseSettlement();
            }

            try
            {
                await StopPausedRunsAsync(runs).ConfigureAwait(false);
                lock (_sync)
                    if (_state == TaskMachineState.Pausing)
                        _state = _faults.Count == 0 ? TaskMachineState.Paused : TaskMachineState.Faulted;
            }
            catch (Exception stopError)
            {
                lock (_sync)
                {
                    _pauseSettlementUnconfirmed = true;
                    if (_state != TaskMachineState.Stopped && _state != TaskMachineState.Closed) _state = TaskMachineState.Faulted;
                    AddFault(new TaskMachineFault("PAUSE_STOP_FAILED", "暂停时等待轴受控停止失败：" + stopError.Message, null));
                }

                throw new InvalidOperationException("暂停未能确认所有轴已停稳；整机已锁定为故障状态。", stopError);
            }
            finally { EndPauseSettlement(); }
        }

        /// <summary>更新外部停机条件；解除条件后仍须复位，不能自动继续。</summary>
        public void SetStopAlarm(string code, string message, bool active)
        {
            if (!(_device is InMemoryTaskDevice))
                throw new InvalidOperationException("真实设备停机条件必须使用 SetStopAlarmAsync，确认轴受控停稳。");
            SetStopAlarmCore(code, message, active);
        }

        /// <summary>锁存外部停机条件并等待所有真实轴受控停止。</summary>
        public async Task SetStopAlarmAsync(string code, string message, bool active)
        {
            TaskRun[] runs = SetStopAlarmCore(code, message, active, true, TaskMachineState.Pausing, active);
            try { await StopPausedRunsAsync(runs).ConfigureAwait(false); }
            catch (Exception error)
            {
                lock (_sync)
                {
                    _pauseSettlementUnconfirmed = true;
                    if (_state != TaskMachineState.Stopped && _state != TaskMachineState.Closed) _state = TaskMachineState.Faulted;
                    AddFault(new TaskMachineFault("ALARM_STOP_FAILED", "停机报警收尾未确认：" + error.Message, null));
                }
                throw;
            }
            finally { if (active) EndPauseSettlement(); }
            if (active)
                lock (_sync)
                    if (_state == TaskMachineState.Pausing || _state == TaskMachineState.Paused) _state = TaskMachineState.Faulted;
        }

        /// <summary>只记录报警快照，报警条件状态仍由共享设备报警服务负责。</summary>
        private async Task HandleAlarmServiceStopAsync(string code, string message)
        {
            TaskRun[] runs = SetStopAlarmCore(code, message, true, false, TaskMachineState.Pausing, true);
            try
            {
                await StopPausedRunsAsync(runs).ConfigureAwait(false);
                lock (_sync)
                    if (_state == TaskMachineState.Pausing || _state == TaskMachineState.Paused) _state = TaskMachineState.Faulted;
            }
            catch (Exception error)
            {
                lock (_sync)
                {
                    _pauseSettlementUnconfirmed = true;
                    if (_state != TaskMachineState.Stopped && _state != TaskMachineState.Closed) _state = TaskMachineState.Faulted;
                    AddFault(new TaskMachineFault("ALARM_STOP_FAILED", "停机报警后等待轴受控停止失败：" + error.Message, null));
                }
                throw;
            }
            finally { EndPauseSettlement(); }
        }

        /// <summary>把共享报警服务的新停机报警送入任务整机并停止当前运动。</summary>
        private async void AlarmService_StopAlarmRaised(object sender, MachineAlarmRaisedEventArgs eventArgs)
        {
            try
            {
                await HandleAlarmServiceStopAsync(eventArgs.Alarm.Definition.Code,
                    eventArgs.Alarm.Definition.Message + " " + eventArgs.Alarm.Detail).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                System.Diagnostics.Trace.TraceError("任务执行收到停机报警后受控停止失败：" + error);
            }
        }

        /// <summary>更新停机条件和整机状态，并冻结全部运行记录。</summary>
        private TaskRun[] SetStopAlarmCore(string code, string message, bool active,
            bool trackLocalCondition = true, TaskMachineState stoppingState = TaskMachineState.Faulted,
            bool trackSettlement = false)
        {
            if (string.IsNullOrWhiteSpace(code) || (active && string.IsNullOrWhiteSpace(message)))
                throw new ArgumentException("停机报警必须提供代码和有效原因。");
            lock (_sync)
            {
                RequireOpen();
                if (!active) { _activeStopAlarms.Remove(code); return new TaskRun[0]; }
                if (trackLocalCondition) _activeStopAlarms[code] = message;
                AddFault(new TaskMachineFault(code, message, null));
                if (trackSettlement) BeginPauseSettlement();
                return PauseAll(stoppingState);
            }
        }

        /// <summary>动作已局部收尾，保存故障步骤并暂停全部其他工位；执行器在锁外等待恢复许可。</summary>
        internal async Task<Task> ReportNodeFaultAsync(StationExecutionControl station, TaskExecutionException error)
        {
            TaskRun[] runs;
            Task permit;
            lock (_sync)
            {
                _lifetime.Token.ThrowIfCancellationRequested();
                station.State = StationTaskState.WaitingReset;
                station.Detail = error.Message;
                if (station.RecoveryPermit == null) station.RecoveryPermit = NewSignal();
                AddFault(new TaskMachineFault(station.Configuration.StationId, error.Message, error));
                runs = PauseAll(TaskMachineState.Pausing);
                BeginPauseSettlement();
                permit = station.RecoveryPermit.Task;
            }

            try
            {
                await StopPausedRunsAsync(runs).ConfigureAwait(false);
                lock (_sync)
                    if (_state == TaskMachineState.Pausing || _state == TaskMachineState.Paused) _state = TaskMachineState.Faulted;
            }
            catch
            {
                lock (_sync)
                {
                    _pauseSettlementUnconfirmed = true;
                    if (_state != TaskMachineState.Stopped && _state != TaskMachineState.Closed) _state = TaskMachineState.Faulted;
                }
                throw;
            }
            finally { EndPauseSettlement(); }
            return permit;
        }

        /// <summary>检查已冻结操作、停止回调、资源记录及隔离；调用方必须持有整机锁。</summary>
        private bool HasUnsettledPauseWork()
        {
            return _pauseSettlementUnconfirmed || _activePauseSettlements != 0
                || AllStations().Any(station => station.CurrentRun != null && station.CurrentRun.HasUnsettledPauseOperations)
                || _device.Resources.HasPendingJournalWrites
                || _device.ReadResources().Any(resource => resource.State == TaskResourceState.Blocked);
        }

        /// <summary>冻结运行的同一整机锁内登记收尾，防止复位先于报警处理交接。</summary>
        private void BeginPauseSettlement()
        {
            if (_activePauseSettlements++ == 0) _pauseSettlementsIdle = NewSignal();
        }

        /// <summary>完成已登记的停止或报警收尾；即使出错也必须解除在途计数。</summary>
        private void EndPauseSettlement()
        {
            lock (_sync)
            {
                if (_activePauseSettlements <= 0) throw new InvalidOperationException("暂停收尾登记不匹配。");
                _activePauseSettlements--;
                if (_activePauseSettlements == 0) _pauseSettlementsIdle.TrySetResult(true);
            }
        }

        /// <summary>把全部周期许可和正在执行的运行冻结；调用方已持有整机锁。</summary>
        private TaskRun[] PauseAll(TaskMachineState state)
        {
            _preparationRun?.Cancel();
            if (_state == TaskMachineState.Running) _runningPermit = NewSignal();
            // 已锁存故障不能被并发到来的另一条暂停/停机处理暂时降级回“暂停中”。
            if (_state != TaskMachineState.Faulted || state == TaskMachineState.Faulted) _state = state;
            TaskRun[] runs = AllStations().Select(station => station.CurrentRun).Where(run => run != null).ToArray();
            foreach (TaskRun run in runs) run.Pause();
            return runs;
        }

        /// <summary>等待指定任务运行中的受控停止登记全部完成。</summary>
        private async Task StopPausedRunsAsync(IEnumerable<TaskRun> runs)
        {
            Task settled = SettlePausedRunsAsync(runs);
            if (await Task.WhenAny(settled, Task.Delay(PauseSettlementTimeout)).ConfigureAwait(false) != settled)
            {
                // 后台收尾仍可能返回错误；观察异常但不将超时当作完成，也不取消受控停止。
                _ = settled.ContinueWith(task => { var ignored = task.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                throw new TimeoutException("等待任务在途操作和资源记录超过 12 秒，状态尚未确认。");
            }
            await settled.ConfigureAwait(false);
        }

        /// <summary>先等动作和异常隔离收尾，再结算暂停前已开始的资源记录写入。</summary>
        private async Task SettlePausedRunsAsync(IEnumerable<TaskRun> runs)
        {
            await Task.WhenAll(runs.Select(run => run.PauseAsync())).ConfigureAwait(false);
            await _device.Resources.SettleJournalWritesAsync().ConfigureAwait(false);
        }

        /// <summary>故障记录有界，避免长期运行无限累积。</summary>
        private void AddFault(TaskMachineFault fault)
        {
            if (_faults.Count >= 100) _faults.RemoveAt(0);
            _faults.Add(fault);
        }

        /// <summary>显式终止整个会话，等待全部工位和内存动作退出；不复位已有输出或区域。</summary>
        public Task AbortAsync()
        {
            lock (_sync)
            {
                if (_abortTask != null) return _abortTask;
                _state = TaskMachineState.Stopped;
                _lifetime.Cancel();
                foreach (StationExecutionControl station in AllStations()) station.CurrentRun?.Cancel();
                _abortTask = FinishAbortAsync(Task.WhenAll(_workers.Concat(new[] { _preparationSettled?.Task ?? Task.CompletedTask })));
                return _abortTask;
            }
        }

        /// <summary>所有后台循环已经退出后标记工位终态，保留最近的节点诊断。</summary>
        private async Task FinishAbortAsync(Task workers)
        {
            await workers.ConfigureAwait(false);
            lock (_sync)
                foreach (StationExecutionControl station in AllStations())
                {
                    station.LastRun = station.CurrentRun?.GetSnapshot() ?? station.LastRun;
                    station.CurrentRun = null;
                    if (station.Configuration.Enabled) station.State = StationTaskState.Stopped;
                }
        }

        /// <summary>关闭前等待终止收尾并归还所有权；失败后允许排除原因并再次关闭。</summary>
        public Task CloseAsync()
        {
            lock (_sync)
            {
                if (_shutdownCloseTask != null && !_shutdownCloseTask.IsCompleted)
                    throw new InvalidOperationException("任务退出交接正在执行，请等待完成。");
                if (_closeTask == null || _closeTask.IsFaulted || _closeTask.IsCanceled) _closeTask = CloseCoreAsync();
                return _closeTask;
            }
        }

        /// <summary>仅用于 Host 退出：记录完整且设备确认停稳时保留 Blocked，而不逐条解除资源。</summary>
        public Task CloseForShutdownAsync()
        {
            lock (_sync)
            {
                if (_closeTask != null && !_closeTask.IsCompleted)
                    throw new InvalidOperationException("任务会话关闭正在执行，请等待完成。");
                if (_shutdownCloseTask == null || _shutdownCloseTask.IsFaulted || _shutdownCloseTask.IsCanceled)
                    _shutdownCloseTask = CloseForShutdownCoreAsync();
                return _shutdownCloseTask;
            }
        }

        /// <summary>等待终止和报警收尾，保留资源日志，并由设备验证可安全断连后封存运行时。</summary>
        private async Task CloseForShutdownCoreAsync()
        {
            await AbortAsync().ConfigureAwait(false);
            var drain = AlarmService as IMachineStopAlarmNotificationDrain;
            var handoff = _device as ITaskShutdownHandoff;
            if (drain == null || handoff == null)
                throw new InvalidOperationException("设备未提供可验证的资源退出交接能力，保留会话和隔离。");
            AlarmService.StopAlarmRaised -= AlarmService_StopAlarmRaised;
            try
            {
                await drain.WaitForStopNotificationDispatchAsync().ConfigureAwait(false);
                Task pendingSettlements;
                lock (_sync) pendingSettlements = _activePauseSettlements == 0 ? Task.CompletedTask : _pauseSettlementsIdle.Task;
                await pendingSettlements.ConfigureAwait(false);
                lock (_sync)
                    if (_pauseSettlementUnconfirmed || _activePauseSettlements != 0
                        || AllStations().Any(station => station.CurrentRun != null && station.CurrentRun.HasUnsettledPauseOperations)
                        || _device.Resources.HasPendingJournalWrites)
                        throw new InvalidOperationException("任务停止或设备调用仍未确认，保留会话和资源隔离。");
                await handoff.PreserveBlockedForShutdownAsync(this).ConfigureAwait(false);
                lock (_sync)
                {
                    // 原控制权与资源隔离继续保留在封存运行时，直到控制卡成功断开。
                    _lifetime.Dispose();
                    _state = TaskMachineState.Closed;
                }
            }
            catch
            {
                AlarmService.StopAlarmRaised += AlarmService_StopAlarmRaised;
                throw;
            }
        }

        /// <summary>在同一个关闭任务中完成取消源回收与所有权归还。</summary>
        private async Task CloseCoreAsync()
        {
            await AbortAsync().ConfigureAwait(false);
            var drain = AlarmService as IMachineStopAlarmNotificationDrain;
            if (drain == null)
                throw new InvalidOperationException("报警服务不支持等待停机通知交接，不能确认任务会话安全关闭。");
            AlarmService.StopAlarmRaised -= AlarmService_StopAlarmRaised;
            try
            {
                // 摘订后排空服务已捕获的旧回调，再等待回调启动的异步停轴处理。
                await drain.WaitForStopNotificationDispatchAsync().ConfigureAwait(false);
                Task pendingSettlements;
                lock (_sync) pendingSettlements = _activePauseSettlements == 0 ? Task.CompletedTask : _pauseSettlementsIdle.Task;
                await pendingSettlements.ConfigureAwait(false);
                lock (_sync)
                    if (HasUnsettledPauseWork())
                        throw new InvalidOperationException("任务停止或资源状态待确认，保留设备控制权和恢复记录。");
                var cleanup = _device as ITaskResourceJournalCleanup;
                if (cleanup != null) await cleanup.CompleteResourceJournalAsync().ConfigureAwait(false);
                lock (_sync)
                {
                    if (HasUnsettledPauseWork())
                        throw new InvalidOperationException("清理期间任务状态变为待确认，保留设备控制权。");
                    _device.ReleaseMachine(this);
                    _lifetime.Dispose();
                    _state = TaskMachineState.Closed;
                }
            }
            catch
            {
                // 会话已终止但仍持有设备，恢复报警监听以便维护；不能重新启动旧任务。
                AlarmService.StopAlarmRaised += AlarmService_StopAlarmRaised;
                throw;
            }
        }
    }
}
