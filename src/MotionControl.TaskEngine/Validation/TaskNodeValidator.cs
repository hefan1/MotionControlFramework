using System;
using System.Collections.Generic;
using System.Linq;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Definitions;

namespace MotionControl.TaskEngine.Validation
{
    /// <summary>逐个检查一份任务的节点；结构和动作校验分文件，调用关系保持直接。</summary>
    internal sealed partial class TaskNodeValidator
    {
        /// <summary>本次校验的共享只读目录。</summary>
        private readonly TaskResourceCatalog _catalog;
        /// <summary>写入全部定位结果的列表。</summary>
        private readonly List<TaskValidationIssue> _issues;
        /// <summary>当前任务 ID。</summary>
        private readonly string _taskId;
        /// <summary>检测同一对象被重复挂载或形成内存循环。</summary>
        private readonly HashSet<TaskNode> _objects = new HashSet<TaskNode>();
        /// <summary>检测任务内重复的稳定节点 ID。</summary>
        private readonly HashSet<string> _ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>限制条件树总量，避免损坏定义无限递归。</summary>
        private int _conditionCount;
        /// <summary>收集子任务引用，交给任务库校验直接或间接递归。</summary>
        internal List<CallTaskNode> Calls { get; } = new List<CallTaskNode>();

        /// <summary>为一份任务建立独立检查上下文，公共校验器可重复或并发使用。</summary>
        internal TaskNodeValidator(string taskId, TaskResourceCatalog catalog, List<TaskValidationIssue> issues)
        {
            _taskId = taskId;
            _catalog = catalog;
            _issues = issues;
        }

        /// <summary>检查节点并递归检查子节点；过深或重复引用立即停止该分支。</summary>
        internal void Check(TaskNode node, string path, int depth)
        {
            if (node == null) { Error(null, path, "节点不能为空。"); return; }
            if (depth > 32 || _objects.Count >= 5000)
            {
                Error(node, path, "任务树超过 32 层或 5000 个节点，拒绝继续展开。");
                return;
            }
            if (!_objects.Add(node))
            {
                Error(node, path, "同一节点对象被重复使用或形成循环；请复制为具有独立 ID 的节点。");
                return;
            }
            if (!ValidName(node.Id) || !_ids.Add(node.Id))
                Error(node, path + ".id", "节点 ID 不能为空、带首尾空格或重复。");
            if (!ValidName(node.Name)) Error(node, path + ".name", "请填写清楚的步骤名称。");
            if (node.TimeoutMs <= 0) Error(node, path + ".timeoutMs", "时限必须大于零。");

            if (node is SequenceNode sequence)
            {
                CheckChildren(node, sequence.Children, path + ".children", depth, false);
            }
            else if (node is ParallelGroupNode parallel)
            {
                if (parallel.Branches == null || parallel.Branches.Count < 2)
                    Error(node, path + ".branches", "并行组至少需要两个顺序分支。");
                CheckChildren(node, parallel.Branches, path + ".branches", depth, true);
            }
            else if (node is ConditionBranchNode branch)
            {
                CheckCondition(branch.Condition, node, path + ".condition", new HashSet<TaskCondition>(), 0);
                CheckSequence(branch.WhenTrue, node, path + ".whenTrue", depth);
                if (branch.WhenFalse != null) CheckSequence(branch.WhenFalse, node, path + ".whenFalse", depth);
            }
            else if (node is ResourceScopeNode scope)
            {
                if (scope.Resources == null || scope.Resources.Count == 0)
                    Error(node, path + ".resources", "资源范围至少需要一个资源。");
                else
                {
                    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string name in scope.Resources)
                        if (!ValidName(name) || !names.Add(name)
                            || !_catalog.Resources.Contains(name, StringComparer.OrdinalIgnoreCase))
                            Error(node, path + ".resources", "资源为空、重复或未登记：" + name);
                }
                CheckSequence(scope.Body, node, path + ".body", depth);
                if (scope.ExitCondition != null)
                    CheckCondition(scope.ExitCondition, node, path + ".exitCondition", new HashSet<TaskCondition>(), 0);
                Warning(node, path, "本阶段只检查资源名称；实际占用、退出确认和死锁检查在资源仲裁阶段实现。");
            }
            else if (node is CallTaskNode call)
            {
                if (!ValidName(call.TaskId)) Error(node, path + ".taskId", "子任务 ID 不能为空或带首尾空格。");
                else Calls.Add(call);
            }
            else
            {
                CheckAction(node, path);
            }
        }

        /// <summary>检查一个子节点列表，必要时要求每项都是顺序分支。</summary>
        private void CheckChildren(TaskNode owner, List<TaskNode> children, string path, int depth, bool sequenceOnly)
        {
            if (children == null) { Error(owner, path, "子节点列表不能为 null。"); return; }
            if (children.Count > 5000) { Error(owner, path, "子节点数量超过 5000。"); return; }
            for (int index = 0; index < children.Count; index++)
            {
                string childPath = path + "[" + index + "]";
                if (sequenceOnly) CheckSequence(children[index], owner, childPath, depth);
                else Check(children[index], childPath, depth + 1);
            }
        }

        /// <summary>分支和资源正文必须是顺序节点，保证树的含义明确。</summary>
        private void CheckSequence(TaskNode child, TaskNode owner, string path, int depth)
        {
            if (!(child is SequenceNode)) Error(owner, path, "这里必须使用 Sequence 顺序节点。");
            Check(child, path, depth + 1);
        }

        /// <summary>登记会阻止保存的定义错误。</summary>
        private void Error(TaskNode node, string field, string message)
        {
            _issues.Add(new TaskValidationIssue(TaskIssueSeverity.Error, _taskId, node?.Id, field, message));
        }

        /// <summary>登记仍须在后续运行阶段检查的事项。</summary>
        private void Warning(TaskNode node, string field, string message)
        {
            _issues.Add(new TaskValidationIssue(TaskIssueSeverity.Warning, _taskId, node?.Id, field, message));
        }

        /// <summary>名称不允许隐式修剪，避免保存与引用出现不同字符串。</summary>
        private static bool ValidName(string name)
        {
            return !string.IsNullOrWhiteSpace(name) && name == name.Trim();
        }
    }
}
