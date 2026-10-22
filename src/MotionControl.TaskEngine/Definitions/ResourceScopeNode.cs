using System.Collections.Generic;
namespace MotionControl.TaskEngine.Definitions
{
    /// <summary>若干动作共同使用资源的定义；资源实际申请、退出确认在后续阶段实现。</summary>
    public sealed class ResourceScopeNode : TaskNode
    {
        /// <summary>资源范围的固定类型。</summary>
        public override string Kind => "ResourceScope";
        /// <summary>共享目录中的资源名称；轴资源统一使用 Axis:逻辑轴名。</summary>
        public List<string> Resources { get; set; } = new List<string>();
        /// <summary>持有资源期间执行的顺序节点。</summary>
        public TaskNode Body { get; set; }
        /// <summary>可选退出确认条件；不能把任务结束直接等同于机械区域已清空。</summary>
        public TaskCondition ExitCondition { get; set; }
    }
}
