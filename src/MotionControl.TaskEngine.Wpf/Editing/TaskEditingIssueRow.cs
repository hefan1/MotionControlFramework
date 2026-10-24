using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Validation;

namespace MotionControl.TaskEngine.Wpf.Editing
{
    /// <summary>问题表显示任务与动作名称，内部仍保存稳定定位信息。</summary>
    internal sealed class TaskEditingIssueRow
    {
        /// <summary>校验器返回的原始问题。</summary>
        public TaskValidationIssue Issue { get; private set; }
        /// <summary>界面等级文字。</summary>
        public string Severity { get; private set; }
        /// <summary>可读任务名称。</summary>
        public string TaskName { get; private set; }
        /// <summary>可读动作名称，不让人员辨认 GUID。</summary>
        public string StepName { get; private set; }
        /// <summary>需要检查的具体字段。</summary>
        public string Field => Issue.Field;
        /// <summary>完整问题原因，点击时也在状态区显示。</summary>
        public string Message => Issue.Message;
        /// <summary>只转换展示，不改变校验结果。</summary>
        internal TaskEditingIssueRow(TaskValidationIssue issue, TaskDefinition task, string severity)
        {
            Issue = issue; Severity = severity; TaskName = task?.Name ?? issue.TaskId;
            StepName = Find(task?.Root, issue.NodeId)?.Name ?? issue.NodeId ?? string.Empty;
        }
        /// <summary>按实际节点关系定位原动作。</summary>
        private static TaskNode Find(TaskNode node, string id)
        {
            if (node == null) return null;
            if (node.Id == id) return node;
            foreach (TaskNode child in TaskEditorNodes.Children(node)) { TaskNode found = Find(child, id); if (found != null) return found; }
            return null;
        }
    }
}
