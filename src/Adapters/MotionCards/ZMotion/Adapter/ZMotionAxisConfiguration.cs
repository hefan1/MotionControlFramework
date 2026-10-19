using System;

namespace MotionControl.Adapter.MotionCard.ZMotion
{
    /// <summary>一个 ZDevelop 仿真轴的逻辑名称、通道和工程单位范围。</summary>
    public sealed class ZMotionAxisConfiguration
    {
        /// <summary>设置仿真轴调试参数的可用初始值；设备配置可按轴覆盖。</summary>
        public ZMotionAxisConfiguration()
        {
            ManualSpeed = 5;
            SoftLimitEnabled = true;
        }

        /// <summary>设备硬件配置中所属的控制卡标识。</summary>
        public string ControllerId { get; set; }

        public string Name { get; set; }

        public int AxisNumber { get; set; }

        /// <summary>控制器 UNITS：每个工程单位对应的脉冲数。</summary>
        public double UnitsPerEngineeringUnit { get; set; }

        public double MinimumPosition { get; set; }

        public double MaximumPosition { get; set; }

        /// <summary>控制卡和适配器是否启用正负软限位，默认启用以保持保护。</summary>
        public bool SoftLimitEnabled { get; set; }

        /// <summary>手动调试页面初始速度，单位为工程单位/秒。</summary>
        public double ManualSpeed { get; set; }

        /// <summary>正运动控制卡手册定义的 DATUM 原始模式编号；框架按原编号调用厂商接口，不转换成其他厂商含义。</summary>
        public int HomeMode { get; set; }

        /// <summary>回零搜索速度，单位为工程单位/秒。</summary>
        public double HomeSearchSpeed { get; set; }

        /// <summary>回零爬行速度，单位为工程单位/秒。</summary>
        public double HomeCreepSpeed { get; set; }

        /// <summary>回零加速度，单位为工程单位/秒²。</summary>
        public double HomeAcceleration { get; set; }

        /// <summary>回零减速度，单位为工程单位/秒²。</summary>
        public double HomeDeceleration { get; set; }

        /// <summary>检查轴名称、通道、单位换算、软限位和手动调试默认速度。</summary>
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

            if (SoftLimitEnabled
                && (!IsFinite(MinimumPosition)
                    || !IsFinite(MaximumPosition)
                    || MinimumPosition >= MaximumPosition))
            {
                throw new InvalidOperationException(
                    "轴 '" + Name + "' 的仿真软限位范围无效：MinimumPosition 必须小于 MaximumPosition。");
            }

            if (!IsFinite(ManualSpeed) || ManualSpeed <= 0)
            {
                throw new InvalidOperationException(
                    "轴 '" + Name + "' 的手动调试速度必须是大于 0 的有限数值。");
            }
        }

        /// <summary>检查回零模式、速度和加减速度，避免向控制器下发无效回零参数。</summary>
        internal void ValidateHomeParameters()
        {
            if (HomeMode < 0)
            {
                throw new InvalidOperationException("轴 '" + Name + "' 的正运动回零模式号不能小于 0。");
            }

            if (!IsFinite(HomeSearchSpeed) || HomeSearchSpeed <= 0
                || !IsFinite(HomeCreepSpeed) || HomeCreepSpeed <= 0
                || !IsFinite(HomeAcceleration) || HomeAcceleration <= 0
                || !IsFinite(HomeDeceleration) || HomeDeceleration <= 0)
            {
                throw new InvalidOperationException(
                    "轴 '" + Name + "' 的回零速度、爬行速度、加速度和减速度必须是大于 0 的有限数值。");
            }
        }

        /// <summary>判断一个配置数值是否为有限数值。</summary>
        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
