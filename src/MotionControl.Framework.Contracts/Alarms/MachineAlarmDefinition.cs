using System;

namespace MotionControl.Framework.Contracts.Alarms
{
    /// <summary>由设备项目定义的一种报警。</summary>
    public sealed class MachineAlarmDefinition
    {
        /// <summary>创建报警代码、报警说明、报警类型和操作指导。</summary>
        public MachineAlarmDefinition(string code, string message, MachineAlarmKind kind,
            string checkGuidance = null, string recoveryGuidance = null)
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
            CheckGuidance = checkGuidance ?? string.Empty;
            RecoveryGuidance = recoveryGuidance ?? string.Empty;
        }

        /// <summary>设备项目定义的稳定报警代码，用于程序判断和历史记录查询。</summary>
        public string Code { get; private set; }

        /// <summary>显示给操作人员的报警说明。</summary>
        public string Message { get; private set; }

        /// <summary>报警类型；停机报警要求整机暂停，非停机报警在条件恢复后清除。</summary>
        public MachineAlarmKind Kind { get; private set; }

        /// <summary>显示给操作人员的优先检查步骤；空值时页面使用报警来源提供的信息。</summary>
        public string CheckGuidance { get; private set; }

        /// <summary>条件处理后的恢复步骤；空值时页面按报警类型显示默认说明。</summary>
        public string RecoveryGuidance { get; private set; }
    }
}
