namespace MotionControl.TaskEngine.Device
{
    /// <summary>设备可选声明资源范围的进入、退出条件能力；不代表持续安全监控。</summary>
    public interface ITaskInterlockSupport
    {
        /// <summary>是否支持条件互锁；不实现本接口时按不支持处理。</summary>
        bool SupportsInterlockConditions { get; }
    }
}
