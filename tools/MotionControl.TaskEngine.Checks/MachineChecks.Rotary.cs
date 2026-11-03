using System;
using System.Collections.Generic;
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
using MotionControl.TaskEngine.Resources;

namespace MotionControl.TaskEngine.Checks
{
    /// <summary>双夹位样例的独立内存行为检查，不读取或控制真实设备。</summary>
    internal static partial class MachineChecks
    {
        /// <summary>加载隔离配置并运行生产检查，结束时等待任务控制器收尾。</summary>
        internal static int RunRotaryWeldingDemo(string root, string machineId)
        {
            int result = 0;
            try
            {
                RunRotaryWeldingDemoAsync(root, machineId).GetAwaiter().GetResult();
                foreach (string fault in new[] { "扫码失败", "夹具冲突", "视觉无效" })
                    RunRotaryFaultAsync(root, machineId, fault).GetAwaiter().GetResult();
            }
            catch (Exception error) { Console.Error.WriteLine(error); result = 1; }
            finally
            {
                foreach (TaskMachineController machine in Machines)
                {
                    try { Wait(machine.CloseAsync()).GetAwaiter().GetResult(); }
                    catch (Exception error) { Console.Error.WriteLine("[失败] 双夹位收尾：" + error.GetType().FullName + "：" + error.Message); result = 1; }
                }
                Machines.Clear();
            }
            return result;
        }

