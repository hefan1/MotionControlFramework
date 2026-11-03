using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.IO;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Contracts.Clearance;

namespace MotionControl.Framework.Runtime.Machine
{
    /// <summary>执行设备固定声明的清料动作；资源持有和全局设备互斥由调用方保证。</summary>
    public sealed class ControlledClearanceExecutor
    {
        /// <summary>本次运行允许访问的逻辑轴。</summary>
        private readonly IReadOnlyDictionary<string, IAxis> _axes;
        /// <summary>读取条件和写入声明输出的接口。</summary>
        private readonly ILogicalIo _io;
        /// <summary>保护运行标志与在途任务。</summary>
        private readonly object _sync = new object();
        /// <summary>保留发出的原始动作和停止调用。</summary>
        private readonly List<Task> _issued = new List<Task>();
        /// <summary>执行中、已使用以及发出命令但未确认状态的标记。</summary>
        private bool _running, _used, _unsettled;
        /// <summary>目标输出写入确认后，在完成反馈及监控收尾期间持续检查其目标状态。</summary>
        private int _monitorTargetOutput;
        /// <summary>停止和收尾等待的上限；到时后仍保留原任务。</summary>
        private const int StopWaitMilliseconds = 10000;
        /// <summary>维持条件单次读取上限；避免步骤长时限掩盖通讯卡住。</summary>
        private const int MaintainReadTimeoutMilliseconds = 500;

        /// <summary>绑定轴与 IO；本类不获取或归还设备资源。</summary>
        public ControlledClearanceExecutor(IReadOnlyDictionary<string, IAxis> axes, ILogicalIo io)
        {
            if (axes == null) throw new ArgumentNullException(nameof(axes));
            _io = io ?? throw new ArgumentNullException(nameof(io));
            _axes = axes.ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>命令下发后直至完成反馈核查保持为真；任何未确定结果都锁存为真。</summary>
        public bool HasUnsettledAction { get { lock (_sync) return _unsettled; } }

        /// <summary>已下发的真实动作与停止调用；完成本身不意味着现场状态已经确认。</summary>
        public Task InFlightSettlement { get { lock (_sync) return Task.WhenAll(_issued.ToArray()); } }

        /// <summary>每步复查两次条件后串行执行；同一个执行器一旦失败不得重复使用。</summary>
        public async Task ExecuteAsync(ControlledClearancePlan plan, CancellationToken cancellationToken)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            lock (_sync)
            {
                if (_running || _used || _unsettled) throw new InvalidOperationException("清料执行器已运行或状态未确认，不能复用。");
                _running = true; _used = true;
            }
            try
            {
                foreach (var action in plan.Actions)
                {
                    var clock = Stopwatch.StartNew();
                    await CheckConditionsAsync(action, clock, cancellationToken).ConfigureAwait(false);
                    await CheckConditionsAsync(action, clock, cancellationToken).ConfigureAwait(false);
                    await CheckMaintainAsync(action, clock, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    await ExecuteMonitoredAsync(action, clock, cancellationToken).ConfigureAwait(false);
                }
            }
            finally { lock (_sync) _running = false; }
        }

        /// <summary>解析已明确提供的逻辑轴，找不到即拒绝。</summary>
        private IAxis Axis(string name)
        {
            IAxis axis;
            if (!_axes.TryGetValue(name, out axis) || axis == null) throw new InvalidOperationException("清料轴未映射：" + name);
            return axis;
        }

        /// <summary>计算步骤剩余等待时间。</summary>
        private static TimeSpan Remaining(ControlledClearanceAction action, Stopwatch clock)
        {
            var remaining = action.Timeout - clock.Elapsed;
            if (remaining <= TimeSpan.Zero) throw new TimeoutException("清料步骤超时：" + action.Name);
            return remaining;
        }

        /// <summary>有界等待调用；超时或取消不会将原始任务标为已完成。</summary>
        private static async Task<T> AwaitAsync<T>(Task<T> task, TimeSpan timeout, CancellationToken token)
        {
            if (task == null) throw new InvalidOperationException("设备调用没有返回任务。");
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                Task delay = Task.Delay(timeout, linked.Token);
                if (await Task.WhenAny(task, delay).ConfigureAwait(false) != task)
                { token.ThrowIfCancellationRequested(); throw new TimeoutException("等待设备反馈超时。"); }
                linked.Cancel();
                return await task.ConfigureAwait(false);
            }
        }

