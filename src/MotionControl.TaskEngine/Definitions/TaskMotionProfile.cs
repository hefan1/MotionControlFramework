namespace MotionControl.TaskEngine.Definitions
{
    /// <summary>工艺动作的运动参数；不使用手动页速度作为自动生产速度。</summary>
    public sealed class TaskMotionProfile
    {
        /// <summary>轴速度或协调路径速度，单位为工程单位每秒，必须大于零。</summary>
        public double Speed { get; set; }
        /// <summary>加速度，单位为工程单位每平方秒，必须大于零。</summary>
        public double Acceleration { get; set; }
        /// <summary>减速度，单位为工程单位每平方秒，必须大于零。</summary>
        public double Deceleration { get; set; }
    }
}
