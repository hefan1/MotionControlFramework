using System;
using System.Collections.Generic;
using System.Globalization;
using MotionControl.Framework.Contracts.Flow;
using System.Threading.Tasks;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Resources;

namespace MotionControl.TaskEngine.Execution
{
    /// <summary>资源申请和让出流程，异常隔离与普通 finally 清理明确分开。</summary>
    internal sealed partial class TaskNodeExecutor
    {
        /// <summary>完整范围只申请一次；正文开始后异常退出，不能直接让其他任务进入。</summary>
        private async Task ResourceScopeAsync(TaskExecutionScope scope, ResourceScopeNode node)
        {
            if (scope.ResourceLease != null) throw new InvalidOperationException("不能嵌套资源范围。");
            TaskResourceRequest request = null;
            bool bodyEntered = false;
            bool released = false;
            try
            {
                request = await AcquireAsync(scope, node.Resources, node.ExitInterlock, node.ExitCondition).ConfigureAwait(false);
                scope.ResourceLease = request;
                await WaitInterlockAsync(scope, node.EntryInterlock, null, "等待进入条件", "interlock.entry", null).ConfigureAwait(false);
                await scope.StepAsync(() => bodyEntered = true).ConfigureAwait(false);
                await ExecuteAsync(scope.TaskId, node.Body, scope, scope.Token).ConfigureAwait(false);
                // 确认和释放在同一次运行许可内完成；不会在两者之间响应暂停或取消。
                await WaitInterlockAsync(scope, node.ExitInterlock, node.ExitCondition, "等待退出确认", "interlock.exit", request).ConfigureAwait(false);
                released = true;
            }
            catch (Exception error)
            {
                if (request != null && bodyEntered && !released)
                    _device.Resources.Block(request, "正文未完成退出确认，需核实区域后显式恢复。" + error.Message + " " + scope.WaitDetail);
                throw;
            }
            finally
            {
                scope.ResourceLease = null;
                if (request != null) _device.Resources.Release(request);
            }
        }

        /// <summary>整组排队，失败时清理尚未交给调用方的申请；暂停时不取得新占用。</summary>
        private async Task<TaskResourceRequest> AcquireAsync(TaskExecutionScope scope, IEnumerable<string> names,
            TaskInterlock exitInterlock = null, TaskCondition exitCondition = null)
        {
            var resourceNames = new List<string>(names);
            TaskResourceRequest request = null;
            bool acquired = false;
            try
            {
                await scope.StepAsync(() => request = _device.Resources.Enqueue(_run.RunId, scope.TaskId,
                    _library[scope.TaskId].Name, scope.Node.Id, scope.Node.Name, scope.ExecutionId,
                    resourceNames, exitInterlock, exitCondition)).ConfigureAwait(false);
                while (!acquired)
                {
                    await scope.StepAsync(() =>
                    {
                        string reason;
                        acquired = _device.Resources.TryAcquire(request, out reason);
                        scope.WaitDetail = acquired ? null : reason;
                        var waits = new List<TaskObservedValue>();
                        foreach (string resourceName in resourceNames)
                            waits.Add(new TaskObservedValue(resourceName, "取得资源占用", acquired ? "已取得" : null,
                                acquired ? (bool?)true : null, StationObjectKind.Resource, resourceName,
                                acquired ? StationValueQuality.Good : StationValueQuality.NotProvided));
                        // 资源管理器返回的是整组结果；不能把失败原因里的第一个名称当成唯一等待对象。
                        RecordWait(scope, StationWaitKind.Resource, "resource.acquire_group", reason, waits, acquired);
                        _run.Report(scope, acquired ? TaskNodeState.Running : TaskNodeState.Waiting, reason);
                    }).ConfigureAwait(false);
                    if (!acquired) await scope.NextPollAsync().ConfigureAwait(false);
                }
                return request;
            }
            finally
            {
                if (request != null && !acquired) _device.Resources.Release(request);
            }
        }

        /// <summary>范围内动作只核对继承凭证；范围外动作取得单个临时资源。</summary>
        private async Task<TaskResourceRequest> AcquireActionAsync(TaskExecutionScope scope, string name)
        {
            if (scope.ResourceLease == null) return await AcquireAsync(scope, new[] { name }).ConfigureAwait(false);
            await scope.StepAsync(() => _device.Resources.RequireHeld(scope.ResourceLease, name)).ConfigureAwait(false);
            return null;
        }

