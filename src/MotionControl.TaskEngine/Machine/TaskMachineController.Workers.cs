using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.TaskEngine.Execution;

namespace MotionControl.TaskEngine.Machine
{
    /// <summary>初始化和各工位的后台循环，等待许可时不占用线程或设备资源。</summary>
    public sealed partial class TaskMachineController
    {
        /// <summary>初始化只执行一次；故障/暂停保持其节点，成功后才放行自动工位。</summary>
        private async Task RunInitializationAsync(CancellationToken token)
        {
            try
            {
                await RunCycleAsync(_initialization, token).ConfigureAwait(false);
                await StepAsync(() =>
                {
                    _initialized = true;
                    _initialization.State = StationTaskState.Completed;
                    _initialization.Detail = "设备初始化完成。";
                    _initializationPermit.TrySetResult(true);
                }, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { /* 统一终止会等待运行收尾。 */ }
            catch (Exception error) { await ReportWorkerFailureAsync(_initialization, error).ConfigureAwait(false); }
        }

        /// <summary>工位独立循环；请求等待在周期外进行，不套用动作时限。</summary>
        private async Task RunStationAsync(StationExecutionControl station, CancellationToken token)
        {
            try
            {
                await StationExecutionControl.WaitSignalAsync(_initializationPermit.Task, token).ConfigureAwait(false);
                while (true)
                {
                    if (station.Configuration.Mode == StationTaskMode.OnRequest)
                        await WaitRequestAsync(station, token).ConfigureAwait(false);
                    await RunCycleAsync(station, token).ConfigureAwait(false);
                    if (station.Configuration.Mode == StationTaskMode.Once)
                    {
                        await StepAsync(() => { station.State = StationTaskState.Completed; station.Detail = "单次工位已完成。"; }, token).ConfigureAwait(false);
                        return;
                    }
                    if (station.Configuration.Mode == StationTaskMode.OnRequest)
                        await CompleteRequestAsync(station, token).ConfigureAwait(false);
                    await Task.Delay(station.Configuration.CycleIntervalMs, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { /* 统一终止会等待运行收尾。 */ }
            catch (Exception error) { await ReportWorkerFailureAsync(station, error).ConfigureAwait(false); }
        }

        /// <summary>在整机许可内发布一个先暂停的周期，登记完成后才允许执行器推进。</summary>
        private async Task RunCycleAsync(StationExecutionControl station, CancellationToken token)
        {
            TaskRun run = null;
            await StepAsync(() =>
            {
                var definition = _library[station.Configuration.TaskId];
                run = new TaskExecutionEngine().StartManaged(definition, _device,
                    _library.Values.Where(task => task.TaskId != definition.TaskId), station, token, AlarmService);
                station.CurrentRun = run;
                station.State = StationTaskState.Executing;
                station.Diagnostics = null;
                station.Detail = "正在执行周期 " + (station.CompletedCycles + 1) + "。";
                run.Resume();
            }, token).ConfigureAwait(false);
            TaskRunSnapshot result = await run.Completion.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (result.State != TaskRunState.Completed)
                throw new InvalidOperationException("工位执行器异常终止，不能自动重放整段任务：" + station.Configuration.StationId, result.Error);
            await StepAsync(() =>
            {
                station.LastRun = result;
                station.CurrentRun = null;
                station.CompletedCycles++;
                station.Detail = "本周期已完成。";
            }, token).ConfigureAwait(false);
        }

        /// <summary>循环边界和信号提交也受整机暂停许可约束，暂停返回后不会再提交本会话写值。</summary>
        private async Task StepAsync(Action action, CancellationToken token)
        {
            while (true)
            {
                Task permit;
                lock (_sync)
                {
                    token.ThrowIfCancellationRequested();
                    if (_state == TaskMachineState.Running) { action(); return; }
                    permit = _runningPermit.Task;
                }
                await StationExecutionControl.WaitSignalAsync(permit, token).ConfigureAwait(false);
            }
        }

        /// <summary>未预期的执行器退出不能用复位重放，锁存诊断并暂停其他工位。</summary>
        internal async Task ReportWorkerFailureAsync(StationExecutionControl station, Exception error)
        {
            TaskRun[] runs;
            lock (_sync)
            {
                if (_lifetime.IsCancellationRequested || station.Unrecoverable) return;
                station.Unrecoverable = true;
                station.State = StationTaskState.WaitingReset;
                station.Detail = error.ToString();
                AddFault(new TaskMachineFault(station.Configuration.StationId, error.Message, error.InnerException as TaskExecutionException));
                runs = PauseAll(TaskMachineState.Pausing);
                BeginPauseSettlement();
            }

            try
            {
                await StopPausedRunsAsync(runs).ConfigureAwait(false);
                lock (_sync)
                    if (_state == TaskMachineState.Pausing || _state == TaskMachineState.Paused) _state = TaskMachineState.Faulted;
            }
            catch (Exception stopError)
            {
                lock (_sync)
                {
                    _pauseSettlementUnconfirmed = true;
                    if (_state != TaskMachineState.Stopped && _state != TaskMachineState.Closed) _state = TaskMachineState.Faulted;
                    AddFault(new TaskMachineFault("WORKER_STOP_FAILED", "工位故障后等待轴受控停止失败：" + stopError.Message, null));
                }
            }
            finally { EndPauseSettlement(); }
        }

        /// <summary>故障节点收到复位启动许可后更新工位阶段，节点本身仍保留原调用身份。</summary>
        internal void MarkNodeRecovered(StationExecutionControl station)
        {
            lock (_sync)
            {
                station.State = StationTaskState.Executing;
                station.Detail = "复位启动后重新进入中断步骤。";
            }
        }
    }
}
