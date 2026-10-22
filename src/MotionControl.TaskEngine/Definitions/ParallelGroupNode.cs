using System.Collections.Generic;
namespace MotionControl.TaskEngine.Definitions
{
    /// <summary>多个独立顺序分支；只定义全部成功后汇合，不代表插补运动。</summary>
    public sealed class ParallelGroupNode : TaskNode
    {
        /// <summary>并行组的固定类型。</summary>
        public override string Kind => "ParallelGroup";
        /// <summary>至少两个顺序节点，各分支有独立进度。</summary>
        public List<TaskNode> Branches { get; set; } = new List<TaskNode>();
    }
}
