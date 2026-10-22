using System.Collections.Generic;
namespace MotionControl.TaskEngine.Definitions
{
    /// <summary>按列表顺序执行的步骤集合；空列表表示明确的空分支。</summary>
    public sealed class SequenceNode : TaskNode
    {
        /// <summary>顺序节点的固定类型。</summary>
        public override string Kind => "Sequence";
        /// <summary>按执行顺序保存的子节点。</summary>
        public List<TaskNode> Children { get; set; } = new List<TaskNode>();
    }
}
