using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Device.InMemory;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Execution;
using MotionControl.TaskEngine.Machine;
using MotionControl.TaskEngine.Resources;

namespace MotionControl.TaskEngine.Checks
{
    /// <summary>故障重进当前节点时保留固定终点和资源范围阶段。</summary>
    internal static partial class MachineChecks
    {
        /// <summary>输出等待超时后，复位只重采样当前输出，不跳过失败步骤。</summary>
        private static async Task OutputWaitTimeoutRecovery()
        {
            var device = Device(); device.WriteOutput("Vacuum", true);
            var node = new WaitOutputNode { Id = "wait-do", Name = "等待输出关闭", OutputName = "Vacuum", Expected = false,
                TimeoutMs = 150, TimeoutAlarmCodes = new List<string> { "DEMO_SENSOR_TIMEOUT" } };
            var machine = Machine(device, new[] { TaskDefinition("Wait", node) }, Station("wait", "Wait", StationTaskMode.Once));
            machine.Reset(); machine.Start();
            await Until(() => machine.GetSnapshot().State == TaskMachineState.Faulted, "输出读回未触发超时故障。");
            CheckAssert.True(machine.AlarmService.Alarms.Any(alarm => alarm.Definition.Code == "DEMO_SENSOR_TIMEOUT"), "输出等待没有上报关联报警。");
            string execution = State(machine, "wait").Run.Nodes.Single(item => item.NodeId == "wait-do").ExecutionId;
            device.WriteOutput("Vacuum", false); machine.Reset(); machine.Start();
            await Until(() => State(machine, "wait").CompletedCycles == 1, "复位后未重新读回输出。");
            CheckAssert.True(State(machine, "wait").Run.Nodes.Single(item => item.NodeId == "wait-do").ExecutionId == execution, "输出等待恢复丢失步骤身份。");
            await Wait(machine.CloseAsync());
        }

        /// <summary>互斥结果超时后复位继续等待，修复输入后采样当前值，不重放已成功步骤。</summary>
        private static async Task ExclusiveInputTimeoutRecovery()
        {
            var catalog = new TaskResourceCatalog(new TaskAxisInfo[0], new[] { "OK", "NG" }, new[] { "Vacuum" },
                new Dictionary<string, DeviceVariableType> { { "OkResult", DeviceVariableType.Boolean }, { "NgResult", DeviceVariableType.Boolean } },
                new TaskTeachPoint[0], alarmDefinitions: new[] { new TaskAlarmInfo("DEMO_SENSOR_TIMEOUT", "等待结果超时", MachineAlarmKind.StopMachine) });
            var device = new InMemoryTaskDevice(catalog);
            device.SetVariable("OkResult", "false"); device.SetVariable("NgResult", "false");
            device.SetInput("OK", false); device.SetInput("NG", false);
            var node = new WaitExclusiveInputsNode { Id = "result", Name = "等待结果", TimeoutMs = 150,
                FirstInputName = "OK", SecondInputName = "NG", FirstVariableName = "OkResult", SecondVariableName = "NgResult", RequiredFirstValue = true,
                TimeoutAlarmCodes = new List<string> { "DEMO_SENSOR_TIMEOUT" } };
            var machine = Machine(device, new[] { TaskDefinition("Wait", node) }, Station("wait", "Wait", StationTaskMode.Once));
            machine.Reset(); machine.Start();
            await Until(() => machine.GetSnapshot().State == TaskMachineState.Faulted, "互斥输入未触发超时故障。");
            CheckAssert.True(machine.AlarmService.Alarms.Any(alarm => alarm.Definition.Code == "DEMO_SENSOR_TIMEOUT"), "互斥输入超时没有上报关联报警。");
            string execution = State(machine, "wait").Run.Nodes.Single(item => item.NodeId == "result").ExecutionId;
            CheckAssert.True(BooleanValue(device, "OkResult") == false && BooleanValue(device, "NgResult") == false, "无效结果覆盖了变量。");
            device.SetInput("NG", true); machine.Reset(); machine.Start();
            await Until(() => State(machine, "wait").Run.Nodes.Any(item => item.NodeId == "result" && item.State == TaskNodeState.Waiting), "复位后未等待指定方向。");
            CheckAssert.True(BooleanValue(device, "OkResult") == false && BooleanValue(device, "NgResult") == false, "复位后错误接受了相反方向。");
            device.SetInput("NG", false); device.SetInput("OK", true);
            await Until(() => State(machine, "wait").CompletedCycles == 1, "复位后未重新采样互斥输入。");
            CheckAssert.True(BooleanValue(device, "OkResult") == true && BooleanValue(device, "NgResult") == false
                && State(machine, "wait").Run.Nodes.Single(item => item.NodeId == "result").ExecutionId == execution,
                "恢复时没有保留节点身份或没有发布当前采样。");
            await Wait(machine.CloseAsync());
        }

