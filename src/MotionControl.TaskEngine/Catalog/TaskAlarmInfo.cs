using MotionControl.Framework.Contracts.Alarms;

namespace MotionControl.TaskEngine.Catalog
{
    /// <summary>任务编辑器可读取的报警库摘要，不包含报警服务或运行句柄。</summary>
    public sealed class TaskAlarmInfo
    {
        /// <summary>报警库中的稳定代码，也是任务保存的引用值。</summary>
        public string Code { get; private set; }
        /// <summary>显示给操作人员的报警说明。</summary>
        public string Message { get; private set; }
        /// <summary>报警类别；停机与非停机由报警库维护。</summary>
        public MachineAlarmKind Kind { get; private set; }
        /// <summary>报警触发后供现场人员查看的检查指导。</summary>
        public string CheckGuidance { get; private set; }
        /// <summary>条件处理后的恢复指导。</summary>
        public string RecoveryGuidance { get; private set; }

        /// <summary>从已校验的报警定义建立只读编辑摘要。</summary>
        public TaskAlarmInfo(string code, string message, MachineAlarmKind kind,
            string checkGuidance = null, string recoveryGuidance = null)
        {
            Code = code;
            Message = message;
            Kind = kind;
            CheckGuidance = checkGuidance ?? string.Empty;
            RecoveryGuidance = recoveryGuidance ?? string.Empty;
        }
    }
}
