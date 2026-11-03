namespace MotionControl.Framework.Runtime.Machine
{
    /// <summary>说明设备当前禁止手动轴或 IO 操作的原因，供界面按当前语言显示提示。</summary>
    public enum ManualOperationBlockReason
    {
        /// <summary>当前没有阻止手动操作的条件。</summary>
        None,

        /// <summary>运动控制卡尚未连接。</summary>
        MotionCardDisconnected,

        /// <summary>整机自动流程正在运行或等待暂停。</summary>
        AutomaticFlowActive,

        /// <summary>任务执行会话已占用设备，手动动作不能与任务并发。</summary>
        TaskExecutionEntryClaimed,

        /// <summary>急停输入当前仍处于有效状态。</summary>
        EmergencyStopInputActive,

        /// <summary>急停输入已恢复，但急停报警尚未复位。</summary>
        EmergencyStopAlarmNotReset,

        /// <summary>运行时无法读取急停输入状态。</summary>
        EmergencyStopStatusUnavailable,

        /// <summary>旧自动流程尚未完成或无法确认安全收尾；仍允许专用受控停止。</summary>
        AutomaticFlowUnsettled
    }
}
