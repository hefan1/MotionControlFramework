using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Adapter.MotionCard.Virtual;
using MotionControl.Framework.Contracts.Configuration;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.Framework.Runtime.Alarms;
using MotionControl.Framework.Runtime.Machine;
using MotionControl.Framework.Runtime.Safety;
using MotionControl.Framework.Runtime.Variables;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Configuration;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Machine;
using MotionControl.TaskEngine.MachineDevice;

namespace MotionControl.VirtualAdapter.Checks
{
    /// <summary>实际软件控制器上的任务操作协调回归，不创建轴替身。</summary>
    internal static partial class Program
    {
        /// <summary>连接实际软件控制器，检查正常操作、并发拒绝、旧会话隔离及异步启动撤销。</summary>
        private static async Task CheckTaskCommandOperationsAsync(int port)
        {
            var axisDefinition = new AxisHardwareDefinition
            {
                Name = "Command_X", ControllerId = "CommandCard", AxisNumber = 1,
                SoftLimitEnabled = false, PositionTolerance = 0.01
            };
            var input = new DigitalIoHardwareDefinition
            {
                Name = "CommandEmergencyStop", ControllerId = "CommandCard", Channel = 43, ActiveHigh = true
            };
            var card = new VirtualMotionCard("CommandCard", new[] { axisDefinition },
                new[] { input }, new DigitalIoHardwareDefinition[0], port, Path.Combine(DiagnosticsRoot, "commands"));
            var group = new MotionCardGroup(new IMotionCard[] { card });
            var alarms = new MachineAlarmService();
            var runtime = new MachineRuntime(group, new IStationFlow[0], alarms,
                new LogicalIoEmergencyStopStatus(card, "CommandEmergencyStop"));
            TaskMachineController machine = null;
            var errors = new List<Exception>();
            try
            {
                await runtime.ConnectAsync(CancellationToken.None);
                IAxis axis = group.GetAxis("Command_X");
                await axis.EnableServoAsync(CancellationToken.None);
                await axis.HomeAsync(CancellationToken.None);
                var device = CreateCommandDevice(group, card, runtime);
                machine = CreateCommandMachine(device, alarms);
                var operations = device.CreateOperations(machine);

                // 使用控制器已有的单次延迟反馈，不用线程替身模拟运动卡。
                RequireCommandFault(port, "delayReply", 300);
                Task resetting = operations.ResetAsync();
                await RequireCommandRejectedAsync(() => operations.StartAsync(), "并发启动");
                await resetting;
                RequireCommandState(runtime.State == MachineRunState.Ready
                    && machine.GetSnapshot().State == TaskMachineState.Ready, "复位未同时使两个执行层就绪");

                await operations.StartAsync();
                RequireCommandState(runtime.State == MachineRunState.Running
                    && machine.GetSnapshot().State == TaskMachineState.Running, "启动没有经过两个执行层");
                await operations.PauseAsync();
                RequireCommandState(runtime.State == MachineRunState.Paused && machine.IsAutomaticPauseConfirmed,
                    "暂停未确认任务收尾及 Runtime 状态");
                await operations.AbortAsync();
                RequireCommandState(machine.GetSnapshot().State == TaskMachineState.Stopped, "终止没有结束原任务");
                await machine.CloseAsync();
                machine = null;
                await runtime.DisconnectAsync(CancellationToken.None);

                // 同一 Runtime 重连后，旧操作实例绝不能复位新设备。
                await runtime.ConnectAsync(CancellationToken.None);
                MachineRunState beforeOldCommand = runtime.State;
                await RequireCommandRejectedAsync(() => operations.ResetAsync(), "旧会话复位");
                RequireCommandState(runtime.State == beforeOldCommand, "旧操作改写了新连接状态");
                device = CreateCommandDevice(group, card, runtime);
                machine = CreateCommandMachine(device, alarms);
                var current = device.CreateOperations(machine);
                await current.ResetAsync();

                // Runtime 建立许可期间撤销页面上下文：迟到结果必须拒绝，工步不能继续运行。
                RequireCommandFault(port, "delayReply", 300);
                Task starting = current.StartAsync();
                current.Invalidate();
                await RequireCommandRejectedAsync(() => starting, "异步启动完成前撤销上下文");
                RequireCommandState(machine.GetSnapshot().State != TaskMachineState.Running
                    && runtime.State != MachineRunState.Running, "撤销的启动仍保留运行许可");
                await RequireCommandRejectedAsync(() => current.StartAsync(), "撤销后重新启动");
                // 停止优先：撤销不能让原会话失去尝试停步的途径，但不得返回假成功。
                await RequireCommandRejectedAsync(() => current.AbortAsync(), "撤销后的终止结果提交");
                RequireCommandState(machine.GetSnapshot().State == TaskMachineState.Stopped,
                    "撤销后没有先终止原工步");
                await machine.CloseAsync();
                machine = null;
                await axis.StopAsync(AxisStopMode.Controlled, CancellationToken.None);
                RequireCommandState((await axis.ReadStatusAsync(CancellationToken.None)).HasStoppedMotionFeedback(),
                    "结束时实际软件控制器没有确认停稳");
                await runtime.DisconnectAsync(CancellationToken.None);
            }
            catch (Exception error) { errors.Add(error); }
            finally
            {
                if (machine != null)
                {
                    try { await machine.AbortAsync(); await machine.CloseAsync(); }
                    catch (Exception error) { errors.Add(error); }
                }
                if (card.IsConnected)
                {
                    try
                    {
                        await group.GetAxis("Command_X").StopAsync(AxisStopMode.Controlled, CancellationToken.None);
                        await runtime.DisconnectAsync(CancellationToken.None);
                    }
                    catch (Exception error) { errors.Add(error); }
                }
            }
            if (errors.Count != 0) throw new AggregateException("任务操作协调 API 检查或退出收尾失败。", errors);
            Console.WriteLine("[通过] 任务操作协调：复位/启动/暂停/终止、并发拒绝、旧连接拒绝、异步撤销与停止优先");
        }

