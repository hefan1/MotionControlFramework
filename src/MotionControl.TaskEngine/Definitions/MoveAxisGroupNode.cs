using System.Collections.Generic;

namespace MotionControl.TaskEngine.Definitions
{
    /// <summary>同卡轴组定位：每轴独立目标和速度，全部到位后完成，不承诺直线路径或同时到达。</summary>
    public sealed class MoveAxisGroupNode : TaskNode
    {
        /// <summary>轴组定位固定类型，与 CoordinatedMove 插补定义分别保存。</summary>
        public override string Kind => "MoveAxisGroup";
        /// <summary>2–64 根不重复逻辑轴的定位参数；上限仅限制定义规模。</summary>
        public List<AxisPositionTarget> Targets { get; set; } = new List<AxisPositionTarget>();
    }

    /// <summary>轴组中的一个定位目标，不是独立任务节点，统一使用所属组的时限。</summary>
    public sealed class AxisPositionTarget
    {
        /// <summary>当前目录中的逻辑轴名。</summary>
        public string AxisName { get; set; }
        /// <summary>显式指定绝对位置或相对距离；相对距离不能使用示教点。</summary>
        public TaskMoveMode Mode { get; set; }
        /// <summary>复用常量、设备变量或所属轴示教点；动作接受后固定终点。</summary>
        public TaskValueBinding Target { get; set; }
        /// <summary>本轴工程单位下的速度、加速度和减速度；每根轴分别配置。</summary>
        public TaskMotionProfile Profile { get; set; }
    }
}
