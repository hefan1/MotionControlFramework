using System.Collections.Generic;

namespace MotionControl.TaskEngine.Definitions
{
    /// <summary>按条件选择一个分支，条件不满足是业务结果，不直接视为故障。</summary>
    public sealed class ConditionBranchNode : TaskNode
    {
        /// <summary>条件分支的固定类型。</summary>
        public override string Kind => "ConditionBranch";
        /// <summary>进入分支前判断的条件。</summary>
        public TaskCondition Condition { get; set; }
        /// <summary>条件成立时执行的顺序节点。</summary>
        public TaskNode WhenTrue { get; set; }
        /// <summary>条件不成立时执行的顺序节点；允许为空。</summary>
        public TaskNode WhenFalse { get; set; }
        /// <summary>条件不成立时需要关联的设备报警代码；允许为空或选择多条。</summary>
        public List<string> WhenFalseAlarmCodes { get; set; } = new List<string>();
    }
}
