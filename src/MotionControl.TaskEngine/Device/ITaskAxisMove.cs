namespace MotionControl.TaskEngine.Device
{
    /// <summary>
    /// 一次已接受的单轴动作。起点和终点在动作接受时固定，暂停恢复后不重新叠加相对位移；
    /// 具体实现可以是内存动作，也可以是控制卡上的真实运动。
    /// </summary>
    public interface ITaskAxisMove
    {
        /// <summary>本次动作占用的逻辑轴名称。</summary>
        string AxisName { get; }

        /// <summary>接受命令时的位置，使用该轴工程单位。</summary>
        double Start { get; }

        /// <summary>已经计算好的绝对终点，使用该轴工程单位。</summary>
        double Target { get; }

        /// <summary>本次动作是否是回零。</summary>
        bool IsHome { get; }
    }
}
