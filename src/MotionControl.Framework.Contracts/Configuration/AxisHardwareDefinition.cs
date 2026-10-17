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

        /// <summary>逻辑轴允许的最小工程位置。</summary>
        public double MinimumPosition { get; set; }

        /// <summary>逻辑轴允许的最大工程位置。</summary>
        public double MaximumPosition { get; set; }
    }
}
