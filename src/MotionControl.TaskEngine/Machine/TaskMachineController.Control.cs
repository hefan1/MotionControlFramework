using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.TaskEngine.Execution;
using MotionControl.TaskEngine.Resources;

namespace MotionControl.TaskEngine.Machine
{
    /// <summary>复位、启动、暂停、终止与故障锁存的具体顺序。</summary>
    public sealed partial class TaskMachineController
    {
        /// <summary>检查停机条件和轴报警后进入就绪；不清报警、不重置工位步骤、不自动运行。</summary>
        public void Reset()
        {
            lock (_sync)
            {
                RequireOpen();
                if (_state == TaskMachineState.Running) throw new InvalidOperationException("请先暂停整机再复位。");
                if (_activeStopAlarms.Count != 0) throw new InvalidOperationException("停机条件仍有效：" + string.Join("；", _activeStopAlarms.Values));
                foreach (string name in _device.Catalog.Axes.Keys)
                    if (!string.IsNullOrWhiteSpace(_device.ReadAxis(name).Alarm))
                        throw new InvalidOperationException("轴报警尚未解除：" + name + "，" + _device.ReadAxis(name).Alarm);
                if (_device.ReadResources().Any(resource => resource.State == TaskResourceState.Blocked))
                    throw new InvalidOperationException("存在待确认区域，请先核实并显式恢复资源。");
                if (AllStations().Any(station => station.Unrecoverable))
                    throw new InvalidOperationException("执行器已终止，无法证明当前步骤可以恢复；请终止并核实生产状态。");
                _faults.Clear();
                _state = TaskMachineState.Ready;
            }
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

        /// <summary>普通停止按钮使用本入口；同步冻结工位动作和周期提交，随后必须复位再启动。</summary>
        public void Pause()
        {
            lock (_sync)
            {
                RequireOpen();
                if (_state == TaskMachineState.Faulted) return;
                if (_state != TaskMachineState.Running) throw new InvalidOperationException("只有自动运行状态可以暂停。");
                PauseAll(TaskMachineState.Paused);
            }
        }

        /// <summary>更新外部停机条件；解除条件后仍须复位，不能自动继续。</summary>
        public void SetStopAlarm(string code, string message, bool active)
        {
            if (string.IsNullOrWhiteSpace(code) || (active && string.IsNullOrWhiteSpace(message)))
                throw new ArgumentException("停机报警必须提供代码和有效原因。");
            lock (_sync)
            {
                RequireOpen();
                if (!active) { _activeStopAlarms.Remove(code); return; }
                _activeStopAlarms[code] = message;
                AddFault(new TaskMachineFault(code, message, null));
                PauseAll(TaskMachineState.Faulted);
            }
        }

        /// <summary>动作已局部收尾，保存故障步骤并暂停全部其他工位；执行器在锁外等待恢复许可。</summary>
        internal Task ReportNodeFault(StationExecutionControl station, TaskExecutionException error)
        {
            lock (_sync)
            {
                _lifetime.Token.ThrowIfCancellationRequested();
                station.State = StationTaskState.WaitingReset;
                station.Detail = error.Message;
                if (station.RecoveryPermit == null) station.RecoveryPermit = NewSignal();
                AddFault(new TaskMachineFault(station.Configuration.StationId, error.Message, error));
                PauseAll(TaskMachineState.Faulted);
                return station.RecoveryPermit.Task;
            }
        }

        /// <summary>把全部周期许可和正在执行的运行冻结；调用方已持有整机锁。</summary>
        private void PauseAll(TaskMachineState state)
        {
            if (_state == TaskMachineState.Running) _runningPermit = NewSignal();
            _state = state;
            foreach (StationExecutionControl station in AllStations()) station.CurrentRun?.Pause();
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
                _device.ReleaseMachine(this);
                _lifetime.Dispose();
                _state = TaskMachineState.Closed;
            }
        }
    }
}
