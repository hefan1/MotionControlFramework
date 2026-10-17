namespace MotionControl.Framework.Contracts.Motion
{
    /// <summary>运动卡适配器返回的一次轴状态快照。</summary>
    public sealed class AxisStatus
    {
        /// <summary>创建包含轴位置、速度、伺服、限位和报警信息的状态快照。</summary>
        public AxisStatus(
            double position,
            double velocity,
            bool servoEnabled,
            bool moving,
            bool homed,
            bool positiveLimitActive,
            bool negativeLimitActive,
            bool alarmActive,
            string alarmCode,
            string alarmMessage)
        {
            Position = position;
            Velocity = velocity;
            ServoEnabled = servoEnabled;
            Moving = moving;
            Homed = homed;
            PositiveLimitActive = positiveLimitActive;
            NegativeLimitActive = negativeLimitActive;
            AlarmActive = alarmActive;
            AlarmCode = alarmCode;
            AlarmMessage = alarmMessage;
        }

        public double Position { get; private set; }

        /// <summary>轴当前速度，正负号表示方向，单位为轴工程单位/秒。</summary>
        public double Velocity { get; private set; }

        public bool ServoEnabled { get; private set; }

        public bool Moving { get; private set; }

        public bool Homed { get; private set; }

        public bool PositiveLimitActive { get; private set; }

        public bool NegativeLimitActive { get; private set; }

        public bool AlarmActive { get; private set; }

        public string AlarmCode { get; private set; }

        public string AlarmMessage { get; private set; }
    }
}
