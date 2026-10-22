namespace MotionControl.TaskEngine.Catalog
{
    /// <summary>从现有轴映射取得的只读信息；不包含轴对象或控制器连接。</summary>
    public sealed class TaskAxisInfo
    {
        /// <summary>逻辑轴名称。</summary>
        public string Name { get; private set; }
        /// <summary>所属卡标识，用于判断协调运动是否跨卡。</summary>
        public string ControllerId { get; private set; }
        /// <summary>当前配置工程单位；缺失时为 null，不能猜测为 mm。</summary>
        public string EngineeringUnit { get; private set; }
        /// <summary>配置行程下限；缺失时不能确认静态目标是否合法。</summary>
        public double? MinimumPosition { get; private set; }
        /// <summary>配置行程上限。</summary>
        public double? MaximumPosition { get; private set; }

        /// <summary>复制轴映射中用于任务校验的信息。</summary>
        public TaskAxisInfo(string name, string controllerId, string engineeringUnit,
            double? minimumPosition, double? maximumPosition)
        {
            Name = name;
            ControllerId = controllerId;
            EngineeringUnit = engineeringUnit;
            MinimumPosition = minimumPosition;
            MaximumPosition = maximumPosition;
        }
    }
}
