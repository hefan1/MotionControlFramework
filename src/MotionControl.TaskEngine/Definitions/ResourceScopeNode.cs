using System.Collections.Generic;
namespace MotionControl.TaskEngine.Definitions
{
    /// <summary>申请整组资源、检查进入条件、执行正文、确认退出后释放；异常保留待确认状态。</summary>
    public sealed class ResourceScopeNode : TaskNode
    {
        /// <summary>资源范围的固定类型。</summary>
        public override string Kind => "ResourceScope";
        /// <summary>完整资源组；轴用 Axis:逻辑轴名，输出用 Output:逻辑输出名，区域使用登记名。</summary>
        public List<string> Resources { get; set; } = new List<string>();
        /// <summary>持有资源期间执行的顺序节点。</summary>
        public TaskNode Body { get; set; }
        /// <summary>可选退出确认条件；不能把任务结束直接等同于机械区域已清空。</summary>
        public TaskCondition ExitCondition { get; set; }

        /// <summary>取得整组资源后、正文开始前检查；不代表持续安全监控。</summary>
        public TaskInterlock EntryInterlock { get; set; }
        /// <summary>正文结束后确认可以让出区域；不能与旧 ExitCondition 同时配置。</summary>
        public TaskInterlock ExitInterlock { get; set; }
    }
}
