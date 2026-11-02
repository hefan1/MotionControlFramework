using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MotionControl.TaskEngine.Execution
{
    /// <summary>隔离一次任务运行；暂停、计时和动作提交共用短锁，不锁住等待过程。</summary>
    public sealed class TaskRun
    {
        /// <summary>保护控制状态和内存动作提交。</summary>
        private readonly object _sync = new object();
        /// <summary>串行提交同次运行的步骤动作；设备调用不能持有运行状态锁。</summary>
        private readonly SemaphoreSlim _stepGate = new SemaphoreSlim(1, 1);
        /// <summary>仅统计实际运行的单调时钟，暂停时停止。</summary>
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        /// <summary>本次运行的取消源，收尾后释放。</summary>
        private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
        /// <summary>预先保存令牌，终态释放取消源后仍可查询。</summary>
        internal CancellationToken Cancellation { get; }
        /// <summary>暂停期间等待恢复的异步信号。</summary>
        private TaskCompletionSource<bool> _resume;
        /// <summary>当前运动在暂停时执行的受控停止动作。</summary>
        private readonly List<Func<Task>> _pauseStopActions = new List<Func<Task>>();
        /// <summary>本轮暂停的所有受控停止动作完成信号。</summary>
        private TaskCompletionSource<bool> _pauseStopCompletion = NewSignal();
        /// <summary>本轮暂停是否已开始执行停止动作。</summary>
        private bool _pauseStopsStarted;
        /// <summary>已经进入运行许可、但仍在设备操作或错误隔离收尾中的数量。</summary>
        private int _inFlightOperations;
        /// <summary>最后一项在途设备操作或错误隔离结束后完成；替换时持有运行锁。</summary>
        private TaskCompletionSource<bool> _inFlightSettled;
        /// <summary>当前在途批次中迟到停轴回调的错误；最后一个操作结束时上报。</summary>
        private Exception _inFlightStopFailure;
        /// <summary>本会话停轴失败或无法确认时锁存，不能因任务完成或异常结束直接复位。</summary>
        private bool _pauseStopUnconfirmed;
        /// <summary>终态结果；异步唤醒调用方，避免在运行锁内回调。</summary>
        private readonly TaskCompletionSource<TaskRunSnapshot> _completion =
            new TaskCompletionSource<TaskRunSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>按执行编号保存不可变节点快照。</summary>
        private readonly Dictionary<string, TaskNodeSnapshot> _nodes = new Dictionary<string, TaskNodeSnapshot>();
        /// <summary>节点进入顺序，保证快照顺序可读。</summary>
        private readonly List<string> _nodeOrder = new List<string>();
        /// <summary>展开数量超限后整次运行不能恢复，防止上层节点被重放。</summary>
        private bool _expansionLimitHit;
        /// <summary>供受控执行器区分可重试的步骤故障与整次运行规模错误。</summary>
        internal bool ExpansionLimitHit { get { lock (_sync) return _expansionLimitHit; } }
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
        /// <summary>读取当前运行是否处于暂停门控内。</summary>
        internal bool IsPaused { get { lock (_sync) return _state == TaskRunState.Paused; } }
        /// <summary>暂停时仍有动作、错误隔离或受控停止未完成，不能复位或释放安全记录。</summary>
        internal bool HasUnsettledPauseOperations
        {
            get
            {
                lock (_sync)
                    return _pauseStopUnconfirmed || _inFlightOperations != 0
                        || (_state == TaskRunState.Paused && _pauseStopCompletion.Task.Status != TaskStatus.RanToCompletion);
            }
        }

        /// <summary>锁存本会话未确认状态；只有结束会话并重新核实才能继续。</summary>
        internal void MarkUnconfirmedPauseFailure()
        {
            lock (_sync) _pauseStopUnconfirmed = true;
        }

        /// <summary>创建独立控制状态，不触碰设备。</summary>
        internal TaskRun(string taskId, int revision)
        {
            _taskId = taskId;
            _revision = revision;
            Cancellation = _cancellation.Token;
        }

        /// <summary>创建异步信号，避免在任务状态锁内执行等待方回调。</summary>
        private static TaskCompletionSource<bool> NewSignal()
        {
            return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        /// <summary>冻结当前任务步骤和活动计时，供内存运行及整机协调暂停使用。</summary>
        public bool Pause()
        {
            lock (_sync)
            {
                if (_state != TaskRunState.Running) return false;
                BeginPause();
                return true;
            }
        }

        /// <summary>冻结运行时钟并等待本次运行中的真实轴全部受控停止。</summary>
        internal Task PauseAsync()
        {
            Func<Task>[] actions = null;
            Task completion;
            Task settled;
            lock (_sync)
            {
                if (_state == TaskRunState.Running) BeginPause();
                if (_state != TaskRunState.Paused) return Task.CompletedTask;
                completion = _pauseStopCompletion.Task;
                settled = _inFlightOperations == 0 ? Task.CompletedTask : _inFlightSettled.Task;
                if (!_pauseStopsStarted)
                {
                    _pauseStopsStarted = true;
                    actions = _pauseStopActions.ToArray();
                    if (actions.Length == 0) _pauseStopCompletion.TrySetResult(true);
                }
            }

            if (actions != null && actions.Length > 0) _ = CompletePauseStopsAsync(actions);
            return Task.WhenAll(completion, settled);
        }

        /// <summary>登记在运行许可内开始、但可能在锁外完成的操作；暂停需等待它及错误隔离结束。</summary>
        internal IDisposable RegisterInFlightOperation()
        {
            lock (_sync)
            {
                if (_inFlightOperations++ == 0) { _inFlightSettled = NewSignal(); _inFlightStopFailure = null; }
                return new InFlightRegistration(this);
            }
        }

        /// <summary>完成一项登记；最后一项结束时通知当前暂停周期。</summary>
        private void CompleteInFlightOperation()
        {
            lock (_sync)
            {
                if (_inFlightOperations <= 0) throw new InvalidOperationException("在途操作登记不匹配。");
                if (--_inFlightOperations == 0)
                {
                    if (_inFlightStopFailure == null) _inFlightSettled.TrySetResult(true);
                    else _inFlightSettled.TrySetException(_inFlightStopFailure);
                }
            }
        }

        /// <summary>登记凭证只允许完成一次，可跨 await 保留。</summary>
        private sealed class InFlightRegistration : IDisposable
        {
            /// <summary>登记所属运行；原子清空避免重复结束。</summary>
            private TaskRun _run;
            /// <summary>保存所属运行。</summary>
            internal InFlightRegistration(TaskRun run) { _run = run; }
            /// <summary>通知所属运行本项已完全收尾。</summary>
            public void Dispose() { Interlocked.Exchange(ref _run, null)?.CompleteInFlightOperation(); }
        }

        /// <summary>为当前轴动作登记暂停时的受控停止；节点离开动作后释放登记。</summary>
        internal IDisposable RegisterPauseStopAction(Func<Task> stopAction)
        {
            if (stopAction == null) throw new ArgumentNullException(nameof(stopAction));
            IDisposable late = null;
            lock (_sync)
            {
                if (_state == TaskRunState.Paused)
                    late = RegisterInFlightOperation();
                else _pauseStopActions.Add(stopAction);
            }
            // 暂停已经开始时，轴动作可能刚刚下发并迟到登记；立即停止并让在途屏障等待结果。
            if (late != null) _ = CompleteLatePauseStopAsync(stopAction, late);
            return new PauseStopRegistration(this, stopAction);
        }

        /// <summary>暂停期间迟到的停轴动作立即启动；失败随在途屏障传播，不能假定停稳。</summary>
        private async Task CompleteLatePauseStopAsync(Func<Task> stopAction, IDisposable registration)
        {
            try
            {
                Task stop = stopAction();
                if (stop == null) throw new InvalidOperationException("停轴回调未返回收尾任务。");
                await stop.ConfigureAwait(false);
            }
            catch (Exception error)
            {
                lock (_sync)
                {
                    _inFlightStopFailure = _inFlightStopFailure ?? error;
                    _pauseStopUnconfirmed = true;
                }
            }
            finally { registration.Dispose(); }
        }

        /// <summary>在受控停止完成后等待操作员恢复，期间任务活动时钟保持冻结。</summary>
        internal async Task WaitForResumeAsync()
        {
            Task stopped;
            Task resumed;
            lock (_sync)
            {
                if (_state != TaskRunState.Paused) return;
                stopped = _pauseStopCompletion.Task;
                resumed = _resume.Task;
            }

            await stopped.ConfigureAwait(false);
            await resumed.ConfigureAwait(false);
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

        /// <summary>在运行许可下串行提交步骤；登记后在状态锁外调用设备，暂停等待在途操作收尾。</summary>
        internal async Task StepAsync(Action action, CancellationToken token)
        {
            while (true)
            {
                Task resume = null;
                IDisposable active = null;
                await _stepGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    lock (_sync)
                    {
                        token.ThrowIfCancellationRequested();
                        if (_state != TaskRunState.Paused) active = RegisterInFlightOperation();
                        else resume = _resume.Task;
                    }
                    if (active != null)
                    {
                        action();
                        return;
                    }
                }
                finally { active?.Dispose(); _stepGate.Release(); }
                var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (token.Register(() => cancelled.TrySetCanceled()))
                    await Task.WhenAny(resume, cancelled.Task).ConfigureAwait(false);
            }
        }

        /// <summary>开始一次暂停周期并停止任务活动计时。</summary>
        private void BeginPause()
        {
            _state = TaskRunState.Paused;
            _clock.Stop();
            _resume = NewSignal();
            _pauseStopCompletion = NewSignal();
            _pauseStopsStarted = false;
            if (_pauseStopActions.Count == 0)
            {
                _pauseStopsStarted = true;
                _pauseStopCompletion.TrySetResult(true);
            }
        }

        /// <summary>顺序执行当前动作的受控停止，并保留首个真实设备错误。</summary>
        private async Task CompletePauseStopsAsync(Func<Task>[] actions)
        {
            try
            {
                var stops = new List<Task>();
                foreach (Func<Task> action in actions)
                {
                    try
                    {
                        Task stop = action();
                        if (stop != null) stops.Add(stop);
                    }
                    catch (Exception error)
                    {
                        stops.Add(Task.FromException(error));
                    }
                }

                await Task.WhenAll(stops).ConfigureAwait(false);
                _pauseStopCompletion.TrySetResult(true);
            }
            catch (Exception error)
            {
                lock (_sync) _pauseStopUnconfirmed = true;
                _pauseStopCompletion.TrySetException(error);
            }
        }

        /// <summary>解除一个轴动作的暂停停止登记，整机锁与运行锁互不嵌套等待。</summary>
        private void UnregisterPauseStopAction(Func<Task> stopAction)
        {
            lock (_sync) _pauseStopActions.Remove(stopAction);
        }

        /// <summary>释放已完成轴动作的暂停停止登记。</summary>
        private sealed class PauseStopRegistration : IDisposable
        {
            /// <summary>所属任务运行。</summary>
            private readonly TaskRun _run;
            /// <summary>需要移除的停止动作。</summary>
            private readonly Func<Task> _stopAction;
            /// <summary>防止重复解除登记。</summary>
            private bool _disposed;

            /// <summary>保存运行和对应停止动作。</summary>
            internal PauseStopRegistration(TaskRun run, Func<Task> stopAction)
            {
                _run = run; _stopAction = stopAction;
            }

            /// <summary>安全解除当前运动的暂停停止登记。</summary>
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _run.UnregisterPauseStopAction(_stopAction);
            }
        }

        /// <summary>创建或替换节点快照；同时限制子任务展开后的总规模。</summary>
        internal void Report(TaskExecutionScope scope, TaskNodeState state, string detail)
        {
            lock (_sync)
            {
                if (!_nodes.ContainsKey(scope.ExecutionId))
                {
                    if (_nodes.Count >= 10000)
                    {
                        _expansionLimitHit = true;
                        throw scope.Error("本次运行已达到 10000 个节点调用上限。");
                    }
                    _nodeOrder.Add(scope.ExecutionId);
                }
                if (state == TaskNodeState.Failed && _nodes.ContainsKey(scope.ExecutionId))
                    detail = "失败前：" + _nodes[scope.ExecutionId].Detail + "；" + detail;
                _nodes[scope.ExecutionId] = new TaskNodeSnapshot(scope.ExecutionId, scope.ParentId,
                    scope.TaskId, scope.Node.Id, scope.Node.Name, state, detail, scope.Diagnostics);
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