        /// <summary>写内存输出也参加资源协调，不能绕过其他范围持有的输出。</summary>
        private async Task SetOutputAsync(TaskExecutionScope scope, SetOutputNode node)
        {
            TaskResourceRequest acquired = null;
            bool writeAttempted = false;
            IDisposable writeSettled = null;
            try
            {
                acquired = await AcquireActionAsync(scope, "Output:" + node.OutputName).ConfigureAwait(false);
                await scope.StepAsync(() =>
                {
                    writeSettled = _run.RegisterInFlightOperation();
                    // 驱动抛错不能证明输出没有变化；从尝试下发开始按状态未知处理。
                    writeAttempted = true;
                    _device.WriteOutput(node.OutputName, node.Value);
                    // 保留原本的一次输出读回；没有读回值时不宣称实际输出已满足。
                    bool? actual = _device.ReadOutput(node.OutputName);
                    scope.Diagnostics = new TaskStepDiagnostics("输出写入记录；机构到位另看反馈输入。", new[] {
                        new TaskObservedValue(node.OutputName, node.Value.ToString(), actual?.ToString(),
                            actual.HasValue ? (bool?)(actual.Value == node.Value) : null,
                            StationObjectKind.Output, node.OutputName,
                            actual.HasValue ? StationValueQuality.Good : StationValueQuality.Unknown) });
                }).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                TaskResourceRequest uncertain = acquired ?? scope.ResourceLease;
                if (uncertain != null && writeAttempted)
                {
                    try { _device.Resources.Block(uncertain, "输出写入或反馈失败，实际状态未知，请核对现场。" + error.Message); }
                    catch { _run.MarkUnconfirmedPauseFailure(); throw; }
                }
                throw;
            }
            finally
            {
                try { if (acquired != null) _device.Resources.Release(acquired); }
                catch
                {
                    scope.RetainFailureHandoff(writeSettled);
                    writeSettled = null;
                    throw;
                }
                finally { writeSettled?.Dispose(); }
            }
        }

        /// <summary>等待明确条件；未知不能放行。退出条件通过时在同一步让出资源。</summary>
        private async Task WaitInterlockAsync(TaskExecutionScope scope, TaskInterlock interlock,
            TaskCondition legacyCondition, string phase, string reasonCode, TaskResourceRequest releaseAfterMatch)
        {
            bool matched = false;
            while (!matched)
            {
                try
                {
                    await scope.StepAsync(() =>
                    {
                        string reason;
                        matched = _device.CheckInterlock(interlock, legacyCondition, out reason);
                        scope.WaitDetail = matched ? null : phase + "：" + reason;
                        RecordWait(scope, StationWaitKind.Interlock, reasonCode, phase + "：" + reason,
                            DescribeInterlockWaits(interlock, legacyCondition, matched), matched);
                        _run.Report(scope, matched ? TaskNodeState.Running : TaskNodeState.Waiting, phase + "：" + reason);
                        if (matched && releaseAfterMatch != null) _device.Resources.Release(releaseAfterMatch);
                    }).ConfigureAwait(false);
                    if (!matched) await scope.NextPollAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (scope.Token.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    if (_station == null) throw;
                    var failure = error as TaskExecutionException ?? scope.Error(error.Message, error);
                    scope.Diagnostics = (scope.Diagnostics ?? new TaskStepDiagnostics(null)).WithFailure(failure);
                    _run.Report(scope, TaskNodeState.Failed, failure.Message);
                    // 正文已经完成时只恢复退出检查，不能从范围开头重放动作。
                    Task recovery;
                    try { recovery = _station.RecoverAsync(scope, failure); }
                    finally { scope.CompleteFailureHandoff(); }
                    await recovery.ConfigureAwait(false);
                    _station.Machine.MarkNodeRecovered(_station);
                }
            }
        }
        /// <summary>列出明确配置的互锁对象；现有设备接口未返回逐项采样时保持未提供，不重新读硬件。</summary>
        internal static IReadOnlyList<TaskObservedValue> DescribeInterlockWaits(
            TaskInterlock interlock, TaskCondition legacyCondition, bool matched)
        {
            var values = new List<TaskObservedValue>();
            if (interlock != null)
            {
                foreach (TaskInputInterlock input in interlock.Inputs)
                    values.Add(new TaskObservedValue(input.InputName, input.Expected?.ToString(), null,
                        matched ? (bool?)true : null, StationObjectKind.Input, input.InputName, StationValueQuality.NotProvided));
                foreach (TaskAxisInterlock axis in interlock.Axes)
                    values.Add(new TaskObservedValue(axis.AxisName,
                        axis.MinimumPosition?.ToString(CultureInfo.InvariantCulture) + " … "
                            + axis.MaximumPosition?.ToString(CultureInfo.InvariantCulture), null,
                        matched ? (bool?)true : null, StationObjectKind.Axis, axis.AxisName, StationValueQuality.NotProvided));
            }
            // 设备只返回整组判定，不把它当作逐变量采样；仍保留每个已配置变量的准确引用。
            if (interlock != null && interlock.Condition != null)
                TaskConditionEvaluator.AddUnobservedCondition(values, interlock.Condition);
            if (legacyCondition != null)
                TaskConditionEvaluator.AddUnobservedCondition(values, legacyCondition);
            return values.AsReadOnly();
        }

    }
}
