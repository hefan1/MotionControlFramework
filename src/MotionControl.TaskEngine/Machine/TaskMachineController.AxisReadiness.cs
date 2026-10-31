using System;
using System.Collections.Generic;
using System.Linq;
using MotionControl.TaskEngine.Definitions;

namespace MotionControl.TaskEngine.Machine
{
    /// <summary>检查正式任务启动前要使用的定位轴原点状态。</summary>
    public sealed partial class TaskMachineController
    {
        /// <summary>返回运行任务中的定位轴；任务自身负责回零的轴不要求预先回零。</summary>
        private IReadOnlyCollection<string> GetAxesRequiringPreHome()
        {
            var positioningAxes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var homeCommandAxes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var visitedTasks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(_configuration.InitializationTaskId))
                CollectTaskAxes(_configuration.InitializationTaskId, positioningAxes, homeCommandAxes, visitedTasks);
            foreach (StationExecutionControl station in _stations.Where(item => item.Configuration.Enabled))
                CollectTaskAxes(station.Configuration.TaskId, positioningAxes, homeCommandAxes, visitedTasks);

            return positioningAxes.Where(axis => !homeCommandAxes.Contains(axis))
                .OrderBy(axis => axis, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        /// <summary>沿已校验的任务调用图累计轴动作，避免未启用工位阻挡本次生产。</summary>
        private void CollectTaskAxes(string taskId, ISet<string> positioningAxes,
            ISet<string> homeCommandAxes, ISet<string> visitedTasks)
        {
            if (!visitedTasks.Add(taskId)) return;
            TaskDefinition task;
            if (_library.TryGetValue(taskId, out task))
                CollectNodeAxes(task.Root, positioningAxes, homeCommandAxes, visitedTasks);
        }

        /// <summary>扫描任务节点的单轴、轴组、分支和子任务引用。</summary>
        private void CollectNodeAxes(TaskNode node, ISet<string> positioningAxes,
            ISet<string> homeCommandAxes, ISet<string> visitedTasks)
        {
            var move = node as MoveAxisNode;
            if (move != null) positioningAxes.Add(move.AxisName);

            var moveGroup = node as MoveAxisGroupNode;
            if (moveGroup != null)
                foreach (AxisPositionTarget target in moveGroup.Targets) positioningAxes.Add(target.AxisName);

            var coordinated = node as CoordinatedMoveNode;
            if (coordinated != null)
                foreach (AxisTarget target in coordinated.Targets) positioningAxes.Add(target.AxisName);

            var command = node as AxisCommandNode;
            if (command != null && command.Command == TaskAxisCommand.Home) homeCommandAxes.Add(command.AxisName);

            var call = node as CallTaskNode;
            if (call != null) CollectTaskAxes(call.TaskId, positioningAxes, homeCommandAxes, visitedTasks);

            var sequence = node as SequenceNode;
            if (sequence != null)
                foreach (TaskNode child in sequence.Children) CollectNodeAxes(child, positioningAxes, homeCommandAxes, visitedTasks);

            var parallel = node as ParallelGroupNode;
            if (parallel != null)
                foreach (TaskNode child in parallel.Branches) CollectNodeAxes(child, positioningAxes, homeCommandAxes, visitedTasks);

            var branch = node as ConditionBranchNode;
            if (branch != null)
            {
                CollectNodeAxes(branch.WhenTrue, positioningAxes, homeCommandAxes, visitedTasks);
                if (branch.WhenFalse != null) CollectNodeAxes(branch.WhenFalse, positioningAxes, homeCommandAxes, visitedTasks);
            }

            var scope = node as ResourceScopeNode;
            if (scope != null) CollectNodeAxes(scope.Body, positioningAxes, homeCommandAxes, visitedTasks);
        }
    }
}
