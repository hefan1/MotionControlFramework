using System;

namespace MotionControl.Framework.Contracts.Alarms
{
    public sealed class MachineAlarmRaisedEventArgs : EventArgs
    {
        /// <summary>创建携带已触发报警记录的事件参数。</summary>
        public MachineAlarmRaisedEventArgs(MachineAlarmRecord alarm)
        {
            Alarm = alarm;
        }

        public MachineAlarmRecord Alarm { get; private set; }
    }
}
