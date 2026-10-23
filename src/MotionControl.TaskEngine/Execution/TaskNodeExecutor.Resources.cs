using System;
using System.Collections.Generic;
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
                await WaitInterlockAsync(scope, node.EntryInterlock, null, "等待进入条件", null).ConfigureAwait(false);
                await scope.StepAsync(() => bodyEntered = true).ConfigureAwait(false);
                await ExecuteAsync(scope.TaskId, node.Body, scope, scope.Token).ConfigureAwait(false);
                // 确认和释放在同一次运行许可内完成；不会在两者之间响应暂停或取消。
                await WaitInterlockAsync(scope, node.ExitInterlock, node.ExitCondition, "等待退出确认", request).ConfigureAwait(false);
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
            TaskResourceRequest request = null;
            bool acquired = false;
            try
            {
                await scope.StepAsync(() => request = _device.Resources.Enqueue(_run.RunId, scope.TaskId,
                    scope.Node.Id, scope.ExecutionId, names, exitInterlock, exitCondition)).ConfigureAwait(false);
                while (!acquired)
                {
                    await scope.StepAsync(() =>
                    {
                        string reason;
                        acquired = _device.TryAcquireResources(request, out reason);
                        scope.WaitDetail = acquired ? null : reason;
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
            try
            {
                acquired = await AcquireActionAsync(scope, "Output:" + node.OutputName).ConfigureAwait(false);
                await scope.StepAsync(() => _device.WriteOutput(node.OutputName, node.Value)).ConfigureAwait(false);
            }
            finally
            {
                if (acquired != null) _device.Resources.Release(acquired);
            }
        }

        /// <summary>等待明确条件；未知不能放行。退出条件通过时在同一步让出资源。</summary>
        private async Task WaitInterlockAsync(TaskExecutionScope scope, TaskInterlock interlock,
            TaskCondition legacyCondition, string phase, TaskResourceRequest releaseAfterMatch)
        {
            bool matched = false;
            while (!matched)
            {
                await scope.StepAsync(() =>
                {
                    string reason;
                    matched = _device.CheckInterlock(interlock, legacyCondition, out reason);
                    scope.WaitDetail = matched ? null : phase + "：" + reason;
                    _run.Report(scope, matched ? TaskNodeState.Running : TaskNodeState.Waiting, phase + "：" + reason);
                    if (matched && releaseAfterMatch != null) _device.Resources.Release(releaseAfterMatch);
                }).ConfigureAwait(false);
                if (!matched) await scope.NextPollAsync().ConfigureAwait(false);
            }
        }
    }
}
