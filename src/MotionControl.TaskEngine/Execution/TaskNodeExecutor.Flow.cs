using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.TaskEngine.Definitions;

namespace MotionControl.TaskEngine.Execution
{
    /// <summary>顺序以外的分支、汇合和可观察等待。</summary>
    internal sealed partial class TaskNodeExecutor
    {
        /// <summary>所有分支成功才继续；受控工位的可恢复故障先暂停等待，真正退出的分支才取消同组并等待清理。</summary>
        private async Task ParallelAsync(TaskExecutionScope scope, ParallelGroupNode parallel)
        {
            using (var groupCancellation = CancellationTokenSource.CreateLinkedTokenSource(scope.Token))
            {
                TaskExecutionException firstFailure = null;
                var branches = new List<Task>();

                // 一个分支负责自己的节点清理；首次故障立即通知同组其他分支。
                async Task RunBranchAsync(TaskNode branch)
                {
                    try
                    {
                        await ExecuteAsync(scope.TaskId, branch, scope, groupCancellation.Token).ConfigureAwait(false);
                    }
                    catch (TaskExecutionException error)
                    {
                        Interlocked.CompareExchange(ref firstFailure, error, null);
                        groupCancellation.Cancel();
                        throw;
                    }
                }

                await scope.StepAsync(() =>
                {
                    _run.Report(scope, TaskNodeState.Waiting, "等待 " + parallel.Branches.Count + " 个并行分支全部完成。");
                    foreach (TaskNode branch in parallel.Branches)
                        branches.Add(Task.Run(() => RunBranchAsync(branch)));
                }).ConfigureAwait(false);
                try { await Task.WhenAll(branches).ConfigureAwait(false); }
                catch
                {
                    // WhenAll 已观察全部任务；首个实际失败优先于它触发的连带取消。
                    if (firstFailure != null) throw firstFailure;
                    throw;
                }
            }
        }

        /// <summary>用一次完整变量快照选择分支；没有有效结果时不进入任一分支。</summary>
        private async Task BranchAsync(TaskExecutionScope scope, ConditionBranchNode branch)
        {
            TaskNode selected = null;
            await scope.StepAsync(() =>
            {
                bool result;
                string reason;
                var values = new List<TaskObservedValue>();
                bool known = TaskConditionEvaluator.TryEvaluate(branch.Condition, _device.Catalog, _device.ReadVariables(), out result, out reason, values);
                scope.Diagnostics = new TaskStepDiagnostics(reason, values, known ? (bool?)result : null,
                    known ? (result ? branch.WhenTrue?.Id : branch.WhenFalse?.Id) ?? string.Empty : null);
                if (!known)
                    throw new InvalidOperationException(reason);
                selected = result ? branch.WhenTrue : branch.WhenFalse;
                _run.Report(scope, TaskNodeState.Running, "条件结果=" + result + "；" + reason);
            }).ConfigureAwait(false);
            if (selected != null) await ExecuteAsync(scope.TaskId, selected, scope, scope.Token).ConfigureAwait(false);
        }

        /// <summary>延时使用活动时间，暂停期间不消耗等待时间。</summary>
        private async Task DelayAsync(TaskExecutionScope scope, int milliseconds)
        {
            double end = _run.ActiveMilliseconds + milliseconds;
            bool complete = false;
            while (!complete)
            {
                await scope.StepAsync(() =>
                {
                    complete = _run.ActiveMilliseconds >= end;
                    scope.Diagnostics = new TaskStepDiagnostics("工艺延时", remainingDelayMilliseconds: Math.Max(0, end - _run.ActiveMilliseconds));
                    _run.Report(scope, TaskNodeState.Waiting, "等待工艺延时 " + milliseconds + " ms。");
                }).ConfigureAwait(false);
                if (!complete) await scope.NextPollAsync().ConfigureAwait(false);
            }
        }

