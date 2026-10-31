using MotionControl.TaskEngine.Device;

namespace MotionControl.TaskEngine.Simulation
{
    /// <summary>轴组内一根轴已接受的动作；只在设备锁内修改到位标记。</summary>
    internal sealed class SimulatedGroupAxisMove : ITaskAxisGroupMove
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
            Operation = operation;
            DurationMilliseconds = durationMilliseconds;
        }

        /// <summary>对执行器暴露的逻辑轴名称。</summary>
        string ITaskAxisGroupMove.AxisName
        {
            get { return Operation.AxisName; }
        }

        /// <summary>对执行器暴露的绝对终点。</summary>
        double ITaskAxisGroupMove.Target
        {
            get { return Operation.Target; }
        }
    }
}
