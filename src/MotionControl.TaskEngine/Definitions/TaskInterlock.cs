using System.Collections.Generic;

namespace MotionControl.TaskEngine.Definitions
{
    /// <summary>明确列出的进入或退出条件；所有项目同时满足才通过，不推断碰撞路径。</summary>
    public sealed class TaskInterlock
    {
        /// <summary>可选的已有变量条件。</summary>
        public TaskCondition Condition { get; set; }
        /// <summary>需停在指定范围内的轴；这些轴必须一起申请占用。</summary>
        public List<TaskAxisInterlock> Axes { get; set; } = new List<TaskAxisInterlock>();
        /// <summary>需达到指定状态的逻辑输入。</summary>
        public List<TaskInputInterlock> Inputs { get; set; } = new List<TaskInputInterlock>();
    }

    /// <summary>一根轴的静止位置条件，位置使用该轴工程单位。</summary>
    public sealed class TaskAxisInterlock
    {
        /// <summary>目录中的逻辑轴名。</summary>
        public string AxisName { get; set; }
        /// <summary>允许位置下限，包含端点，必须明确填写。</summary>
        public double? MinimumPosition { get; set; }
        /// <summary>允许位置上限，包含端点，必须明确填写。</summary>
        public double? MaximumPosition { get; set; }
        /// <summary>默认要求已经回零；无论此值如何，都要求停止、使能、无报警。</summary>
        public bool RequireHomed { get; set; } = true;
    }

    /// <summary>一个逻辑输入的期望状态。</summary>
    public sealed class TaskInputInterlock
    {
        /// <summary>目录中的 DI 名称，不允许使用 DO。</summary>
        public string InputName { get; set; }
        /// <summary>必须显式填写 true 或 false，未知输入永远不通过。</summary>
        public bool? Expected { get; set; }
    }
}
