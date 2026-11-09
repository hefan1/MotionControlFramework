using MotionControl.Framework.Contracts.Motion;

namespace MotionControl.Framework.Runtime.Machine
{
    /// <summary>统一判断控制器反馈是否足以确认轴已停止。</summary>
    public static class AxisStopFeedback
    {
        /// <summary>兼容原运行时调用：复用轴快照唯一的停稳反馈规则，空快照绝不算已停止。</summary>
        public static bool IsConfirmedStopped(AxisStatus status)
        {
            return status != null && status.HasStoppedMotionFeedback();
        }
    }
}
