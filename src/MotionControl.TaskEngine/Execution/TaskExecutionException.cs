using System;

namespace MotionControl.TaskEngine.Execution
{
    /// <summary>执行器明确报告的失败类别，不通过错误说明的语言或数字推断。</summary>
    public enum TaskExecutionFailureKind
    {
        /// <summary>普通执行失败，具体原因保留在原始异常。</summary>
        ExecutionFailed,
        /// <summary>父节点或本节点活动时限已经到期。</summary>
        Timeout,
        /// <summary>本次运行超过明确的节点调用数量上限，属于不可继续的运行错误。</summary>
        ExpansionLimit
    }

    /// <summary>保留具体失败节点及原始异常，不用笼统失败覆盖原因。</summary>
    public sealed class TaskExecutionException : Exception
    {
        /// <summary>失败任务的定义编号。</summary>
        public string TaskId { get; }
        /// <summary>失败节点的定义编号。</summary>
        public string NodeId { get; }
        /// <summary>失败发生在哪一次节点调用。</summary>
        public string ExecutionId { get; }

        /// <summary>与说明文字无关的明确失败类别。</summary>
        public TaskExecutionFailureKind FailureKind { get; }
        /// <summary>稳定原因代码，供诊断、自动检查和本地化使用。</summary>
        public string ReasonCode { get; }

        /// <summary>不含任务和调用编号前缀的原始原因，供操作员界面直接显示。</summary>
        public string Cause { get; }
        /// <summary>实际到期节点的活动时限，单位毫秒；非超时异常为空。</summary>
        public int? TimeoutMilliseconds { get; }

        /// <summary>把节点上下文加到错误信息前，并保留完整原始异常。</summary>
        internal TaskExecutionException(string taskId, string nodeId, string executionId,
            string message, Exception inner = null, int? timeoutMilliseconds = null,
            TaskExecutionFailureKind failureKind = TaskExecutionFailureKind.ExecutionFailed)
            : base("任务 " + taskId + " / 节点 " + nodeId + " / 调用 " + executionId + "：" + message, inner)
        {
            FailureKind = failureKind;
            switch (failureKind)
            {
                case TaskExecutionFailureKind.Timeout: ReasonCode = "execution.timeout"; break;
                case TaskExecutionFailureKind.ExpansionLimit: ReasonCode = "execution.expansion_limit"; break;
                default: ReasonCode = "execution.failed"; break;
            }
            Cause = message;
            TimeoutMilliseconds = timeoutMilliseconds;
            TaskId = taskId;
            NodeId = nodeId;
            ExecutionId = executionId;
        }
    }
}
