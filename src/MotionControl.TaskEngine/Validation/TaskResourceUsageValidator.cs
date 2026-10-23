using System;
using System.Collections.Generic;
using MotionControl.TaskEngine.Definitions;

namespace MotionControl.TaskEngine.Validation
{
    /// <summary>在合法且无递归的任务树上检查资源范围，包含子任务内隐藏的资源使用。</summary>
    internal static class TaskResourceUsageValidator
    {
        /// <summary>逐份寻找资源范围；每个范围独立检查其正文和所有被调用任务。</summary>
        internal static void Check(Dictionary<string, TaskDefinition> tasks, List<TaskValidationIssue> issues)
        {
            foreach (TaskDefinition task in tasks.Values) FindScopes(task.TaskId, task.Root, tasks, issues);
        }

        /// <summary>只沿定义树找范围；被调用任务会在其自身定义中检查，避免重复展开。</summary>
        private static void FindScopes(string taskId, TaskNode node, Dictionary<string, TaskDefinition> tasks,
            List<TaskValidationIssue> issues)
        {
            if (node is ResourceScopeNode scope)
            {
                var resources = new HashSet<string>(scope.Resources, StringComparer.OrdinalIgnoreCase);
                var called = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                CheckBody(taskId, scope.Body, taskId, scope, resources, called, tasks, issues);
            }
            foreach (TaskNode child in Children(node)) FindScopes(taskId, child, tasks, issues);
        }

        /// <summary>要求动作继承已持有的整组资源，拒绝再申请范围或在同一凭证下并行。</summary>
        private static void CheckBody(string taskId, TaskNode node, string ownerTask, ResourceScopeNode owner,
            HashSet<string> resources, HashSet<string> called, Dictionary<string, TaskDefinition> tasks,
            List<TaskValidationIssue> issues)
        {
            string error = null;
            string required = null;
            if (node is ResourceScopeNode) error = "范围内不能嵌套 ResourceScope，包含子任务中的范围。";
            else if (node is ParallelGroupNode) error = "范围内不能使用 ParallelGroup；请将各自的资源范围放入外层并行分支。";
            else if (node is MoveAxisNode move) required = "Axis:" + move.AxisName;
            else if (node is AxisCommandNode command) required = "Axis:" + command.AxisName;
            else if (node is SetOutputNode output) required = "Output:" + output.OutputName;
            else if (node is MoveAxisGroupNode group)
            {
                foreach (AxisPositionTarget axis in group.Targets)
                    if (!resources.Contains("Axis:" + axis.AxisName)) error = "范围未声明动作资源：Axis:" + axis.AxisName;
            }
            else if (node is CoordinatedMoveNode coordinated)
            {
                foreach (AxisTarget axis in coordinated.Targets)
                    if (!resources.Contains("Axis:" + axis.AxisName)) error = "范围未声明动作资源：Axis:" + axis.AxisName;
            }
            if (required != null && !resources.Contains(required)) error = "范围未声明动作资源：" + required;
            if (error != null)
            {
                issues.Add(new TaskValidationIssue(TaskIssueSeverity.Error, ownerTask, owner.Id, "resources/body",
                    error + " 引用位置：任务 " + taskId + " / 节点 " + node.Id));
                return;
            }
            if (node is CallTaskNode call && called.Add(call.TaskId))
                CheckBody(call.TaskId, tasks[call.TaskId].Root, ownerTask, owner, resources, called, tasks, issues);
            foreach (TaskNode child in Children(node))
                CheckBody(taskId, child, ownerTask, owner, resources, called, tasks, issues);
        }

        /// <summary>按明确节点类型返回树中子节点，不展开任务调用。</summary>
        private static IEnumerable<TaskNode> Children(TaskNode node)
        {
            if (node is SequenceNode sequence)
                foreach (TaskNode child in sequence.Children) yield return child;
            else if (node is ParallelGroupNode parallel)
                foreach (TaskNode child in parallel.Branches) yield return child;
            else if (node is ConditionBranchNode branch)
            {
                yield return branch.WhenTrue;
                if (branch.WhenFalse != null) yield return branch.WhenFalse;
            }
            else if (node is ResourceScopeNode scope) yield return scope.Body;
        }
    }
}
