using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Execution;
using MotionControl.TaskEngine.Device.InMemory;

namespace MotionControl.TaskEngine.Checks
{
    /// <summary>验证数据引用、类型比较和未知数据处理。</summary>
    internal static partial class ExecutionChecks
    {
        /// <summary>赋值先于运动；示教与变量目标都来自共享目录。</summary>
        private static async Task SequenceAndTargets()
        {
            var device = Device();
            var teach = TaskFixtures.Move("teach");
            teach.Target = new TaskValueBinding { Source = TaskValueSource.TeachPoint, AxisName = "X", PointId = "P001" };
            teach.Profile.Speed = 100;
            var dynamicMove = TaskFixtures.Move("dynamic", "Y");
            dynamicMove.Target = Variable("Vision.OffsetX");
            dynamicMove.Profile.Speed = 100;
            TaskRun run = Start(TaskFixtures.Task(Assign("value", "Vision.OffsetX", TaskFixtures.Number("12.5")),
                teach, dynamicMove, Output()), device);
            Success(await Complete(run));
            CheckAssert.True(device.ReadAxis("X").Position == 10 && device.ReadAxis("Y").Position == 12.5, "目标没有正确引用。");
            CheckAssert.True(device.ReadOutput("Vacuum") == true && !device.ReadAxis("X").IsBusy, "顺序末尾没有完成或轴仍占用。");
        }

        /// <summary>业务 NG 选择 false 分支，All/Any/Not 组合与六种比较保持明确语义。</summary>
        private static async Task BranchConditions()
        {
            var device = Device();
            device.SetVariable("Vision.Result", "NG");
            var branch = new ConditionBranchNode { Id = "choose", Name = "检查检测结果",
                Condition = Compare(Variable("Vision.Result"), Constant(DeviceVariableType.String, "OK"), TaskComparison.Equal),
                WhenTrue = Sequence("yes", Output("yesOutput", true)),
                WhenFalse = Sequence("no", Output("noOutput", false)) };
            Success(await Complete(Start(TaskFixtures.Task(branch), device)));
            CheckAssert.True(device.ReadOutput("Vacuum") == false, "NG 未走业务分支。");

            var comparisons = new List<TaskCondition>();
            foreach (TaskComparison comparison in Enum.GetValues(typeof(TaskComparison)))
            {
                string right = comparison == TaskComparison.Equal || comparison == TaskComparison.LessOrEqual
                    || comparison == TaskComparison.GreaterOrEqual ? "2" : comparison == TaskComparison.Less ? "3" : "1";
                comparisons.Add(Compare(Constant(DeviceVariableType.UInt32, "2"), TaskFixtures.Number(right), comparison));
            }
            var combination = new TaskCondition { Kind = TaskConditionKind.All, Conditions = comparisons };
            combination.Conditions.Add(new TaskCondition { Kind = TaskConditionKind.Not,
                Conditions = new List<TaskCondition> { Compare(TaskFixtures.Number("1"), TaskFixtures.Number("2"), TaskComparison.Equal) } });
            combination.Conditions.Add(new TaskCondition { Kind = TaskConditionKind.Any,
                Conditions = new List<TaskCondition> {
                    Compare(TaskFixtures.Number("1"), TaskFixtures.Number("2"), TaskComparison.Equal),
                    Compare(TaskFixtures.Number("2"), TaskFixtures.Number("2"), TaskComparison.Equal) } });
            branch.Condition = combination;
            Success(await Complete(Start(TaskFixtures.Task(branch), device)));
            CheckAssert.True(device.ReadOutput("Vacuum") == true, "组合条件结果错误。");
        }

        /// <summary>八种基础类型都通过已有变量实现赋值和读取，日期仍使用往返格式。</summary>
        private static async Task AllValueTypes()
        {
            var values = new Dictionary<DeviceVariableType, string> {
                { DeviceVariableType.Boolean, "true" }, { DeviceVariableType.Integer, "-12" },
                { DeviceVariableType.Number, "1.25" }, { DeviceVariableType.String, "NG" },
                { DeviceVariableType.Byte, "255" }, { DeviceVariableType.Character, "中" },
                { DeviceVariableType.DateTime, "2026-10-23T10:30:00.0000000Z" }, { DeviceVariableType.UInt32, "4294967295" }
            };
            var catalog = new TaskResourceCatalog(new TaskAxisInfo[0], new string[0], new[] { "Vacuum" },
                values.ToDictionary(item => item.Key.ToString(), item => item.Key), new TaskTeachPoint[0],
                alarmCodes: new[] { "DEMO_SENSOR_TIMEOUT" });
            var device = new InMemoryTaskDevice(catalog);
            var nodes = new List<TaskNode>();
            foreach (var item in values)
            {
                nodes.Add(Assign("set" + item.Key, item.Key.ToString(), Constant(item.Key, item.Value)));
                nodes.Add(new WaitConditionNode { Id = "read" + item.Key, Name = "检查类型值",
                    Condition = Compare(Variable(item.Key.ToString()), Constant(item.Key, item.Value), TaskComparison.Equal),
                    TimeoutAlarmCodes = new List<string> { "DEMO_SENSOR_TIMEOUT" } });
            }
            nodes.Add(new WaitConditionNode { Id = "date", Name = "比较日期",
                Condition = Compare(Variable("DateTime"), Constant(DeviceVariableType.DateTime, "2026-10-24T00:00:00.0000000Z"), TaskComparison.Less),
                TimeoutAlarmCodes = new List<string> { "DEMO_SENSOR_TIMEOUT" } });
            nodes.Add(new WaitConditionNode { Id = "dateZone", Name = "同一时刻不同写法",
                Condition = Compare(Variable("DateTime"), Constant(DeviceVariableType.DateTime, "2026-10-23T18:30:00.0000000+08:00"), TaskComparison.Equal),
                TimeoutAlarmCodes = new List<string> { "DEMO_SENSOR_TIMEOUT" } });
            nodes.Add(Assign("convert", "Number", Variable("UInt32")));
            Success(await Complete(Start(TaskFixtures.Task(nodes.ToArray()), device)));
            CheckAssert.True(Value(device, "Number") == "4294967295", "uint 到 double 转换损失范围。");
            CheckAssert.True(device.ReadVariables().All(value => value.HasValue), "存在未赋值类型。");
            var ambiguous = new WaitConditionNode { Id = "ambiguous", Name = "拒绝混用时区",
                Condition = Compare(Variable("DateTime"), Constant(DeviceVariableType.DateTime, "2026-10-23T10:30:00.0000000"), TaskComparison.Equal),
                TimeoutAlarmCodes = new List<string> { "DEMO_SENSOR_TIMEOUT" } };
            Failure(await Complete(Start(TaskFixtures.Task(ambiguous), device)), "时区信息不一致");
        }

