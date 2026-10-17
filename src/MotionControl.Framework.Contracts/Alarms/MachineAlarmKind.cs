namespace MotionControl.Framework.Contracts.Alarms
{
    /// <summary>整机报警对流程的影响。</summary>
    public enum MachineAlarmKind
    {
        /// <summary>触发后暂停整机，条件恢复后仍需复位。</summary>
        StopMachine,

        /// <summary>只显示报警；触发条件恢复后自动清除。</summary>
        NonStop
    }
}
