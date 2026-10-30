using System.Collections.Generic;

namespace MotionControl.TaskEngine.Definitions
{
    /// <summary>等待一个类型明确的条件成立，时限使用节点 TimeoutMs。</summary>
    public sealed class WaitConditionNode : TaskNode
    {
        /// <summary>等待条件的固定类型。</summary>
        public override string Kind => "WaitCondition";
        /// <summary>需要成立的条件。</summary>
        public TaskCondition Condition { get; set; }
        /// <summary>等待超时时需要关联的设备报警代码；允许为空或选择多条。</summary>
        public List<string> TimeoutAlarmCodes { get; set; } = new List<string>();
    }
}