        /// <summary>单轴或轴组运动中断后重进原绝对终点，修改距离变量也不追加相对位移。</summary>
        private static async Task RelativeTargetRecovery()
        {
            for (int scenario = 0; scenario < 2; scenario++)
            {
                var device = Device(); device.SetVariable("Vision.OffsetX", "10");
                TaskNode node;
                if (scenario == 0)
                {
                    var move = TaskFixtures.Move(); move.Mode = TaskMoveMode.Relative; move.Profile.Speed = 20;
                    move.Target = new TaskValueBinding { Source = TaskValueSource.Variable, VariableName = "Vision.OffsetX" }; node = move;
                }
                else
                    node = new MoveAxisGroupNode { Id = "group", Name = "相对轴组", Targets = new List<AxisPositionTarget> {
                        new AxisPositionTarget { AxisName = "X", Mode = TaskMoveMode.Relative, Target = TaskFixtures.Number("10"), Profile = new TaskMotionProfile { Speed = 20, Acceleration = 50, Deceleration = 50 } },
                        new AxisPositionTarget { AxisName = "Y", Mode = TaskMoveMode.Relative, Target = TaskFixtures.Number("20"), Profile = new TaskMotionProfile { Speed = 40, Acceleration = 50, Deceleration = 50 } } } };
                var machine = Machine(device, new[] { TaskDefinition("Work", node) }, Station("work", "Work", StationTaskMode.Once));
                machine.Reset(); machine.Start();
                await Until(() => device.ReadAxis("X").Position > 0.1, "相对动作尚未推进。");
                string faultAxis = scenario == 0 ? "X" : "Y";
                device.SetAxisAlarm(faultAxis, "中途注入报警");
                await Until(() => machine.GetSnapshot().State == TaskMachineState.Faulted, "相对动作报警未锁存。");
                CheckAssert.True(!device.ReadAxis("X").IsBusy && !device.ReadAxis("Y").IsBusy, "故障动作尚未收尾。");
                CheckAssert.Throws(machine.Reset, "未解除轴报警就复位。");
                device.SetAxisAlarm(faultAxis, null); device.SetVariable("Vision.OffsetX", "70");
                machine.Reset(); machine.Start();
                await Until(() => State(machine, "work").CompletedCycles == 1, "相对动作未恢复。");
                CheckAssert.True(device.ReadAxis("X").Position == 10 && device.ReadAxis("X").AcceptedCommands == 2
                    && (scenario == 0 || device.ReadAxis("Y").Position == 20), "恢复累计了第二次相对距离。");
                await Wait(machine.CloseAsync());
            }
        }

        /// <summary>超时重新检查当前条件；范围退出超时不重放已经成功的正文。</summary>
        private static async Task TimeoutAndExitRecovery()
        {
            var device = Device();
            var input = new WaitInputNode { Id = "wait", Name = "等待输入", InputName = "PartReady", Expected = true, TimeoutMs = 80,
                TimeoutAlarmCodes = new List<string> { "DEMO_SENSOR_TIMEOUT" } };
            var machine = Machine(device, new[] { TaskDefinition("Wait", input) }, Station("wait", "Wait", StationTaskMode.Once));
            machine.Reset(); machine.Start();
            await Until(() => machine.GetSnapshot().State == TaskMachineState.Faulted, "动作超时没有锁存。");
            string execution = State(machine, "wait").Run.Nodes.Single(node => node.NodeId == "wait").ExecutionId;
            device.SetInput("PartReady", true); machine.Reset(); machine.Start();
            await Until(() => State(machine, "wait").CompletedCycles == 1, "当前条件超时后不能重进。");
            CheckAssert.True(State(machine, "wait").Run.Nodes.Single(node => node.NodeId == "wait").ExecutionId == execution, "恢复丢失当前节点调用身份。");
            await Wait(machine.CloseAsync());

            device = Device();
            var move = TaskFixtures.Move(); move.Mode = TaskMoveMode.Relative; move.Target.Constant = "5"; move.Profile.Speed = 100;
            var scope = new ResourceScopeNode { Id = "scope", Name = "退出确认", TimeoutMs = 180,
                Resources = new List<string> { "Axis:X", "Zone:Camera" }, Body = Sequence("body", move),
                ExitInterlock = new TaskInterlock { Inputs = new List<TaskInputInterlock> { new TaskInputInterlock { InputName = "PartReady", Expected = true } } } };
            machine = Machine(device, new[] { TaskDefinition("Exit", scope) }, Station("exit", "Exit", StationTaskMode.Once));
            machine.Reset(); machine.Start();
            await Until(() => machine.GetSnapshot().State == TaskMachineState.Faulted, "退出条件未超时。");
            CheckAssert.True(device.ReadAxis("X").Position == 5 && device.ReadAxis("X").AcceptedCommands == 1
                && device.ReadResources().Single().State == TaskResourceState.Held, "退出故障提前让出区域。");
            device.SetInput("PartReady", true); machine.Reset(); machine.Start();
            await Until(() => State(machine, "exit").CompletedCycles == 1, "退出确认修复后未完成。");
            CheckAssert.True(device.ReadAxis("X").Position == 5 && device.ReadAxis("X").AcceptedCommands == 1
                && device.ReadResources().Count == 0, "退出恢复重新做了范围正文。");
        }

