using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.TaskEngine.Configuration;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Device;
using MotionControl.TaskEngine.Validation;
using MotionControl.TaskEngine.Machine;

namespace MotionControl.TaskEngine.Execution
{
    /// <summary>任务执行入口；通过设备接口执行已校验的任务，设备实现负责实际动作与不支持能力的报错。</summary>
    public sealed class TaskExecutionEngine
    {
        /// <summary>先校验并冻结定义，再后台执行；返回值可暂停、恢复、取消和读取诊断。</summary>
        public TaskRun Start(TaskDefinition definition, ITaskDevice device,
            IEnumerable<TaskDefinition> dependencies = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            return StartCore(definition, device, dependencies, null, cancellationToken, null, null, false);
        }

        /// <summary>先冻结准备任务并登记设备使用，等待整机确认报警门禁后才允许运行。</summary>
        internal TaskRun StartPreparation(TaskDefinition definition, ITaskDevice device,
            IEnumerable<TaskDefinition> dependencies, object owner, CancellationToken cancellationToken)
        {
            return StartCore(definition, device, dependencies, null, cancellationToken, null, owner, true);
        }

        /// <summary>整机控制器专用入口，先暂停再发布，确保全机启动许可生效前不接受动作。</summary>
        internal TaskRun StartManaged(TaskDefinition definition, ITaskDevice device,
            IEnumerable<TaskDefinition> dependencies, StationExecutionControl station, CancellationToken token,
            IMachineAlarmService alarmService)
        {
            return StartCore(definition, device, dependencies, station, token, alarmService, station.Machine, false);
        }

        /// <summary>统一校验和冻结；设备所有权登记成功后才安排后台执行。</summary>
        private TaskRun StartCore(TaskDefinition definition, ITaskDevice device,
            IEnumerable<TaskDefinition> dependencies, StationExecutionControl station, CancellationToken cancellationToken,
            IMachineAlarmService alarmService, object owner, bool startPaused)
        {
            if (device == null) throw new ArgumentNullException(nameof(device));
            var dependencyList = new List<TaskDefinition>();
            if (dependencies != null)
                foreach (TaskDefinition dependency in dependencies)
                {
                    if (dependencyList.Count >= 127) throw new ArgumentException("任务库最多 128 份定义。");
                    dependencyList.Add(dependency);
                }
            RequireValid(definition, device, dependencyList);
            TaskDefinition frozen = Copy(definition);
            var frozenDependencies = dependencyList.Select(Copy).ToList();
            RequireValid(frozen, device, frozenDependencies);
            var library = new Dictionary<string, TaskDefinition>(StringComparer.OrdinalIgnoreCase) { { frozen.TaskId, frozen } };
            foreach (TaskDefinition task in frozenDependencies) library.Add(task.TaskId, task);
            foreach (TaskDefinition task in library.Values) RequireSupported(task.TaskId, task.Root, device);

            var run = new TaskRun(frozen.TaskId, frozen.Revision);
            try { device.RegisterRun(run.RunId, owner); }
            catch { run.Cancel(); run.Finish(null); throw; }
            if (station != null || startPaused) run.Pause();
            // 不把取消令牌传给 Task.Run：即使立即取消，也必须进入后台的统一收尾路径。
            Task.Run(() => ExecuteAsync(run, frozen, device, library, station, cancellationToken, alarmService));
            return run;
        }

        /// <summary>等待执行器和全部分支退出，最后只发布一次终态。</summary>
        private static async Task ExecuteAsync(TaskRun run, TaskDefinition definition, ITaskDevice device,
            Dictionary<string, TaskDefinition> library, StationExecutionControl station,
            CancellationToken externalCancellation, IMachineAlarmService alarmService)
        {
            TaskExecutionException failure = null;
            try
            {
                using (externalCancellation.Register(() => run.Cancel()))
                {
                    var executor = new TaskNodeExecutor(run, device, library, station, alarmService);
                    await executor.ExecuteAsync(definition.TaskId, definition.Root, null, run.Cancellation).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (run.Cancellation.IsCancellationRequested)
            {
                // 外部取消作为独立终态记录；节点执行器已完成各分支和轴的清理。
            }
            catch (TaskExecutionException error) { failure = error; }
            catch (Exception error)
            {
                failure = new TaskExecutionException(definition.TaskId, definition.Root.Id, null, error.Message, error);
            }
            finally { device.UnregisterRun(run.RunId); run.Finish(failure); }
        }

        /// <summary>复用阶段 1 的完整定义校验，任何错误都在动作开始前拒绝。</summary>
        private static void RequireValid(TaskDefinition definition, ITaskDevice device, List<TaskDefinition> dependencies)
        {
            var errors = new TaskDefinitionValidator().Validate(definition, device.Catalog, dependencies)
                .Where(issue => issue.Severity == TaskIssueSeverity.Error).ToList();
            if (errors.Count != 0) throw new ArgumentException(string.Join(Environment.NewLine, errors.Select(issue => issue.ToString())));
        }

        /// <summary>使用严格 JSON 创建独立定义，界面后续修改不会改变运行中的步骤。</summary>
        private static TaskDefinition Copy(TaskDefinition definition)
        {
            return TaskDefinitionJson.Deserialize(TaskDefinitionJson.Serialize(definition));
        }

        /// <summary>检查所有分支而非只检查本次选中路径；未实现能力不能静默跳过。</summary>
        internal static void RequireSupported(string taskId, TaskNode node, ITaskDevice device)
        {
            if (node is CoordinatedMoveNode)
                throw new NotSupportedException("任务 " + taskId + " / 节点 " + node.Id + "：当前尚不执行 " + node.Kind + "，请等待后续能力接入。");
            if (node is SequenceNode sequence)
                foreach (TaskNode child in sequence.Children) RequireSupported(taskId, child, device);
            else if (node is ParallelGroupNode parallel)
                foreach (TaskNode branch in parallel.Branches) RequireSupported(taskId, branch, device);
            else if (node is ConditionBranchNode condition)
            {
                RequireSupported(taskId, condition.WhenTrue, device);
                if (condition.WhenFalse != null) RequireSupported(taskId, condition.WhenFalse, device);
            }
            else if (node is ResourceScopeNode scope)
            {
                if (!SupportsInterlockConditions(device) && (scope.EntryInterlock != null || scope.ExitInterlock != null || scope.ExitCondition != null))
                    throw new NotSupportedException("任务 " + taskId + " / 节点 " + node.Id + "：设备暂不支持配置进入或退出互锁，不能运行。");
                RequireSupported(taskId, scope.Body, device);
            }
        }

        /// <summary>旧设备没有声明可选能力时按不支持处理，不要求修改原设备接口。</summary>
        internal static bool SupportsInterlockConditions(object device)
        {
            return device is ITaskInterlockSupport capability && capability.SupportsInterlockConditions;
        }
    }
}
