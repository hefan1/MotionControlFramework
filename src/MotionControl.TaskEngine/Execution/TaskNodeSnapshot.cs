using System;

namespace MotionControl.TaskEngine.Execution
{
    /// <summary>某个节点调用的不可变快照；可供后续监控页面定位。</summary>
    public sealed class TaskNodeSnapshot
    {
        /// <summary>本次调用唯一编号，重复调用同一子任务时仍不重复。</summary>
        public string ExecutionId { get; }
        /// <summary>父调用编号；主任务根节点为空。</summary>
        public string ParentExecutionId { get; }
        /// <summary>原始任务定义编号。</summary>
        public string TaskId { get; }
        /// <summary>原始节点定义编号。</summary>
        public string NodeId { get; }
        /// <summary>步骤中文名称。</summary>
        public string Name { get; }
        /// <summary>当前节点进度；全局暂停通过 TaskRunSnapshot.State 判断。</summary>
        public TaskNodeState State { get; }
        /// <summary>具体等待条件或失败原因。</summary>
        public string Detail { get; }
        /// <summary>本次状态更新时间，使用 UTC。</summary>
        public DateTime UpdatedAtUtc { get; }
        /// <summary>执行时记录的诊断值；尚未采样时为空，完成后保留最后记录。</summary>
        public TaskStepDiagnostics Diagnostics { get; }

        /// <summary>复制一次节点状态，避免监控方修改运行数据。</summary>
        internal TaskNodeSnapshot(string executionId, string parentId, string taskId,
            string nodeId, string name, TaskNodeState state, string detail, TaskStepDiagnostics diagnostics)
        {
            ExecutionId = executionId;
            ParentExecutionId = parentId;
            TaskId = taskId;
            NodeId = nodeId;
            Name = name;
            State = state;
            Detail = detail;
            UpdatedAtUtc = DateTime.UtcNow;
            Diagnostics = diagnostics;
        }
    }
}
