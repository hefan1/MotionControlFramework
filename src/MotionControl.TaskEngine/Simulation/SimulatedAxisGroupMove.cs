namespace MotionControl.TaskEngine.Simulation
{
    /// <summary>执行器用同一份变量快照解析出的一个轴目标，尚未接受为动作。</summary>
    internal sealed class SimulatedAxisGroupTarget
    {
        /// <summary>逻辑轴名。</summary>
        internal string AxisName { get; }
        /// <summary>绝对位置或相对距离，使用本轴工程单位。</summary>
        internal double Target { get; }
        /// <summary>是否从接受动作时的位置计算相对终点。</summary>
        internal bool Relative { get; }
        /// <summary>本轴独立速度，单位为本轴工程单位每秒。</summary>
        internal double Speed { get; }

        /// <summary>保存已解析的定位参数，不读取设备或变量。</summary>
        internal SimulatedAxisGroupTarget(string axisName, double target, bool relative, double speed)
        {
            AxisName = axisName; Target = target; Relative = relative; Speed = speed;
        }
    }

    /// <summary>轴组内一根轴已接受的动作；只在设备锁内修改到位标记。</summary>
    internal sealed class SimulatedGroupAxisMove
    {
        /// <summary>本次动作固定的起点、终点与占用凭证。</summary>
        internal SimulatedMove Operation { get; }
        /// <summary>匀速模型的独立运动时长，单位毫秒；零距离时为零。</summary>
        internal double DurationMilliseconds { get; }
        /// <summary>已经完成此轴到位；资源占用仍由整个组持有。</summary>
        internal bool Completed { get; set; }

        /// <summary>保存固定动作和有限时长。</summary>
        internal SimulatedGroupAxisMove(SimulatedMove operation, double durationMilliseconds)
        {
            Operation = operation; DurationMilliseconds = durationMilliseconds;
        }
    }
}
