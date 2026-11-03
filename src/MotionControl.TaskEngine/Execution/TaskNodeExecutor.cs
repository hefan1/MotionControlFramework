using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Device;
using MotionControl.TaskEngine.Machine;

namespace MotionControl.TaskEngine.Execution
{
    /// <summary>按明确的节点类型直接分发，定义不保存任何运行状态。</summary>
    internal sealed partial class TaskNodeExecutor
    {
        /// <summary>本次运行的控制器。</summary>
        private readonly TaskRun _run;
        /// <summary>本次运行驱动的设备端口；可以是内存设备，也可以是真实控制卡设备。</summary>
        private readonly ITaskDevice _device;
        /// <summary>启动时冻结的任务库。</summary>
        private readonly Dictionary<string, TaskDefinition> _library;
        /// <summary>可选受控工位；独立单次执行时为空，保留原来的失败终态行为。</summary>
        private readonly StationExecutionControl _station;
        /// <summary>可选设备报警服务；受控整机任务使用正式或会话级同一报警源。</summary>
        private readonly IMachineAlarmService _alarmService;

        /// <summary>保存本次运行依赖，不读取全局服务。</summary>
        internal TaskNodeExecutor(TaskRun run, ITaskDevice device, Dictionary<string, TaskDefinition> library,
            StationExecutionControl station = null, IMachineAlarmService alarmService = null)
        {
            _run = run; _device = device; _library = library; _station = station; _alarmService = alarmService;
        }

        /// <summary>执行一个节点并统一记录结果；只在动作收尾后标记取消或失败。</summary>
        internal async Task ExecuteAsync(string taskId, TaskNode node, TaskExecutionScope parent, CancellationToken token)
        {
            // 每个节点让出一次执行权，避免大量即时步骤或多层子任务递归耗尽线程调用栈。
            await Task.Yield();
            var scope = new TaskExecutionScope(_run, taskId, node, parent, token);
            bool registered = false;
            bool bodyCompleted = false;
            while (true)
            {
                try
                {
                    await scope.StepAsync(() =>
                    {
                        _run.Report(scope, TaskNodeState.Running, "正在执行：" + node.Name);
                        registered = true;
                    }).ConfigureAwait(false);
                    if (!bodyCompleted)
                    {
                        await ExecuteBodyAsync(scope).ConfigureAwait(false);
                        bodyCompleted = true;
                    }
                    await scope.StepAsync(() => _run.Report(scope, TaskNodeState.Completed, "执行完成。")).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    if (registered) _run.Report(scope, TaskNodeState.Cancelled, "已取消，节点清理完成。");
                    scope.CompleteFailureHandoff();
                    throw;
                }
                catch (Exception error)
                {
                    TaskExecutionException failure = error as TaskExecutionException ?? scope.Error(error.Message, error);
                    // 父级时限可能在新子节点首次登记前到期；保留这个子节点，不回到父级重做前面的动作。
                    if (!registered && _station != null && !_run.ExpansionLimitHit)
                    {
                        registered = true;
                    }
                    if (registered) _run.Report(scope, TaskNodeState.Failed, failure.Message);
                    if (_station == null)
                    {
                        scope.CompleteFailureHandoff();
                        throw failure;
                    }
                    if (!registered || _run.ExpansionLimitHit)
                    {
                        // 规模上限等不可恢复故障必须先锁存工位，再放行可能并发的暂停屏障。
                        Task failedStop;
                        try { failedStop = _station.Machine.ReportWorkerFailureAsync(_station, failure); }
                        finally { scope.CompleteFailureHandoff(); }
                        await failedStop.ConfigureAwait(false);
                        throw failure;
                    }
                    Task recovery;
                    try { recovery = _station.RecoverAsync(scope, failure); }
                    finally { scope.CompleteFailureHandoff(); }
                    await recovery.ConfigureAwait(false);
                    _station.Machine.MarkNodeRecovered(_station);
                }
            }
        }

        /// <summary>普通类型分支直接调用动作方法，未知类型明确失败。</summary>
        private async Task ExecuteBodyAsync(TaskExecutionScope scope)
        {
            TaskNode node = scope.Node;
            if (node is SequenceNode sequence)
            {
                foreach (TaskNode child in sequence.Children)
                    await ExecuteAsync(scope.TaskId, child, scope, scope.Token).ConfigureAwait(false);
            }
            else if (node is ParallelGroupNode parallel) await ParallelAsync(scope, parallel).ConfigureAwait(false);
            else if (node is ConditionBranchNode branch) await BranchAsync(scope, branch).ConfigureAwait(false);
            else if (node is ResourceScopeNode resources) await ResourceScopeAsync(scope, resources).ConfigureAwait(false);
            else if (node is CallTaskNode call)
                await ExecuteAsync(call.TaskId, _library[call.TaskId].Root, scope, scope.Token).ConfigureAwait(false);
            else if (node is DelayNode delay) await DelayAsync(scope, delay.Milliseconds).ConfigureAwait(false);
            else if (node is WaitInputNode input) await WaitInputAsync(scope, input).ConfigureAwait(false);
            else if (node is WaitOutputNode outputWait) await WaitOutputAsync(scope, outputWait).ConfigureAwait(false);
            else if (node is WaitExclusiveInputsNode pair) await WaitExclusiveInputsAsync(scope, pair).ConfigureAwait(false);
            else if (node is ReadInputNode sample)
                await scope.StepAsync(() =>
                {
                    bool? value = _device.ReadInput(sample.InputName);
                    if (!value.HasValue) throw new InvalidOperationException("逻辑输入 '" + sample.InputName + "' 当前没有有效数据，未写入变量 '" + sample.VariableName + "'。");
                    _device.WriteVariable(sample.VariableName, value.Value);
                    scope.Diagnostics = new TaskStepDiagnostics("本次读取逻辑输入并写入设备变量", new[] {
                        new TaskObservedValue(sample.InputName, "有效采样", value.Value.ToString(), true),
                        new TaskObservedValue(sample.VariableName, "本次采样值", value.Value.ToString(), true) });
                }).ConfigureAwait(false);
            else if (node is WaitConditionNode condition) await WaitConditionAsync(scope, condition).ConfigureAwait(false);
            else if (node is SetOutputNode output)
                await SetOutputAsync(scope, output).ConfigureAwait(false);
            else if (node is SetVariableNode assignment)
                await scope.StepAsync(() =>
                {
                    object value = ReadRequired(assignment.Value);
                    _device.WriteVariable(assignment.VariableName, value);
                    string text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
                    scope.Diagnostics = new TaskStepDiagnostics("本次写入设备变量", new[] {
                        new TaskObservedValue(assignment.VariableName, TaskRuntimeValues.Describe(assignment.Value), text, true) });
                }).ConfigureAwait(false);
            else if (node is MoveAxisNode move) await MoveAsync(scope, move).ConfigureAwait(false);
            else if (node is MoveAxisGroupNode group) await MoveAxisGroupAsync(scope, group).ConfigureAwait(false);
            else if (node is AxisCommandNode command) await AxisCommandAsync(scope, command).ConfigureAwait(false);
            else throw new NotSupportedException("没有执行实现：" + node.Kind);
        }

        /// <summary>动作和分支必须有确定的输入；缺少值时保留具体变量名称。</summary>
        private object ReadRequired(TaskValueBinding binding)
        {
            object value;
            string reason;
            if (!TaskRuntimeValues.TryRead(binding, _device.Catalog, _device.ReadVariables(), out value, out reason))
                throw new InvalidOperationException(reason);
            return value;
        }
    }
}