        /// <summary>在隔离内存设备中验证双工位、唯一编号和两个检测出口。</summary>
        private static async Task RunRotaryWeldingDemoAsync(string root, string machineId)
        {
            var store = new MachineConfigurationStore(root);
            var definitionsStore = new TaskDefinitionStore(store);
            var definitions = Directory.GetFiles(Path.Combine(store.GetMachineDirectory(machineId), "Tasks"), "*.json")
                .Select(path => definitionsStore.LoadForEditing(machineId, Path.GetFileName(path))).ToArray();
            var machineStore = new TaskMachineConfigurationStore(store);
            var catalog = new TaskResourceCatalogLoader(store).Load(machineId, machineStore.LoadNamedResources(machineId));
            var config = machineStore.Load(machineId, definitions, catalog);
            if (!config.Stations.Any(item => item.StationId == "RotaryTable")) throw new InvalidOperationException("设备清单不是双夹位样例。");
            foreach (TaskDefinition definition in definitions.Where(item => item.TaskId.StartsWith("Rotary", StringComparison.Ordinal)))
                SpeedUpRotaryTest(definition.Root);

            var device = new InMemoryTaskDevice(catalog);
            foreach (string axis in new[] { "Feeder_X", "Feeder_Z", "Load_X", "Transfer_Y", "Conveyor_R", "Weld_Z", "Press_Z", "Unload_X", "Buffer_X", "Buffer_Y", "Camera_X", "Camera_Z", "Reject_Z" })
                device.ConfigureAxis(axis, axis == "Conveyor_R" ? -90 : 0, true, true);
            foreach (string input in catalog.Inputs) device.SetInput(input, false);
            foreach (string output in catalog.Outputs) device.WriteOutput(output, false);
            foreach (string input in new[] { "上料到位", "取料松开到位", "A松开到位", "B松开到位", "缓存接收允许", "合格出口允许", "不良出口允许" })
                device.SetInput(input, true);
            // 先阻塞合格出口，观察产品在三处累积后再放行。
            device.SetInput("合格出口允许", false);
            var machine = new TaskMachineController(config, definitions, device);
            Machines.Add(machine);
            var signals = new RotaryTestSignals(device);
            using (var cancel = new CancellationTokenSource())
            {
                Task poll = Task.Run(async () =>
                {
                    while (!cancel.IsCancellationRequested) { signals.Tick(); await Task.Delay(5).ConfigureAwait(false); }
                });
                try
                {
                    machine.Reset(); machine.Start();
                    var completedIds = new HashSet<string>(StringComparer.Ordinal);
                    long observed = 0;
                    bool observedThreeProducts = false;
                    var timeout = DateTime.UtcNow.AddMinutes(3);
                    while (observed < 50 && DateTime.UtcNow < timeout)
                    {
                        TaskMachineSnapshot snapshot = machine.GetSnapshot();
                        if (snapshot.State == TaskMachineState.Faulted)
                            throw new InvalidOperationException("双夹位样例发生故障：" + string.Join("；", snapshot.Faults.Select(item => item.Source + ": " + item.Message)));
                        if (!observedThreeProducts && BooleanValue(device, "Rotary.WeldOccupied") == true
                            && BooleanValue(device, "Rotary.PressOccupied") == true && BooleanValue(device, "Rotary.BufferOccupied") == true)
                        {
                            string weldProduct = Value(device, "Rotary.WeldProduct");
                            string pressProduct = Value(device, "Rotary.PressProduct");
                            string bufferProduct = Value(device, "Rotary.BufferProduct");
                            if (new[] { weldProduct, pressProduct, bufferProduct }.Distinct(StringComparer.Ordinal).Count() == 3
                                && !new[] { weldProduct, pressProduct, bufferProduct }.Any(string.IsNullOrEmpty))
                            {
                                observedThreeProducts = true;
                                device.SetInput("合格出口允许", true);
                            }
                        }
                        long count = State(machine, "RotaryInspect").CompletedCycles;
                        if (count > observed)
                        {
                            if (count != observed + 1) throw new InvalidOperationException("完成计数跳变，无法逐件核对编号。");
                            string product = "SCAN-" + count.ToString("D5");
                            var matching = device.ReadRecords().Where(record => record["产品编号"] == product).ToArray();
                            var finished = matching.Where(record => record["阶段"] == "已出料").ToArray();
                            var ready = matching.Where(record => record["阶段"] == "待出料").ToArray();
                            if (!completedIds.Add(product) || finished.Length != 1 || ready.Length != 1)
                                throw new InvalidOperationException("产品记录缺失、重复或编号不符：" + product);
                            string expected = count % 2 == 1 ? "OK" : "NG";
                            string carrier = count % 2 == 1 ? "A" : "B";
                            if (matching.Any(record => record["检测结果"] != expected || record["出口"] != expected + "出口" || record["夹位"] != carrier || record["工位"] != "复检分拣"))
                                throw new InvalidOperationException("产品记录的夹位、检测结果或去向不符：" + product);
                            observed = count;
                        }
                        await Task.Delay(10).ConfigureAwait(false);
                    }
                    if (observed != 50) throw new TimeoutException("双夹位样例未完成五十件，当前完成 " + observed + " 件，状态：" + machine.GetSnapshot().State);
                    if (!observedThreeProducts) throw new InvalidOperationException("未观察到三个不同产品同时处于不同位置。");
                    await Until(() => device.ReadResources().Any(item => item.State == TaskResourceState.Held), "未能观察到工位占用资源。");
                    await Wait(machine.PauseAsync()).ConfigureAwait(false);
                    foreach (string axis in new[] { "Feeder_X", "Feeder_Z", "Load_X", "Transfer_Y", "Conveyor_R", "Weld_Z", "Press_Z", "Unload_X", "Buffer_X", "Buffer_Y", "Camera_X", "Camera_Z", "Reject_Z" })
                    {
                        var state = device.ReadAxis(axis);
                        if (state.AcceptedCommands < 2) throw new InvalidOperationException("轴未完成至少两个样例动作：" + axis);
                    }
                    await Wait(machine.AbortAsync()).ConfigureAwait(false);
                    var blocked = device.ReadResources().Where(item => item.State == TaskResourceState.Blocked).ToArray();
                    if (blocked.Length == 0) throw new InvalidOperationException("暂停后终止时未保留待确认的资源。");
                    bool closeRefused = false;
                    try { await Wait(machine.CloseAsync()).ConfigureAwait(false); }
                    catch (InvalidOperationException) { closeRefused = true; }
                    if (!closeRefused) throw new InvalidOperationException("资源待确认时错误地允许关闭并归还控制权。");
                    // 测试设备全在内存中：模拟维护人员已将输出置安全值并核对区域，再逐项显式确认。
                    foreach (string output in catalog.Outputs) device.WriteOutput(output, false);
                    foreach (var resource in blocked)
                    {
                        string reason;
                        if (!device.TryRecoverResources(resource.RequestId, "仅内存测试：已模拟人工清理区域", out reason))
                            throw new InvalidOperationException("内存测试资源确认失败：" + reason);
                    }
                    await Wait(machine.CloseAsync()).ConfigureAwait(false);
                    Console.WriteLine("[通过] 双夹位五十件唯一编号、OK/NG 交替与分拣，13 轴均接受过动作并进入暂停；仅内存设备，不验证实体停轴。");
                    Console.WriteLine("[通过] 堵住合格出口时三个不同产品分别处于焊接位、压合位和缓存位，放行后继续完成。");
                    Console.WriteLine("[通过] 暂停后终止保留待确认资源并拒绝关闭；内存测试显式模拟确认后才关闭。");
                }
                finally { cancel.Cancel(); await poll.ConfigureAwait(false); }
            }
        }

