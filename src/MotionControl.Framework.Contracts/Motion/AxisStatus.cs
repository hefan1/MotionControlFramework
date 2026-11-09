namespace MotionControl.Framework.Contracts.Motion
{
    /// <summary>运动卡适配器返回的一次轴状态快照。</summary>
    public sealed class AxisStatus
    {
        /// <summary>创建包含轴位置、速度、伺服、限位、报警说明和回零要求的状态快照。</summary>
        public AxisStatus(
            double position,
            double velocity,
            bool servoEnabled,
            bool moving,
            bool homed,
            bool? positiveLimitActive,
            bool? negativeLimitActive,
            bool? positiveSoftLimitActive,
            bool? negativeSoftLimitActive,
            bool alarmActive,
            string alarmMessage,
            bool requiresHomingAfterLimitAlarm,
            double? commandPosition = null,
            bool? originInputActive = null,
            string limitStatusSource = null)
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
            AlarmMessage = alarmMessage;
            RequiresHomingAfterLimitAlarm = requiresHomingAfterLimitAlarm;
            CommandPosition = commandPosition;
            OriginInputActive = originInputActive;
            LimitStatusSource = limitStatusSource;
        }

        /// <summary>控制器读取的轴实际位置反馈（通常为编码器位置），使用该轴配置的工程单位。</summary>
        public double Position { get; private set; }

        /// <summary>轴当前速度，正负号表示方向，单位为轴工程单位/秒。</summary>
        public double Velocity { get; private set; }

        /// <summary>伺服是否已使能。</summary>
        public bool ServoEnabled { get; private set; }

        /// <summary>轴当前是否正在运动。</summary>
        public bool Moving { get; private set; }

        /// <summary>轴是否已完成回零并建立当前位置参考点。</summary>
        public bool Homed { get; private set; }

        /// <summary>正向硬限位输入当前是否有效；为空表示适配器无法确认状态。</summary>
        public bool? PositiveLimitActive { get; private set; }

        /// <summary>负向硬限位输入当前是否有效；为空表示适配器无法确认状态。</summary>
        public bool? NegativeLimitActive { get; private set; }

        /// <summary>正向软件限位当前是否触发；为空表示适配器无法确认状态。</summary>
        public bool? PositiveSoftLimitActive { get; private set; }

        /// <summary>负向软件限位当前是否触发；为空表示适配器无法确认状态。</summary>
        public bool? NegativeSoftLimitActive { get; private set; }

        /// <summary>适配器是否检测到轴报警或驱动报警；限位状态由独立属性表示。</summary>
        public bool AlarmActive { get; private set; }

        /// <summary>适配器转换出的通用报警说明；没有报警或无法判断时为空。</summary>
        public string AlarmMessage { get; private set; }

        /// <summary>硬限位恢复后，是否必须重新回零才能启动自动流程或执行定位。</summary>
        public bool RequiresHomingAfterLimitAlarm { get; private set; }

        /// <summary>控制器当前指令位置；适配器无法读取时为空，单位为轴工程单位。</summary>
        public double? CommandPosition { get; private set; }

        /// <summary>控制器配置的原点输入当前是否有效；适配器无法读取或未配置时为空。</summary>
        public bool? OriginInputActive { get; private set; }

        /// <summary>限位状态的读取来源标识，例如控制器轴状态 API 或逻辑 DI；未知时为空。</summary>
        public string LimitStatusSource { get; private set; }

        /// <summary>
        /// 上位机确认轴停稳使用的速度阈值，单位为逻辑轴工程单位/秒。
        /// 只用于软件反馈检查，不是安全回路或机械制动的判据。
        /// </summary>
        public const double StoppedVelocityTolerance = 0.001;

        /// <summary>
        /// 必须同时看到停止标志和有限的低速反馈，才把本次轴快照看作已停稳。
        /// 不能由此推断 PLCopen 的 Standstill 状态、命令 Done 或报警已恢复。
        /// </summary>
        public bool HasStoppedMotionFeedback()
        {
            return !Moving && !double.IsNaN(Velocity) && !double.IsInfinity(Velocity)
                && System.Math.Abs(Velocity) <= StoppedVelocityTolerance;
        }
    }
}