        /// <summary>有界等待无返回值的设备调用。</summary>
        private static async Task AwaitAsync(Task task, TimeSpan timeout, CancellationToken token)
        {
            if (task == null) throw new InvalidOperationException("设备调用没有返回任务。");
            await AwaitAsync(WaitResult(task), timeout, token).ConfigureAwait(false);
        }
        /// <summary>适配无返回值任务，不改变原始调用。</summary>
        private static async Task<bool> WaitResult(Task task) { await task.ConfigureAwait(false); return true; }

        /// <summary>逐个读取输入及轴条件；单次检查不代表持续硬件互锁。</summary>
        private async Task CheckConditionsAsync(ControlledClearanceAction action, Stopwatch clock, CancellationToken token)
        {
            foreach (var input in action.Inputs)
            {
                bool value = await AwaitAsync(_io.ReadInputAsync(input.Name, token), Remaining(action, clock), token).ConfigureAwait(false);
                if (value != input.Expected) throw new InvalidOperationException("清料输入条件不满足：" + input.Name);
            }
            foreach (var condition in action.Axes)
            {
                AxisStatus state = await AwaitAsync(Axis(condition.Name).ReadStatusAsync(token), Remaining(action, clock), token).ConfigureAwait(false);
                if (!ValidAxis(state) || state.Position < condition.Minimum || state.Position > condition.Maximum)
                    throw new InvalidOperationException("清料轴停稳或位置条件不满足：" + condition.Name);
            }
            foreach (var output in action.MaintainOutputs)
            {
                bool value = await AwaitAsync(_io.ReadOutputAsync(output.Name, token), Remaining(action, clock), token).ConfigureAwait(false);
                if (value != output.Expected) throw new InvalidOperationException("清料前输出条件不满足：" + output.Name);
            }
            if (action is ClearanceSetOutput target)
            {
                bool value = await AwaitAsync(_io.ReadOutputAsync(target.OutputName, token), Remaining(action, clock), token).ConfigureAwait(false);
                if (value != target.BeforeValue) throw new InvalidOperationException("清料目标输出的原状态不满足：" + target.OutputName);
            }
        }

        /// <summary>要求有效位置、已有参考、伺服、无报警和限位，且运动标志及速度均表明停稳。</summary>
        private static bool ValidAxis(AxisStatus state)
        {
            return AxisStopFeedback.IsConfirmedStopped(state) && !double.IsNaN(state.Position) && !double.IsInfinity(state.Position)
                && state.ServoEnabled && state.Homed && !state.AlarmActive && !state.RequiresHomingAfterLimitAlarm
                && state.PositiveLimitActive == false && state.NegativeLimitActive == false
                && state.PositiveSoftLimitActive == false && state.NegativeSoftLimitActive == false;
        }

