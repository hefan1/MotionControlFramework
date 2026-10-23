using System;
using System.Collections.Generic;
using MotionControl.TaskEngine.Definitions;

namespace MotionControl.TaskEngine.Resources
{
    /// <summary>仅供协调器和执行器使用的占用凭证，不持有 TaskRun 或整个任务正文。</summary>
    internal sealed class TaskResourceRequest
    {
        /// <summary>每次申请唯一编号，防止旧恢复操作作用到新申请。</summary>
        internal string Id { get; } = Guid.NewGuid().ToString("N");
        /// <summary>所属运行编号。</summary>
        internal string RunId { get; }
        /// <summary>所属任务编号。</summary>
        internal string TaskId { get; }
        /// <summary>所属节点编号。</summary>
        internal string NodeId { get; }
        /// <summary>所属调用编号。</summary>
        internal string ExecutionId { get; }
        /// <summary>已排序的资源名称，创建后不再修改。</summary>
        internal IReadOnlyList<string> Names { get; }
        /// <summary>只在协调器锁内修改的申请状态。</summary>
        internal TaskResourceState State { get; set; }
        /// <summary>只在协调器锁内修改的隔离原因。</summary>
        internal string Detail { get; set; }
        /// <summary>启动时冻结的退出互锁，用于异常后的重新确认。</summary>
        internal TaskInterlock ExitInterlock { get; }
        /// <summary>兼容旧定义的退出条件。</summary>
        internal TaskCondition ExitCondition { get; }

        /// <summary>保存诊断身份、资源组及最少的恢复条件。</summary>
        internal TaskResourceRequest(string runId, string taskId, string nodeId, string executionId,
            List<string> names, TaskInterlock exitInterlock, TaskCondition exitCondition)
        {
            RunId = runId; TaskId = taskId; NodeId = nodeId; ExecutionId = executionId;
            Names = names.AsReadOnly(); ExitInterlock = exitInterlock; ExitCondition = exitCondition;
            State = TaskResourceState.Waiting;
        }
    }
}
