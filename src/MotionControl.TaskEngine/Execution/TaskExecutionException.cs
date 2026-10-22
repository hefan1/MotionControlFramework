using System;

namespace MotionControl.TaskEngine.Execution
{
    /// <summary>保留具体失败节点及原始异常，不用笼统失败覆盖原因。</summary>
    public sealed class TaskExecutionException : Exception
    {
        /// <summary>失败任务的定义编号。</summary>
        public string TaskId { get; }
        /// <summary>失败节点的定义编号。</summary>
        public string NodeId { get; }
        /// <summary>失败发生在哪一次节点调用。</summary>
        public string ExecutionId { get; }

        /// <summary>把节点上下文加到错误信息前，并保留完整原始异常。</summary>
        internal TaskExecutionException(string taskId, string nodeId, string executionId,
            string message, Exception inner = null)
            : base("任务 " + taskId + " / 节点 " + nodeId + " / 调用 " + executionId + "：" + message, inner)
        {
            TaskId = taskId;
            NodeId = nodeId;
            ExecutionId = executionId;
        }
    }
}
