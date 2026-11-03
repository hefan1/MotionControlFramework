using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Configuration;
using MotionControl.TaskEngine.Device.InMemory;
using MotionControl.TaskEngine.Machine;
using MotionControl.TaskEngine.Resources;

namespace MotionControl.TaskEngine.Checks
{
    /// <summary>只检查十三轴双夹位运动样例，不连接 Host 或真实控制器。</summary>
    internal static partial class MachineChecks
    {
        /// <summary>分别运行无扫码循环与夹具、视觉故障，结束时检查会话正常收尾。</summary>
        internal static int RunRotaryMotionDemo(string root, string machineId)
        {
            try
            {
                RunRotaryMotionAsync(root, machineId, null).GetAwaiter().GetResult();
                RunRotaryMotionAsync(root, machineId, "夹具冲突").GetAwaiter().GetResult();
                RunRotaryMotionAsync(root, machineId, "视觉无效").GetAwaiter().GetResult();
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
            finally { Machines.Clear(); }
        }

        /// <summary>以独立输入输出反馈验证循环、占料和故障，不依赖扫码或产品记录。</summary>
        private static async Task RunRotaryMotionAsync(string root, string machineId, string fault)
        {
            var store = new MachineConfigurationStore(root);
            var definitionsStore = new TaskDefinitionStore(store);
            var definitions = Directory.GetFiles(Path.Combine(store.GetMachineDirectory(machineId), "Tasks"), "*.json")
                .Select(path => definitionsStore.LoadForEditing(machineId, Path.GetFileName(path))).ToArray();
            var machineStore = new TaskMachineConfigurationStore(store);
            var catalog = new TaskResourceCatalogLoader(store).Load(machineId, machineStore.LoadNamedResources(machineId));
            var config = machineStore.Load(machineId, definitions, catalog);
            if (!config.Stations.Any(item => item.StationId == "RotaryMotionTable")) throw new InvalidOperationException("设备清单不是纯运动双夹位样例。");
            if (catalog.Inputs.Any(name => name.StartsWith("扫码", StringComparison.Ordinal)) || catalog.Outputs.Any(name => name.StartsWith("扫码", StringComparison.Ordinal)))
                throw new InvalidOperationException("测试目录仍配置扫码 IO，不能证明纯运动样例独立运行。");
            foreach (var definition in definitions.Where(item => item.TaskId.StartsWith("RotaryMotion", StringComparison.Ordinal)))
            {
                SpeedUpRotaryTest(definition.Root);
                if (fault != null) ShortenRotaryWaits(definition.Root);
            }
            var device = new InMemoryTaskDevice(catalog);
            string[] axes = { "Feeder_X", "Feeder_Z", "Load_X", "Transfer_Y", "Conveyor_R", "Weld_Z", "Press_Z", "Unload_X", "Buffer_X", "Buffer_Y", "Camera_X", "Camera_Z", "Reject_Z" };
            foreach (string axis in axes) device.ConfigureAxis(axis, axis == "Conveyor_R" ? -90 : 0, true, true);
            foreach (string input in catalog.Inputs) device.SetInput(input, false);
            foreach (string output in catalog.Outputs) device.WriteOutput(output, false);
            foreach (string input in new[] { "上料到位", "取料松开到位", "A松开到位", "B松开到位", "缓存接收允许", "合格出口允许", "不良出口允许" }) device.SetInput(input, true);
            if (fault == "夹具冲突") device.SetInput("取料夹紧到位", true);
            if (fault == null) device.SetInput("合格出口允许", false);
            // 端口没有实现逐件记录接口，确保样例不隐式依赖产品追溯。
            var machine = new TaskMachineController(config, definitions, new NoRecordTaskDevice(device));
            Machines.Add(machine);
            var signals = new RotaryTestSignals(device, false) { Fault = fault };
            using (var cancel = new CancellationTokenSource())
            {
                Task poll = Task.Run(async () => { while (!cancel.IsCancellationRequested) { signals.Tick(); await Task.Delay(5).ConfigureAwait(false); } });
                try
                {
                    machine.Reset(); machine.Start();
                    DateTime deadline = DateTime.UtcNow.AddMinutes(3);
                    bool threeOccupied = false;
                    while (DateTime.UtcNow < deadline)
                    {
                        var snapshot = machine.GetSnapshot();
                        if (fault != null && snapshot.State == TaskMachineState.Faulted) break;
                        if (fault == null && snapshot.State == TaskMachineState.Faulted)
                            throw new InvalidOperationException("纯运动样例发生故障：" + string.Join("；", snapshot.Faults.Select(item => item.Source + ": " + item.Message)));
                        if (!threeOccupied && fault == null && BooleanValue(device, "RotaryMotion.WeldOccupied") == true
                            && BooleanValue(device, "RotaryMotion.PressOccupied") == true && BooleanValue(device, "RotaryMotion.BufferOccupied") == true)
                        { threeOccupied = true; device.SetInput("合格出口允许", true); }
                        if (fault == null && State(machine, "RotaryMotionInspect").CompletedCycles >= 50) break;
                        await Task.Delay(10).ConfigureAwait(false);
                    }
                    if (fault == null)
                    {
                        if (!threeOccupied || State(machine, "RotaryMotionInspect").CompletedCycles < 50) throw new TimeoutException("纯运动样例未完成三处占料与五十周期。");
                        if (device.ReadRecords().Count != 0) throw new InvalidOperationException("纯运动样例意外写入了产品追溯记录。");
                        await Wait(machine.PauseAsync()).ConfigureAwait(false);
                        foreach (string axis in axes) if (device.ReadAxis(axis).AcceptedCommands < 2) throw new InvalidOperationException("轴未完成至少两次动作：" + axis);
                    }
                    else
                    {
                        if (machine.GetSnapshot().State != TaskMachineState.Faulted) throw new TimeoutException(fault + " 未触发停机。");
                        if (fault == "夹具冲突" && device.ReadAxis("Feeder_X").AcceptedCommands != 0) throw new InvalidOperationException("夹具反馈冲突仍发出送料动作。");
                        if (fault == "视觉无效" && (BooleanValue(device, "RotaryMotion.BufferOccupied") != true || signals.IllegalExitAfterInvalid)) throw new InvalidOperationException("视觉无效时错误放行或清除占料。");
                    }
                    await Wait(machine.AbortAsync()).ConfigureAwait(false);
                    foreach (string output in catalog.Outputs) device.WriteOutput(output, false);
                    foreach (var resource in device.ReadResources().Where(item => item.State == TaskResourceState.Blocked).ToArray())
                    {
                        string reason;
                        if (!device.TryRecoverResources(resource.RequestId, "仅内存测试：已模拟核对", out reason)) throw new InvalidOperationException(reason);
                    }
                    await Wait(machine.CloseAsync()).ConfigureAwait(false);
                    Machines.Remove(machine);
                    Console.WriteLine("[通过] 纯运动双夹位：" + (fault ?? "无扫码和逐件记录完成五十周期及三处占料") + "；仅内存设备。");
                }
                finally { cancel.Cancel(); await poll.ConfigureAwait(false); }
            }
        }
    }
}