        /// <summary>等待明确的输入值；未知与 OFF 不相同。</summary>
        private async Task WaitInputAsync(TaskExecutionScope scope, WaitInputNode node)
        {
            bool complete = false;
            bool timeoutReportedThisAttempt = false;
            while (!complete)
            {
                try
                {
                    await scope.StepAsync(() =>
                    {
                        bool? actual = _device.ReadInput(node.InputName);
                        complete = actual.HasValue && actual.Value == node.Expected;
                        string current = actual.HasValue ? actual.Value.ToString() : "未知";
                        scope.WaitDetail = "等待 DI " + node.InputName + "=" + node.Expected + "；当前=" + current;
                        scope.Diagnostics = new TaskStepDiagnostics("等待输入：" + node.InputName, new[] {
                            new TaskObservedValue(node.InputName, node.Expected.ToString(), actual?.ToString(), actual.HasValue ? (bool?)complete : null) });
                        _run.Report(scope, TaskNodeState.Waiting, scope.WaitDetail);
                    }).ConfigureAwait(false);
                }
                catch (TaskExecutionException error) when (!timeoutReportedThisAttempt
                    && error.NodeId == scope.Node.Id && scope.IsOwnTimeoutOwnerExpired
                    && error.InnerException is TimeoutException)
                {
                    string detail = "等待 DI '" + node.InputName + "' 达到超时时限 " + node.TimeoutMs + " ms；" + scope.WaitDetail;
                    bool stopRequired = ReportTimeoutAlarms(scope, node.TimeoutAlarmCodes, detail);
                    timeoutReportedThisAttempt = true;
                    scope.TimeoutAlarmsReported = true;
                    if (stopRequired) throw;
                    scope.IgnoreOwnTimeout();
                    scope.CompleteFailureHandoff();
                }
                if (!complete) await scope.NextPollAsync().ConfigureAwait(false);
            }

            if (scope.TimeoutAlarmsReported)
            {
                ClearTimeoutAlarms(scope, node.TimeoutAlarmCodes, "等待 DI '" + node.InputName + "' 已满足。");
                scope.TimeoutAlarmsReported = false;
            }
        }

        /// <summary>等待读回输出达到指定状态；未知或读取异常保留等待并显示原因，不推断机构到位。</summary>
        private async Task WaitOutputAsync(TaskExecutionScope scope, WaitOutputNode node)
        {
            bool complete = false;
            bool timeoutReportedThisAttempt = false;
            while (!complete)
            {
                try
                {
                    await scope.StepAsync(() =>
                    {
                        bool? actual = null;
                        string readError = null;
                        try { actual = _device.ReadOutput(node.OutputName); }
                        catch (Exception error) { readError = error.Message; }
                        complete = readError == null && actual.HasValue && actual.Value == node.Expected;
                        scope.WaitDetail = "等待 DO " + node.OutputName + "=" + node.Expected
                            + "；当前=" + (actual?.ToString() ?? "未知") + (readError == null ? "" : "；读取失败：" + readError);
                        scope.Diagnostics = new TaskStepDiagnostics("等待输出读回：" + node.OutputName, new[] {
                            new TaskObservedValue(node.OutputName, node.Expected.ToString(), actual?.ToString(), actual.HasValue ? (bool?)complete : null) });
                        _run.Report(scope, TaskNodeState.Waiting, scope.WaitDetail);
                    }).ConfigureAwait(false);
                }
                catch (TaskExecutionException error) when (!timeoutReportedThisAttempt
                    && error.NodeId == scope.Node.Id && scope.IsOwnTimeoutOwnerExpired
                    && error.InnerException is TimeoutException)
                {
                    string detail = "等待 DO '" + node.OutputName + "' 达到超时时限 " + node.TimeoutMs + " ms；" + scope.WaitDetail;
                    bool stopRequired = ReportTimeoutAlarms(scope, node.TimeoutAlarmCodes, detail);
                    timeoutReportedThisAttempt = true;
                    scope.TimeoutAlarmsReported = true;
                    if (stopRequired) throw;
                    scope.IgnoreOwnTimeout();
                    scope.CompleteFailureHandoff();
                }
                if (!complete) await scope.NextPollAsync().ConfigureAwait(false);
            }
            if (scope.TimeoutAlarmsReported)
            {
                ClearTimeoutAlarms(scope, node.TimeoutAlarmCodes, "等待 DO '" + node.OutputName + "' 已满足。");
                scope.TimeoutAlarmsReported = false;
            }
        }

