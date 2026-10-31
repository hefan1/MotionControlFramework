using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Resources;
using MotionControl.TaskEngine.Simulation;

namespace MotionControl.TaskEngine.Execution
{
    /// <summary>轴组定位使用单个节点生命周期，各轴独立速度，统一等待和收尾。</summary>
    internal sealed partial class TaskNodeExecutor
    {
        /// <summary>申请完整轴组后解析并接受动作；失败或取消时先停止本组，再交还临时占用。</summary>
        private async Task MoveAxisGroupAsync(TaskExecutionScope scope, MoveAxisGroupNode node)
        {
            TaskResourceRequest acquired = null;
            List<SimulatedGroupAxisMove> moves = null;
            var names = new List<string>();
            foreach (AxisPositionTarget target in node.Targets) names.Add("Axis:" + target.AxisName);
            double started = 0;
            try
            {
                if (scope.ResourceLease == null)
                    acquired = await AcquireAsync(scope, names).ConfigureAwait(false);
                else
                    await scope.StepAsync(() =>
                    {
                        foreach (string name in names) _device.Resources.RequireHeld(scope.ResourceLease, name);
                    }).ConfigureAwait(false);

                await scope.StepAsync(() =>
                {
                    moves = _device.BeginGroupMove(ReadGroupTargets(scope, node));
                    foreach (SimulatedGroupAxisMove move in moves)
                        scope.AcceptedAxisTargets[move.Operation.AxisName] = move.Operation.Target;
                    started = _run.ActiveMilliseconds;
                }).ConfigureAwait(false);
                bool completed = false;
                while (!completed)
                {
                    await scope.StepAsync(() =>
                    {
                        string detail;
                        completed = _device.AdvanceGroupMove(moves, _run.ActiveMilliseconds - started, out detail);
                        ObserveAxes(scope, scope.AcceptedAxisTargets);
                        scope.WaitDetail = completed ? null : detail;
                        _run.Report(scope, completed ? TaskNodeState.Running : TaskNodeState.Moving, detail);
                    }).ConfigureAwait(false);
                    if (!completed) await scope.NextPollAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                // 清理不等待恢复许可，暂停期间也能取消并停下本组的剩余动作。
                if (moves != null) _device.EndGroupMove(moves);
                if (acquired != null) _device.Resources.Release(acquired);
            }
        }

        /// <summary>全部目标使用同一份变量快照；任何目标无效时不发出组内任一动作。</summary>
        private List<SimulatedAxisGroupTarget> ReadGroupTargets(TaskExecutionScope scope, MoveAxisGroupNode node)
        {
            var variables = _device.ReadVariables();
            var targets = new List<SimulatedAxisGroupTarget>();
            foreach (AxisPositionTarget target in node.Targets)
            {
                double accepted;
                if (scope.AcceptedAxisTargets.TryGetValue(target.AxisName, out accepted))
                {
                    targets.Add(new SimulatedAxisGroupTarget(target.AxisName, accepted, false, target.Profile.Speed));
                    continue;
                }
                object value;
                string reason;
                if (!TaskRuntimeValues.TryRead(target.Target, _device.Catalog, variables, out value, out reason))
                    throw new InvalidOperationException("轴组目标无效：轴=" + target.AxisName + "。" + reason);
                targets.Add(new SimulatedAxisGroupTarget(target.AxisName, TaskRuntimeValues.Number(value),
                    target.Mode == TaskMoveMode.Relative, target.Profile.Speed));
            }
            return targets;
        }
    }
}
