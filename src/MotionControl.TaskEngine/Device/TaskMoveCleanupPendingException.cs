using System;
using System.Threading.Tasks;

namespace MotionControl.TaskEngine.Device
{
    /// <summary>轴停止失败且原动作仍有异步收尾；调用方必须隔离资源并等待它结束。</summary>
    public sealed class TaskMoveCleanupPendingException : InvalidOperationException
    {
        /// <summary>原动作的收尾任务；完成不代表轴已安全，仍须现场核实。</summary>
        public Task PendingCleanup { get; }

        /// <summary>保存停止错误及仍在途的原动作。</summary>
        public TaskMoveCleanupPendingException(string message, Exception innerException, Task pendingCleanup)
            : base(message, innerException)
        { PendingCleanup = pendingCleanup ?? throw new ArgumentNullException(nameof(pendingCleanup)); }
    }
}
