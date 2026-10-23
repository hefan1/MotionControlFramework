using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Execution;
using MotionControl.TaskEngine.Machine;
using MotionControl.TaskEngine.Resources;

namespace MotionControl.TaskEngine.Checks
{
    /// <summary>故障重进当前节点时保留固定终点和资源范围阶段。</summary>
    internal static partial class MachineChecks
    {
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
            var input = new WaitInputNode { Id = "wait", Name = "等待输入", InputName = "PartReady", Expected = true, TimeoutMs = 80 };
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
                new WaitInputNode { Id = "wait", Name = "等待产品", InputName = "PartReady", Expected = true });
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
