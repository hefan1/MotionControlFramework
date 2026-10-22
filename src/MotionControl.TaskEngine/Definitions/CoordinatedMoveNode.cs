using System.Collections.Generic;
namespace MotionControl.TaskEngine.Definitions
{
    /// <summary>同一卡上的多轴绝对直线协调运动定义；不是多个单轴任务的并发。</summary>
    public sealed class CoordinatedMoveNode : TaskNode
    {
        /// <summary>协调运动的固定类型。</summary>
        public override string Kind => "CoordinatedMove";
        /// <summary>至少两根不重复的逻辑轴及其绝对目标。</summary>
        public List<AxisTarget> Targets { get; set; } = new List<AxisTarget>();
        /// <summary>协调路径的运动参数；本版只定义统一 mm 单位的直线轴组。</summary>
        public TaskMotionProfile Profile { get; set; }
    }

    /// <summary>协调运动中一根逻辑轴的目标。</summary>
    public sealed class AxisTarget
    {
        /// <summary>现有配置中的逻辑轴名称。</summary>
        public string AxisName { get; set; }
        /// <summary>该轴的绝对目标位置。</summary>
        public TaskValueBinding Target { get; set; }
    }
}
