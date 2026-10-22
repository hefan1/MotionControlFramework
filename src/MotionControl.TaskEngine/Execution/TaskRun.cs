using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace MotionControl.TaskEngine.Execution
{
    /// <summary>只控制一次内存仿真；暂停、计时和一次写值共用短锁，不锁住等待过程。</summary>
    public sealed class TaskRun
    {
        /// <summary>保护控制状态和内存动作提交。</summary>
        private readonly object _sync = new object();
        /// <summary>仅统计实际运行的单调时钟，暂停时停止。</summary>
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        /// <summary>本次运行的取消源，收尾后释放。</summary>
        private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
        /// <summary>预先保存令牌，终态释放取消源后仍可查询。</summary>
        internal CancellationToken Cancellation { get; }
        /// <summary>暂停期间等待恢复的异步信号。</summary>
        private TaskCompletionSource<bool> _resume;
        /// <summary>终态结果；异步唤醒调用方，避免在运行锁内回调。</summary>
        private readonly TaskCompletionSource<TaskRunSnapshot> _completion =
            new TaskCompletionSource<TaskRunSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>按执行编号保存不可变节点快照。</summary>
        private readonly Dictionary<string, TaskNodeSnapshot> _nodes = new Dictionary<string, TaskNodeSnapshot>();
        /// <summary>节点进入顺序，保证快照顺序可读。</summary>
        private readonly List<string> _nodeOrder = new List<string>();
        /// <summary>当前整体状态。</summary>
        private TaskRunState _state = TaskRunState.Running;
        /// <summary>本次终态错误。</summary>
        private TaskExecutionException _error;
        /// <summary>主任务编号。</summary>
        private readonly string _taskId;
        /// <summary>启动时冻结的主任务修订号。</summary>
        private readonly int _revision;
        /// <summary>启动时间，使用 UTC。</summary>
        private readonly DateTime _started = DateTime.UtcNow;
        /// <summary>本次运行唯一编号。</summary>
        public string RunId { get; } = Guid.NewGuid().ToString("N");
        /// <summary>等待所有分支收尾后的结果；失败通过结果 Error 提供，不丢失原始异常。</summary>
        public Task<TaskRunSnapshot> Completion => _completion.Task;
        /// <summary>活动时间仅供本模块计算节点时限。</summary>
        internal double ActiveMilliseconds { get { lock (_sync) return _clock.Elapsed.TotalMilliseconds; } }

        /// <summary>创建独立控制状态，不触碰设备。</summary>
        internal TaskRun(string taskId, int revision)
        {
            _taskId = taskId;
            _revision = revision;
            Cancellation = _cancellation.Token;
        }

        /// <summary>冻结内存动作和活动计时；返回后不会继续提交本次运行的仿真写值。</summary>
        public bool Pause()
        {
            lock (_sync)
            {
                if (_state != TaskRunState.Running) return false;
                _state = TaskRunState.Paused;
                _clock.Stop();
                _resume = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                return true;
            }
        }

        /// <summary>从冻结点继续，不重发已经接受的相对位移。</summary>
        public bool Resume()
        {
            lock (_sync)
            {
                if (_state != TaskRunState.Paused) return false;
                _state = TaskRunState.Running;
                _clock.Start();
                _resume.TrySetResult(true);
                return true;
            }
        }

        /// <summary>请求取消；Completion 完成才表示所有内存动作已停止并释放占用。</summary>
        public bool Cancel()
        {
            lock (_sync)
            {
                if (_state != TaskRunState.Running && _state != TaskRunState.Paused) return false;
                _state = TaskRunState.Cancelling;
                _cancellation.Cancel();
                return true;
            }
        }

        /// <summary>取得一致快照；旧快照不会被后续运行更新。</summary>
        public TaskRunSnapshot GetSnapshot()
        {
            lock (_sync)
            {
                var nodes = new List<TaskNodeSnapshot>();
                foreach (string id in _nodeOrder) nodes.Add(_nodes[id]);
                return new TaskRunSnapshot(RunId, _taskId, _revision, _state, _started,
                    _clock.Elapsed.TotalMilliseconds, nodes.AsReadOnly(), _error);
            }
        }

        /// <summary>仅在运行时提交一个短小的内存动作；暂停等待和取消均不占用锁。</summary>
        internal async Task StepAsync(Action action, CancellationToken token)
        {
            while (true)
            {
                Task resume;
                lock (_sync)
                {
                    token.ThrowIfCancellationRequested();
                    if (_state != TaskRunState.Paused) { action(); return; }
                    resume = _resume.Task;
                }
                var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (token.Register(() => cancelled.TrySetCanceled()))
                    await Task.WhenAny(resume, cancelled.Task).ConfigureAwait(false);
            }
        }

        /// <summary>创建或替换节点快照；同时限制子任务展开后的总规模。</summary>
        internal void Report(TaskExecutionScope scope, TaskNodeState state, string detail)
        {
            lock (_sync)
            {
                if (!_nodes.ContainsKey(scope.ExecutionId))
                {
                    if (_nodes.Count >= 10000) throw scope.Error("本次运行已达到 10000 个节点调用上限。");
                    _nodeOrder.Add(scope.ExecutionId);
                }
                if (state == TaskNodeState.Failed && _nodes.ContainsKey(scope.ExecutionId))
                    detail = "失败前：" + _nodes[scope.ExecutionId].Detail + "；" + detail;
                _nodes[scope.ExecutionId] = new TaskNodeSnapshot(scope.ExecutionId, scope.ParentId,
                    scope.TaskId, scope.Node.Id, scope.Node.Name, state, detail);
            }
        }

        /// <summary>由后台执行器在全部清理结束后发布终态，并释放取消源。</summary>
        internal void Finish(TaskExecutionException error)
        {
            lock (_sync)
            {
                _clock.Stop();
                _error = error;
                _state = error != null ? TaskRunState.Failed
                    : Cancellation.IsCancellationRequested ? TaskRunState.Cancelled : TaskRunState.Completed;
                _cancellation.Dispose();
                _completion.TrySetResult(GetSnapshot());
            }
        }
    }
}
