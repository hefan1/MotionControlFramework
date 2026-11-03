using System;
using System.Diagnostics;
using Newtonsoft.Json.Linq;

namespace MotionControl.VirtualController
{
    /// <summary>一个使用工程单位的虚拟轴；移动按恒速估算，不模拟电机动力学。</summary>
    internal sealed partial class VirtualAxis
    {
        /// <summary>单调时间来源，避免系统时间调整影响运动位置。</summary>
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        /// <summary>上一次运动计算后的反馈位置。</summary>
        private double _position;
        /// <summary>定位目标；点动时不表示有限目标。</summary>
        private double _target;
        /// <summary>带方向的速度，零代表静止。</summary>
        private double _velocity;
        /// <summary>上一次运动位置计算的单调秒数。</summary>
        private double _lastSeconds;
        /// <summary>伺服是否已使能。</summary>
        private bool _servo;
        /// <summary>当前是否正执行有限行程运动。</summary>
        private bool _positioning;
        /// <summary>当前有限行程是否为回零。</summary>
        private bool _homing;
        /// <summary>自进程启动后是否成功回零。</summary>
        private bool _homed;
        /// <summary>由测试命令注入的轴报警。</summary>
        private bool _alarm;
        /// <summary>最近一次轴报警原因。</summary>
        private string _alarmMessage;

        /// <summary>按当前时间更新位置并返回独立状态快照。</summary>
        public JObject Snapshot()
        {
            Update();
            return new JObject
            {
                ["position"] = _position - _coordinateOffset,
                ["commandPosition"] = (_positioning ? _target : _position) - _coordinateOffset,
                ["velocity"] = _velocity,
                ["servoEnabled"] = _servo,
                ["moving"] = _velocity != 0,
                ["homed"] = _homed,
                ["positiveLimitActive"] = SensorPositiveLimit(),
                ["negativeLimitActive"] = SensorNegativeLimit(),
                ["positiveSoftLimitActive"] = false,
                ["negativeSoftLimitActive"] = false,
                ["alarmActive"] = _alarm,
                ["alarmMessage"] = _alarm ? _alarmMessage : null,
                ["requiresHomingAfterLimitAlarm"] = _requiresSensorHome,
                ["originInputActive"] = _sensorRule == null ? JValue.CreateNull() : new JValue(SensorOrigin())
            };
        }

        /// <summary>切换伺服；运行时禁止断使能，报警时禁止使能。</summary>
        public void SetServo(bool enabled)
        {
            Update();
            if (!enabled && _velocity != 0) throw new InvalidOperationException("轴正在运动，不能关闭伺服。");
            if (enabled && _alarm) throw new InvalidOperationException("轴报警未清除，不能使能伺服。");
            _servo = enabled;
        }

        /// <summary>以固定测试速度移动到零位；到达后才置为已回零。</summary>
        public void Home()
        {
            EnsureCanStart();
            _homed = false;
            if (_sensorRule != null) { StartSensorHome(); return; }
            StartPosition(_coordinateOffset, 10, true);
        }

        /// <summary>开始一次绝对定位，轴停止并已回零后才允许启动。</summary>
        public void Move(double position, double speed)
        {
            EnsureCanStart();
            if (!_homed) throw new InvalidOperationException("轴未回零，不能定位。");
            if (speed <= 0) throw new ArgumentOutOfRangeException("speed", "运动速度必须大于零。");
            double physicalTarget = position + _coordinateOffset;
            if (double.IsInfinity(physicalTarget) || double.IsNaN(physicalTarget)) throw new ArgumentOutOfRangeException("position", "物理目标位置超出有限数值范围。");
            CheckSensorDirection(physicalTarget - _position);
            StartPosition(physicalTarget, speed, false);
        }

        /// <summary>开始连续点动；速度符号表示运动方向。</summary>
        public void Jog(double speed)
        {
            EnsureCanStart();
            if (speed == 0) throw new ArgumentOutOfRangeException("speed", "点动速度不能为零。");
            CheckSensorDirection(speed);
            _lastSeconds = _clock.Elapsed.TotalSeconds;
            _positioning = false;
            _homing = false;
            _velocity = speed;
        }

        /// <summary>按当前位置立即停止；未完成的回零不会被认定为成功。</summary>
        public void Stop()
        {
            Update();
            _velocity = 0;
            _target = _position;
            _positioning = false;
            _homing = false;
        }

        /// <summary>清除注入的轴报警，保留伺服和回零状态。</summary>
        public void ResetAlarm()
        {
            Update();
            _alarm = false;
            _alarmMessage = null;
        }

        /// <summary>注入轴报警并停止运动；解除注入后仍需显式复位。</summary>
        public void SetAlarm(bool active)
        {
            if (!active) throw new InvalidOperationException("请通过 resetAlarm 清除轴报警。");
            Stop();
            _alarm = true;
            _alarmMessage = "测试控制器轴报警";
        }

        /// <summary>确保伺服、报警和运动状态允许接收新动作。</summary>
        private void EnsureCanStart()
        {
            Update();
            if (!_servo) throw new InvalidOperationException("伺服未使能。");
            if (_alarm) throw new InvalidOperationException("轴报警未复位。");
            if (_velocity != 0) throw new InvalidOperationException("轴正在运动。");
        }

        /// <summary>创建有限行程，零距离回零立即完成。</summary>
        private void StartPosition(double target, double speed, bool homing)
        {
            _lastSeconds = _clock.Elapsed.TotalSeconds;
            _target = target;
            _homing = homing;
            if (target == _position)
            {
                _velocity = 0;
                _positioning = false;
                if (homing) _homed = true;
                _homing = false;
                return;
            }
            _positioning = true;
            _velocity = target > _position ? speed : -speed;
        }

        /// <summary>根据经过时间计算位置，防止跨越目标点。</summary>
        private void Update()
        {
            double now = _clock.Elapsed.TotalSeconds;
            if (_velocity != 0)
            {
                double next = _position + _velocity * (now - _lastSeconds);
                // 有限定位不能越过已接受的目标；先截断再检查传感器，避免慢轮询凭空触发目标之外的限位。
                if (_positioning && (_velocity > 0 ? next > _target : next < _target)) next = _target;
                if (_sensorRule != null && HandleSensorMotion(next)) { _lastSeconds = now; return; }
                bool arrived = _positioning && (_velocity > 0 ? next >= _target : next <= _target);
                if (arrived)
                {
                    _position = _target;
                    _velocity = 0;
                    _positioning = false;
                    if (_homing) _homed = true;
                    _homing = false;
                }
                else _position = next;
            }
            _lastSeconds = now;
        }
    }
}
