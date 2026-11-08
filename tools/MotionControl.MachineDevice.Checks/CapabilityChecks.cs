using System;
using System.Collections.Generic;
using System.Linq;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.Framework.Runtime.Alarms;
using MotionControl.Framework.Runtime.Machine;
using MotionControl.Framework.Runtime.Variables;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Device;
using MotionControl.TaskEngine.Device.InMemory;
using MotionControl.TaskEngine.Execution;
using MotionControl.TaskEngine.Machine;
using MotionControl.TaskEngine.MachineDevice;
using MotionControl.TaskEngine.Validation;

namespace MotionControl.MachineDevice.Checks
{
    /// <summary>检查“能够编辑”与“当前后端能够执行”的边界，所有动作均使用假设备。</summary>
    internal static partial class Program
    {
        /// <summary>即使输出位于不支持的组定位之前，也必须在运行登记和接管之前拒绝整个任务。</summary>
        private static void GroupCapabilityRejectedBeforeActions()
        {
            var axis = new FakeAxis("X");
            var io = new FakeLogicalIo();
            var catalog = new TaskResourceCatalog(new[] {
                new TaskAxisInfo("X", "CardA", "mm", -100, 100),
                new TaskAxisInfo("Y", "CardA", "mm", -100, 100) },
                new[] { "Sensor" }, new[] { "Vacuum" },
                new Dictionary<string, DeviceVariableType>(), new TaskTeachPoint[0]);
            // 预检必须在访问任何轴之前结束；只提供 X 轴，使漏过预检的错误也可被发现。
            var cards = new MotionCardGroup(new IMotionCard[] { new FakeMotionCard("CardA", axis) });
            var runtime = new MachineRuntime(cards, new IStationFlow[0], new MachineAlarmService(), new FakeSafetyStatus());
            var device = new MachineTaskDevice(catalog, cards, io, new DeviceVariableStore(new DeviceVariableDefinition[0]), runtime);
            var task = CapabilityTask("main", new SetOutputNode { Id = "first-output", Name = "前序输出", OutputName = "Vacuum", Value = true },
                CapabilityGroup("blocked-group"));
            var definitionIssues = new TaskDefinitionValidator().Validate(task, catalog);
            Ensure(!definitionIssues.Any(item => item.Severity == TaskIssueSeverity.Error),
                "能力检查样例本身的定义不合法：" + string.Join("\n", definitionIssues));
            ExpectMessage(() => new TaskExecutionEngine().Start(task, device), "Capability.MoveAxisGroup");
            var configuration = new TaskMachineConfiguration { MachineId = "Capability", Name = "能力准入",
                Stations = new List<StationTaskConfiguration> { new StationTaskConfiguration {
                    StationId = "S1", Name = "工位", TaskId = task.TaskId, Enabled = true, Mode = StationTaskMode.Once } } };
            ExpectMessage(() => new TaskMachineController(configuration, new[] { task }, device, runtime.AlarmService), "Capability.MoveAxisGroup");
            var child = CapabilityTask("dependent", CapabilityGroup("dependent-group"));
            var caller = CapabilityTask("caller",
                new SetOutputNode { Id = "before-call", Name = "调用前输出", OutputName = "Vacuum", Value = true },
                new CallTaskNode { Id = "call-child", Name = "调用子任务", TaskId = child.TaskId });
            ExpectMessage(() => new TaskExecutionEngine().Start(caller, device, new[] { child }), "dependent-group");
            Ensure(axis.MoveCalls == 0 && io.OutputWrites == 0 && runtime.ControlOwner == MachineControlOwner.None,
                "能力未满足时已经接管设备或执行前序动作。");
            Ensure(!TaskDeviceCapabilities.FromDevice(device).SupportsAxisGroupMove,
                "正式任务设备错误声明支持多轴分别定位。");
            Ensure(TaskDeviceCapabilities.FromDevice(new InMemoryTaskDevice(catalog)).SupportsAxisGroupMove,
                "内存设备已实现的组定位能力未被识别。");
        }

        /// <summary>遍历未选择分支、资源正文和库中子任务；普通并行单轴不能误判为组定位。</summary>
        private static void GroupCapabilityTraversesWholeLibrary()
        {
            var unsupported = MachineTaskDevice.ExecutionCapabilities;
            var nested = CapabilityTask("nested", new ParallelGroupNode { Id = "parallel", Name = "并行",
                Branches = new List<TaskNode> {
                    new SequenceNode { Id = "left", Name = "左分支", Children = new List<TaskNode> {
                        new ConditionBranchNode { Id = "condition", Name = "选择", WhenFalse = new ResourceScopeNode {
                            Id = "scope", Name = "区域", Body = CapabilityGroup("deep-group") } } } },
                    new SequenceNode { Id = "right", Name = "右分支", Children = new List<TaskNode> {
                        new MoveAxisNode { Id = "ordinary-axis", Name = "单轴", AxisName = "X" } } } } });
            var child = CapabilityTask("child", CapabilityGroup("child-group"));
            var issues = new TaskDeviceCapabilityValidator().Validate(new[] { nested, child }, unsupported);
            Ensure(issues.Count == 2 && issues.Any(item => item.TaskId == "nested" && item.NodeId == "deep-group")
                && issues.Any(item => item.TaskId == "child" && item.NodeId == "child-group")
                && issues.All(item => item.Field == "Capability.MoveAxisGroup"),
                "能力遍历遗漏未选分支、资源正文、任务库，或误判了普通单轴。");

            var normal = CapabilityTask("normal", new ParallelGroupNode { Id = "normal-parallel", Name = "并行单轴",
                Branches = new List<TaskNode> { new MoveAxisNode { Id = "x", AxisName = "X" }, new MoveAxisNode { Id = "y", AxisName = "Y" } } });
            Ensure(new TaskDeviceCapabilityValidator().Validate(new[] { normal }, unsupported).Count == 0,
                "将并行单轴误认为未实现的组定位。");

            var device = CreateDevice(new FakeAxis("X"), new FakeLogicalIo(), out _, out _);
            Ensure(TaskDeviceCapabilities.FromDevice(device).SupportsInterlockConditions == unsupported.SupportsInterlockConditions,
                "离线 Host 能力与实际设备不一致。");
        }

        /// <summary>创建两根轴的合法组定位定义，速度及目标均使用轴的工程单位。</summary>
        private static MoveAxisGroupNode CapabilityGroup(string nodeId)
        {
            var group = new MoveAxisGroupNode { Id = nodeId, Name = "多轴分别定位" };
            foreach (string axis in new[] { "X", "Y" })
            {
                group.Targets.Add(new AxisPositionTarget { AxisName = axis, Mode = TaskMoveMode.Absolute,
                    Target = new TaskValueBinding { Source = TaskValueSource.Constant, DataType = DeviceVariableType.Number, Constant = "1" },
                    Profile = new TaskMotionProfile { Speed = 10, Acceleration = 100, Deceleration = 100 } });
            }
            return group;
        }

        /// <summary>建立顺序任务，保留调用方提供的节点身份用于错误定位断言。</summary>
        private static TaskDefinition CapabilityTask(string taskId, params TaskNode[] nodes)
        {
            return new TaskDefinition { TaskId = taskId, Name = taskId,
                Root = new SequenceNode { Id = taskId + "-root", Name = "顺序", Children = nodes.ToList() } };
        }
    }
}
