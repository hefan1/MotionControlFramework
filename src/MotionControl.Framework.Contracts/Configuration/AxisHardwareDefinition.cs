namespace MotionControl.Framework.Contracts.Configuration
{
    /// <summary>设备配置中的一个逻辑轴到物理轴通道的映射。</summary>
    public sealed class AxisHardwareDefinition
    {
        /// <summary>设备流程使用的逻辑轴名称。</summary>
        public string Name { get; set; }

        /// <summary>该物理轴所属的控制卡标识。</summary>
        public string ControllerId { get; set; }

        /// <summary>厂商控制卡上的物理轴号。</summary>
        public int AxisNumber { get; set; }

        /// <summary>轴位置显示使用的工程单位，例如 mm 或 deg；未提供时页面显示工程单位。</summary>
        public string EngineeringUnit { get; set; }

        /// <summary>逻辑轴允许的最小工程位置。</summary>
        public double MinimumPosition { get; set; }

        /// <summary>逻辑轴允许的最大工程位置。</summary>
        public double MaximumPosition { get; set; }

        /// <summary>是否允许轴超出配置的正负软件限位；空值表示未完成配置。</summary>
        public bool? SoftLimitEnabled { get; set; }

        /// <summary>手动调试时默认使用的速度，直线轴为 mm/s，旋转轴为度/s。</summary>
        public double ManualSpeed { get; set; }

        /// <summary>手动调试定位和寸动的初始加速度；由 Host 从轴参数配置提供，0 表示未配置。</summary>
        public double ManualAcceleration { get; set; }

        /// <summary>手动调试定位和寸动的初始减速度；由 Host 从轴参数配置提供，0 表示未配置。</summary>
        public double ManualDeceleration { get; set; }
    }
}
