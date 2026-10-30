using System.Collections.Generic;

namespace MotionControl.TaskEngine.Definitions
{
    /// <summary>等待逻辑 DI 达到指定状态。</summary>
    public sealed class WaitInputNode : TaskNode
    {
        /// <summary>等待输入的固定类型。</summary>
        public override string Kind => "WaitInput";
        /// <summary>现有逻辑 DI 名称。</summary>
        public string InputName { get; set; }
        /// <summary>期望的逻辑状态。</summary>
        public bool Expected { get; set; }
        /// <summary>等待超时时需要关联的设备报警代码；任务检查要求至少一条，可关联多条。</summary>
        public List<string> TimeoutAlarmCodes { get; set; } = new List<string>();
    }
}
