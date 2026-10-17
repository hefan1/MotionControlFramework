using System;

namespace MotionControl.Adapter.MotionCard.ZMotion
{
    /// <summary>一个 ZDevelop 仿真轴的逻辑名称、通道和工程单位范围。</summary>
    public sealed class ZMotionAxisConfiguration
    {
        /// <summary>设备硬件配置中所属的控制卡标识。</summary>
        public string ControllerId { get; set; }

        public string Name { get; set; }

        public int AxisNumber { get; set; }

        /// <summary>控制器 UNITS：每个工程单位对应的脉冲数。</summary>
        public double UnitsPerEngineeringUnit { get; set; }

        public double MinimumPosition { get; set; }

        public double MaximumPosition { get; set; }

        /// <summary>连续点动使用的加速度，单位为工程单位/秒²。</summary>
        public double JogAcceleration { get; set; }

        /// <summary>连续点动使用的减速度，单位为工程单位/秒²。</summary>
        public double JogDeceleration { get; set; }

        /// <summary>检查轴名称、通道、单位换算、软限位和点动参数。</summary>
        internal void Validate()
        {
            if (string.IsNullOrWhiteSpace(ControllerId) || ControllerId != ControllerId.Trim())
            {
                throw new InvalidOperationException("正运动轴 ControllerId 不能为空或包含首尾空格。");
            }

            if (string.IsNullOrWhiteSpace(Name) || Name != Name.Trim())
            {
                throw new InvalidOperationException("正运动逻辑轴名称不能为空或包含首尾空格。");
            }

            if (AxisNumber < 0)
            {
                throw new InvalidOperationException("正运动物理轴号不能小于 0：" + Name + "。");
            }

            if (!IsFinite(UnitsPerEngineeringUnit) || UnitsPerEngineeringUnit <= 0)
            {
                throw new InvalidOperationException(
                    "轴 '" + Name + "' 的 UnitsPerEngineeringUnit 必须是大于 0 的有限数值。");
            }

            if (!IsFinite(MinimumPosition)
                || !IsFinite(MaximumPosition)
                || MinimumPosition >= MaximumPosition)
            {
                throw new InvalidOperationException(
                    "轴 '" + Name + "' 的仿真软限位范围无效：MinimumPosition 必须小于 MaximumPosition。");
            }

            if (!IsFinite(JogAcceleration) || JogAcceleration <= 0
                || !IsFinite(JogDeceleration) || JogDeceleration <= 0)
            {
                throw new InvalidOperationException(
                    "轴 '" + Name + "' 的点动加速度和减速度必须是大于 0 的有限数值。");
            }
        }

        /// <summary>判断一个配置数值是否为有限数值。</summary>
        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
