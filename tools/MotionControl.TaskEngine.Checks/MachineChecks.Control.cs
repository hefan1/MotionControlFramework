using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Execution;
using MotionControl.TaskEngine.Machine;
using MotionControl.TaskEngine.Resources;

namespace MotionControl.TaskEngine.Checks
{
    /// <summary>整机统一控制与工位进度保持的检查。</summary>
    internal static partial class MachineChecks
    {
        /// <summary>快慢工位各自循环，普通暂停必须复位后才继续，单次和禁用工位不重复执行。</summary>
        private static async Task IndependentCycles()
        {
            var device = Device();
            var fast = TaskDefinition("Fast", TaskFixtures.Delay("fast"));
            var slow = TaskDefinition("Slow", new DelayNode { Id = "slow", Name = "慢工位", Milliseconds = 700 });
            var once = TaskDefinition("Once", new DelayNode { Id = "once", Name = "单次", Milliseconds = 10 });
            var disabled = Station("disabled", "Once", StationTaskMode.Once); disabled.Enabled = false;
            var machine = Machine(device, new[] { fast, slow, once }, Station("fast", "Fast", StationTaskMode.Cyclic),
                Station("slow", "Slow", StationTaskMode.Cyclic), Station("once", "Once", StationTaskMode.Once), disabled);
            CheckAssert.Throws(machine.Start, "未复位就启动。");
            machine.Reset(); machine.Start();
            CheckAssert.Throws(machine.Start, "重复启动创建了新工位。");
            await Until(() => State(machine, "fast").CompletedCycles >= 5, "快工位没有独立推进。");
            CheckAssert.True(State(machine, "slow").CompletedCycles == 0, "没有观察到快慢工位独立周期。");
            machine.Pause();
            var old = machine.GetSnapshot(); long count = State(machine, "fast").CompletedCycles;
            await Task.Delay(80);
            CheckAssert.True(State(machine, "fast").CompletedCycles == count && machine.GetSnapshot().State == TaskMachineState.Paused, "暂停后仍提交新周期。");
            CheckAssert.Throws(machine.Start, "暂停后未复位就继续。");
            machine.Reset(); await Task.Delay(40);
            CheckAssert.True(State(machine, "fast").CompletedCycles == count && machine.GetSnapshot().State == TaskMachineState.Ready, "复位自动启动。");
            machine.Start();
            await Until(() => State(machine, "slow").CompletedCycles >= 1, "慢工位不能从原位置恢复。");
            CheckAssert.True(State(machine, "once").CompletedCycles == 1 && State(machine, "disabled").CompletedCycles == 0
                && old.State == TaskMachineState.Paused && old.Stations.Single(item => item.StationId == "fast").CompletedCycles == count, "单次/禁用状态或旧快照被改写。");
        }

        /// <summary>初始化完成前自动工位不接受动作，原定义和配置后续修改不影响已冻结会话。</summary>
        private static async Task Initialization()
        {
            var device = Device(); device.ConfigureAxis("X", 30, true, false);
            var home = new AxisCommandNode { Id = "home", Name = "初始化回零", AxisName = "X", Command = TaskAxisCommand.Home };
            var init = TaskDefinition("Init", home);
            var move = TaskFixtures.Move(); move.Profile.Speed = 100;
            var work = TaskDefinition("Work", move);
            var configuration = new TaskMachineConfiguration { MachineId = "Test01", Name = "初始化设备", InitializationTaskId = "Init",
                Stations = new List<StationTaskConfiguration> { Station("work", "Work", StationTaskMode.Once) } };
            var machine = new TaskMachineController(configuration, new[] { init, work }, device); Machines.Add(machine);
            configuration.Stations.Clear(); move.Target.Constant = "80";
            machine.Reset(); machine.Start();
            await Until(() => device.ReadAxis("X").IsBusy && !machine.GetSnapshot().InitializationCompleted, "初始化没有先运行。");
            CheckAssert.True(State(machine, "work").Run == null && device.ReadAxis("X").AcceptedCommands == 1, "初始化未完成就执行自动任务。");
            await Until(() => State(machine, "work").CompletedCycles == 1, "初始化后未放行任务。");
            CheckAssert.True(machine.GetSnapshot().InitializationCompleted && device.ReadAxis("X").Homed
                && device.ReadAxis("X").Position == 10 && device.ReadAxis("X").AcceptedCommands == 2, "初始化/定义冻结错误。");
            machine.Pause(); machine.Reset(); machine.Start(); await Task.Delay(40);
            CheckAssert.True(device.ReadAxis("X").AcceptedCommands == 2, "暂停后重复初始化或重复单次工位。");
        }

