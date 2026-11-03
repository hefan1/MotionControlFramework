using System;
using MotionControl.Framework.Contracts.Motion;

namespace MotionControl.Framework.Runtime.Machine
{
    /// <summary>统一判断控制器反馈是否足以确认轴已停止。</summary>
    internal static class AxisStopFeedback
    {
        /// <summary>沿用任务设备已有的停稳反馈容差，单位为该轴工程单位每秒；不是安全回路判据。</summary>
        private const double StoppedVelocityTolerance = 0.001;

        /// <summary>状态必须存在、运动标志为停止且速度为有限的接近零值。</summary>
        internal static bool IsConfirmedStopped(AxisStatus status)
        {
            return status != null && !status.Moving && !double.IsNaN(status.Velocity)
                && !double.IsInfinity(status.Velocity) && Math.Abs(status.Velocity) <= StoppedVelocityTolerance;
        }
    }
}
