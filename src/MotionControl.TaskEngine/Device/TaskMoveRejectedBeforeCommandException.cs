using System;

namespace MotionControl.TaskEngine.Device
{
    /// <summary>设备在发出任何运动命令之前明确拒绝动作；不能用于命令是否下发未知的错误。</summary>
    internal sealed class TaskMoveRejectedBeforeCommandException : InvalidOperationException
    {
        /// <summary>保留前置检查失败原因和原异常。</summary>
        public TaskMoveRejectedBeforeCommandException(string message, Exception innerException)
            : base(message, innerException) { }
    }
}
