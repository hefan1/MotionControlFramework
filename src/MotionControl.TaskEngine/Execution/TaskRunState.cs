namespace MotionControl.TaskEngine.Execution
{
    /// <summary>一次任务运行的状态；终态表示所有分支已退出。</summary>
    public enum TaskRunState
    {
        /// <summary>正在执行或等待条件。</summary>
        Running,
        /// <summary>内存仿真已冻结，等待显式恢复。</summary>
        Paused,
        /// <summary>已请求取消，尚在等待分支清理。</summary>
        Cancelling,
        /// <summary>全部执行成功。</summary>
        Completed,
        /// <summary>取消完成，仿真动作已清理。</summary>
        Cancelled,
        /// <summary>执行失败，仿真动作已清理。</summary>
        Failed
    }

    /// <summary>单次节点调用的进度；相同定义可有多次独立调用。</summary>
    public enum TaskNodeState
    {
        /// <summary>执行动作或容器内的步骤。</summary>
        Running,
        /// <summary>正在等待输入、变量、运动或分支汇合。</summary>
        Waiting,
        /// <summary>节点执行成功。</summary>
        Completed,
        /// <summary>节点因取消退出。</summary>
        Cancelled,
        /// <summary>节点执行失败。</summary>
        Failed
    }
}
