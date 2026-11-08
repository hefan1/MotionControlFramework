using System;
using System.Collections.Generic;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Device;

namespace MotionControl.TaskEngine.Validation
{
    /// <summary>离线检查任务库所需能力；编辑、工程预检和执行前准入使用同一组明确规则。</summary>
    public sealed class TaskDeviceCapabilityValidator
    {
        /// <summary>检查全部任务和分支；未提供后端能力时仅返回空结果，调用方不得声称已核对可执行性。</summary>
        public IReadOnlyList<TaskValidationIssue> Validate(IEnumerable<TaskDefinition> tasks, TaskDeviceCapabilities capabilities)
        {
            var issues = new List<TaskValidationIssue>();
            if (capabilities == null) return issues.AsReadOnly();
            if (tasks == null) throw new ArgumentNullException(nameof(tasks));
            foreach (TaskDefinition task in tasks)
            {
                if (task == null) continue;
                Visit(task.TaskId, task.Root, capabilities, new HashSet<TaskNode>(), issues);
            }
            return issues.AsReadOnly();
        }

        /// <summary>执行前检查一个任务的所有分支；定义及调用引用必须先通过完整定义校验。</summary>
        public IReadOnlyList<TaskValidationIssue> ValidateNode(string taskId, TaskNode node, TaskDeviceCapabilities capabilities)
        {
            if (capabilities == null) throw new ArgumentNullException(nameof(capabilities));
            var issues = new List<TaskValidationIssue>();
            Visit(taskId, node, capabilities, new HashSet<TaskNode>(), issues);
            return issues.AsReadOnly();
        }

        /// <summary>明确遍历已有容器，不能只检查本次选择的分支；循环对象由定义校验报错，此处避免重复访问。</summary>
        private static void Visit(string taskId, TaskNode node, TaskDeviceCapabilities capabilities,
            HashSet<TaskNode> visited, List<TaskValidationIssue> issues)
        {
            if (node == null || !visited.Add(node)) return;
            if (node is CoordinatedMoveNode)
                AddIssue(issues, taskId, node, "CoordinatedMove", capabilities, "插补动作尚未接入执行器");
            else if (node is MoveAxisGroupNode && !capabilities.SupportsAxisGroupMove)
                AddIssue(issues, taskId, node, "MoveAxisGroup", capabilities, "多轴分别定位尚未接入，不能改成顺序单轴动作代替");

            if (node is SequenceNode sequence)
            {
                if (sequence.Children != null)
                    foreach (TaskNode child in sequence.Children) Visit(taskId, child, capabilities, visited, issues);
            }
            else if (node is ParallelGroupNode parallel)
            {
                if (parallel.Branches != null)
                    foreach (TaskNode branch in parallel.Branches) Visit(taskId, branch, capabilities, visited, issues);
            }
            else if (node is ConditionBranchNode condition)
            {
                Visit(taskId, condition.WhenTrue, capabilities, visited, issues);
                Visit(taskId, condition.WhenFalse, capabilities, visited, issues);
            }
            else if (node is ResourceScopeNode scope)
            {
                if (!capabilities.SupportsInterlockConditions
                    && (scope.EntryInterlock != null || scope.ExitInterlock != null || scope.ExitCondition != null))
                    AddIssue(issues, taskId, node, "ResourceInterlock", capabilities, "资源范围的进入或退出互锁尚未接入");
                Visit(taskId, scope.Body, capabilities, visited, issues);
            }
        }

        /// <summary>保留稳定能力字段、任务和节点身份，页面可以定位错误而不解析中文句子。</summary>
        private static void AddIssue(List<TaskValidationIssue> issues, string taskId, TaskNode node,
            string capability, TaskDeviceCapabilities capabilities, string reason)
        {
            issues.Add(new TaskValidationIssue(TaskIssueSeverity.Error, taskId, node.Id, "Capability." + capability,
                "当前后端 " + capabilities.BackendName + " 暂不支持步骤“" + node.Name + "”：" + reason
                + "。可保留编辑定义，但不能发布为当前后端可执行任务；任何前序动作均不得开始。"));
        }
    }
}
