using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Simulation;

namespace MotionControl.TaskEngine.Execution
{
    /// <summary>按明确的节点类型直接分发，定义不保存任何运行状态。</summary>
    internal sealed partial class TaskNodeExecutor
    {
        /// <summary>本次运行的控制器。</summary>
        private readonly TaskRun _run;
        /// <summary>只可能是内存设备，不接受硬件替身注入。</summary>
        private readonly SimulatedTaskDevice _device;
        /// <summary>启动时冻结的任务库。</summary>
        private readonly Dictionary<string, TaskDefinition> _library;

        /// <summary>保存本次运行依赖，不读取全局服务。</summary>
        internal TaskNodeExecutor(TaskRun run, SimulatedTaskDevice device, Dictionary<string, TaskDefinition> library)
        {
            _run = run; _device = device; _library = library;
        }

        /// <summary>执行一个节点并统一记录结果；只在动作收尾后标记取消或失败。</summary>
        internal async Task ExecuteAsync(string taskId, TaskNode node, TaskExecutionScope parent, CancellationToken token)
        {
            // 每个节点让出一次执行权，避免大量即时步骤或多层子任务递归耗尽线程调用栈。
            await Task.Yield();
            var scope = new TaskExecutionScope(_run, taskId, node, parent, token);
            bool registered = false;
            try
            {
                await scope.StepAsync(() =>
                {
                    _run.Report(scope, TaskNodeState.Running, "正在执行：" + node.Name);
                    registered = true;
                }).ConfigureAwait(false);
                await ExecuteBodyAsync(scope).ConfigureAwait(false);
                await scope.StepAsync(() => _run.Report(scope, TaskNodeState.Completed, "执行完成。")).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                if (registered) _run.Report(scope, TaskNodeState.Cancelled, "已取消，节点清理完成。");
                throw;
            }
            catch (Exception error)
            {
                TaskExecutionException failure = error as TaskExecutionException ?? scope.Error(error.Message, error);
                if (registered) _run.Report(scope, TaskNodeState.Failed, failure.Message);
                throw failure;
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
            else if (node is CallTaskNode call)
                await ExecuteAsync(call.TaskId, _library[call.TaskId].Root, scope, scope.Token).ConfigureAwait(false);
            else if (node is DelayNode delay) await DelayAsync(scope, delay.Milliseconds).ConfigureAwait(false);
            else if (node is WaitInputNode input) await WaitInputAsync(scope, input).ConfigureAwait(false);
            else if (node is WaitConditionNode condition) await WaitConditionAsync(scope, condition).ConfigureAwait(false);
            else if (node is SetOutputNode output)
                await scope.StepAsync(() => _device.WriteOutput(output.OutputName, output.Value)).ConfigureAwait(false);
            else if (node is SetVariableNode assignment)
                await scope.StepAsync(() => _device.WriteVariable(assignment.VariableName, ReadRequired(assignment.Value))).ConfigureAwait(false);
            else if (node is MoveAxisNode move) await MoveAsync(scope, move).ConfigureAwait(false);
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
