using System.Collections.Generic;

namespace MotionControl.TaskEngine.Definitions
{
    /// <summary>等待读回的逻辑 DO 达到明确状态；输出电平不代表机构到位。</summary>
    public sealed class WaitOutputNode : TaskNode
    {
        /// <summary>等待输出的固定类型。</summary>
        public override string Kind => "WaitOutput";
        /// <summary>现有逻辑 DO 名称。</summary>
        public string OutputName { get; set; }
        /// <summary>期望的逻辑输出状态。</summary>
        public bool Expected { get; set; }
        /// <summary>等待超时关联的设备报警代码。</summary>
        public List<string> TimeoutAlarmCodes { get; set; } = new List<string>();
    }
}
