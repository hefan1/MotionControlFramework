namespace MotionControl.Framework.Contracts.Flow
{
    /// <summary>任务资源在暂停和恢复检查时的保守状态。</summary>
    public enum TaskResourceRecoveryState
    {
        /// <summary>没有仍在占用或待确认的资源。</summary>
        Clear,
        /// <summary>正常暂停后仍由当前任务持有，不能跨进程认为已清空。</summary>
        Held,
        /// <summary>异常隔离，需要现场处理后才能继续。</summary>
        Blocked,
        /// <summary>无法确认当前会话的资源状态，不得按空闲处理。</summary>
        Unknown
    }

    /// <summary>任务设备可选能力：只读查询资源状态，不进行磁盘或控制器操作。</summary>
    public interface ITaskResourceRecoveryState
    {
        /// <summary>读取当前会话的资源隔离状态。</summary>
        TaskResourceRecoveryState GetResourceRecoveryState();
    }
}
