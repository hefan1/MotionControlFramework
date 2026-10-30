using System.Collections.Generic;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Framework.Wpf.Configuration;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Machine;
using MotionControl.TaskEngine.Simulation;

namespace MotionControl.TaskEngine.Wpf.Simulation
{
    /// <summary>界面演示的明确内存工艺，覆盖多轴、并行、分支、子任务与资源等待。</summary>
    public static class TaskMonitorExample
    {
        /// <summary>创建三个独立循环与一个停用工位，所有数据都与现场配置隔离。</summary>
        public static TaskMonitorSession Create()
        {
            var catalog = new TaskResourceCatalog(new[] {
                new TaskAxisInfo("搬运X", "Memory", "mm", -500, 500),
                new TaskAxisInfo("搬运Y", "Memory", "mm", -500, 500),
                new TaskAxisInfo("检测Z", "Memory", "mm", -100, 100) },
                new[] { "产品到位", "上料机构安全位", "夹紧到位", "启动按钮", "停止按钮", "复位按钮" },
                new[] { "夹紧输出", "相机触发" },
                new Dictionary<string, DeviceVariableType> { { "Vision.OK", DeviceVariableType.Boolean },
                    { "Product.Result", DeviceVariableType.String } },
                new[] { new TaskTeachPoint("搬运X", "place", "放料位置", 120) }, new[] { "拍照区域" },
                alarmDefinitions: new[] { new TaskAlarmInfo("DEMO_SENSOR_TIMEOUT", "等待传感器超时",
                    MachineAlarmKind.StopMachine, "检查传感器与 DI 映射。", "输入条件恢复后执行整机复位。") });
            var device = new SimulatedTaskDevice(catalog);
            foreach (string axis in catalog.Axes.Keys) device.ConfigureAxis(axis, 0, true, true);
            device.SetInput("产品到位", true); device.SetInput("上料机构安全位", true); device.SetInput("夹紧到位", false);
            device.SetInput("启动按钮", false); device.SetInput("停止按钮", false); device.SetInput("复位按钮", false);
            device.SetVariable("Vision.OK", "true");
            var place = new MoveAxisGroupNode { Id = "place", Name = "XY 移动到放料位置", Targets = new List<AxisPositionTarget> {
                new AxisPositionTarget { AxisName = "搬运X", Mode = TaskMoveMode.Absolute,
                    Target = new TaskValueBinding { Source = TaskValueSource.TeachPoint, AxisName = "搬运X", PointId = "place" }, Profile = Profile(100) },
                new AxisPositionTarget { AxisName = "搬运Y", Mode = TaskMoveMode.Absolute, Target = Number("80"), Profile = Profile(80) } } };
            var returnHome = new MoveAxisGroupNode { Id = "returnHome", Name = "XY 一起退回安全位置", Targets = new List<AxisPositionTarget> {
                new AxisPositionTarget { AxisName = "搬运X", Mode = TaskMoveMode.Absolute, Target = Number("0"), Profile = Profile(100) },
                new AxisPositionTarget { AxisName = "搬运Y", Mode = TaskMoveMode.Absolute, Target = Number("0"), Profile = Profile(80) } } };
            var transfer = Definition("Transfer", "搬运任务",
                new ResourceScopeNode { Id = "area", Name = "申请拍照区域", Resources = new List<string> { "拍照区域", "Axis:搬运X", "Axis:搬运Y" },
                    Body = Sequence("transferBody", "区域内搬运", place, Delay("hold", "保持放料位置", 2000), returnHome) });
            var load = Definition("Load", "上料任务",
                new ParallelGroupNode { Id = "loadReady", Name = "等待产品与机构同时就绪", Branches = new List<TaskNode> {
                    Sequence("productReadyBranch", "等待产品", new WaitInputNode { Id = "product", Name = "等待产品到位", InputName = "产品到位", Expected = true,
                        TimeoutAlarmCodes = new List<string> { "DEMO_SENSOR_TIMEOUT" } }),
                    Sequence("feederReadyBranch", "等待机构", new WaitInputNode { Id = "feeder", Name = "等待上料机构安全位", InputName = "上料机构安全位", Expected = true,
                        TimeoutAlarmCodes = new List<string> { "DEMO_SENSOR_TIMEOUT" } }) } },
                Delay("load", "上料准备", 3000));
            var camera = Definition("Camera", "拍照子任务", new SetOutputNode { Id = "on", Name = "触发相机", OutputName = "相机触发", Value = true },
                Delay("exposure", "等待曝光", 1500), new SetOutputNode { Id = "off", Name = "关闭相机触发", OutputName = "相机触发", Value = false });
            var inspect = Definition("Inspect", "检测任务",
                new SetOutputNode { Id = "clamp", Name = "夹紧产品", OutputName = "夹紧输出", Value = true },
                new WaitInputNode { Id = "clamped", Name = "等待夹紧到位", InputName = "夹紧到位", Expected = true, TimeoutMs = 20000,
                    TimeoutAlarmCodes = new List<string> { "DEMO_SENSOR_TIMEOUT" } },
                new ResourceScopeNode { Id = "cameraArea", Name = "等待拍照区域", Resources = new List<string> { "拍照区域", "Axis:检测Z", "Output:相机触发" },
                    Body = Sequence("inspectBody", "检测过程", Move("z10", "检测Z", 10),
                        new CallTaskNode { Id = "photo", Name = "执行拍照子任务", TaskId = "Camera" }, Move("z0", "检测Z", 0)) },
                new ConditionBranchNode { Id = "judge", Name = "判断检测结果", Condition = new TaskCondition { Kind = TaskConditionKind.Compare,
                    Left = new TaskValueBinding { Source = TaskValueSource.Variable, VariableName = "Vision.OK" }, Comparison = TaskComparison.Equal,
                    Right = new TaskValueBinding { Source = TaskValueSource.Constant, DataType = DeviceVariableType.Boolean, Constant = "true" } },
                    WhenTrue = Sequence("ok", "合格分支", Result("okResult", "OK")), WhenFalse = Sequence("ng", "不合格分支", Result("ngResult", "NG")) },
                new SetOutputNode { Id = "unclamp", Name = "释放夹紧", OutputName = "夹紧输出", Value = false }, Delay("interval", "等待下次检测", 2000));
            var config = new TaskMachineConfiguration { MachineId = "TaskUiExample", Name = "任务页面内存示例",
                NamedResources = new List<string> { "拍照区域" }, Stations = new List<StationTaskConfiguration> {
                Station("load", load), Station("transfer", transfer), Station("inspect", inspect),
                new StationTaskConfiguration { StationId = "disabled", Name = "备用工位", TaskId = "Load", Enabled = false, Mode = StationTaskMode.Once } } };
            var controlInputs = new SafetyInterlockConfigurationDocument
            {
                StartInputName = "启动按钮",
                PauseInputName = "停止按钮",
                ResetInputName = "复位按钮"
            };
            return new TaskMonitorSession(config, new[] { load, transfer, inspect, camera }, device,
                "内置示例（未读取现场映射）", true, controlInputs);
        }

