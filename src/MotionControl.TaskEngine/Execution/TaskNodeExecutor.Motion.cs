using System;
using System.Threading.Tasks;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Simulation;

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

        /// <summary>模拟回零或确认停止；这里不会发出任何厂商指令。</summary>
        private async Task AxisCommandAsync(TaskExecutionScope scope, AxisCommandNode node)
        {
            if (node.Command == TaskAxisCommand.Home)
                await RunMotionAsync(scope, node.AxisName, null, true).ConfigureAwait(false);
            else await scope.StepAsync(() => _device.ConfirmStopped(node.AxisName)).ConfigureAwait(false);
        }

        /// <summary>按活动时间推进内存位置，finally 确保失败与取消均释放本次轴占用。</summary>
        private async Task RunMotionAsync(TaskExecutionScope scope, string axisName, MoveAxisNode node, bool home)
        {
            SimulatedMove operation = null;
            double started = 0;
            double duration = 0;
            try
            {
                await scope.StepAsync(() =>
                {
                    double target = home ? 0 : TaskRuntimeValues.Number(ReadRequired(node.Target));
                    operation = _device.BeginMove(axisName, target, !home && node.Mode == TaskMoveMode.Relative, home);
                    started = _run.ActiveMilliseconds;
                    // 回零只验证流程生命周期；100ms 是内存模型时间，不是硬件回零参数。
                    duration = home ? 100 : Math.Abs(operation.Target - operation.Start) / node.Profile.Speed * 1000;
                    if (double.IsInfinity(duration) || double.IsNaN(duration)) throw new InvalidOperationException("仿真运动时长超出有限范围。");
                }).ConfigureAwait(false);
                while (operation != null)
                {
                    await scope.StepAsync(() =>
                    {
                        double progress = duration == 0 ? 1 : (_run.ActiveMilliseconds - started) / duration;
                        _device.AdvanceMove(operation, progress);
                        _run.Report(scope, TaskNodeState.Waiting, "等待仿真轴 " + axisName + " 到位；目标=" + operation.Target);
                        if (progress >= 1)
                        {
                            _device.EndMove(operation, true);
                            operation = null;
                        }
                    }).ConfigureAwait(false);
                    if (operation != null) await scope.NextPollAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                // 清理不等待恢复许可：暂停期间收到取消也必须立即释放自己的内存动作。
                if (operation != null) _device.EndMove(operation, false);
            }
        }
    }
}