        /// <summary>仅加速隔离内存检查中的轴动作，不修改配置文件。</summary>
        private static void SpeedUpRotaryTest(TaskNode node)
        {
            if (node is MoveAxisNode move) move.Profile.Speed = 1000;
            if (node is SequenceNode sequence) foreach (TaskNode child in sequence.Children) SpeedUpRotaryTest(child);
            if (node is ConditionBranchNode branch) { SpeedUpRotaryTest(branch.WhenTrue); if (branch.WhenFalse != null) SpeedUpRotaryTest(branch.WhenFalse); }
            if (node is ResourceScopeNode scope) SpeedUpRotaryTest(scope.Body);
        }

        /// <summary>只在内存副本中缩短故障检测超时，保持样例文件不变。</summary>
        private static void ShortenRotaryWaits(TaskNode node)
        {
            if (node is WaitInputNode || node is WaitExclusiveInputsNode || node is WaitAxisPositionNode) node.TimeoutMs = 1500;
            if (node is SequenceNode sequence) foreach (TaskNode child in sequence.Children) ShortenRotaryWaits(child);
            if (node is ConditionBranchNode branch) { ShortenRotaryWaits(branch.WhenTrue); if (branch.WhenFalse != null) ShortenRotaryWaits(branch.WhenFalse); }
            if (node is ResourceScopeNode scope) ShortenRotaryWaits(scope.Body);
        }

        /// <summary>验证扫码失败、夹具反馈冲突或视觉结果无效时，不误完成产品或输出。</summary>
        private static async Task RunRotaryFaultAsync(string root, string machineId, string fault)
        {
            var store = new MachineConfigurationStore(root);
            var definitionStore = new TaskDefinitionStore(store);
            var definitions = Directory.GetFiles(Path.Combine(store.GetMachineDirectory(machineId), "Tasks"), "*.json")
                .Select(path => definitionStore.LoadForEditing(machineId, Path.GetFileName(path))).ToArray();
            var machineStore = new TaskMachineConfigurationStore(store);
            var catalog = new TaskResourceCatalogLoader(store).Load(machineId, machineStore.LoadNamedResources(machineId));
            var config = machineStore.Load(machineId, definitions, catalog);
            foreach (TaskDefinition item in definitions.Where(item => item.TaskId.StartsWith("Rotary", StringComparison.Ordinal)))
            { SpeedUpRotaryTest(item.Root); ShortenRotaryWaits(item.Root); }
            var device = new InMemoryTaskDevice(catalog);
            foreach (string axis in new[] { "Feeder_X", "Feeder_Z", "Load_X", "Transfer_Y", "Conveyor_R", "Weld_Z", "Press_Z", "Unload_X", "Buffer_X", "Buffer_Y", "Camera_X", "Camera_Z", "Reject_Z" })
                device.ConfigureAxis(axis, axis == "Conveyor_R" ? -90 : 0, true, true);
            foreach (string input in catalog.Inputs) device.SetInput(input, false);
            foreach (string output in catalog.Outputs) device.WriteOutput(output, false);
            foreach (string input in new[] { "上料到位", "取料松开到位", "A松开到位", "B松开到位", "缓存接收允许", "合格出口允许", "不良出口允许" })
                device.SetInput(input, true);
            var machine = new TaskMachineController(config, definitions, device);
            Machines.Add(machine);
            if (fault == "夹具冲突") device.SetInput("取料夹紧到位", true);
            var signals = new RotaryTestSignals(device) { Fault = fault };
            using (var cancel = new CancellationTokenSource())
            {
                Task poll = Task.Run(async () => { while (!cancel.IsCancellationRequested) { signals.Tick(); await Task.Delay(5).ConfigureAwait(false); } });
                try
                {
                    machine.Reset(); machine.Start();
                    var deadline = DateTime.UtcNow.AddSeconds(30);
                    while (machine.GetSnapshot().State != TaskMachineState.Faulted && DateTime.UtcNow < deadline)
                        await Task.Delay(10).ConfigureAwait(false);
                    var snapshot = machine.GetSnapshot();
                    if (snapshot.State != TaskMachineState.Faulted) throw new TimeoutException(fault + " 未产生停机报警。");
                    string station = fault == "视觉无效" ? "RotaryInspect" : "RotaryTable";
                    if (!snapshot.Faults.Any(item => item.Source == station)) throw new InvalidOperationException(fault + " 未定位正确工位。");
                    if (fault == "扫码失败" && (!string.IsNullOrEmpty(Value(device, "Rotary.ScanProduct")) || device.ReadAxis("Weld_Z").AcceptedCommands != 0))
                        throw new InvalidOperationException("扫码失败时错误生成编号或启动焊接。");
                    if (fault == "夹具冲突" && device.ReadAxis("Feeder_X").AcceptedCommands != 0)
                        throw new InvalidOperationException("取料夹具两个到位同时有效仍启动送料轴。");
                    if (fault == "视觉无效" && (BooleanValue(device, "Rotary.BufferOccupied") != true || signals.IllegalExitAfterInvalid))
                        throw new InvalidOperationException("视觉结果无效时丢失缓存载具或误出料。");
                    await Wait(machine.AbortAsync()).ConfigureAwait(false);
                    foreach (string output in catalog.Outputs) device.WriteOutput(output, false);
                    foreach (var resource in device.ReadResources().Where(item => item.State == TaskResourceState.Blocked).ToArray())
                    {
                        string reason;
                        if (!device.TryRecoverResources(resource.RequestId, "仅内存故障检查：已模拟清理", out reason)) throw new InvalidOperationException(reason);
                    }
                    await Wait(machine.CloseAsync()).ConfigureAwait(false);
                    Machines.Remove(machine);
                    Console.WriteLine("[通过] 双夹位故障：" + fault + "，未错误放行；仅内存设备。");
                }
                finally { cancel.Cancel(); await poll.ConfigureAwait(false); }
            }
        }

