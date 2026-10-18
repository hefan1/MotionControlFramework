namespace MotionControl.Adapter.MotionCard.ZMotion
{
    /// <summary>从正运动控制器读取或写入的单个物理轴参数。</summary>
    public sealed class ZMotionPhysicalAxisParameters
    {
        /// <summary>控制器上的物理轴通道号，从零开始。</summary>
        public int AxisNumber { get; set; }

        /// <summary>每工程单位对应的控制器位置单位数。</summary>
        public double UnitsPerEngineeringUnit { get; set; }

        /// <summary>控制器当前负向软限位位置。</summary>
        public double MinimumPosition { get; set; }

        /// <summary>控制器当前正向软限位位置。</summary>
        public double MaximumPosition { get; set; }

        /// <summary>控制器当前速度参数。</summary>
        public double Speed { get; set; }

        /// <summary>控制器当前爬行速度参数。</summary>
        public double CreepSpeed { get; set; }

        /// <summary>控制器当前加速度参数。</summary>
        public double Acceleration { get; set; }

        /// <summary>控制器当前减速度参数。</summary>
        public double Deceleration { get; set; }
    }
}
