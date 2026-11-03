using System;
using Newtonsoft.Json.Linq;

namespace MotionControl.VirtualController
{
    /// <summary>软件轴对应的原点和硬限位测试配置；位置使用独立物理坐标。</summary>
    internal sealed class AxisSensorRule
    {
        /// <summary>原点信号对应输入通道。</summary>
        public int OriginInput;
        /// <summary>负限位信号对应输入通道。</summary>
        public int NegativeInput;
        /// <summary>正限位信号对应输入通道。</summary>
        public int PositiveInput;
        /// <summary>原点感应区中心物理位置。</summary>
        public double OriginPosition;
        /// <summary>原点感应区半宽；必须大于零。</summary>
        public double OriginHalfWidth;
        /// <summary>负限位感应位置。</summary>
        public double NegativeLimit;
        /// <summary>正限位感应位置。</summary>
        public double PositiveLimit;
        /// <summary>原点信号故障：normal、stuckOff 或 stuckOn。</summary>
        public string OriginFault = "normal";
        /// <summary>负限位信号故障。</summary>
        public string NegativeFault = "normal";
        /// <summary>正限位信号故障。</summary>
        public string PositiveFault = "normal";
    }

    internal sealed partial class VirtualAxis
    {
        /// <summary>显式配置的感应规则；为空时保持旧软件轴行为。</summary>
        private AxisSensorRule _sensorRule;
        /// <summary>已确认原点的物理坐标；显示和命令坐标均减去此偏移。</summary>
        private double _coordinateOffset;
        /// <summary>限位故障后必须重新建立坐标参考。</summary>
        private bool _requiresSensorHome;

        /// <summary>轴静止时安装模型并撤销旧坐标参考；不改变当前位置。</summary>
        public void ConfigureSensors(AxisSensorRule rule)
        {
            Update();
            if (_sensorRule != null) throw new InvalidOperationException("该轴已有传感器模型，请先移除。");
            if (_velocity != 0) throw new InvalidOperationException("轴正在运动，不能设置传感器模型。");
            _sensorRule = rule ?? throw new ArgumentNullException(nameof(rule));
            _homed = false;
            _requiresSensorHome = true;
        }

        /// <summary>只在停止时移除模型，保留当前位置但撤销原坐标参考。</summary>
        public void RemoveSensors()
        {
            Update();
            if (_sensorRule == null) throw new InvalidOperationException("该轴未配置传感器模型。");
            if (_velocity != 0) throw new InvalidOperationException("轴正在运动，不能移除传感器模型。");
            _sensorRule = null;
            _homed = false;
            _requiresSensorHome = false;
        }

        /// <summary>注入单个信号故障；仅改变测试反馈，不替代现场安全回路。</summary>
        public void SetSensorFault(string sensor, string mode)
        {
            Update();
            if (_sensorRule == null) throw new InvalidOperationException("该轴未配置传感器模型。");
            if (mode != "normal" && mode != "stuckOff" && mode != "stuckOn")
                throw new ArgumentException("传感器故障仅支持 normal、stuckOff、stuckOn。");
            switch (sensor)
            {
                case "origin": _sensorRule.OriginFault = mode; break;
                case "negative": _sensorRule.NegativeFault = mode; break;
                case "positive": _sensorRule.PositiveFault = mode; break;
                default: throw new ArgumentException("传感器名称仅支持 origin、negative、positive。");
            }
        }

        /// <summary>更新物理位置并取得三路电平，供控制器刷新对应输入。</summary>
        public void ReadSensors(out bool origin, out bool negative, out bool positive)
        {
            Update();
            if (_sensorRule == null) throw new InvalidOperationException("该轴未配置传感器模型。");
            origin = SensorOrigin(); negative = SensorNegativeLimit(); positive = SensorPositiveLimit();
        }

        /// <summary>返回模型参数、当前物理位置和故障状态。</summary>
        public JObject SensorSnapshot()
        {
            Update();
            if (_sensorRule == null) throw new InvalidOperationException("该轴未配置传感器模型。");
            return new JObject {
                ["originInputChannel"] = _sensorRule.OriginInput, ["negativeInputChannel"] = _sensorRule.NegativeInput,
                ["positiveInputChannel"] = _sensorRule.PositiveInput, ["originPosition"] = _sensorRule.OriginPosition,
                ["originHalfWidth"] = _sensorRule.OriginHalfWidth, ["negativeLimit"] = _sensorRule.NegativeLimit,
                ["positiveLimit"] = _sensorRule.PositiveLimit, ["physicalPosition"] = _position,
                ["originValue"] = SensorOrigin(), ["negativeValue"] = SensorNegativeLimit(), ["positiveValue"] = SensorPositiveLimit(),
                ["originFault"] = _sensorRule.OriginFault, ["negativeFault"] = _sensorRule.NegativeFault,
                ["positiveFault"] = _sensorRule.PositiveFault };
        }

