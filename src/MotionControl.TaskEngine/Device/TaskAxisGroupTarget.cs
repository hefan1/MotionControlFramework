namespace MotionControl.TaskEngine.Device
{
    /// <summary>
    /// 执行器用同一份变量快照解析出的一个轴组目标，尚未被设备接受为动作。
    /// 目标值可为绝对位置或相对距离，单位为本轴工程单位。
    /// </summary>
    public sealed class TaskAxisGroupTarget
    {
        /// <summary>逻辑轴名称。</summary>
        public string AxisName { get; }
        /// <summary>绝对位置或相对距离，使用本轴工程单位。</summary>
        public double Target { get; }
        /// <summary>是否从接受动作时的位置计算相对终点。</summary>
        public bool Relative { get; }
        /// <summary>本轴独立速度，单位为本轴工程单位每秒。</summary>
        public double Speed { get; }

        /// <summary>保存已解析的定位参数，不读取设备或变量。</summary>
        public TaskAxisGroupTarget(string axisName, double target, bool relative, double speed)
        {
            AxisName = axisName;
            Target = target;
            Relative = relative;
            Speed = speed;
        }
    }
}
