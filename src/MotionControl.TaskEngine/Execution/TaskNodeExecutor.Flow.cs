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
                }
                if (!complete) await scope.NextPollAsync().ConfigureAwait(false);
            }

            if (scope.TimeoutAlarmsReported)
            {
                ClearTimeoutAlarms(scope, node.TimeoutAlarmCodes, "等待 DI '" + node.InputName + "' 已满足。");
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
