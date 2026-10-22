using System;
using System.Collections.Generic;

namespace MotionControl.TaskEngine.Execution
{
    /// <summary>某次运行的一致快照；不包含可执行的设备服务。</summary>
    public sealed class TaskRunSnapshot
    {
        /// <summary>本次运行唯一编号。</summary>
        public string RunId { get; }
        /// <summary>主任务定义编号。</summary>
        public string TaskId { get; }
        /// <summary>本次运行使用的定义修订号。</summary>
        public int Revision { get; }
        /// <summary>整体运行状态。</summary>
        public TaskRunState State { get; }
        /// <summary>启动时间，使用 UTC。</summary>
        public DateTime StartedAtUtc { get; }
        /// <summary>扣除暂停后的活动时间，单位毫秒。</summary>
        public double ActiveMilliseconds { get; }
        /// <summary>已经进入过的节点调用，按创建顺序排列。</summary>
        public IReadOnlyList<TaskNodeSnapshot> Nodes { get; }
        /// <summary>根本失败原因；正常完成和外部取消时为空。</summary>
        public TaskExecutionException Error { get; }

        /// <summary>保存运行控制器在锁内复制的数据。</summary>
        internal TaskRunSnapshot(string runId, string taskId, int revision, TaskRunState state,
            DateTime started, double elapsed, IReadOnlyList<TaskNodeSnapshot> nodes, TaskExecutionException error)
        {
            RunId = runId;
            TaskId = taskId;
            Revision = revision;
            State = state;
            StartedAtUtc = started;
            ActiveMilliseconds = elapsed;
            Nodes = nodes;
            Error = error;
        }
    }
}
