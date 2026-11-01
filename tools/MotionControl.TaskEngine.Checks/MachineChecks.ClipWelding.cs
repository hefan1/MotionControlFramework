using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Configuration;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Device.InMemory;
using MotionControl.TaskEngine.Machine;

namespace MotionControl.TaskEngine.Checks
{
    /// <summary>隔离验证夹具焊接样例的多件并行、堵料及继续运行。</summary>
    internal static partial class MachineChecks
    {
        /// <summary>读取已安装的隔离配置，独立构造内存设备，不修改配置或连接控制器。</summary>
        internal static int RunClipWeldingDemo(string root, string machineId)
        {
            try { RunClipWeldingDemoAsync(root, machineId).GetAwaiter().GetResult(); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
            finally
            {
                foreach (TaskMachineController machine in Machines) Wait(machine.CloseAsync()).GetAwaiter().GetResult();
                Machines.Clear();
            }
        }

        /// <summary>先阻塞下游，确认不同样例产品同时占据工位，再放行检查标识和继续生产。</summary>
        private static async Task RunClipWeldingDemoAsync(string root, string machineId)
        {
            var store = new MachineConfigurationStore(root);
            var definitionStore = new TaskDefinitionStore(store);
            var files = Directory.GetFiles(Path.Combine(store.GetMachineDirectory(machineId), "Tasks"), "*.json");
            var definitions = files.Select(path => definitionStore.LoadForEditing(machineId, Path.GetFileName(path))).ToArray();
            var machineStore = new TaskMachineConfigurationStore(store);
            var catalog = new TaskResourceCatalogLoader(store).Load(machineId, machineStore.LoadNamedResources(machineId));
            var config = machineStore.Load(machineId, definitions, catalog);
            if (!config.Stations.Any(item => item.StationId == "ClipInspect")) throw new InvalidOperationException("配置不是夹具焊接样例。");
            // 仅缩短此内存实例的等待时限，不修改样例文件或现场配置。
            var weld = (SequenceNode)definitions.Single(item => item.TaskId == "ClipWeld").Root;
            ((WaitInputNode)weld.Children.Single(item => item.Id == "weldDone")).TimeoutMs = 1200;
            ((WaitInputNode)weld.Children.Single(item => item.Id == "weldReady")).TimeoutMs = 1200;
            ((WaitInputNode)weld.Children.Single(item => item.Id == "weldPrecheck")).TimeoutMs = 1200;
            var device = new InMemoryTaskDevice(catalog);
            foreach (string axis in new[] { "Load_X", "Press_Z", "Camera_X", "Unload_X" }) device.ConfigureAxis(axis, 0, true, true);
            foreach (string output in new[] { "夹具夹紧", "压头动作", "相机触发", "下料电磁阀" }) device.WriteOutput(output, false);
            device.SetInput("上料到位", true);
            device.SetInput("压装完成", false); device.SetInput("检测完成", false); device.SetInput("下料到位", false);
            device.SetVariable("Clip.ReceiveEnabled", "false");
            // 模拟上次会话留下的完成记录，初始化后不能误认为当前会话已产出。
            foreach (string name in new[] { "Clip.LastProduct", "Clip.LastCarrier", "Clip.LastResult", "Clip.LastDestination" })
                device.SetVariable(name, "上一会话");
            var machine = new TaskMachineController(config, definitions, device); Machines.Add(machine);
            // 3 表示焊接轴开始运动后卡在有效，2 表示一直有效，1 表示一直无效，0 表示根据输出正常变化。
            int weldFeedbackFault = 2;
            using (var cancel = new CancellationTokenSource())
            {
                Task signals = Task.Run(async () =>
                {
                    bool clamped = false; DateTime feedAgain = DateTime.MinValue;
                    while (!cancel.IsCancellationRequested)
                    {
                        if (device.ReadOutput("夹具夹紧") == true) clamped = true;
                        if (clamped && device.ReadOutput("夹具夹紧") == false)
                        { device.SetInput("上料到位", false); feedAgain = DateTime.UtcNow.AddMilliseconds(400); clamped = false; }
                        if (feedAgain != DateTime.MinValue && DateTime.UtcNow >= feedAgain)
                        { device.SetInput("上料到位", true); feedAgain = DateTime.MinValue; }
                        int feedbackFault = Volatile.Read(ref weldFeedbackFault);
                        device.SetInput("压装完成", feedbackFault == 2 || (feedbackFault == 3 && device.ReadAxis("Press_Z").AcceptedCommands > 0)
                            || (feedbackFault == 0 && device.ReadOutput("压头动作") == true));
                        device.SetInput("检测完成", device.ReadOutput("相机触发") == true);
                        device.SetInput("下料到位", device.ReadOutput("下料电磁阀") == true);
                        await Task.Delay(20).ConfigureAwait(false);
                    }
                });
                try
                {
                    machine.Reset(); machine.Start();
                    await WaitClipFaultAsync(machine, "焊接旧反馈未复位时未触发停机。");
                    CheckAssert.True(machine.GetSnapshot().Faults.Any(item => item.Source == "ClipWeld"), "旧反馈故障未定位焊接工位。");
                    CheckAssert.True(device.ReadOutput("压头动作") == false && State(machine, "ClipWeld").CompletedCycles == 0,
                        "旧反馈未复位时，不应打开焊接测试输出或完成周期。");
                    CheckAssert.True(device.ReadAxis("Press_Z").AcceptedCommands == 0, "旧反馈未复位时，不应发送焊接轴运动。");
                    Volatile.Write(ref weldFeedbackFault, 0);
                    await Until(() => device.ReadInput("压装完成") == false, "旧反馈未恢复无效。");
                    Volatile.Write(ref weldFeedbackFault, 3);
                    machine.Reset(); machine.Start();
                    await WaitClipFaultAsync(machine, "焊接轴运动期间反馈异常未触发停机。");
                    CheckAssert.True(machine.GetSnapshot().Faults.Any(item => item.Source == "ClipWeld"), "运动期间反馈故障未定位焊接工位。");
                    CheckAssert.True(device.ReadAxis("Press_Z").AcceptedCommands == 1 && device.ReadOutput("压头动作") == false,
                        "焊接轴运动期间反馈异常时，不应打开焊接测试输出。");
                    Volatile.Write(ref weldFeedbackFault, 0);
                    await Until(() => device.ReadInput("压装完成") == false, "运动期间异常反馈未恢复无效。");
                    machine.Reset(); machine.Start();
                    Console.WriteLine("[通过] 焊接反馈初始有效时不发轴运动；轴运动期间变为有效时不发焊接输出，故障恢复后继续。");
                    await WaitClipAsync(() => BooleanValue(device, "Clip.Inspect.Occupied") == true
                        && BooleanValue(device, "Clip.Press.Occupied") == true
                        && Value(device, "Clip.Inspect.Product") != Value(device, "Clip.Press.Product"), machine, "下游堵料时未观察到两个不同测试产品同时在工位。");
                    CheckAssert.True(Value(device, "Clip.Inspect.Product") == "SAMPLE-A", "复检工位产品与预期不符。");
                    CheckAssert.True(Value(device, "Clip.Inspect.Carrier") == "CARRIER-A", "复检载具标识未跟随产品。");
                    CheckAssert.True(Value(device, "Clip.Press.Product") == "SAMPLE-B", "压合工位产品顺序不符。");
                    foreach (string name in new[] { "Clip.LastProduct", "Clip.LastCarrier", "Clip.LastResult", "Clip.LastDestination" })
                        CheckAssert.True(Value(device, name) == "", "新会话尚未产出，不能显示旧的完成记录：" + name);
                    await Task.Delay(700).ConfigureAwait(false);
                    CheckAssert.True(State(machine, "ClipInspect").CompletedCycles == 0, "下游堵料时不应完成复检出料。");
                    Console.WriteLine("[通过] 下游堵料时 A/B 两件测试产品同时在不同工位，标识未混淆。");
                    await Wait(machine.PauseAsync()).ConfigureAwait(false);
                    CheckAssert.True(machine.GetSnapshot().State == TaskMachineState.Paused
                        && BooleanValue(device, "Clip.Inspect.Occupied") == true
                        && BooleanValue(device, "Clip.Press.Occupied") == true, "暂停后未保留工位中的测试载具。");
                    device.SetVariable("Clip.ReceiveEnabled", "true");
                    machine.Reset(); machine.Start();
                    for (int cycle = 1; cycle <= 4; cycle++)
                    {
                        int expectedCycle = cycle;
                        await WaitClipAsync(() => State(machine, "ClipInspect").CompletedCycles >= expectedCycle, machine, "放行后复检未完成预期产品。");
                        string suffix = cycle % 2 == 1 ? "A" : "B";
                        CheckAssert.True(State(machine, "ClipInspect").CompletedCycles == cycle, "未能逐件观察完成记录。");
                        CheckAssert.True(Value(device, "Clip.LastProduct") == "SAMPLE-" + suffix && Value(device, "Clip.LastCarrier") == "CARRIER-" + suffix
                            && Value(device, "Clip.LastResult") == (suffix == "A" ? "OK" : "NG")
                            && Value(device, "Clip.LastDestination") == (suffix == "A" ? "OK缓存" : "NG缓存"), "第 " + cycle + " 件身份、载具、结果或去向不符。");
                    }
                    await Wait(machine.PauseAsync()).ConfigureAwait(false);
                    Console.WriteLine("[通过] 暂停、放行、复位启动后逐件观察 A/B/A/B 顺序、载具与测试结果；整机已暂停。身份循环复用，不能证明唯一追溯。");
                    long previousCount = State(machine, "ClipInspect").CompletedCycles;
                    Volatile.Write(ref weldFeedbackFault, 1);
                    machine.Reset(); machine.Start();
                    await Until(() => machine.GetSnapshot().State == TaskMachineState.Faulted, "焊接反馈故障未触发停机。");
                    CheckAssert.True(machine.GetSnapshot().Faults.Any(item => item.Source == "ClipWeld"), "故障未定位焊接工位。");
                    Volatile.Write(ref weldFeedbackFault, 0);
                    machine.Reset(); machine.Start();
                    await WaitClipAsync(() => State(machine, "ClipInspect").CompletedCycles > previousCount, machine, "恢复焊接反馈后未继续生产。");
                    await Wait(machine.PauseAsync()).ConfigureAwait(false);
                    Console.WriteLine("[通过] 焊接反馈超时定位至焊接工位，反馈恢复并复位启动后继续完成产品。");
                }
                finally { cancel.Cancel(); await signals.ConfigureAwait(false); }
            }
        }

        /// <summary>在有限时间内等待样例达到目标；故障时输出工位原因。</summary>
        private static async Task WaitClipAsync(Func<bool> condition, TaskMachineController machine, string message)
        {
            var until = DateTime.UtcNow.AddSeconds(30);
            while (!condition())
            {
                var snapshot = machine.GetSnapshot();
                if (snapshot.State == TaskMachineState.Faulted) throw new InvalidOperationException(string.Join("；", snapshot.Faults.Select(item => item.Source + "：" + item.Message)));
                if (DateTime.UtcNow >= until) throw new TimeoutException(message);
                await Task.Delay(20).ConfigureAwait(false);
            }
        }

        /// <summary>等待前序工位完成后发生预期故障，不将有界的工艺运行时间误判为失败。</summary>
        private static async Task WaitClipFaultAsync(TaskMachineController machine, string message)
        {
            var until = DateTime.UtcNow.AddSeconds(30);
            while (machine.GetSnapshot().State != TaskMachineState.Faulted)
            {
                if (DateTime.UtcNow >= until) throw new TimeoutException(message);
                await Task.Delay(20).ConfigureAwait(false);
            }
        }
    }
}
