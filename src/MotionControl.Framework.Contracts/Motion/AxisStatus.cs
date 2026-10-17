namespace MotionControl.Framework.Contracts.Motion
{
    /// <summary>运动卡适配器返回的一次轴状态快照。</summary>
    public sealed class AxisStatus
    {
        /// <summary>创建包含轴位置、速度、伺服、限位、报警、历史停止原因和回零要求的状态快照。</summary>
        public AxisStatus(
            double position,
            double velocity,
            bool servoEnabled,
            bool moving,
            bool homed,
            bool positiveLimitActive,
            bool negativeLimitActive,
            bool positiveSoftLimitActive,
            bool negativeSoftLimitActive,
            bool alarmActive,
            string alarmCode,
            string alarmMessage,
            int stopReasonCode,
            bool requiresHomingAfterLimitAlarm)
        {
            Position = position;
            Velocity = velocity;
            ServoEnabled = servoEnabled;
            Moving = moving;
            Homed = homed;
            PositiveLimitActive = positiveLimitActive;
            NegativeLimitActive = negativeLimitActive;
            PositiveSoftLimitActive = positiveSoftLimitActive;
            NegativeSoftLimitActive = negativeSoftLimitActive;
            AlarmActive = alarmActive;
            AlarmCode = alarmCode;
            AlarmMessage = alarmMessage;
            StopReasonCode = stopReasonCode;
            RequiresHomingAfterLimitAlarm = requiresHomingAfterLimitAlarm;
        }

        public double Position { get; private set; }

        /// <summary>轴当前速度，正负号表示方向，单位为轴工程单位/秒。</summary>
        public double Velocity { get; private set; }

        public bool ServoEnabled { get; private set; }

        public bool Moving { get; private set; }

        public bool Homed { get; private set; }

        public bool PositiveLimitActive { get; private set; }

        public bool NegativeLimitActive { get; private set; }

        /// <summary>正向软件限位当前是否触发。</summary>
        public bool PositiveSoftLimitActive { get; private set; }

        /// <summary>负向软件限位当前是否触发。</summary>
        public bool NegativeSoftLimitActive { get; private set; }

        public bool AlarmActive { get; private set; }

        public string AlarmCode { get; private set; }

        public string AlarmMessage { get; private set; }

        /// <summary>控制卡锁存的历史停止原因位掩码；0 表示没有历史原因。</summary>
        public int StopReasonCode { get; private set; }

        /// <summary>硬限位恢复后，是否必须重新回零才能启动自动流程或执行定位。</summary>
        public bool RequiresHomingAfterLimitAlarm { get; private set; }
    }
}
