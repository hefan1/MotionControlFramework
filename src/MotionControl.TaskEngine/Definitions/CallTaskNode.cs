namespace MotionControl.TaskEngine.Definitions
{
    /// <summary>调用另一份任务定义；第一阶段不支持子任务参数绑定。</summary>
    public sealed class CallTaskNode : TaskNode
    {
        /// <summary>子任务调用的固定类型。</summary>
        public override string Kind => "CallTask";
        /// <summary>已加载的子任务 ID；不得形成直接或间接递归。</summary>
        public string TaskId { get; set; }
    }
}
