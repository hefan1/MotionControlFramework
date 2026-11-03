using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Resources;

namespace MotionControl.TaskEngine.Execution
{
    /// <summary>一次节点调用的上下文；子节点继承最早的父级截止时间。</summary>
    internal sealed class TaskExecutionScope
    {
        /// <summary>本次运行控制器。</summary>
        internal TaskRun Run { get; }
        /// <summary>所属任务编号。</summary>
        internal string TaskId { get; }
        /// <summary>启动时冻结的节点定义。</summary>
        internal TaskNode Node { get; }
        /// <summary>本次调用唯一编号。</summary>
        internal string ExecutionId { get; } = Guid.NewGuid().ToString("N");
        /// <summary>首次采集的记录字段；恢复重试时保持相同内容。</summary>
        internal Dictionary<string, string> RecordFields { get; set; }
        /// <summary>记录首次完整采样的 UTC 时间；节点重试时不变。</summary>
        internal DateTimeOffset? RecordTimeUtc { get; set; }
        /// <summary>父调用编号。</summary>
        internal string ParentId { get; }
        /// <summary>运行取消或所在并行组取消令牌。</summary>
        internal CancellationToken Token { get; }
        /// <summary>从外层资源范围继承的占用；普通动作不会把自己的临时占用传给其他节点。</summary>
        internal TaskResourceRequest ResourceLease { get; set; }
        /// <summary>当前资源或互锁等待原因，超时时保留在错误中。</summary>
        internal string WaitDetail { get; set; }
        /// <summary>当前调用最后一次明确采样，重进节点时由新采样替换。</summary>
        internal TaskStepDiagnostics Diagnostics { get; set; }
        /// <summary>此节点已上报过超时报警；故障复位后节点重进时仍需在条件恢复后清除。</summary>
        internal bool TimeoutAlarmsReported { get; set; }
        /// <summary>父调用，故障复位后可为仍然活动的步骤重新建立时限。</summary>
        private readonly TaskExecutionScope _parent;
        /// <summary>本节点的绝对活动时间截止值，单位毫秒。</summary>
        private double _deadline;
        /// <summary>步骤内异常从运行许可传递至故障锁存期间保留的在途凭证。</summary>
        private IDisposable _failureHandoff;
        /// <summary>已接受的运动绝对终点；故障重进当前节点时不重复叠加相对位移。</summary>
        internal Dictionary<string, double> AcceptedAxisTargets { get; } = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        /// <summary>当前节点拥有最早截止时间且已经超时；父级更早超时仍由父节点处理。</summary>
        internal bool IsOwnTimeoutOwnerExpired
        {
            get
            {
                double deadline = _deadline;
                if (Run.ActiveMilliseconds < deadline) return false;
                for (TaskExecutionScope parent = _parent; parent != null; parent = parent._parent)
                    if (parent._deadline <= deadline) return false;
                return true;
            }
        }

        /// <summary>从父调用继承取消和时间边界，并生成独立调用编号。</summary>
        internal TaskExecutionScope(TaskRun run, string taskId, TaskNode node,
            TaskExecutionScope parent, CancellationToken token)
        {
            Run = run; TaskId = taskId; Node = node; ParentId = parent?.ExecutionId; Token = token;
            ResourceLease = parent?.ResourceLease;
            _parent = parent;
            _deadline = run.ActiveMilliseconds + node.TimeoutMs;
        }

        /// <summary>每次内存动作前检查取消和父子时限；暂停时异步等待恢复。</summary>
        internal Task StepAsync(Action action)
        {
            return Run.StepAsync(() =>
            {
                IDisposable handoff = Run.RegisterInFlightOperation();
                try
                {
                    TaskExecutionScope owner = this;
                    for (TaskExecutionScope parent = _parent; parent != null; parent = parent._parent)
                        if (parent._deadline <= owner._deadline) owner = parent;
                    if (Run.ActiveMilliseconds >= owner._deadline)
                        throw owner.Error("执行超时，活动时限 " + owner.Node.TimeoutMs + " ms。" + WaitDetail,
                            new TimeoutException("节点及其子动作未在活动时限内完成。"));
                    action();
                }
                catch
                {
                    _failureHandoff = handoff;
                    handoff = null;
                    throw;
                }
                finally { handoff?.Dispose(); }
            }, Token);
        }

        /// <summary>故障已进入整机暂停或异常已被明确处理后，允许暂停等待其余在途操作。</summary>
        internal void CompleteFailureHandoff()
        {
            Interlocked.Exchange(ref _failureHandoff, null)?.Dispose();
        }

        /// <summary>把锁外异步操作失败的在途凭证交给节点故障处理，避免暂停先于故障锁存完成。</summary>
        internal void RetainFailureHandoff(IDisposable handoff)
        {
            if (handoff == null) return;
            // 同一节点可能先发生动作失败、随后又发生收尾失败；已有故障凭证足以阻止暂停提前完成。
            if (Interlocked.CompareExchange(ref _failureHandoff, handoff, null) != null) handoff.Dispose();
        }

        /// <summary>只在整机复位启动恢复后给当前活动调用链新时限，不改变已完成节点和固定终点。</summary>
        internal Task ResetTimeBudgetAsync()
        {
            return Run.StepAsync(() =>
            {
                double now = Run.ActiveMilliseconds;
                for (TaskExecutionScope scope = this; scope != null; scope = scope._parent)
                    scope._deadline = now + scope.Node.TimeoutMs;
            }, Token);
        }

        /// <summary>非停机报警触发后继续等待原条件；父级截止时间仍保持有效。</summary>
        internal void IgnoreOwnTimeout()
        {
            _deadline = double.PositiveInfinity;
        }

        /// <summary>等待下一次状态检查，不用阻塞线程的 Sleep。</summary>
        internal Task NextPollAsync()
        {
            return Task.Delay(10, Token);
        }

        /// <summary>创建保留具体节点位置的异常。</summary>
        internal TaskExecutionException Error(string message, Exception inner = null)
        {
            return new TaskExecutionException(TaskId, Node.Id, ExecutionId, message, inner);
        }
    }
}