        /// <summary>按设备声明复核维持输入和其他轴；采样不替代硬件持续互锁。</summary>
        private async Task CheckMaintainAsync(ControlledClearanceAction action, Stopwatch clock, CancellationToken token)
        {
            foreach (var input in action.MaintainInputs)
            {
                bool value = await AwaitAsync(_io.ReadInputAsync(input.Name, token), MaintainReadTimeout(action, clock), token).ConfigureAwait(false);
                if (value != input.Expected) throw new InvalidOperationException("清料维持输入失效：" + input.Name);
            }
            foreach (var condition in action.MaintainAxes)
            {
                AxisStatus state = await AwaitAsync(Axis(condition.Name).ReadStatusAsync(token), MaintainReadTimeout(action, clock), token).ConfigureAwait(false);
                if (!ValidAxis(state) || state.Position < condition.Minimum || state.Position > condition.Maximum)
                    throw new InvalidOperationException("清料维持轴状态失效：" + condition.Name);
            }
            foreach (var output in action.MaintainOutputs)
            {
                bool value = await AwaitAsync(_io.ReadOutputAsync(output.Name, token), MaintainReadTimeout(action, clock), token).ConfigureAwait(false);
                if (value != output.Expected) throw new InvalidOperationException("清料维持输出状态失效：" + output.Name);
            }
            if (action is ClearanceSetOutput target && Interlocked.CompareExchange(ref _monitorTargetOutput, 0, 0) != 0)
                await CheckTargetOutputAsync(target, clock, token).ConfigureAwait(false);
        }

        /// <summary>核实已发送的目标输出仍保持期望状态；失败或不可读均不得完成动作。</summary>
        private async Task CheckTargetOutputAsync(ClearanceSetOutput output, Stopwatch clock, CancellationToken token)
        {
            bool actual = await AwaitAsync(_io.ReadOutputAsync(output.OutputName, token), MaintainReadTimeout(output, clock), token).ConfigureAwait(false);
            if (actual != output.Value) throw new InvalidOperationException("清料目标输出状态失效：" + output.OutputName);
        }

        /// <summary>维持条件通讯限时不超过步骤剩余时间。</summary>
        private static TimeSpan MaintainReadTimeout(ControlledClearanceAction action, Stopwatch clock)
        {
            return TimeSpan.FromMilliseconds(Math.Min(MaintainReadTimeoutMilliseconds, Remaining(action, clock).TotalMilliseconds));
        }

        /// <summary>动作执行期间周期采样，读取失败或条件失效立即向执行器报告。</summary>
        private async Task MonitorMaintainAsync(ControlledClearanceAction action, Stopwatch clock, CancellationToken token, Task stopSignal)
        {
            while (true)
            {
                await CheckMaintainAsync(action, clock, token).ConfigureAwait(false);
                if (stopSignal.IsCompleted) return;
                Task interval = Task.Delay(25, token);
                if (await Task.WhenAny(interval, stopSignal).ConfigureAwait(false) == stopSignal) return;
                await interval.ConfigureAwait(false);
            }
        }

        /// <summary>并行监控并在失效时取消动作；无论哪一方失败都等待动作有界收尾。</summary>
        private async Task ExecuteMonitoredAsync(ControlledClearanceAction action, Stopwatch clock, CancellationToken token)
        {
            Interlocked.Exchange(ref _monitorTargetOutput, 0);
            try
            {
            using (var motionCancel = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                // 停止信号不取消在途设备读取；底层自行取消与读取失败均不能伪装成正常收尾。
                var stopMonitor = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                Task monitoring = MonitorMaintainAsync(action, clock, token, stopMonitor.Task);
                // 首次监控已经失败时不得再发动作；之后发生的变化由并行监控请求停车。
                if (monitoring.IsCompleted)
                {
                    await monitoring.ConfigureAwait(false);
                    throw new InvalidOperationException("清料维持监控意外退出。");
                }
                Task execution = action is ClearanceAbsoluteMove move ? MoveAsync(move, clock, motionCancel.Token)
                    : action is ClearanceSetOutput output ? SetOutputAsync(output, clock, motionCancel.Token)
                    : Task.FromException(new InvalidOperationException("不支持的清料动作。"));
                Exception monitorError = null, executionError = null, cancelError = null;
                if (await Task.WhenAny(monitoring, execution).ConfigureAwait(false) == monitoring)
                {
                    try { await monitoring.ConfigureAwait(false); monitorError = new InvalidOperationException("清料维持监控意外退出。"); }
                    catch (Exception error) { monitorError = error; }
                    try { motionCancel.Cancel(); } catch (Exception error) { cancelError = error; }
                }
                try { await execution.ConfigureAwait(false); } catch (Exception error) { executionError = error; }
                // 动作刚完成时重新采样，避免监控结束的并发窗口掩盖失效条件。
                if (executionError == null && monitorError == null)
                {
                    try { await CheckMaintainAsync(action, clock, token).ConfigureAwait(false); }
                    catch (Exception error) { executionError = error; }
                }
                stopMonitor.TrySetResult(true);
                if (monitorError == null)
                {
                    try { await monitoring.ConfigureAwait(false); }
                    catch (Exception error) { monitorError = error; }
                }
                var errors = new[] { monitorError, executionError, cancelError }.Where(x => x != null).ToArray();
                if (errors.Length != 0)
                {
                    // 有可能与动作正常完成同时发生，仍保留未知状态并交由调用方隔离。
                    BeginCommand();
                    if (errors.Length == 1) throw errors[0];
                    throw new AggregateException("清料动作或维持条件异常。", errors);
                }
            }
            }
            finally { Interlocked.Exchange(ref _monitorTargetOutput, 0); }
        }

