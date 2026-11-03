using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Preparation;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Configuration;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Device.InMemory;
using MotionControl.TaskEngine.Machine;
using MotionControl.TaskEngine.MachineDevice.Preparation;

namespace MotionControl.TaskPreparation.Checks
{
    /// <summary>验证独立任务准备与生产互斥、停止、失败、在制品保护和准备结果复查；不连接控制器。</summary>
    internal static class Program
    {
        /// <summary>读取仓库样例并运行隔离检查，失败返回非零值。</summary>
        private static int Main(string[] args)
        {
            try
            {
                string sample = args.Length == 1 ? args[0] : Path.Combine(Environment.CurrentDirectory, "src/MotionControl.TaskEngine/Samples/ClipWelding");
                CheckAsync(sample).GetAwaiter().GetResult();
                Console.WriteLine("任务工位准备检查通过：不启动生产、顺序准备、占料拦截、取消/超时、结果复查和生产后禁止重新准备。");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }

        /// <summary>用真实任务定义与内存设备验证准备边界，不读取或改写用户现场配置。</summary>
        private static async Task CheckAsync(string sample)
        {
            string root = Path.Combine(Path.GetTempPath(), "MotionControl-TaskPreparation-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "Machine01/Parameters"));
            File.Copy(Path.Combine(sample, "StationPreparation.json"), Path.Combine(root, "Machine01/Parameters/StationPreparation.json"));
            TaskMachineController machine = null;
            try
            {
                var catalog = new TaskResourceCatalog(new[] {
                    new TaskAxisInfo("Weld_Z", "Card", "mm", -10, 10), new TaskAxisInfo("Press_Z", "Card", "mm", -10, 10) },
                    new[] { "上料到位", "焊接完成", "压装完成" }, new[] { "焊接启动", "压头动作" },
                    new Dictionary<string, DeviceVariableType> { { "Clip.Weld.Occupied", DeviceVariableType.Boolean }, { "Clip.Press.Occupied", DeviceVariableType.Boolean } },
                    new TaskTeachPoint[0], variableInitialValues: new Dictionary<string, string> { { "Clip.Weld.Occupied", "false" }, { "Clip.Press.Occupied", "false" } },
                    alarmCodes: new[] { "DEMO_SENSOR_TIMEOUT" });
                var definitions = new[] { "ClipWeldPrepare", "ClipPressPrepare" }.Select(id => TaskDefinitionJson.Deserialize(File.ReadAllText(Path.Combine(sample, id + ".json")))).ToList();
                // 只缩短内存检查的反馈时限，不修改仓库样例。
                foreach (TaskDefinition task in definitions)
                    foreach (WaitInputNode wait in ((SequenceNode)task.Root).Children.OfType<WaitInputNode>()) wait.TimeoutMs = 250;
                definitions.Add(new TaskDefinition { TaskId = "Production", Name = "生产检查", Revision = 1,
                    Root = new SequenceNode { Id = "production", Name = "生产检查", TimeoutMs = 30000,
                        Children = new List<TaskNode> { new DelayNode { Id = "cycle", Name = "生产周期", TimeoutMs = 10000, Milliseconds = 3000 } } } });
                var library = definitions.ToDictionary(item => item.TaskId, StringComparer.OrdinalIgnoreCase);
                var device = new InMemoryTaskDevice(catalog);
                device.ConfigureAxis("Weld_Z", 1, true, false); device.ConfigureAxis("Press_Z", 1, true, false);
                device.SetInput("上料到位", false); device.SetInput("焊接完成", false); device.SetInput("压装完成", false);
                device.WriteOutput("焊接启动", false); device.WriteOutput("压头动作", false);
                var configuration = new TaskMachineConfiguration { MachineId = "Machine01", Name = "准备检查", Revision = 1,
                    Stations = new List<StationTaskConfiguration> { new StationTaskConfiguration { StationId = "Production", Name = "生产工位", TaskId = "Production", Mode = StationTaskMode.Cyclic } } };
                machine = new TaskMachineController(configuration, definitions, device);
                var preparation = TaskStationPreparationLoader.Load(new MachineConfigurationStore(root), "Machine01", machine, device, library);
                Ensure(preparation.Count == 2 && preparation[1].Dependencies.Single() == preparation[0].StationId, "工位依赖没有正确加载。");
                machine.Reset();
                Throws(() => machine.Start(), "未准备允许生产启动。");
                device.SetVariable("Clip.Weld.Occupied", "true");
                await ThrowsAsync(() => preparation[0].ValidateStartAsync(CancellationToken.None), "有料允许准备。");
                Ensure(device.ReadVariables().Single(item => item.Name == "Clip.Weld.Occupied").CurrentValue == "True", "准备检查清除了占料变量。");
                device.SetVariable("Clip.Weld.Occupied", "false");

                // 仅准备任务要求上料位置无料；不得在有料时下发回零。
                device.SetInput("上料到位", true);
                await ThrowsAsync(() => preparation[0].ExecuteAsync(null, CancellationToken.None), "上料有料时焊接准备未拦截。");
                await ThrowsAsync(() => preparation[1].ExecuteAsync(null, CancellationToken.None), "上料有料时压合准备未拦截。");
                Ensure(device.ReadAxis("Weld_Z").AcceptedCommands == 0 && device.ReadAxis("Press_Z").AcceptedCommands == 0,
                    "上料有料时错误下发了回零。");
                device.SetInput("上料到位", false);

                device.SetInput("焊接完成", true);
                using (var cancel = new CancellationTokenSource())
                {
                    var entered = new TaskCompletionSource<string>();
                    Task running = preparation[0].ExecuteAsync(new DirectProgress(value => { if (value.Action.Contains("反馈")) entered.TrySetResult(value.Action); }), cancel.Token);
                    await Bounded(entered.Task);
                    Ensure(await entered.Task == "等待焊接完成反馈复位", "准备进度没有定位到实际等待的末端步骤。");
                    Throws(() => machine.Start(), "准备运行期间允许生产启动。");
                    cancel.Cancel();
                    await ThrowsAsync(() => running, "取消准备没有返回中断结果。");
                }
                Ensure(device.ReadAxis("Weld_Z").AcceptedCommands == 0, "等待反馈期间错误发送回零。");
                // 反馈复位等待期间新来料，回零前应再次检查，不能沿用准备开始时的无料结果。
                var waitingFeedback = new TaskCompletionSource<string>();
                Task interruptedByMaterial = preparation[0].ExecuteAsync(
                    new DirectProgress(value => { if (value.Action.Contains("反馈")) waitingFeedback.TrySetResult(value.Action); }),
                    CancellationToken.None);
                await Bounded(waitingFeedback.Task);
                device.SetInput("上料到位", true);
                device.SetInput("焊接完成", false);
                await ThrowsAsync(() => interruptedByMaterial, "反馈等待期间来料后仍允许回零。");
                Ensure(device.ReadAxis("Weld_Z").AcceptedCommands == 0, "回零前来料后错误下发了轴动作。");
                device.SetInput("上料到位", false);
                device.SetInput("焊接完成", true);
                await ThrowsAsync(() => preparation[0].ExecuteAsync(null, CancellationToken.None), "反馈超时被当成准备完成。");
                Throws(() => machine.Start(), "准备失败后允许生产启动。");
                device.SetInput("焊接完成", false);
                foreach (IStationPreparation item in preparation)
                {
                    await item.ValidateStartAsync(CancellationToken.None);
                    await Bounded(item.ExecuteAsync(null, CancellationToken.None));
                    await item.ValidateReadyAsync(CancellationToken.None);
                }
                Ensure(machine.GetSnapshot().Stations.All(item => item.CompletedCycles == 0 && item.Run == null), "准备结束后自动启动了生产。");
                Ensure(device.ReadAxis("Weld_Z").Homed && device.ReadAxis("Press_Z").Homed, "实际回零结果未完成。");
                device.SetInput("压装完成", true);
                Throws(() => machine.Start(), "准备后反馈变化未重新检查。");
                device.SetInput("压装完成", false);
                // 已准备完成后允许正常来料和生产，不把准备前置条件扩大为生产启动条件。
                device.SetInput("上料到位", true);
                machine.Start(); await Task.Delay(30); await machine.PauseAsync();
                await ThrowsAsync(() => preparation[0].ValidateStartAsync(CancellationToken.None), "生产启动过的会话允许重新准备。");
                device.SetInput("压装完成", true);
                machine.Reset(); machine.Start(); await Task.Delay(30); await machine.PauseAsync();
                Ensure(machine.GetSnapshot().Stations.Single().Run != null, "正常暂停续跑丢失生产步骤。");
            }
            finally
            {
                if (machine != null) await machine.CloseAsync();
                // 仅删除本检查自己创建且带唯一前缀的临时目录。
                if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true);
            }
        }

        /// <summary>限制检查等待时间，防止停止故障使检查永远挂起。</summary>
        private static async Task Bounded(Task task)
        {
            if (await Task.WhenAny(task, Task.Delay(8000)) != task) throw new InvalidOperationException("检查等待超时。");
            await task;
        }

        /// <summary>断言现场相关行为，不允许静默跳过。</summary>
        private static void Ensure(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

        /// <summary>检查同步入口拒绝无效操作。</summary>
        private static void Throws(Action action, string message)
        {
            try { action(); } catch (InvalidOperationException) { return; }
            throw new InvalidOperationException(message);
        }

        /// <summary>检查异步入口返回故障或取消，而不是假成功。</summary>
        private static async Task ThrowsAsync(Func<Task> action, string message)
        {
            try { await Bounded(action()); } catch (OperationCanceledException) { return; } catch (InvalidOperationException error) when (error.Message != "检查等待超时。") { return; }
            throw new InvalidOperationException(message);
        }

        /// <summary>同步收集步骤进度，检查不依赖 UI 同步上下文。</summary>
        private sealed class DirectProgress : IProgress<StationPreparationProgress>
        {
            /// <summary>进度验证回调。</summary>
            private readonly Action<StationPreparationProgress> _report;
            /// <summary>保存检查回调。</summary>
            internal DirectProgress(Action<StationPreparationProgress> report) { _report = report; }
            /// <summary>把真实步骤进度传给检查。</summary>
            public void Report(StationPreparationProgress value) { _report(value); }
        }
    }
}
