namespace MotionControl.TaskEngine.Definitions
{
    /// <summary>单根逻辑轴定位；相对运动的恢复目标由后续执行器另行保存。</summary>
    public sealed class MoveAxisNode : TaskNode
    {
        /// <summary>单轴定位的固定类型。</summary>
        public override string Kind => "MoveAxis";
        /// <summary>现有配置中的逻辑轴名称。</summary>
        public string AxisName { get; set; }
        /// <summary>绝对位置或相对距离。</summary>
        public TaskMoveMode Mode { get; set; }
        /// <summary>目标位置或距离的数据来源；相对距离不能引用绝对示教点。</summary>
        public TaskValueBinding Target { get; set; }
        /// <summary>本次工艺动作的速度、加速度和减速度。</summary>
        public TaskMotionProfile Profile { get; set; }
    }

    /// <summary>单轴定位的坐标方式。</summary>
    public enum TaskMoveMode
    {
        /// <summary>移动到指定绝对位置。</summary>
        Absolute,
        /// <summary>从动作开始位置移动指定距离。</summary>
        Relative
    }
}