        /// <summary>一个步骤失败会冻结其他工位，修复后重进该步骤，前面已完成的相对动作不重复。</summary>
        private static async Task FaultAndStepRecovery()
        {
            var device = Device();
            var first = TaskFixtures.Move("first"); first.Mode = TaskMoveMode.Relative; first.Target.Constant = "5"; first.Profile.Speed = 100;
            var after = TaskFixtures.Move("after"); after.Mode = TaskMoveMode.Relative; after.Target.Constant = "2"; after.Profile.Speed = 100;
            var task = TaskDefinition("Fault", first, new WaitInputNode { Id = "gate", Name = "等故障场景就绪", InputName = "PartReady", Expected = true },
                CopyValue("bad", "Vision.OffsetX", "Vision.OffsetX"), after);
            var otherMove = TaskFixtures.Move("other", "Y"); otherMove.Target.Constant = "20"; otherMove.Profile.Speed = 10;
            var machine = Machine(device, new[] { task, TaskDefinition("Other", otherMove) },
                Station("fault", "Fault", StationTaskMode.Once), Station("other", "Other", StationTaskMode.Once));
            machine.Reset(); machine.Start();
            await Until(() => device.ReadAxis("X").Position == 5 && device.ReadAxis("Y").IsBusy, "故障前场景未建立。");
            device.SetInput("PartReady", true);
            await Until(() => machine.GetSnapshot().State == TaskMachineState.Faulted, "故障没有联动整机。");
            double y = device.ReadAxis("Y").Position;
            var error = machine.GetSnapshot().Faults.Single().Error;
            CheckAssert.True(error.NodeId == "bad" && error.TaskId == "Fault", "故障没有具体节点上下文。");
            await Task.Delay(50);
            CheckAssert.True(device.ReadAxis("Y").Position == y && device.ReadAxis("X").AcceptedCommands == 1, "故障暂停后其他工位仍运动。");
            device.SetVariable("Vision.OffsetX", "2"); machine.Reset(); machine.Start();
            await Until(() => State(machine, "fault").CompletedCycles == 1, "修复后当前步骤未继续。");
            CheckAssert.True(device.ReadAxis("X").Position == 7 && device.ReadAxis("X").AcceptedCommands == 2
                && State(machine, "fault").Run.Nodes.Count(node => node.NodeId == "first") == 1, "恢复重放了前面的动作。");
        }

        /// <summary>停机条件有效时禁止复位，条件恢复仍保持锁存，复位本身不继续生产。</summary>
        private static async Task StopAlarm()
        {
            var device = Device();
            var machine = Machine(device, new[] { TaskDefinition("Loop", TaskFixtures.Delay("delay")) }, Station("loop", "Loop", StationTaskMode.Cyclic));
            machine.Reset(); machine.Start();
            await Until(() => State(machine, "loop").CompletedCycles > 0, "报警前没有运行。");
            machine.SetStopAlarm("Sensor", "测试传感器条件有效", true);
            CheckAssert.True(machine.GetSnapshot().State == TaskMachineState.Faulted, "停机报警未锁存。");
            CheckAssert.Throws(machine.Reset, "有效停机条件被复位。");
            machine.SetStopAlarm("Sensor", "", false);
            CheckAssert.True(machine.GetSnapshot().State == TaskMachineState.Faulted, "条件恢复自动消除停机锁存。");
            long cycles = State(machine, "loop").CompletedCycles;
            machine.Reset(); await Task.Delay(40);
            CheckAssert.True(State(machine, "loop").CompletedCycles == cycles, "复位后自动推进。");
            machine.Start(); await Until(() => State(machine, "loop").CompletedCycles > cycles, "复位启动后未恢复。");
        }

        /// <summary>整机持有设备时禁止混用入口，终止等待动作停止，范围保留待确认，关闭后才归还所有权。</summary>
        private static async Task OwnershipAndAbort()
        {
            var device = Device();
            var move = TaskFixtures.Move(); move.Target.Constant = "80"; move.Profile.Speed = 1;
            var scope = new ResourceScopeNode { Id = "scope", Name = "占用区域", Resources = new List<string> { "Axis:X", "Zone:Camera" }, Body = Sequence("body", move) };
            var definition = TaskDefinition("Work", scope);
            var machine = Machine(device, new[] { definition }, Station("work", "Work", StationTaskMode.Once));
            CheckAssert.Throws(() => new TaskExecutionEngine().Start(definition, device), "整机期间独立入口绕过控制。");
            CheckAssert.Throws(() => Machine(device, new[] { definition }, Station("other", "Work", StationTaskMode.Once)), "两个控制器同时持有设备。");
            machine.Reset(); machine.Start();
            await Until(() => device.ReadAxis("X").IsBusy, "终止场景未开始。");
            await Wait(machine.AbortAsync());
            CheckAssert.True(!device.ReadAxis("X").IsBusy && machine.GetSnapshot().State == TaskMachineState.Stopped
                && device.ReadResources().Single().State == TaskResourceState.Blocked, "终止没有停止动作或提前让出区域。");
            CheckAssert.Throws(machine.Reset, "终止会话被复位续跑。");
            await Wait(machine.CloseAsync()); await Wait(machine.CloseAsync());
            string reason;
            CheckAssert.True(device.TryRecoverResources(device.ReadResources().Single().RequestId, "已核实内存区域", out reason), reason);
            var next = TaskDefinition("Next", TaskFixtures.Delay("next"));
            CheckAssert.True((await new TaskExecutionEngine().Start(next, device).Completion).State == TaskRunState.Completed, "关闭后仍未归还设备。");
        }
    }
}
