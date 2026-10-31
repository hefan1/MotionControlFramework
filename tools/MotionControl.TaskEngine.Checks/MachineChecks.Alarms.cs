using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Execution;
using MotionControl.TaskEngine.Machine;
using MotionControl.TaskEngine.Device.InMemory;

namespace MotionControl.TaskEngine.Checks
{
    /// <summary>任务等待超时的多报警和工位并行处理检查。</summary>
    internal static partial class MachineChecks
    {
        /// <summary>同一超时原子上报停机与非停机报警，复位后从等待节点继续。</summary>
        private static async Task LinkedTimeoutAlarms()
        {
            InMemoryTaskDevice device = Device();
            device.SetInput("PartReady", false);
            var wait = new WaitInputNode
            {
                Id = "wait-ready", Name = "等待上料到位", InputName = "PartReady", Expected = true,
                TimeoutMs = 80, TimeoutAlarmCodes = { "DEMO_SENSOR_TIMEOUT", "OPERATOR_NOTICE" }
            };
            TaskDefinition task = TaskDefinition("Load", wait);
            StationTaskConfiguration station = Station("load", task.TaskId, StationTaskMode.Once);
            TaskMachineController machine = Machine(device, new[] { task }, station);
            machine.Reset(); machine.Start();

            await Until(() => machine.GetSnapshot().State == TaskMachineState.Faulted,
                "停机级等待超时没有暂停整机。");
            MachineAlarmRecord[] alarms = machine.AlarmService.Alarms.ToArray();
            CheckAssert.True(alarms.All(alarm => alarm.RelatedInputName == "PartReady"),
                "任务等待输入超时后，报警没有保留实际输入名称。");
            CheckAssert.True(alarms.Length == 2
                && alarms.Any(alarm => alarm.Definition.Code == "DEMO_SENSOR_TIMEOUT"
                    && alarm.RequiresReset && !alarm.ConditionActive)
                && alarms.Any(alarm => alarm.Definition.Code == "OPERATOR_NOTICE"
                    && !alarm.RequiresReset && alarm.ConditionActive),
                "同一等待超时没有同时留下正确级别的两条报警。");

            device.SetInput("PartReady", true);
            machine.Reset();
            CheckAssert.True(machine.AlarmService.Alarms.Count == 1
                && machine.AlarmService.Alarms.Single().Definition.Code == "OPERATOR_NOTICE",
                "整机复位没有只清除已恢复的停机报警。");
            machine.Start();
            await Until(() => State(machine, "load").CompletedCycles == 1
                && machine.AlarmService.Alarms.Count == 0,
                "等待条件满足后任务没有完成或非停机报警没有清除。");
        }

        /// <summary>仅关联非停机报警时，等待工位不锁死整机，其他工位仍可独立生产。</summary>
        private static async Task NonStopTimeoutContinuesProduction()
        {
            InMemoryTaskDevice device = Device();
            device.SetInput("PartReady", false);
            TaskDefinition load = TaskDefinition("Load", new WaitInputNode
            {
                Id = "wait-ready", Name = "等待上料到位", InputName = "PartReady", Expected = true,
                TimeoutMs = 80, TimeoutAlarmCodes = { "OPERATOR_NOTICE" }
            });
            TaskDefinition inspect = TaskDefinition("Inspect", new DelayNode
            {
                Id = "inspect-delay", Name = "视觉检测周期", Milliseconds = 10
            });
            TaskMachineController machine = Machine(device, new[] { load, inspect },
                Station("load", load.TaskId, StationTaskMode.Cyclic),
                Station("vision", inspect.TaskId, StationTaskMode.Cyclic));
            machine.Reset(); machine.Start();

            await Until(() => machine.AlarmService.Alarms.Count == 1
                && State(machine, "vision").CompletedCycles >= 2,
                "非停机等待超时没有显示报警，或阻断了其他工位。");
            CheckAssert.True(machine.GetSnapshot().State == TaskMachineState.Running
                && State(machine, "load").State == StationTaskState.Executing
                && machine.AlarmService.Alarms.Single().Definition.Kind == MachineAlarmKind.NonStop,
                "仅非停机报警错误地锁存或暂停整机。");

            device.SetInput("PartReady", true);
            await Until(() => machine.AlarmService.Alarms.Count == 0
                && State(machine, "load").CompletedCycles > 0,
                "输入恢复后提醒没有自动清除或等待工位没有继续。");
            CheckAssert.True(State(machine, "vision").CompletedCycles >= 2,
                "非停机报警期间另一工位没有保持连续生产。");
        }

