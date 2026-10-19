using System;

namespace MotionControl.Framework.Contracts.Alarms
{
    /// <summary>携带新触发报警记录的事件参数。</summary>
    public sealed class MachineAlarmRaisedEventArgs : EventArgs
    {
        /// <summary>创建携带已触发报警记录的事件参数。</summary>
        public MachineAlarmRaisedEventArgs(MachineAlarmRecord alarm)
        {
            Alarm = alarm;
        }

        /// <summary>本次事件对应的报警记录。</summary>
        public MachineAlarmRecord Alarm { get; private set; }
    }
}