        /// <summary>在独立内存设备中提供各机构反馈与依次产生的扫码、检测结果。</summary>
        private sealed class RotaryTestSignals
        {
            /// <summary>此检查的内存设备。</summary>
            private readonly InMemoryTaskDevice _device;
            /// <summary>上一轮扫码触发。</summary>
            private bool _scannerTrigger;
            /// <summary>由扫码设备生成的本会话编号。</summary>
            private int _scanCount;
            /// <summary>上一轮相机触发。</summary>
            private bool _cameraTrigger;
            /// <summary>本会话相机触发次数。</summary>
            private int _cameraCount;
            /// <summary>此检查注入的故障名称，空值表示正常。</summary>
            public string Fault;
            /// <summary>无效结果之后是否错误出现过出料动作。</summary>
            public bool IllegalExitAfterInvalid;
            /// <summary>当前是否已经锁存过无效结果。</summary>
            private bool _invalidCameraLatched;

            /// <summary>绑定独立内存设备。</summary>
            public RotaryTestSignals(InMemoryTaskDevice device) { _device = device; }

            /// <summary>推进独立反馈；输出撤销不清除已锁存的相机结果。</summary>
            public void Tick()
            {
                foreach (string fixture in new[] { "取料", "A", "B" })
                {
                    bool closed = _device.ReadOutput(fixture + "夹紧") == true;
                    if (fixture == "取料" && Fault == "夹具冲突")
                    { _device.SetInput("取料夹紧到位", true); _device.SetInput("取料松开到位", true); }
                    else
                    { _device.SetInput(fixture + "夹紧到位", closed); _device.SetInput(fixture + "松开到位", !closed); }
                }
                foreach (var pair in new[] { new[] { "焊接启动", "焊接完成" }, new[] { "焊接启动", "焊接合格" }, new[] { "压装启动", "压装完成" }, new[] { "压装启动", "压装合格" }, new[] { "缓存交接", "缓存载具到位" }, new[] { "合格出料", "合格出料完成" }, new[] { "剔除启动", "剔除完成" } })
                    _device.SetInput(pair[1], _device.ReadOutput(pair[0]) == true);
                bool scan = _device.ReadOutput("扫码触发") == true;
                if (scan && !_scannerTrigger)
                {
                    int code = ++_scanCount;
                    if (Fault == "扫码失败") code = 0;
                    for (int i = 0; i < 16; i++) _device.SetInput("扫码数据" + i.ToString("D2"), (code & (1 << i)) != 0);
                    _device.SetInput("扫码成功", Fault != "扫码失败"); _device.SetInput("扫码完成", true);
                }
                if (!scan) { _device.SetInput("扫码完成", false); _device.SetInput("扫码成功", false); }
                _scannerTrigger = scan;
                bool camera = _device.ReadOutput("相机触发") == true;
                if (camera && !_cameraTrigger)
                {
                    bool ok = ++_cameraCount % 2 == 1;
                    _invalidCameraLatched = Fault == "视觉无效";
                    _device.SetInput("检测OK", !_invalidCameraLatched && ok); _device.SetInput("检测NG", !_invalidCameraLatched && !ok); _device.SetInput("检测完成", true);
                }
                if (_invalidCameraLatched && (_device.ReadOutput("合格出料") == true || _device.ReadOutput("剔除启动") == true)) IllegalExitAfterInvalid = true;
                _device.SetInput("相机触发反馈", camera);
                if (_device.ReadOutput("相机结果复位") == true)
                { _device.SetInput("检测OK", false); _device.SetInput("检测NG", false); _device.SetInput("检测完成", false); }
                _cameraTrigger = camera;
            }
        }
    }
}
