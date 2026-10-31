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
                _faults.Clear();
                _state = TaskMachineState.Ready;
            }
            AlarmService.ResetStopAlarms();
        }

        /// <summary>检查任务复位条件；Runtime 复位前允许仍待清除的轴报警存在。</summary>
        private void ValidateResetConditions(bool requireAxisAlarmsCleared)
        {
            RequireOpen();
            if (_state == TaskMachineState.Running || _state == TaskMachineState.Pausing)
                throw new InvalidOperationException("请先暂停整机并确认所有轴已停稳，再复位。");
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
                        throw new InvalidOperationException("任务使用的轴尚未使能：" + name + "。请在轴手动页面使能后再复位任务。");
                    if (!axis.Homed) throw new InvalidOperationException("任务使用的轴尚未回零：" + name + "。请在轴手动页面回零后再复位任务。");
                }
            }
            if (_device.ReadResources().Any(resource => resource.State == TaskResourceState.Blocked))
                throw new InvalidOperationException("存在待确认区域，请先核实并显式恢复资源。");
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
            }

            try
            {
                await StopPausedRunsAsync(runs).ConfigureAwait(false);
                lock (_sync)
                    if (_state == TaskMachineState.Pausing) _state = TaskMachineState.Paused;
            }
            catch (Exception stopError)
            {
                lock (_sync)
                {
                    _state = TaskMachineState.Faulted;
                    AddFault(new TaskMachineFault("PAUSE_STOP_FAILED", "暂停时等待轴受控停止失败：" + stopError.Message, null));
                }

                throw new InvalidOperationException("暂停未能确认所有轴已停稳；整机已锁定为故障状态。", stopError);
            }
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
            TaskRun[] runs = SetStopAlarmCore(code, message, active, true, TaskMachineState.Pausing);
            await StopPausedRunsAsync(runs).ConfigureAwait(false);
            if (active)
                lock (_sync)
                    if (_state == TaskMachineState.Pausing) _state = TaskMachineState.Faulted;
        }

        /// <summary>只记录报警快照，报警条件状态仍由共享设备报警服务负责。</summary>
        private async Task HandleAlarmServiceStopAsync(string code, string message)
        {
            TaskRun[] runs = SetStopAlarmCore(code, message, true, false, TaskMachineState.Pausing);
            try
            {
                await StopPausedRunsAsync(runs).ConfigureAwait(false);
                lock (_sync)
                    if (_state == TaskMachineState.Pausing) _state = TaskMachineState.Faulted;
            }
            catch (Exception error)
            {
                lock (_sync)
                {
                    _state = TaskMachineState.Faulted;
                    AddFault(new TaskMachineFault("ALARM_STOP_FAILED", "停机报警后等待轴受控停止失败：" + error.Message, null));
                }
                throw;
            }
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
            bool trackLocalCondition = true, TaskMachineState stoppingState = TaskMachineState.Faulted)
        {
            if (string.IsNullOrWhiteSpace(code) || (active && string.IsNullOrWhiteSpace(message)))
                throw new ArgumentException("停机报警必须提供代码和有效原因。");
            lock (_sync)
            {
                RequireOpen();
                if (!active) { _activeStopAlarms.Remove(code); return new TaskRun[0]; }
                if (trackLocalCondition) _activeStopAlarms[code] = message;
                AddFault(new TaskMachineFault(code, message, null));
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
                permit = station.RecoveryPermit.Task;
            }

            try
            {
                await StopPausedRunsAsync(runs).ConfigureAwait(false);
                lock (_sync)
                    if (_state == TaskMachineState.Pausing) _state = TaskMachineState.Faulted;
            }
            catch
            {
                lock (_sync) _state = TaskMachineState.Faulted;
                throw;
            }
            return permit;
        }

        /// <summary>把全部周期许可和正在执行的运行冻结；调用方已持有整机锁。</summary>
        private TaskRun[] PauseAll(TaskMachineState state)
        {
            if (_state == TaskMachineState.Running) _runningPermit = NewSignal();
            _state = state;
            TaskRun[] runs = AllStations().Select(station => station.CurrentRun).Where(run => run != null).ToArray();
            foreach (TaskRun run in runs) run.Pause();
            return runs;
        }

        /// <summary>等待指定任务运行中的受控停止登记全部完成。</summary>
        private static Task StopPausedRunsAsync(IEnumerable<TaskRun> runs)
        {
            return Task.WhenAll(runs.Select(run => run.PauseAsync()));
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
                _abortTask = FinishAbortAsync(Task.WhenAll(_workers));
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

        /// <summary>关闭前先等待终止收尾，再归还设备所有权；可重复调用，不阻塞 UI 线程。</summary>
        public Task CloseAsync()
        {
            lock (_sync)
            {
                if (_closeTask == null) _closeTask = CloseCoreAsync();
                return _closeTask;
            }
        }

        /// <summary>在同一个关闭任务中完成取消源回收与所有权归还。</summary>
        private async Task CloseCoreAsync()
        {
            await AbortAsync().ConfigureAwait(false);
            lock (_sync)
            {
                AlarmService.StopAlarmRaised -= AlarmService_StopAlarmRaised;
                _device.ReleaseMachine(this);
                _lifetime.Dispose();
                _state = TaskMachineState.Closed;
            }
        }
    }
}
