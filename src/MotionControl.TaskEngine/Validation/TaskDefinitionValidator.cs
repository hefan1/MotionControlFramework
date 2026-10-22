using System;
using System.Collections.Generic;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Definitions;

namespace MotionControl.TaskEngine.Validation
{
    /// <summary>校验任务库的定义和引用，不创建执行线程或调用任何控制卡。</summary>
    public sealed class TaskDefinitionValidator
    {
        /// <summary>检查主任务及其依赖；dependencies 仅传其他任务，不重复放入主任务。</summary>
        public IReadOnlyList<TaskValidationIssue> Validate(TaskDefinition definition, TaskResourceCatalog catalog,
            IEnumerable<TaskDefinition> dependencies = null)
        {
            if (catalog == null) throw new ArgumentNullException(nameof(catalog));
            var definitions = new List<TaskDefinition> { definition };
            if (dependencies != null)
            {
                foreach (TaskDefinition dependency in dependencies)
                {
                    definitions.Add(dependency);
                    if (definitions.Count > 128)
                        throw new ArgumentException("一次校验最多允许 128 份任务定义。", nameof(dependencies));
                }
            }
            var issues = new List<TaskValidationIssue>();
            var tasks = new Dictionary<string, TaskDefinition>(StringComparer.OrdinalIgnoreCase);
            var calls = new Dictionary<string, List<CallTaskNode>>(StringComparer.OrdinalIgnoreCase);
            foreach (TaskDefinition task in definitions)
            {
                if (task == null)
                {
                    issues.Add(new TaskValidationIssue(TaskIssueSeverity.Error, null, null, "task", "任务定义不能为空。"));
                    continue;
                }
                if (string.IsNullOrWhiteSpace(task.TaskId) || task.TaskId != task.TaskId.Trim() || tasks.ContainsKey(task.TaskId))
                {
                    issues.Add(new TaskValidationIssue(TaskIssueSeverity.Error, task.TaskId, null, "taskId", "任务 ID 为空、带首尾空格或重复。"));
                    continue;
                }
                tasks.Add(task.TaskId, task);
                if (task.SchemaVersion != 1)
                    issues.Add(new TaskValidationIssue(TaskIssueSeverity.Error, task.TaskId, null, "schemaVersion", "只支持版本 1；禁止静默降级。"));
                if (task.Revision < 1 || string.IsNullOrWhiteSpace(task.Name))
                    issues.Add(new TaskValidationIssue(TaskIssueSeverity.Error, task.TaskId, null, "revision/name", "修订号须大于零，任务名称不能为空。"));
                if (!(task.Root is SequenceNode))
                    issues.Add(new TaskValidationIssue(TaskIssueSeverity.Error, task.TaskId, null, "root", "任务根节点必须是 Sequence。"));
                var checker = new TaskNodeValidator(task.TaskId, catalog, issues);
                checker.Check(task.Root, "root", 0);
                calls.Add(task.TaskId, checker.Calls);
            }
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var active = new List<string>();
            foreach (string taskId in tasks.Keys) CheckCalls(taskId, calls, visited, active, issues);
            return issues.AsReadOnly();
        }

        /// <summary>沿子任务调用关系检查缺失引用和递归；每个任务只完整展开一次。</summary>
        private static void CheckCalls(string taskId, Dictionary<string, List<CallTaskNode>> calls,
            HashSet<string> visited, List<string> active, List<TaskValidationIssue> issues)
        {
            if (!visited.Add(taskId)) return;
            active.Add(taskId);
            foreach (CallTaskNode call in calls[taskId])
            {
                if (!calls.ContainsKey(call.TaskId))
                {
                    issues.Add(new TaskValidationIssue(TaskIssueSeverity.Error, taskId, call.Id, "taskId", "子任务未加载：" + call.TaskId));
                    continue;
                }
                bool recursion = active.Exists(item => string.Equals(item, call.TaskId, StringComparison.OrdinalIgnoreCase));
                if (recursion)
                {
                    issues.Add(new TaskValidationIssue(TaskIssueSeverity.Error, taskId, call.Id, "taskId",
                        "子任务形成递归：" + string.Join(" -> ", active) + " -> " + call.TaskId));
                    continue;
                }
                CheckCalls(call.TaskId, calls, visited, active, issues);
            }
            active.RemoveAt(active.Count - 1);
        }
    }
}
