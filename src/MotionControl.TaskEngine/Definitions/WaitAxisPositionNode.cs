using System.Collections.Generic;

namespace MotionControl.TaskEngine.Definitions
{
    /// <summary>等待一根轴已回零、已使能、无报警且停在指定位置。</summary>
    public sealed class WaitAxisPositionNode : TaskNode
    {
        /// <summary>等待轴位置的步骤类型。</summary>
        public override string Kind => "WaitAxisPosition";
        /// <summary>要检查的已映射逻辑轴。</summary>
        public string AxisName { get; set; }
        /// <summary>预期位置，单位由轴映射决定。</summary>
        public double Position { get; set; }
        /// <summary>允许位置误差，必须为非负有限数。</summary>
        public double Tolerance { get; set; }
        /// <summary>等待超时关联的报警代码。</summary>
        public List<string> TimeoutAlarmCodes { get; set; } = new List<string>();
    }
}
