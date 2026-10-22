namespace MotionControl.TaskEngine.Definitions
{
    /// <summary>有时限的延时定义。</summary>
    public sealed class DelayNode : TaskNode
    {
        /// <summary>延时节点的固定类型。</summary>
        public override string Kind => "Delay";
        /// <summary>延时毫秒数，必须大于零且不大于节点总时限。</summary>
        public int Milliseconds { get; set; }
    }
}
