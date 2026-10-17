using System;

namespace MotionControl.Framework.Contracts.Motion
{
    /// <summary>
    /// 一次运动的速度、加速度和减速度。直线轴单位为 mm/s、mm/s²；旋转轴单位为度/s、度/s²。
    /// </summary>
    public sealed class AxisMotionProfile
    {
        /// <summary>创建一次运动使用的速度、加速度和减速度参数。</summary>
        public AxisMotionProfile(double speed, double acceleration, double deceleration)
        {
            if (double.IsNaN(speed) || double.IsInfinity(speed) || speed <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(speed), "Speed must be greater than zero.");
            }

            if (double.IsNaN(acceleration) || double.IsInfinity(acceleration) || acceleration <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(acceleration), "Acceleration must be greater than zero.");
            }

            if (double.IsNaN(deceleration) || double.IsInfinity(deceleration) || deceleration <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(deceleration), "Deceleration must be greater than zero.");
            }

            Speed = speed;
            Acceleration = acceleration;
            Deceleration = deceleration;
        }

        public double Speed { get; private set; }

        public double Acceleration { get; private set; }

        public double Deceleration { get; private set; }
    }
}
