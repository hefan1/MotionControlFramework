namespace MotionControl.TaskEngine.Device
{
    /// <summary>设备明确声明多轴分别定位能力；该能力不代表插补、同步起动或同时到达。</summary>
    public interface ITaskAxisGroupSupport
    {
        /// <summary>是否完整支持组定位的接受、推进和收尾；没有声明时按不支持处理。</summary>
        bool SupportsAxisGroupMove { get; }
    }
}