        /// <summary>完成动作前请求被撤销时锁存，恢复只发布完成，不再执行动作。</summary>
        private static async Task EarlyRequestWithdrawal()
        {
            var device = Device(); device.SetVariable("Feed.Request", "true"); device.SetVariable("Feed.Complete", "false");
            var move = TaskFixtures.Move(); move.Profile.Speed = 40;
            var station = Station("load", "Load", StationTaskMode.OnRequest);
            station.RequestVariable = "Feed.Request"; station.CompletedVariable = "Feed.Complete";
            var machine = Machine(device, new[] { TaskDefinition("Load", move) }, station);
            machine.Reset(); machine.Start();
            await Until(() => device.ReadAxis("X").IsBusy, "请求任务未执行。");
            device.SetVariable("Feed.Request", "false");
            await Until(() => machine.GetSnapshot().State == TaskMachineState.Faulted, "提前撤销请求没有锁存。");
            CheckAssert.True(State(machine, "load").CompletedCycles == 1 && BooleanValue(device, "Feed.Complete") == false, "交接故障改变了成功周期或误发完成。");
            device.SetVariable("Feed.Request", "true"); machine.Reset(); machine.Start();
            await Until(() => BooleanValue(device, "Feed.Complete") == true, "恢复未重新发布完成。");
            CheckAssert.True(device.ReadAxis("X").AcceptedCommands == 1, "交接恢复重做了产品动作。");
            device.SetVariable("Feed.Request", "false");
            await Until(() => BooleanValue(device, "Feed.Complete") == false, "完成信号没有复归。");
        }

        /// <summary>父级超时保留先前动作；调用展开超限则禁止复位重试整个父任务。</summary>
        private static async Task ParentTimeoutAndExpansionLimit()
        {
            var device = Device();
            var move = TaskFixtures.Move("done"); move.Mode = TaskMoveMode.Relative;
            move.Target.Constant = "5"; move.Profile.Speed = 1000;
            var definition = TaskDefinition("Parent", move,
                new WaitInputNode { Id = "wait", Name = "等待产品", InputName = "PartReady", Expected = true,
                    TimeoutAlarmCodes = new List<string> { "DEMO_SENSOR_TIMEOUT" } });
            definition.Root.TimeoutMs = 150;
            var machine = Machine(device, new[] { definition }, Station("work", "Parent", StationTaskMode.Once));
            machine.Reset(); machine.Start();
            await Until(() => machine.GetSnapshot().State == TaskMachineState.Faulted, "父级时限未锁存。");
            CheckAssert.True(device.ReadAxis("X").Position == 5 && device.ReadAxis("X").AcceptedCommands == 1,
                "父级超时前动作未完成。");
            device.SetInput("PartReady", true); machine.Reset(); machine.Start();
            await Until(() => State(machine, "work").CompletedCycles == 1, "父级超时后未恢复当前子步骤。");
            CheckAssert.True(device.ReadAxis("X").Position == 5 && device.ReadAxis("X").AcceptedCommands == 1,
                "父级超时恢复重放了之前成功的相对运动。");
            await Wait(machine.CloseAsync());

            var definitions = new List<TaskDefinition>();
            for (int index = 0; index < 14; index++)
            {
                var task = TaskDefinition("Expand" + index); task.Root.TimeoutMs = 60000;
                if (index < 13)
                    ((SequenceNode)task.Root).Children.AddRange(new TaskNode[] {
                        new CallTaskNode { Id = "a", Name = "第一处调用", TaskId = "Expand" + (index + 1), TimeoutMs = 60000 },
                        new CallTaskNode { Id = "b", Name = "第二处调用", TaskId = "Expand" + (index + 1), TimeoutMs = 60000 } });
                definitions.Add(task);
            }
            machine = Machine(Device(), definitions, Station("large", "Expand0", StationTaskMode.Once));
            machine.Reset(); machine.Start();
            await Until(() => machine.GetSnapshot().State == TaskMachineState.Faulted, "规模超限没有终止工位。");
            var run = State(machine, "large").Run;
            CheckAssert.True(run.State == TaskRunState.Failed && run.Nodes.Count == 10000
                && run.Error.Message.Contains("10000"), "规模错误被当作可重试步骤隐藏。");
            CheckAssert.Throws(machine.Reset, "规模超限后仍允许复位重放父任务。");
        }
    }
}
