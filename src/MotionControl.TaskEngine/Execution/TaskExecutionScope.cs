using System;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.TaskEngine.Definitions;

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
        /// <summary>父调用编号。</summary>
        internal string ParentId { get; }
        /// <summary>运行取消或所在并行组取消令牌。</summary>
        internal CancellationToken Token { get; }
        /// <summary>当前约束中最早到期的节点。</summary>
        private readonly TaskExecutionScope _deadlineOwner;
        /// <summary>本节点的绝对活动时间截止值，单位毫秒。</summary>
        private readonly double _deadline;

        /// <summary>从父调用继承取消和时间边界，并生成独立调用编号。</summary>
        internal TaskExecutionScope(TaskRun run, string taskId, TaskNode node,
            TaskExecutionScope parent, CancellationToken token)
        {
            Run = run; TaskId = taskId; Node = node; ParentId = parent?.ExecutionId; Token = token;
            _deadline = run.ActiveMilliseconds + node.TimeoutMs;
            _deadlineOwner = this;
            if (parent != null && parent._deadlineOwner._deadline <= _deadline)
                _deadlineOwner = parent._deadlineOwner;
        }

        /// <summary>每次内存动作前检查取消和父子时限；暂停时异步等待恢复。</summary>
        internal Task StepAsync(Action action)
        {
            return Run.StepAsync(() =>
            {
                if (Run.ActiveMilliseconds >= _deadlineOwner._deadline)
                    throw _deadlineOwner.Error("执行超时，活动时限 " + _deadlineOwner.Node.TimeoutMs + " ms。",
                        new TimeoutException("节点及其子动作未在活动时限内完成。"));
                action();
            }, Token);
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
