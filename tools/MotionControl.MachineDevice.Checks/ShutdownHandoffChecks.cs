using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Runtime.Machine;
using MotionControl.TaskEngine.Configuration;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Machine;
using MotionControl.TaskEngine.Resources;

namespace MotionControl.MachineDevice.Checks
{
    /// <summary>验证任务停止后保留隔离记录退出，不把输出保持等同设备安全。</summary>
    internal static partial class Program
    {
        /// <summary>覆盖完成交接、断连策略未知、读失败、输出变化、记录损坏和设备调用在途。</summary>
        private static void ShutdownHandoffChecks()
        {
            foreach (string scenario in new[] { "成功", "断卡失败", "断连未知", "读取失败", "读取迟到", "输出变化", "记录损坏", "调用在途", "输出在途" })
                ShutdownHandoffScenarioAsync(scenario).GetAwaiter().GetResult();
        }

        /// <summary>每次使用独立临时设备，验证未确认状态不能意外释放。</summary>
        private static async Task ShutdownHandoffScenarioAsync(string scenario)
        {
            string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "mcf-exit-handoff-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(root);
            try
            {
                var axis = new FakeAxis("X");
                var io = new FakeLogicalIo();
                var device = CreateDevice(axis, io, out _, out MachineRuntime runtime,
                    journal: new TaskResourceJournal(root, "M1"), outputPreserved: scenario != "断连未知",
                    disconnectError: scenario == "断卡失败" ? new IOException("控制器断开失败") : null);
                await runtime.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
                var task = new TaskDefinition { TaskId = "exit", Name = "退出检查", Root = new SequenceNode {
                    Id = "root", Name = "步骤", Children = new List<TaskNode> { new DelayNode { Id = "wait", Name = "等待", Milliseconds = 1 } } } };
                var config = new TaskMachineConfiguration { MachineId = "M1", Name = "退出检查",
                    Stations = new List<StationTaskConfiguration> { new StationTaskConfiguration {
                        StationId = "S", Name = "工位", TaskId = "exit", Enabled = true, Mode = StationTaskMode.Once } } };
                var machine = new TaskMachineController(config, new[] { task }, device, runtime.AlarmService);
                var request = device.Resources.Enqueue("run", "task", "工位", "node", "待确认", "call", new[] { "Axis:X" });
                Ensure(SpinWait.SpinUntil(() => device.Resources.TryAcquire(request, out _), 5000), "隔离记录未保存。");
                device.Resources.Block(request, "现场待确认");
                await io.WriteOutputAsync("Vacuum", true, CancellationToken.None).ConfigureAwait(false);
                Task delayedWrite = null;
                if (scenario == "输出在途")
                {
                    io.PendingOutputWrite = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    delayedWrite = Task.Run(() => device.WriteOutput("Vacuum", true));
                    Ensure(SpinWait.SpinUntil(() => Volatile.Read(ref io.OutputWrites) >= 2, 5000), "测试输出命令未发出。");
                }
                if (scenario == "读取失败") io.OutputReadError = new IOException("输出读取故障");
                if (scenario == "读取迟到") io.PendingOutputRead = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (scenario == "输出变化") io.FlipOutputAfterRead = true;
                if (scenario == "调用在途") device.RegisterRun("still-running", machine);
                if (scenario == "记录损坏")
                {
                    string guard = Directory.GetFiles(Path.Combine(root, "Runtime", "ResourceGuards"), "*.guard", SearchOption.AllDirectories).Single();
                    File.WriteAllText(guard, "损坏");
                }
                await runtime.BeginShutdownAsync(CancellationToken.None).ConfigureAwait(false);
                if (scenario == "断连未知")
                    ExpectMessage(() => runtime.VerifyShutdownOutputsAsync(new string[0], CancellationToken.None).GetAwaiter().GetResult(), "尚未验证");
                int writesBefore = io.OutputWrites;
                ExpectMessage(() => device.WriteOutput("Vacuum", false), "尚未下发");
                ExpectMessage(() => device.BeginMove("X", 1, false, false, Profile()), "尚未下发");
                Ensure(io.OutputWrites == writesBefore && axis.MoveCalls == 0, "关闭意图建立后仍发出了新的任务轴或输出命令。");
                if (scenario != "成功" && scenario != "断卡失败")
                {
                    string expected = scenario == "断连未知" ? "尚未验证" : scenario == "读取失败" ? "读取输出" : scenario == "读取迟到" ? "超时"
                        : scenario == "输出变化" ? "状态发生变化" : scenario == "记录损坏" ? "缺失或损坏" : "仍在收尾";
                    Task closing = machine.CloseForShutdownAsync();
                    if (scenario == "读取迟到")
                    {
                        Ensure(!closing.IsCompleted, "读取尚未返回却提前完成退出交接。");
                        ExpectMessage(() => machine.CloseAsync().GetAwaiter().GetResult(), "退出交接正在执行");
                    }
                    ExpectMessage(() => closing.GetAwaiter().GetResult(), expected);
                    Ensure(!runtime.IsShutdownSealed && runtime.IsTaskEngineControlClaimed && device.ReadResources().Count == 1,
                        scenario + "时不应释放控制权或隔离。");
                    if (scenario == "调用在途") device.UnregisterRun("still-running");
                    if (scenario == "读取迟到")
                    {
                        ExpectMessage(() => machine.CloseForShutdownAsync().GetAwaiter().GetResult(), "上次退出核验");
                        ExpectMessage(() => runtime.DisconnectAsync(CancellationToken.None).GetAwaiter().GetResult(), "上次退出核验");
                        io.PendingOutputRead.TrySetResult(true);
                        io.PendingOutputRead = null;
                        await machine.CloseForShutdownAsync().ConfigureAwait(false);
                        Ensure(runtime.IsShutdownSealed, "迟到读取结束后未能重试交接。");
                    }
                    if (scenario == "输出在途")
                    {
                        io.PendingOutputWrite.TrySetResult(true);
                        await delayedWrite.ConfigureAwait(false);
                        await machine.CloseForShutdownAsync().ConfigureAwait(false);
                        Ensure(runtime.IsShutdownSealed && io.OutputValue, "迟到输出完全收尾后未能安全交接或误改输出。");
                    }
                    return;
                }
                await machine.CloseForShutdownAsync().ConfigureAwait(false);
                // 已封存后常规关闭只能复用成功结果，不能删除日志或提前归还控制权。
                Task sealedClose = machine.CloseForShutdownAsync();
                Ensure(ReferenceEquals(sealedClose, machine.CloseAsync()), "两种关闭入口未复用已成功的封存结果。");
                await machine.CloseAsync().ConfigureAwait(false);
                Ensure(runtime.IsShutdownSealed && runtime.IsTaskEngineControlClaimed && device.ReadResources().Count == 1,
                    "交接成功后必须仍由封存运行时持有控制权和隔离。");
                Ensure(io.OutputValue && io.OutputWrites == 1, "退出交接不能改变夹紧或吸附输出。");
                Ensure(TaskResourceJournal.Inspect(root, "M1")?.Contains("Axis:X") == true,
                    "模拟重启检查未发现保留的待确认资源。");
                Ensure(!device.Resources.Recover(request), "退出交接后资源被错误放行。");
                ExpectMessage(() => runtime.ResetAsync(CancellationToken.None).GetAwaiter().GetResult(), "正在安全退出");
                ExpectMessage(() => runtime.AcquireTaskEngineControlAsync(new object(), CancellationToken.None).GetAwaiter().GetResult(), "正在安全退出");
                ExpectMessage(() => runtime.ReleaseTaskEngineControl(device), "不能提前释放");
                bool called = false;
                var reason = await runtime.RunManualOperationAsync(() => { called = true; return Task.CompletedTask; }, CancellationToken.None).ConfigureAwait(false);
                Ensure(reason != ManualOperationBlockReason.None && !called, "退出交接期间错误放行手动动作。");
                if (scenario == "断卡失败")
                {
                    ExpectMessage(() => runtime.DisconnectAsync(CancellationToken.None).GetAwaiter().GetResult(), "断开失败");
                    Ensure(runtime.IsShutdownSealed && runtime.IsTaskEngineControlClaimed && io.OutputValue
                        && TaskResourceJournal.Inspect(root, "M1")?.Contains("Axis:X") == true,
                        "断卡失败后未保留封存、控制权、输出和旧证据。");
                    return;
                }
                await runtime.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
                Ensure(TaskResourceJournal.Inspect(root, "M1")?.Contains("Axis:X") == true, "安全断卡后错误删除资源证据。");
            }
            finally
            {
                string temp = Path.GetFullPath(Path.GetTempPath());
                if (root.StartsWith(temp, StringComparison.OrdinalIgnoreCase) && Directory.Exists(root)) Directory.Delete(root, true);
            }
        }
    }
}
