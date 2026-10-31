using System;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Device;
using MotionControl.TaskEngine.Resources;

namespace MotionControl.TaskEngine.Execution
{
    /// <summary>内存单轴动作；多轴插补及真实回零模式在后续阶段接入。</summary>
    internal sealed partial class TaskNodeExecutor
    {
        /// <summary>在动作接受时读取目标，随后固定绝对终点，恢复不会再读变量或叠加相对距离。</summary>
        private Task MoveAsync(TaskExecutionScope scope, MoveAxisNode node)
        {
            return RunMotionAsync(scope, node.AxisName, node, false);
        }

        /// <summary>回零或确认停止；具体指令由设备实现发出。</summary>
        private async Task AxisCommandAsync(TaskExecutionScope scope, AxisCommandNode node)
        {
            if (node.Command == TaskAxisCommand.Home)
            {
                await RunMotionAsync(scope, node.AxisName, null, true).ConfigureAwait(false);
                return;
            }

            // 先经过运行门控（暂停与时限），再向设备确认轴已闲置；确认可能需要读取真实轴状态。
            await scope.StepAsync(() => { }).ConfigureAwait(false);
            await _device.ConfirmStoppedAsync(node.AxisName, scope.Token).ConfigureAwait(false);
        }

        /// <summary>按活动时间推进动作，finally 确保失败与取消均停止轴并释放本次轴占用。</summary>
        private async Task RunMotionAsync(TaskExecutionScope scope, string axisName, MoveAxisNode node, bool home)
        {
            ITaskAxisMove operation = null;
            TaskResourceRequest acquired = null;
            double started = 0;
            double duration = 0;
            try
            {
                acquired = await AcquireActionAsync(scope, "Axis:" + axisName).ConfigureAwait(false);
                await scope.StepAsync(() =>
                {
                    double target = 0;
                    bool acceptedBefore = !home && scope.AcceptedAxisTargets.TryGetValue(axisName, out target);
                    if (!home && !acceptedBefore) target = TaskRuntimeValues.Number(ReadRequired(node.Target));
                    operation = _device.BeginMove(axisName, target, !home && !acceptedBefore && node.Mode == TaskMoveMode.Relative, home);
                    if (!home) scope.AcceptedAxisTargets[axisName] = operation.Target;
                    started = _run.ActiveMilliseconds;
                    // 回零只验证流程生命周期；100ms 是内存模型时间，不是硬件回零参数。
                    duration = home ? 100 : Math.Abs(operation.Target - operation.Start) / node.Profile.Speed * 1000;
                    if (double.IsInfinity(duration) || double.IsNaN(duration)) throw new InvalidOperationException("运动时长超出有限范围。");
                }).ConfigureAwait(false);
                while (operation != null)
                {
                    bool completed = false;
                    await scope.StepAsync(() =>
                    {
                        double progress = duration == 0 ? 1 : (_run.ActiveMilliseconds - started) / duration;
                        _device.AdvanceMove(operation, progress);
                        if (!home) ObserveAxes(scope, scope.AcceptedAxisTargets);
                        else scope.Diagnostics = new TaskStepDiagnostics(progress >= 1
                            ? "内存回零结束，位置参考已建立；未模拟厂商原点与限位信号。"
                            : "正在执行内存回零；未模拟厂商回零模式。");
                        _run.Report(scope, TaskNodeState.Moving, "运动中：等待轴 " + axisName + " 到位；目标=" + operation.Target);
                        completed = _device.IsMoveComplete(operation, progress);
                    }).ConfigureAwait(false);

                    if (completed)
                    {
                        ITaskAxisMove finished = operation;
                        operation = null;
                        await _device.EndMoveAsync(finished, true, scope.Token).ConfigureAwait(false);
                        break;
                    }

                    await scope.NextPollAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                // 清理不等待恢复许可：暂停期间收到取消也必须立即停下并释放本次动作。
                if (operation != null)
                {
                    await _device.EndMoveAsync(operation, false, CancellationToken.None).ConfigureAwait(false);
                }

                if (acquired != null) _device.Resources.Release(acquired);
            }
        }
    }
}