        /// <summary>使用实际卡、逻辑 IO、空变量表和当前连接创建正式任务设备。</summary>
        private static MachineTaskDevice CreateCommandDevice(MotionCardGroup group, VirtualMotionCard card, MachineRuntime runtime)
        {
            var catalog = new TaskResourceCatalog(new[] { new TaskAxisInfo("Command_X", "CommandCard", "mm", -100, 100) },
                new[] { "CommandEmergencyStop" }, new string[0], new Dictionary<string, DeviceVariableType>(), new TaskTeachPoint[0]);
            return new MachineTaskDevice(catalog, group, card,
                new DeviceVariableStore(new DeviceVariableDefinition[0]), runtime, runtime.ConnectionSessionId.Value);
        }

        /// <summary>建立保持等待的工步，专门观察协调状态；实际轴命令由现有单/双控制器测试覆盖。</summary>
        private static TaskMachineController CreateCommandMachine(MachineTaskDevice device, MachineAlarmService alarms)
        {
            var definition = new TaskDefinition
            {
                TaskId = "CommandCycle", Name = "操作协调检查",
                Root = new SequenceNode { Id = "root", Name = "流程", Children = new List<TaskNode>
                {
                    new DelayNode { Id = "wait", Name = "等待操作员暂停", Milliseconds = 30000, TimeoutMs = 60000 }
                } }
            };
            var configuration = new TaskMachineConfiguration
            {
                MachineId = "CommandMachine", Name = "操作协调设备",
                Stations = new List<StationTaskConfiguration>
                {
                    new StationTaskConfiguration { StationId = "station", Name = "验证工位", TaskId = definition.TaskId, Enabled = true, Mode = StationTaskMode.Once }
                }
            };
            return new TaskMachineController(configuration, new[] { definition }, device, alarms);
        }

        /// <summary>只向本轮隔离软件控制器注入一次 readAxis 回读延迟。</summary>
        private static void RequireCommandFault(int port, string mode, int delay)
        {
            string response = SendRaw(port, "{\"command\":\"setCommandFault\",\"targetCommand\":\"readAxis\",\"targetIndex\":1,\"mode\":\""
                + mode + "\",\"delayMs\":" + delay + "}");
            RequireCommandState(response.Contains("\"ok\":true"), "控制器未接受延迟反馈配置");
        }

        /// <summary>操作确实被明确拒绝才算成功，不能吞掉不相关异常。</summary>
        private static async Task RequireCommandRejectedAsync(Func<Task> command, string scenario)
        {
            bool rejected = false;
            try { await command(); }
            catch (InvalidOperationException) { rejected = true; }
            RequireCommandState(rejected, scenario + "未被拒绝");
        }

        /// <summary>明确报告失败场景，不把未知反馈当作成功。</summary>
        private static void RequireCommandState(bool success, string message)
        {
            if (!success) throw new InvalidOperationException(message);
        }
    }
}