        /// <summary>同一工位两个并行等待共用报警时，一个条件恢复不能清除另一个条件。</summary>
        private static async Task ParallelConditionsShareAlarm()
        {
            InMemoryTaskDevice device = Device();
            device.SetInput("PartReady", null);
            var parallel = new ParallelGroupNode { Id = "parallel-wait", Name = "并行检查输入", Branches = new List<TaskNode> {
                Sequence("branch-on", new WaitInputNode { Id = "wait-on", Name = "等待有效", InputName = "PartReady", Expected = true,
                    TimeoutMs = 80, TimeoutAlarmCodes = { "OPERATOR_NOTICE" } }),
                Sequence("branch-off", new WaitInputNode { Id = "wait-off", Name = "等待无效", InputName = "PartReady", Expected = false,
                    TimeoutMs = 80, TimeoutAlarmCodes = { "OPERATOR_NOTICE" } })
            } };
            TaskDefinition task = TaskDefinition("Parallel", parallel);
            TaskMachineController machine = Machine(device, new[] { task }, Station("station", task.TaskId, StationTaskMode.Once));
            int notifications = 0;
            machine.AlarmService.AlarmsChanged += (sender, args) => Interlocked.Increment(ref notifications);
            machine.Reset(); machine.Start();
            await Until(() => Volatile.Read(ref notifications) >= 2, "两个并行条件未分别上报报警。");
            device.SetInput("PartReady", true);
            await Until(() => State(machine, "station").Run?.Nodes.Any(node => node.NodeId == "wait-on"
                && node.State == TaskNodeState.Completed) == true, "有效输入对应的并行等待未完成。");
            CheckAssert.True(machine.AlarmService.Alarms.Count == 1
                && machine.AlarmService.Alarms.Single().Detail.Contains("PartReady=False"),
                "一个并行条件恢复后，另一条件的报警或详情丢失。");
            CheckAssert.True(State(machine, "station").CompletedCycles == 0, "另一个等待未完成，工位却提前完成。");
            device.SetInput("PartReady", false);
            await Until(() => machine.AlarmService.Alarms.Count == 0
                && State(machine, "station").CompletedCycles == 1, "全部并行条件恢复后未清除报警或完成任务。");
        }

        /// <summary>复现前序延时使上层时限先到，并核对增大上层时限后等待报警能触发。</summary>
        private static async Task ParentDeadlineCanPreemptWaitAlarm()
        {
            TaskDefinition MakeTask(string id, int parentTimeout)
            {
                TaskDefinition task = TaskDefinition(id,
                    new DelayNode { Id = "settle", Name = "启动稳定等待", Milliseconds = 40, TimeoutMs = 1000 },
                    new WaitInputNode { Id = "wait", Name = "等待上料到位", InputName = "PartReady", Expected = true,
                        TimeoutMs = 120, TimeoutAlarmCodes = { "OPERATOR_NOTICE" } });
                task.Root.Name = "上料顺序段";
                task.Root.TimeoutMs = parentTimeout;
                return task;
            }
            var firstDevice = Device();
            firstDevice.SetInput("PartReady", false);
            TaskDefinition shorter = MakeTask("ShortParent", 120);
            var first = Machine(firstDevice, new[] { shorter }, Station("load-a", shorter.TaskId, StationTaskMode.Once));
            first.Reset(); first.Start();
            await Until(() => first.GetSnapshot().State == TaskMachineState.Faulted, "上层时限到期后没有报告任务故障。");
            CheckAssert.True(first.AlarmService.Alarms.Count == 0, "测试条件未复现：上层先超时时不应冒充等待节点超时。");

            var secondDevice = Device();
            secondDevice.SetInput("PartReady", false);
            TaskDefinition longer = MakeTask("LongParent", 2000);
            var second = Machine(secondDevice, new[] { longer }, Station("load-b", longer.TaskId, StationTaskMode.Once));
            second.Reset(); second.Start();
            await Until(() => second.AlarmService.Alarms.Any(alarm => alarm.Definition.Code == "OPERATOR_NOTICE"),
                "为等待步骤留足时限后，没有触发关联报警。");
            secondDevice.SetInput("PartReady", true);
            await Until(() => second.AlarmService.Alarms.Count == 0 && State(second, "load-b").CompletedCycles == 1,
                "输入恢复后未清除提醒或完成任务。");
        }
    }
}
