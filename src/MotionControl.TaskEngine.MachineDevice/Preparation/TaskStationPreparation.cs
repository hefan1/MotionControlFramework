using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Preparation;
using MotionControl.TaskEngine.Device;
using MotionControl.TaskEngine.Execution;
using MotionControl.TaskEngine.Machine;
using MotionControl.TaskEngine.Preparation;

namespace MotionControl.TaskEngine.MachineDevice.Preparation
{
    /// <summary>把一次任务准备适配为框架通用工位准备；只使用现有会话设备，不创建第二控制器。</summary>
    public sealed class TaskStationPreparation : IStationPreparation
    {
        /// <summary>已冻结的工位准备定义。</summary>
        private readonly TaskStationPreparationDefinition _definition;
        /// <summary>拥有本设备命令权的原任务会话。</summary>
        private readonly TaskMachineController _machine;
        /// <summary>原任务会话共用的轴、IO 和变量端口。</summary>
        private readonly ITaskDevice _device;

        /// <summary>保存配置并登记生产前必须完成的准备，不发运动或写变量。</summary>
        internal TaskStationPreparation(TaskStationPreparationDefinition definition, TaskMachineController machine, ITaskDevice device)
        {
            _definition = definition; _machine = machine; _device = device;
            _machine.RegisterPreparationTask(definition.TaskId, ValidateReady);
        }

        /// <summary>稳定工位标识，用于依赖和状态显示。</summary>
        public string StationId => _definition.Id;
        /// <summary>向操作员显示的工位名称。</summary>
        public string StationName => _definition.Name;
        /// <summary>当前工位之前必须准备好的工位标识。</summary>
        public IReadOnlyCollection<string> Dependencies => _definition.Dependencies.AsReadOnly();

        /// <summary>开始前核对会话未生产、无在制品且关联轴停稳；不把未知信号当作安全。</summary>
        public Task ValidateStartAsync(CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                _machine.ValidatePreparationAllowed();
                ValidateEmpty();
                foreach (TaskPreparationAxis axis in _definition.Axes) ValidateAxis(axis, false);
                ValidateSignals(_definition.Outputs, false);
            }, cancellationToken);
        }

        /// <summary>执行已校验准备任务并转发实际活动节点；只完成准备，不进入生产。</summary>
        public async Task ExecuteAsync(IProgress<StationPreparationProgress> progress, CancellationToken cancellationToken)
        {
            await ValidateStartAsync(cancellationToken).ConfigureAwait(false);
            await _machine.ExecutePreparationTaskAsync(_definition.TaskId, snapshot =>
            {
                var active = snapshot.Nodes.Where(item => item.State == TaskNodeState.Running
                    || item.State == TaskNodeState.Waiting || item.State == TaskNodeState.Moving
                    || item.State == TaskNodeState.Failed).ToArray();
                var leaves = active.Where(item => !active.Any(child => child.ParentExecutionId == item.ExecutionId)).ToArray();
                if (leaves.Length > 0)
                    progress?.Report(new StationPreparationProgress(string.Join(" / ", leaves.Select(item => item.Name)),
                        string.Join("；", leaves.Select(item => item.Detail).Where(detail => !string.IsNullOrWhiteSpace(detail)))));
                else if (snapshot.State == TaskRunState.Completed)
                    progress?.Report(new StationPreparationProgress("准备任务完成", "正在核对工位待机状态。"));
            }, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>读取轴坐标、待机位、输入输出及占料条件；任务执行完成不直接等同准备通过。</summary>
        public Task ValidateReadyAsync(CancellationToken cancellationToken)
        {
            return Task.Run(() => { cancellationToken.ThrowIfCancellationRequested(); ValidateReady(); }, cancellationToken);
        }

        /// <summary>生产开始前也使用同一套验收条件，防止绕过准备页面直接启动。</summary>
        private void ValidateReady()
        {
            ValidateEmpty();
            foreach (TaskPreparationAxis axis in _definition.Axes) ValidateAxis(axis, true);
            ValidateSignals(_definition.Inputs, true);
            ValidateSignals(_definition.Outputs, false);
        }

        /// <summary>核对停稳与驱动状态；完成检查还要求有效坐标与配置待机位。</summary>
        private void ValidateAxis(TaskPreparationAxis definition, bool requireReady)
        {
            TaskAxisState axis = _device.ReadAxis(definition.Name);
            if (!axis.Enabled)
                throw new InvalidOperationException(StationName + "：轴 " + definition.Name + " 尚未使能。请先结束任务会话，在轴手动页面使能后重新读取任务。");
            if (axis.IsBusy || !string.IsNullOrEmpty(axis.Alarm))
                throw new InvalidOperationException(StationName + "：轴 " + definition.Name + " 必须已使能、停稳且无报警。" + axis.Alarm);
            if (requireReady && (!axis.Homed || double.IsNaN(axis.Position) || double.IsInfinity(axis.Position)
                || Math.Abs(axis.Position - definition.Position.Value) > definition.Tolerance.Value))
                throw new InvalidOperationException(StationName + "：轴 " + definition.Name + " 尚未回零或未到待机位置。");
        }

        /// <summary>关联输入输出必须有有效采样且符合配置，错误中包含可定位逻辑名称。</summary>
        private void ValidateSignals(IEnumerable<TaskPreparationSignal> signals, bool input)
        {
            foreach (TaskPreparationSignal signal in signals)
            {
                bool? current = input ? _device.ReadInput(signal.Name) : _device.ReadOutput(signal.Name);
                if (!current.HasValue || current.Value != signal.Expected.Value)
                    throw new InvalidOperationException(StationName + "：" + signal.Name + " 应为" + (signal.Expected.Value ? "有效" : "无效")
                        + "，当前" + (!current.HasValue ? "未知" : current.Value ? "有效" : "无效") + "。");
            }
        }

        /// <summary>只读取占料记录，要求明确无料；禁止回零流程清除生产载具或产品记录。</summary>
        private void ValidateEmpty()
        {
            var variables = _device.ReadVariables();
            foreach (string name in _definition.EmptyVariables)
            {
                var value = variables.SingleOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
                bool occupied;
                if (value == null || !value.HasValue || !bool.TryParse(value.CurrentValue, out occupied) || occupied)
                    throw new InvalidOperationException(StationName + "：占料状态 " + name + " 未确认无料，请按设备工艺处理在制品后再准备。");
            }
        }
    }
}