        /// <summary>下发前锁存待确认状态。</summary>
        private void BeginCommand() { lock (_sync) _unsettled = true; }
        /// <summary>仅在动作及反馈全部成功后清除待确认标志。</summary>
        private void FinishCommand() { lock (_sync) _unsettled = false; }
        /// <summary>保留原始任务以便运行时核查尚未完成的调用。</summary>
        private Task Track(Task task)
        {
            if (task == null) throw new InvalidOperationException("设备命令没有返回任务。");
            lock (_sync) _issued.Add(task);
            return task;
        }

        /// <summary>执行绝对定位；故障时发出停止并等待原动作退出及轴停稳反馈。</summary>
        private async Task MoveAsync(ClearanceAbsoluteMove move, Stopwatch clock, CancellationToken token)
        {
            IAxis axis = Axis(move.AxisName);
            using (var commandCancellation = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                BeginCommand();
                Task command = null;
                try
                {
                    command = Track(axis.MoveAbsoluteAsync(move.Target, move.Profile, commandCancellation.Token));
                    await AwaitAsync(command, Remaining(move, clock), token).ConfigureAwait(false);
                    for (int i = 0; i < 2; i++)
                    {
                        AxisStatus state = await AwaitAsync(axis.ReadStatusAsync(token), Remaining(move, clock), token).ConfigureAwait(false);
                        if (!ValidAxis(state) || Math.Abs(state.Position - move.Target) > move.Tolerance)
                            throw new InvalidOperationException("清料轴到位或停稳反馈无效：" + move.AxisName);
                    }
                    FinishCommand();
                }
                catch (Exception error)
                {
                    // 先发停止，再取消原动作；原调用和停止任务均保留供外部等待。
                    Task stop = null;
                    try { stop = Track(axis.StopAsync(AxisStopMode.Controlled, CancellationToken.None)); }
                    catch (Exception stopError) { stop = Track(Task.FromException(stopError)); }
                    Exception cancellationError = null;
                    try { commandCancellation.Cancel(); } catch (Exception cancelError) { cancellationError = cancelError; }
                    var pending = new[] { command, stop }.Where(item => item != null).ToArray();
                    Exception cleanupError = null;
                    if (pending.Length != 0)
                    {
                        try { await AwaitAsync(Task.WhenAll(pending), TimeSpan.FromMilliseconds(StopWaitMilliseconds), CancellationToken.None).ConfigureAwait(false); }
                        catch (Exception waitError) { cleanupError = waitError; }
                    }
                    // 原动作被取消是正常结果；但停止调用失败、动作未退出或反馈未知都不能当成停稳。
                    if (command != null && command.IsCompleted && command.IsCanceled && stop != null && stop.Status == TaskStatus.RanToCompletion)
                        cleanupError = null;
                    if (cleanupError == null && stop != null && stop.Status == TaskStatus.RanToCompletion && (command == null || command.IsCompleted))
                    {
                        try
                        {
                            for (int i = 0; i < 2; i++)
                            {
                                AxisStatus state = await AwaitAsync(axis.ReadStatusAsync(CancellationToken.None), TimeSpan.FromMilliseconds(StopWaitMilliseconds), CancellationToken.None).ConfigureAwait(false);
                                if (!AxisStopFeedback.IsConfirmedStopped(state)) throw new InvalidOperationException("停止后轴状态未知或未停稳：" + move.AxisName);
                            }
                        }
                        catch (Exception feedbackError) { cleanupError = feedbackError; }
                    }
                    if (cleanupError != null && cancellationError != null)
                        throw new AggregateException("清料轴动作失败，取消、停止或收尾未确认。", error, cancellationError, cleanupError);
                    if (cleanupError != null || cancellationError != null)
                        throw new AggregateException("清料轴动作失败，取消、停止或收尾未确认。", error, cleanupError ?? cancellationError);
                    throw;
                }
            }
        }

