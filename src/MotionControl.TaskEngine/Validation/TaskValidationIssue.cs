namespace MotionControl.TaskEngine.Validation
{
    /// <summary>任务校验结果，携带可以直接定位的任务、节点和字段。</summary>
    public sealed class TaskValidationIssue
    {
        /// <summary>错误会阻止保存，提示说明仍需后续运行检查的事项。</summary>
        public TaskIssueSeverity Severity { get; private set; }
        /// <summary>问题所属任务。</summary>
        public string TaskId { get; private set; }
        /// <summary>问题所属节点；定义级问题为空。</summary>
        public string NodeId { get; private set; }
        /// <summary>字段或 JSON 路径。</summary>
        public string Field { get; private set; }
        /// <summary>便于初级开发者理解的中文原因。</summary>
        public string Message { get; private set; }

        /// <summary>记录一条定位信息，不吞掉校验失败。</summary>
        public TaskValidationIssue(TaskIssueSeverity severity, string taskId, string nodeId, string field, string message)
        {
            Severity = severity;
            TaskId = taskId;
            NodeId = nodeId;
            Field = field;
            Message = message;
        }

        /// <summary>生成供控制台或未来页面直接显示的中文定位说明。</summary>
        public override string ToString()
        {
            return Severity + " 任务[" + TaskId + "] 节点[" + NodeId + "] 字段[" + Field + "]：" + Message;
        }
    }

    /// <summary>区分不合法定义和后续必须核实的运行条件。</summary>
    public enum TaskIssueSeverity
    {
        /// <summary>定义有错误，不能保存为有效任务。</summary>
        Error,
        /// <summary>定义可保存，但不能据此判定可运行。</summary>
        Warning
    }
}
