using System;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Validation;

namespace MotionControl.TaskEngine.Wpf.Editing
{
    /// <summary>问题表显示可读任务、动作和字段名称，内部仍保存稳定定位信息。</summary>
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
        public string Field { get; private set; }
        /// <summary>完整问题原因，点击时也在状态区显示。</summary>
        public string Message => Issue.Message;
        /// <summary>只转换展示，不改变校验结果。</summary>
        internal TaskEditingIssueRow(TaskValidationIssue issue, TaskDefinition task, string severity, Func<string, string> translate)
        {
            Issue = issue; Severity = severity; TaskName = task?.Name ?? issue.TaskId;
            StepName = Find(task?.Root, issue.NodeId)?.Name ?? issue.NodeId ?? string.Empty;
            Field = GetReadableField(issue.Field, translate);
        }
        /// <summary>把内部路径转换成现场易读的字段名；未知路径只显示通用字段名。</summary>
        internal static string GetReadableField(string field, Func<string, string> translate)
        {
            if (string.IsNullOrWhiteSpace(field)) return translate("字段");
            if (field.EndsWith(".condition.left", StringComparison.Ordinal)) return translate("左值");
            if (field.EndsWith(".condition.right", StringComparison.Ordinal)) return translate("右值");
            if (field.EndsWith(".condition.comparison", StringComparison.Ordinal)) return translate("比较");
            if (field.EndsWith(".timeoutMs", StringComparison.Ordinal)) return translate("步骤超时(ms)");
            if (field.EndsWith(".axisName", StringComparison.Ordinal)) return translate("逻辑轴");
            if (field.EndsWith(".variableName", StringComparison.Ordinal)) return translate("设备变量");
            if (field.EndsWith(".speed", StringComparison.Ordinal)) return translate("速度");
            if (field.EndsWith(".acceleration", StringComparison.Ordinal)) return translate("加速度");
            if (field.EndsWith(".deceleration", StringComparison.Ordinal)) return translate("减速度");
            if (field.EndsWith(".target", StringComparison.Ordinal)) return translate("目标");
            if (field.EndsWith(".resources", StringComparison.Ordinal)) return translate("资源范围");
            if (string.Equals(field, "taskId", StringComparison.Ordinal)) return translate("任务标识");
            if (string.Equals(field, "revision/name", StringComparison.Ordinal)) return translate("任务名称");
            return translate("字段");
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