        /// <summary>写入固定输出并核对两次回读；失败时保留原写入任务及状态。</summary>
        private async Task SetOutputAsync(ClearanceSetOutput output, Stopwatch clock, CancellationToken token)
        {
            bool before = await AwaitAsync(_io.ReadOutputAsync(output.OutputName, token), Remaining(output, clock), token).ConfigureAwait(false);
            if (before != output.BeforeValue) throw new InvalidOperationException("清料目标输出在下发前发生变化：" + output.OutputName);
            token.ThrowIfCancellationRequested();
            BeginCommand();
            Task command = null;
            try
            {
                command = Track(_io.WriteOutputAsync(output.OutputName, output.Value, token));
                await AwaitAsync(command, Remaining(output, clock), token).ConfigureAwait(false);
                Interlocked.Exchange(ref _monitorTargetOutput, 1);
                for (int i = 0; i < 2; i++)
                {
                    bool actual = await AwaitAsync(_io.ReadOutputAsync(output.OutputName, token), Remaining(output, clock), token).ConfigureAwait(false);
                    if (actual != output.Value) throw new InvalidOperationException("清料输出回读不一致：" + output.OutputName);
                }
                await WaitOutputFeedbackAsync(output, clock, token).ConfigureAwait(false);
                await CheckTargetOutputAsync(output, clock, token).ConfigureAwait(false);
                FinishCommand();
            }
            catch (Exception error)
            {
                Exception settlementError = null;
                if (command != null)
                {
                    try { await AwaitAsync(command, TimeSpan.FromMilliseconds(StopWaitMilliseconds), CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception pendingError) { settlementError = pendingError; }
                }
                if (settlementError != null && !ReferenceEquals(error, settlementError)
                    && !(error is OperationCanceledException && settlementError is OperationCanceledException))
                    throw new AggregateException("清料输出失败且原始写入收尾异常。", error, settlementError);
                throw;
            }
        }

        /// <summary>等待设备独立输入到位，连续两次满足且无互斥冲突才结束。</summary>
        private async Task WaitOutputFeedbackAsync(ClearanceSetOutput output, Stopwatch clock, CancellationToken token)
        {
            int stable = 0;
            while (stable < 2)
            {
                var values = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                foreach (var input in output.CompletionInputs)
                    values.Add(input.Name, await AwaitAsync(_io.ReadInputAsync(input.Name, token), Remaining(output, clock), token).ConfigureAwait(false));
                foreach (var conflict in output.CompletionConflicts)
                    if (values[conflict.First] && values[conflict.Second])
                        throw new InvalidOperationException("清料输出反馈冲突：" + conflict.First + " / " + conflict.Second);
                stable = output.CompletionInputs.All(input => values[input.Name] == input.Expected) ? stable + 1 : 0;
                if (stable < 2) await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(50, Remaining(output, clock).TotalMilliseconds)), token).ConfigureAwait(false);
            }
        }
    }
}
