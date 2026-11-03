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
            await CheckClipInitializationRejectsOccupiedAsync(config, definitions, catalog).ConfigureAwait(false);
            // 仅缩短此内存实例的等待时限，不修改样例文件或现场配置。
            var weld = (SequenceNode)definitions.Single(item => item.TaskId == "ClipWeld").Root;
            ((WaitInputNode)weld.Children.Single(item => item.Id == "weldDone")).TimeoutMs = 1200;
            ((WaitInputNode)weld.Children.Single(item => item.Id == "weldReady")).TimeoutMs = 1200;
            ((WaitInputNode)weld.Children.Single(item => item.Id == "weldPrecheck")).TimeoutMs = 1200;
            var inspect = (SequenceNode)definitions.Single(item => item.TaskId == "ClipInspect").Root;
            inspect.Children.Single(item => item.Id == "cameraValidateResult").TimeoutMs = 1200;
            var device = new InMemoryTaskDevice(catalog);
            PrepareClipTestDevice(device);
            device.SetVariable("Clip.ReceiveEnabled", "false");
            // 模拟上次会话留下的完成记录，初始化后不能误认为当前会话已产出。
            foreach (string name in new[] { "Clip.LastProduct", "Clip.LastCarrier", "Clip.LastResult", "Clip.LastDestination" })
                device.SetVariable(name, "上一会话");
            var machine = new TaskMachineController(config, definitions, device); Machines.Add(machine);
            // 3 表示焊接轴开始运动后卡在有效，2 表示一直有效，1 表示一直无效，0 表示根据输出正常变化。
            var signalsModel = new ClipTestSignals(device) { WeldFeedbackFault = 2 };
            using (var cancel = new CancellationTokenSource())
            {
                Task signals = Task.Run(async () =>
                {
                    while (!cancel.IsCancellationRequested)
                    {
                        signalsModel.Tick();
                        await Task.Delay(20).ConfigureAwait(false);
                    }
                });
                try
                {
                    machine.Reset(); machine.Start();
                    await WaitClipFaultAsync(machine, "焊接旧反馈未复位时未触发停机。");
                    CheckAssert.True(machine.GetSnapshot().Faults.Any(item => item.Source == "ClipWeld"), "旧反馈故障未定位焊接工位。");
                    CheckAssert.True(device.ReadOutput("焊接启动") == false && State(machine, "ClipWeld").CompletedCycles == 0,
                        "旧反馈未复位时，不应打开焊接测试输出或完成周期。");
                    CheckAssert.True(device.ReadAxis("Weld_Z").AcceptedCommands == 0, "旧反馈未复位时，不应发送焊接轴运动。");
                    Volatile.Write(ref signalsModel.WeldFeedbackFault, 0);
                    await Until(() => device.ReadInput("焊接完成") == false, "旧反馈未恢复无效。");
                    Volatile.Write(ref signalsModel.WeldFeedbackFault, 3);
                    machine.Reset(); machine.Start();
                    await WaitClipFaultAsync(machine, "焊接轴运动期间反馈异常未触发停机。");
                    CheckAssert.True(machine.GetSnapshot().Faults.Any(item => item.Source == "ClipWeld"), "运动期间反馈故障未定位焊接工位。");
                    CheckAssert.True(device.ReadAxis("Weld_Z").AcceptedCommands == 1 && device.ReadOutput("焊接启动") == false,
                        "焊接轴运动期间反馈异常时，不应打开焊接测试输出。");
                    Volatile.Write(ref signalsModel.WeldFeedbackFault, 0);
                    await Until(() => device.ReadInput("焊接完成") == false, "运动期间异常反馈未恢复无效。");
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
                    Volatile.Write(ref signalsModel.WeldFeedbackFault, 1);
                    machine.Reset(); machine.Start();
                    await Until(() => machine.GetSnapshot().State == TaskMachineState.Faulted, "焊接反馈故障未触发停机。");
                    CheckAssert.True(machine.GetSnapshot().Faults.Any(item => item.Source == "ClipWeld"), "故障未定位焊接工位。");
                    Volatile.Write(ref signalsModel.WeldFeedbackFault, 0);
                    machine.Reset(); machine.Start();
                    await WaitClipAsync(() => State(machine, "ClipInspect").CompletedCycles > previousCount, machine, "恢复焊接反馈后未继续生产。");
                    await Wait(machine.PauseAsync()).ConfigureAwait(false);
                    Console.WriteLine("[通过] 焊接反馈超时定位至焊接工位，反馈恢复并复位启动后继续完成产品。");
                }
                finally { cancel.Cancel(); await signals.ConfigureAwait(false); }
            }
            await CheckInvalidClipResultAsync(config, definitions, catalog, 1).ConfigureAwait(false);
            await CheckInvalidClipResultAsync(config, definitions, catalog, 2).ConfigureAwait(false);
        }

        /// <summary>准备隔离内存设备的五根测试轴、独立输出及明确初始输入。</summary>
        private static void PrepareClipTestDevice(InMemoryTaskDevice device)
        {
            foreach (string axis in new[] { "Load_X", "Weld_Z", "Press_Z", "Camera_X", "Unload_X" }) device.ConfigureAxis(axis, 0, true, true);
            foreach (string output in new[] { "夹具夹紧", "焊接启动", "压头动作", "相机触发", "相机结果复位", "下料电磁阀" })
                device.WriteOutput(output, false);
            foreach (string input in new[] { "夹具夹紧到位", "焊接完成", "压装完成", "相机触发反馈", "检测完成", "检测OK", "检测NG", "下料到位" })
                device.SetInput(input, false);
            device.SetInput("夹具松开到位", true);
            device.SetInput("上料到位", true);
        }

        /// <summary>不连接控制器，模拟夹具到位、焊压反馈和相机触发后锁存结果。</summary>
        private sealed class ClipTestSignals
        {
            /// <summary>本检查专用内存设备。</summary>
            private readonly InMemoryTaskDevice _device;
            /// <summary>焊接反馈故障：0 正常，1 一直无效，2 一直有效，3 焊接轴动作后有效。</summary>
            public int WeldFeedbackFault;
            /// <summary>视觉结果：0 交替 OK/NG，1 两者均无效，2 两者均有效。</summary>
            public int CameraResultMode;
            /// <summary>无效结果锁存后，仍出现下料启动的次数。</summary>
            public int UnloadsAfterInvalid;
            /// <summary>夹具上次命令状态。</summary>
            private bool _clampCommand;
            /// <summary>夹具目标到位的时间，初始已经松开。</summary>
            private DateTime _clampDue = DateTime.MinValue;
            /// <summary>下一件物料到位时间。</summary>
            private DateTime _feedDue = DateTime.MinValue;
            /// <summary>上次看到的相机触发电平。</summary>
            private bool _cameraTrigger;
            /// <summary>当前相机处理的预计完成时间。</summary>
            private DateTime _cameraDue = DateTime.MinValue;
            /// <summary>本内存检查中已经接受的触发数量。</summary>
            private int _exposures;
            /// <summary>当前相机输出的无效结果是否仍锁存。</summary>
            private bool _invalidResult;
            /// <summary>上次看到的下料输出电平。</summary>
            private bool _unloadCommand;

            /// <summary>绑定单独的内存设备，不接触外部配置或软件控制器。</summary>
            public ClipTestSignals(InMemoryTaskDevice device) { _device = device; }

            /// <summary>在故障暂停期间模拟外部结果信号被修复；只属于本内存检查，不代表相机可改写锁存。</summary>
            public void CorrectLatchedResult()
            {
                if (_device.ReadInput("检测完成") != true || !_invalidResult)
                    throw new InvalidOperationException("当前没有等待处理的无效检测结果。");
                _device.SetInput("检测OK", true);
                _device.SetInput("检测NG", false);
                _invalidResult = false;
                Volatile.Write(ref CameraResultMode, 0);
            }

            /// <summary>推进测试设备状态；相机触发下降不取消处理，完成与结果由复位输出清除。</summary>
            public void Tick()
            {
                DateTime now = DateTime.UtcNow;
                bool clamp = _device.ReadOutput("夹具夹紧") == true;
                if (clamp != _clampCommand)
                {
                    _clampCommand = clamp;
                    _device.SetInput("夹具夹紧到位", false);
                    _device.SetInput("夹具松开到位", false);
                    _clampDue = now.AddMilliseconds(100);
                    if (!clamp) { _device.SetInput("上料到位", false); _feedDue = now.AddMilliseconds(400); }
                }
                if (_clampDue != DateTime.MinValue && now >= _clampDue)
                {
                    _device.SetInput("夹具夹紧到位", _clampCommand);
                    _device.SetInput("夹具松开到位", !_clampCommand);
                    _clampDue = DateTime.MinValue;
                }
                if (_feedDue != DateTime.MinValue && now >= _feedDue)
                { _device.SetInput("上料到位", true); _feedDue = DateTime.MinValue; }

                int fault = Volatile.Read(ref WeldFeedbackFault);
                _device.SetInput("焊接完成", fault == 2 || (fault == 3 && _device.ReadAxis("Weld_Z").AcceptedCommands > 0)
                    || (fault == 0 && _device.ReadOutput("焊接启动") == true));
                _device.SetInput("压装完成", _device.ReadOutput("压头动作") == true);

                bool trigger = _device.ReadOutput("相机触发") == true;
                bool reset = _device.ReadOutput("相机结果复位") == true;
                if (reset && !trigger)
                {
                    _cameraDue = DateTime.MinValue;
                    _device.SetInput("检测完成", false);
                    _device.SetInput("检测OK", false);
                    _device.SetInput("检测NG", false);
                    _invalidResult = false;
                }
                if (trigger && !_cameraTrigger && !reset)
                {
                    _cameraDue = now.AddMilliseconds(150);
                    _exposures++;
                }
                _cameraTrigger = trigger;
                _device.SetInput("相机触发反馈", trigger && !reset);
                if (_cameraDue != DateTime.MinValue && now >= _cameraDue)
                {
                    int result = Volatile.Read(ref CameraResultMode);
                    bool ok = result == 2 || (result == 0 && _exposures % 2 == 1);
                    bool ng = result == 2 || (result == 0 && _exposures % 2 == 0);
                    _device.SetInput("检测OK", ok);
                    _device.SetInput("检测NG", ng);
                    _device.SetInput("检测完成", true);
                    _invalidResult = result != 0;
                    _cameraDue = DateTime.MinValue;
                }
                bool unload = _device.ReadOutput("下料电磁阀") == true;
                if (unload && !_unloadCommand && _invalidResult) Interlocked.Increment(ref UnloadsAfterInvalid);
                _unloadCommand = unload;
                _device.SetInput("下料到位", unload);
            }
        }

        /// <summary>结果双低或双高不能转成 NG 放行；校验故障前无下料动作且载具保持占用。</summary>
        private static async Task CheckInvalidClipResultAsync(TaskMachineConfiguration config,
            TaskDefinition[] definitions, TaskResourceCatalog catalog, int invalidMode)
        {
            var device = new InMemoryTaskDevice(catalog);
            PrepareClipTestDevice(device);
            device.SetVariable("Clip.ReceiveEnabled", "true");
            var model = new ClipTestSignals(device) { CameraResultMode = invalidMode };
            var machine = new TaskMachineController(config, definitions, device);
            using (var cancel = new CancellationTokenSource())
            {
                Task signals = Task.Run(async () =>
                {
                    while (!cancel.IsCancellationRequested) { model.Tick(); await Task.Delay(20).ConfigureAwait(false); }
                });
                try
                {
                    machine.Reset(); machine.Start();
                    await WaitClipFaultAsync(machine, "无效视觉结果未触发故障。").ConfigureAwait(false);
                    CheckAssert.True(machine.GetSnapshot().Faults.Any(item => item.Source == "ClipInspect"), "视觉故障未定位复检工位。");
                    CheckAssert.True(BooleanValue(device, "Clip.Inspect.Occupied") == true
                        && device.ReadOutput("下料电磁阀") == false && Volatile.Read(ref model.UnloadsAfterInvalid) == 0
                        && State(machine, "ClipInspect").CompletedCycles == 0,
                        "视觉结果无效时错误下料或丢失在制品。");
                    model.CorrectLatchedResult();
                    machine.Reset(); machine.Start();
                    await WaitClipAsync(() => State(machine, "ClipInspect").CompletedCycles >= 1, machine, "外部输入纠正后未恢复该产品。").ConfigureAwait(false);
                    CheckAssert.True(Value(device, "Clip.LastResult") == "OK" && Value(device, "Clip.LastDestination") == "OK缓存",
                        "输入修复后未重新采样当前结果或选择了错误去向。");
                    await Wait(machine.PauseAsync()).ConfigureAwait(false);
                    Console.WriteLine("[通过] 视觉结果" + (invalidMode == 1 ? "双低" : "双高") + "时报警且不下料；内存检查中人工纠正输入后重采样并完成。相机锁存的现场恢复尚未验证。");
                }
                finally { await Wait(machine.CloseAsync()).ConfigureAwait(false); cancel.Cancel(); await signals.ConfigureAwait(false); }
            }
        }

        /// <summary>逐个验证有料时初始化不会清除占料和交接信息；只运行独立内存设备。</summary>
        private static async Task CheckClipInitializationRejectsOccupiedAsync(TaskMachineConfiguration config,
            TaskDefinition[] definitions, TaskResourceCatalog catalog)
        {
            var init = (SequenceNode)definitions.Single(item => item.TaskId == "ClipInit").Root;
            var guard = (WaitConditionNode)init.Children.Single(item => item.Id == "checkAllStationsEmpty");
            int originalTimeout = guard.TimeoutMs;
            guard.TimeoutMs = 250;
            try
            {
                foreach (string occupied in new[] { "Clip.Scan.Occupied", "Clip.Weld.Occupied", "Clip.Press.Occupied", "Clip.Inspect.Occupied" })
                {
                    var device = new InMemoryTaskDevice(catalog);
                    foreach (string axis in new[] { "Load_X", "Weld_Z", "Press_Z", "Camera_X", "Unload_X" }) device.ConfigureAxis(axis, 0, true, true);
                    device.SetVariable(occupied, "true");
                    device.SetVariable("Clip.SW.Ready", "true");
                    device.SetVariable("Clip.LastProduct", "保留上次产品");
                    var machine = new TaskMachineController(config, definitions, device);
                    try
                    {
                        machine.Reset(); machine.Start();
                        await WaitClipFaultAsync(machine, occupied + " 有料时初始化未中止。").ConfigureAwait(false);
                        CheckAssert.True(BooleanValue(device, occupied) == true, occupied + " 有料标记被初始化清除。");
                        CheckAssert.True(BooleanValue(device, "Clip.SW.Ready") == true, occupied + " 有料时交接信号被清除。");
                        CheckAssert.True(Value(device, "Clip.LastProduct") == "保留上次产品", occupied + " 有料时产品记录被清除。");
                        CheckAssert.True(machine.GetSnapshot().Stations.All(item => item.CompletedCycles == 0 && item.Run == null),
                            occupied + " 有料时生产工位被启动。");
                    }
                    finally { await Wait(machine.CloseAsync()).ConfigureAwait(false); }
                }
                Console.WriteLine("[通过] 任一工位有料时初始化报警，保留占料、交接信号和上次产品信息，不启动生产。");
            }
            finally { guard.TimeoutMs = originalTimeout; }
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