        /// <summary>未知 DI 不能满足 OFF，未初始化变量也不能直接满足条件。</summary>
        private static async Task UnknownValues()
        {
            var device = Device();
            var input = Input();
            input.Expected = false;
            TaskRun run = Start(TaskFixtures.Task(input, new WaitConditionNode { Id = "variableWait", Name = "等变量",
                Condition = TaskFixtures.Condition(), TimeoutAlarmCodes = new List<string> { "DEMO_SENSOR_TIMEOUT" } }, Output()), device);
            await Until(() => HasNode(run, "input", TaskNodeState.Waiting), "没有等待未知 DI。");
            CheckAssert.True(device.ReadOutput("Vacuum") == null, "未知 DI 被当作 false。");
            device.SetInput("PartReady", false);
            await Until(() => HasNode(run, "variableWait", TaskNodeState.Waiting), "没有等待无效变量。");
            CheckAssert.True(run.GetSnapshot().Nodes.Any(node => node.Detail.Contains("尚无有效值")), "没有解释变量未知原因。");
            device.SetVariable("Process.Ready", "true");
            Success(await Complete(run));
        }

        /// <summary>无法判定的分支必须失败，不会进入 false 分支。</summary>
        private static async Task InvalidBranch()
        {
            var device = Device();
            var branch = new ConditionBranchNode { Id = "choose", Name = "判定未知变量", Condition = TaskFixtures.Condition(),
                WhenTrue = Sequence("yes", Output("a")), WhenFalse = Sequence("no", Output("b")) };
            var result = await Complete(Start(TaskFixtures.Task(branch), device));
            Failure(result, "Process.Ready");
            CheckAssert.True(device.ReadOutput("Vacuum") == null && result.Error.NodeId == "choose", "未知分支执行了写值或丢失节点位置。");
        }

        /// <summary>真实采样 true、false 决定分支；未知数据不能覆盖旧变量或执行后续分支。</summary>
        private static async Task InputSampling()
        {
            var device = Device();
            var sample = new ReadInputNode { Id = "sample", Name = "读取检测输入", InputName = "PartReady", VariableName = "Process.Ready" };
            var branch = new ConditionBranchNode { Id = "choose", Name = "判定检测", Condition = TaskFixtures.Condition(),
                WhenTrue = Sequence("yes", Output("yesOutput", true)), WhenFalse = Sequence("no", Output("noOutput", false)) };
            device.SetInput("PartReady", true);
            Success(await Complete(Start(TaskFixtures.Task(sample, branch), device)));
            CheckAssert.True(bool.Parse(Value(device, "Process.Ready")) && device.ReadOutput("Vacuum") == true, "有效输入未写入或真分支错误。");
            device.SetInput("PartReady", false);
            Success(await Complete(Start(TaskFixtures.Task(sample, branch), device)));
            CheckAssert.True(!bool.Parse(Value(device, "Process.Ready")) && device.ReadOutput("Vacuum") == false, "无效电平未写入或假分支错误。");
            device.SetVariable("Process.Ready", "true");
            device.SetInput("PartReady", null);
            var result = await Complete(Start(TaskFixtures.Task(sample, branch), device));
            Failure(result, "没有有效数据");
            CheckAssert.True(bool.Parse(Value(device, "Process.Ready")) && result.Error.NodeId == "sample", "未知输入覆盖变量或丢失失败节点。");
            CheckAssert.True(device.ReadOutput("Vacuum") == false && !result.Nodes.Any(node => node.NodeId == "choose"), "未知输入后错误执行了分支。");
        }

        /// <summary>构造明确的类型比较。</summary>
        private static TaskCondition Compare(TaskValueBinding left, TaskValueBinding right, TaskComparison comparison)
        {
            return new TaskCondition { Kind = TaskConditionKind.Compare, Left = left, Right = right, Comparison = comparison };
        }
    }
}