        /// <summary>示例工位以独立循环方式执行。</summary>
        private static StationTaskConfiguration Station(string id, TaskDefinition definition)
        { return new StationTaskConfiguration { StationId = id, Name = definition.Name, TaskId = definition.TaskId, Mode = StationTaskMode.Cyclic, CycleIntervalMs = 500 }; }
        /// <summary>创建一份有稳定编号的顺序任务。</summary>
        private static TaskDefinition Definition(string id, string name, params TaskNode[] children)
        { return new TaskDefinition { TaskId = id, Name = name, Root = Sequence(id + "Root", name, children) }; }
        /// <summary>创建顺序段，较长时限允许现场查看等待详情。</summary>
        private static SequenceNode Sequence(string id, string name, params TaskNode[] children)
        { return new SequenceNode { Id = id, Name = name, TimeoutMs = 120000, Children = new List<TaskNode>(children) }; }
        /// <summary>创建明确的毫秒延时。</summary>
        private static DelayNode Delay(string id, string name, int ms) { return new DelayNode { Id = id, Name = name, Milliseconds = ms }; }
        /// <summary>创建已声明为 double 的固定目标。</summary>
        private static TaskValueBinding Number(string value) { return new TaskValueBinding { Source = TaskValueSource.Constant, DataType = DeviceVariableType.Number, Constant = value }; }
        /// <summary>示例参数使用工程单位。</summary>
        private static TaskMotionProfile Profile(double speed) { return new TaskMotionProfile { Speed = speed, Acceleration = 100, Deceleration = 100 }; }
        /// <summary>创建单轴绝对定位。</summary>
        private static MoveAxisNode Move(string id, string axis, double target)
        { return new MoveAxisNode { Id = id, Name = axis + " 定位", AxisName = axis, Mode = TaskMoveMode.Absolute,
            Target = Number(target.ToString(System.Globalization.CultureInfo.InvariantCulture)), Profile = Profile(20) }; }
        /// <summary>把本次检测结果保存到内存变量。</summary>
        private static SetVariableNode Result(string id, string result)
        { return new SetVariableNode { Id = id, Name = "记录 " + result, VariableName = "Product.Result",
            Value = new TaskValueBinding { Source = TaskValueSource.Constant, DataType = DeviceVariableType.String, Constant = result } }; }
    }
}
