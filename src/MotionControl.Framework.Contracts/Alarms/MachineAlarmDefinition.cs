using System;

namespace MotionControl.Framework.Contracts.Alarms
{
    /// <summary>由设备项目定义的一种报警。</summary>
    public sealed class MachineAlarmDefinition
    {
        /// <summary>创建报警代码、报警说明和报警类型定义。</summary>
        public MachineAlarmDefinition(string code, string message, MachineAlarmKind kind)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                throw new ArgumentException("报警代码不能为空。", nameof(code));
            }

            if (string.IsNullOrWhiteSpace(message))
            {
                throw new ArgumentException("报警说明不能为空。", nameof(message));
            }

            if (kind != MachineAlarmKind.StopMachine && kind != MachineAlarmKind.NonStop)
            {
                throw new ArgumentOutOfRangeException(nameof(kind));
            }

            Code = code;
            Message = message;
            Kind = kind;
        }

        public string Code { get; private set; }

        public string Message { get; private set; }

        public MachineAlarmKind Kind { get; private set; }
    }
}
