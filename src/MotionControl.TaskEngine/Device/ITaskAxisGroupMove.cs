namespace MotionControl.TaskEngine.Device
{
    /// <summary>
    /// 轴组动作中的一根轴。整组共用一次资源占用和一次节点生命周期，
    /// 各轴按自身速度推进，快轴到位后仍占用资源直到慢轴完成。
    /// </summary>
    public interface ITaskAxisGroupMove
    {
        /// <summary>本组内的逻辑轴名称。</summary>
        string AxisName { get; }

        /// <summary>本轴已经计算好的绝对终点，使用该轴工程单位。</summary>
        double Target { get; }
    }
}