        /// <summary>从当前位置向原点搜索，只有信号有效才建立零位。</summary>
        private void StartSensorHome()
        {
            if (_sensorRule.OriginFault == "stuckOn")
            {
                SensorStopWithAlarm("原点信号卡在有效，无法确认回零。", false);
                return;
            }
            if (SensorOrigin())
            {
                if (SensorNegativeLimit() || SensorPositiveLimit())
                {
                    SensorStopWithAlarm("原点与限位信号同时有效，无法确认回零。", true);
                    return;
                }
                _coordinateOffset = _position;
                _homed = true;
                _requiresSensorHome = false;
                return;
            }
            double target = _position >= _sensorRule.OriginPosition ? _sensorRule.NegativeLimit : _sensorRule.PositiveLimit;
            CheckSensorDirection(target - _position);
            StartPosition(target, 10, true);
        }

        /// <summary>轴已在某侧限位时禁止继续向该侧运动，允许在报警复位后向相反方向退出。</summary>
        private void CheckSensorDirection(double direction)
        {
            if (_sensorRule == null) return;
            if (direction < 0 && SensorNegativeLimit()) throw new InvalidOperationException("负限位有效，不能继续向负方向运动。");
            if (direction > 0 && SensorPositiveLimit()) throw new InvalidOperationException("正限位有效，不能继续向正方向运动。");
        }

        /// <summary>模拟穿越原点感应区及触发硬限位，返回是否已完成停止处理。</summary>
        private bool HandleSensorMotion(double next)
        {
            // 搜索原点时用跨越区间判断，轮询间隔再长也不会直接跳过有效的感应区。
            if (_homing && _sensorRule.OriginFault == "normal")
            {
                double low = _sensorRule.OriginPosition - _sensorRule.OriginHalfWidth;
                double high = _sensorRule.OriginPosition + _sensorRule.OriginHalfWidth;
                if (_position >= low && _position <= high)
                {
                    _coordinateOffset = _position;
                    _velocity = 0; _positioning = false; _homing = false;
                    _homed = true; _requiresSensorHome = false;
                    return true;
                }
                if ((_velocity > 0 && _position < low && next >= low) || (_velocity < 0 && _position > high && next <= high))
                {
                    _position = _velocity > 0 ? low : high;
                    _coordinateOffset = _position;
                    _velocity = 0; _positioning = false; _homing = false;
                    _homed = true; _requiresSensorHome = false;
                    return true;
                }
            }
            bool negativeHit = _velocity < 0 && SensorValue(_sensorRule.NegativeFault, next <= _sensorRule.NegativeLimit);
            bool positiveHit = _velocity > 0 && SensorValue(_sensorRule.PositiveFault, next >= _sensorRule.PositiveLimit);
            if (negativeHit || positiveHit)
            {
                if (negativeHit && _sensorRule.NegativeFault == "normal") _position = _sensorRule.NegativeLimit;
                if (positiveHit && _sensorRule.PositiveFault == "normal") _position = _sensorRule.PositiveLimit;
                SensorStopWithAlarm((negativeHit ? "负限位" : "正限位") + "触发，轴已停止。", true);
                return true;
            }
            bool searchFinished = _homing && (_velocity > 0 ? next >= _target : next <= _target);
            if (searchFinished)
            {
                _position = _target;
                SensorStopWithAlarm("搜索范围内原点未触发，回零失败。", false);
                return true;
            }
            return false;
        }

        /// <summary>停止并锁存原因，不将失败的回零标记为成功。</summary>
        private void SensorStopWithAlarm(string message, bool limit)
        {
            _velocity = 0; _target = _position; _positioning = false; _homing = false;
            _homed = false; _alarm = true; _alarmMessage = message;
            if (limit) _requiresSensorHome = true;
        }

        /// <summary>依据物理位置和故障模式得到原点信号。</summary>
        private bool SensorOrigin()
        { return _sensorRule != null && SensorValue(_sensorRule.OriginFault, Math.Abs(_position - _sensorRule.OriginPosition) <= _sensorRule.OriginHalfWidth + 1e-9); }
        /// <summary>依据物理位置和故障模式得到负限位信号。</summary>
        private bool SensorNegativeLimit()
        { return _sensorRule != null && SensorValue(_sensorRule.NegativeFault, _position <= _sensorRule.NegativeLimit); }
        /// <summary>依据物理位置和故障模式得到正限位信号。</summary>
        private bool SensorPositiveLimit()
        { return _sensorRule != null && SensorValue(_sensorRule.PositiveFault, _position >= _sensorRule.PositiveLimit); }
        /// <summary>将明确故障模式映射到实际输入电平。</summary>
        private static bool SensorValue(string fault, bool normal)
        { return fault == "stuckOn" || (fault == "normal" && normal); }
    }
}