        /// <summary>反复核对两路锁存输入，恰好一路有效且重复读取一致时原子发布变量；未知和读错均不放行。</summary>
        private async Task WaitExclusiveInputsAsync(TaskExecutionScope scope, WaitExclusiveInputsNode node)
        {
            bool complete = false;
            bool timeoutReportedThisAttempt = false;
            while (!complete)
            {
                try
                {
                    await scope.StepAsync(() =>
                    {
                        bool? first = null, second = null, firstAgain = null, secondAgain = null;
                        string readError = null;
                        try
                        {
                            first = _device.ReadInput(node.FirstInputName);
                            second = _device.ReadInput(node.SecondInputName);
                            firstAgain = _device.ReadInput(node.FirstInputName);
                            secondAgain = _device.ReadInput(node.SecondInputName);
                        }
                        catch (Exception error) { readError = error.Message; }
                        bool stable = readError == null && first.HasValue && second.HasValue
                            && firstAgain.HasValue && secondAgain.HasValue
                            && first.Value == firstAgain.Value && second.Value == secondAgain.Value;
                        bool matched = stable && first.Value != second.Value
                            && (!node.RequiredFirstValue.HasValue || first.Value == node.RequiredFirstValue.Value);
                        scope.WaitDetail = "等待互斥 DI " + node.FirstInputName + " / " + node.SecondInputName
                            + "；当前=" + (first?.ToString() ?? "未知") + " / " + (second?.ToString() ?? "未知")
                            + (node.RequiredFirstValue.HasValue ? "；要求第一路=" + node.RequiredFirstValue.Value : "；任一路有效")
                            + (readError == null ? (stable ? "" : "；采样未知或变化") : "；读取失败：" + readError);
                        scope.Diagnostics = new TaskStepDiagnostics("两路锁存输入须恰好一路有效", new[] {
                            new TaskObservedValue(node.FirstInputName, "互斥且稳定", first?.ToString(), stable ? (bool?)matched : null),
                            new TaskObservedValue(node.SecondInputName, "互斥且稳定", second?.ToString(), stable ? (bool?)matched : null) });
                        _run.Report(scope, TaskNodeState.Waiting, scope.WaitDetail);
                        if (matched)
                        {
                            _device.WriteBooleanPair(node.FirstVariableName, first.Value, node.SecondVariableName, second.Value);
                            complete = true;
                        }
                    }).ConfigureAwait(false);
                }
                catch (TaskExecutionException error) when (!timeoutReportedThisAttempt
                    && error.NodeId == scope.Node.Id && scope.IsOwnTimeoutOwnerExpired
                    && error.InnerException is TimeoutException)
                {
                    string detail = "等待互斥 DI 达到超时时限 " + node.TimeoutMs + " ms；" + scope.WaitDetail;
                    bool stopRequired = ReportTimeoutAlarms(scope, node.TimeoutAlarmCodes, detail);
                    timeoutReportedThisAttempt = true;
                    scope.TimeoutAlarmsReported = true;
                    if (stopRequired) throw;
                    scope.IgnoreOwnTimeout();
                    scope.CompleteFailureHandoff();
                }
                if (!complete) await scope.NextPollAsync().ConfigureAwait(false);
            }
            if (scope.TimeoutAlarmsReported)
            {
                ClearTimeoutAlarms(scope, node.TimeoutAlarmCodes, "互斥 DI 已取得有效结果。");
                scope.TimeoutAlarmsReported = false;
            }
        }

        /// <summary>条件不满足或变量尚未有效时持续显示原因；节点时限负责终止等待。</summary>
        private async Task WaitConditionAsync(TaskExecutionScope scope, WaitConditionNode node)
        {
            bool complete = false;
            bool timeoutReportedThisAttempt = false;
            while (!complete)
            {
                try
                {
                    await scope.StepAsync(() =>
                    {
                        bool result;
                        string reason;
                        var values = new List<TaskObservedValue>();
                        bool known = TaskConditionEvaluator.TryEvaluate(node.Condition, _device.Catalog, _device.ReadVariables(), out result, out reason, values);
                        scope.Diagnostics = new TaskStepDiagnostics(reason, values, known ? (bool?)result : null);
                        complete = known && result;
                        scope.WaitDetail = "等待条件：" + reason;
                        _run.Report(scope, TaskNodeState.Waiting, scope.WaitDetail);
                    }).ConfigureAwait(false);
                }
                catch (TaskExecutionException error) when (!timeoutReportedThisAttempt
                    && error.NodeId == scope.Node.Id && scope.IsOwnTimeoutOwnerExpired
                    && error.InnerException is TimeoutException)
                {
                    string detail = "等待条件达到超时时限 " + node.TimeoutMs + " ms；" + scope.WaitDetail;
                    bool stopRequired = ReportTimeoutAlarms(scope, node.TimeoutAlarmCodes, detail);
                    timeoutReportedThisAttempt = true;
                    scope.TimeoutAlarmsReported = true;
                    if (stopRequired) throw;
                    scope.IgnoreOwnTimeout();
                    scope.CompleteFailureHandoff();
                }
                if (!complete) await scope.NextPollAsync().ConfigureAwait(false);
            }

            if (scope.TimeoutAlarmsReported)
            {
                ClearTimeoutAlarms(scope, node.TimeoutAlarmCodes, "等待条件已成立。");
                scope.TimeoutAlarmsReported = false;
            }
        }
    }
}
