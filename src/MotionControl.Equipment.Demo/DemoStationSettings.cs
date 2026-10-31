using System;
using System.Collections.Generic;
using System.Linq;

namespace MotionControl.Equipment.Demo
{
    /// <summary>显式启用的单工位验证参数；逻辑名称和运动值均由设备配置提供。</summary>
    public sealed class DemoStationSettings
    {
        /// <summary>旧版设备配置未写报警关联时使用的默认超时报警。</summary>
        public const string DefaultTimeoutAlarmCode = "DEMO_SENSOR_TIMEOUT";

        /// <summary>创建默认关闭的示例工位设置，避免安装后自动获得运动入口。</summary>
        public DemoStationSettings()
        {
            SchemaVersion = 1;
            StationName = "流程验证工位";
            Speed = 1;
            Acceleration = 10;
            Deceleration = 10;
            SensorTimeoutMilliseconds = 3000;
            SensorPollMilliseconds = 50;
            OutputPulseMilliseconds = 1000;
            TimeoutAlarmCodes = new List<string> { DefaultTimeoutAlarmCode };
        }

        /// <summary>配置格式版本，当前只接受 1。</summary>
        public int SchemaVersion { get; set; }

        /// <summary>只有设为 true 且配置完整时，Host 才创建该工位。</summary>
        public bool Enabled { get; set; }

        /// <summary>页面和报警中显示的工位名称。</summary>
        public string StationName { get; set; }

        /// <summary>轴映射中的逻辑轴名称。</summary>
        public string AxisName { get; set; }

        /// <summary>IO 映射中的逻辑 DI 名称。</summary>
        public string SensorInputName { get; set; }

        /// <summary>IO 映射中的逻辑 DO 名称。</summary>
        public string OutputName { get; set; }

        /// <summary>绝对定位目标，单位为所选轴的工程单位。</summary>
        public double TargetPosition { get; set; }

        /// <summary>定位速度，单位为所选轴的工程单位每秒。</summary>
        public double Speed { get; set; }

        /// <summary>定位加速度，单位为所选轴的工程单位每秒平方。</summary>
        public double Acceleration { get; set; }

        /// <summary>定位减速度，单位为所选轴的工程单位每秒平方。</summary>
        public double Deceleration { get; set; }

        /// <summary>等待传感器的最长时间，单位为毫秒；暂停后重新计时。</summary>
        public int SensorTimeoutMilliseconds { get; set; }

        /// <summary>传感器轮询间隔，单位为毫秒。</summary>
        public int SensorPollMilliseconds { get; set; }

        /// <summary>输出保持有效的时间，单位为毫秒；取消时仍请求关闭输出。</summary>
        public int OutputPulseMilliseconds { get; set; }

        /// <summary>等待传感器超时时同时报告的设备报警代码；旧配置默认关联停机超时报警。</summary>
        public List<string> TimeoutAlarmCodes { get; set; }

        /// <summary>在创建工位前检查逻辑名称、运动曲线和等待时间。</summary>
        public void Validate()
        {
            if (SchemaVersion != 1)
            {
                throw new InvalidOperationException("示例工位只支持 schemaVersion 为 1 的配置。");
            }

            if (string.IsNullOrWhiteSpace(StationName)
                || string.IsNullOrWhiteSpace(AxisName)
                || string.IsNullOrWhiteSpace(SensorInputName)
                || string.IsNullOrWhiteSpace(OutputName))
            {
                throw new InvalidOperationException("示例工位必须填写工位、逻辑轴、传感器 DI 和输出 DO 名称。");
            }

            if (!IsFinite(TargetPosition) || !IsPositiveFinite(Speed)
                || !IsPositiveFinite(Acceleration) || !IsPositiveFinite(Deceleration))
            {
                throw new InvalidOperationException("示例工位的目标位置和运动曲线必须是有效工程单位数值。");
            }

            if (SensorTimeoutMilliseconds <= 0 || SensorPollMilliseconds <= 0
                || SensorPollMilliseconds > SensorTimeoutMilliseconds || OutputPulseMilliseconds <= 0)
            {
                throw new InvalidOperationException("示例工位的传感器超时、轮询间隔和输出保持时间必须大于 0，且轮询间隔不得超过超时。");
            }

            if (TimeoutAlarmCodes == null || TimeoutAlarmCodes.Count == 0 || TimeoutAlarmCodes.Count > 32
                || TimeoutAlarmCodes.Any(string.IsNullOrWhiteSpace)
                || TimeoutAlarmCodes.Distinct(StringComparer.OrdinalIgnoreCase).Count() != TimeoutAlarmCodes.Count)
            {
                throw new InvalidOperationException("示例工位超时必须关联 1 到 32 条非空且不重复的报警代码。");
            }
        }

        /// <summary>确认浮点参数不是无穷大或非数值。</summary>
        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        /// <summary>确认运动曲线参数为大于零的有限数值。</summary>
        private static bool IsPositiveFinite(double value)
        {
            return IsFinite(value) && value > 0;
        }
    }
}
