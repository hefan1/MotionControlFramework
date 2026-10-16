namespace MotionControl.Framework.Contracts.Motion
{
    /// <summary>普通轴停止方式。急停由独立安全回路处理。</summary>
    public enum AxisStopMode
    {
        /// <summary>按减速度停止。</summary>
        Controlled,

        /// <summary>立即中止运动指令；使用前需确认设备允许这种停止方式。</summary>
        Abort
    }
}
