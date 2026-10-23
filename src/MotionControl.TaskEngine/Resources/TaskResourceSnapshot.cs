using System.Collections.Generic;

namespace MotionControl.TaskEngine.Resources
{
    /// <summary>一组资源申请的可观察状态。</summary>
    public enum TaskResourceState
    {
        /// <summary>在队列等待，尚未占用任何资源。</summary>
        Waiting,
        /// <summary>已取得完整资源组。</summary>
        Held,
        /// <summary>正文异常退出，尚未确认区域清空，禁止自动让出。</summary>
        Blocked
    }

    /// <summary>资源诊断副本；释放后旧快照仍可用于记录，不暴露内部占用对象。</summary>
    public sealed class TaskResourceSnapshot
    {
        /// <summary>本次申请编号，恢复待确认资源时使用。</summary>
        public string RequestId { get; }
        /// <summary>所属运行编号。</summary>
        public string RunId { get; }
        /// <summary>所属任务编号。</summary>
        public string TaskId { get; }
        /// <summary>所属节点编号。</summary>
        public string NodeId { get; }
        /// <summary>本次节点调用编号。</summary>
        public string ExecutionId { get; }
        /// <summary>整组资源的只读副本。</summary>
        public IReadOnlyList<string> Resources { get; }
        /// <summary>等待、持有或待确认。</summary>
        public TaskResourceState State { get; }
        /// <summary>等待对象或待确认的原因。</summary>
        public string Detail { get; }

        /// <summary>在协调器锁内复制一次完整状态。</summary>
        internal TaskResourceSnapshot(TaskResourceRequest request, string detail)
        {
            RequestId = request.Id; RunId = request.RunId; TaskId = request.TaskId;
            NodeId = request.NodeId; ExecutionId = request.ExecutionId;
            Resources = new List<string>(request.Names).AsReadOnly();
            State = request.State; Detail = detail;
        }
    }
}
