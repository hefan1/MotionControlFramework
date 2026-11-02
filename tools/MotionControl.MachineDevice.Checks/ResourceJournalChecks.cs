using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Execution;
using MotionControl.TaskEngine.Machine;
using MotionControl.TaskEngine.Resources;

namespace MotionControl.MachineDevice.Checks
{
    /// <summary>资源记录只使用临时目录，模拟进程重启和存储故障。</summary>
    internal static partial class Program
    {
        /// <summary>取得前落盘、重启拦截、正常清理和写失败不放行。</summary>
        private static void ResourceJournalChecks()
        {
            string root = Path.Combine(Path.GetTempPath(), "mcf-guard-check-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var journal = new TaskResourceJournal(root, "M1");
                var coordinator = new TaskResourceCoordinator(new[] { "Zone:Weld" }, journal);
                var condition = new TaskInterlock();
                condition.Inputs.Add(new TaskInputInterlock { InputName = "夹具松开", Expected = true });
                var request = coordinator.Enqueue("run", "task", "焊接", "step", "退离", "call", new[] { "Zone:Weld" }, condition);
                Ensure(SpinWait.SpinUntil(() => coordinator.TryAcquire(request, out _), 5000), "资源记录写入后未取得资源。");
                string warning = TaskResourceJournal.Inspect(root, "M1");
                Ensure(warning != null && warning.Contains("Zone:Weld") && warning.Contains("退离"), "模拟重启未保留资源和步骤。");
                string path = Directory.GetFiles(Path.Combine(root, "Runtime", "ResourceGuards"), "*.guard", SearchOption.AllDirectories).Single();
                Ensure(File.ReadAllText(path).Contains("夹具松开"), "退出条件没有持久保存。");
                coordinator.Release(request);
                Ensure(TaskResourceJournal.Inspect(root, "M1") != null, "运行中释放资源过早删除重启证据。");
                journal.ClearSafeSession();
                Ensure(TaskResourceJournal.Inspect(root, "M1") == null, "安全收尾后记录未清理。");

                // 模拟大量周期和并行工位：运行编号变化不能造成文件数持续增长。
                var repeated = new TaskResourceJournal(root, "M1");
                Func<int, int, TaskResourceRequest> buildRequest = (cycle, variant) =>
                {
                    var exit = new TaskInterlock();
                    exit.Inputs.Add(new TaskInputInterlock { InputName = "夹具松开", Expected = variant != 1 });
                    return new TaskResourceRequest("run-" + cycle, variant == 3 ? "other-task" : "task",
                        variant == 3 ? "其他工位" : "焊接", variant == 2 ? "step2" : "step", "退出",
                        "execution-" + cycle, new List<string> { variant == 2 ? "Axis:X" : "Zone:Weld" }, exit, null);
                };
                Parallel.For(0, 1000, cycle => repeated.Save(buildRequest(cycle, cycle % 4)));
                string[] retained = Directory.GetFiles(Path.Combine(root, "Runtime", "ResourceGuards"), "*.guard", SearchOption.AllDirectories);
                Ensure(retained.Length == 4, "一千次申请后记录数应只由资源、任务、节点和退出条件的组合决定。");
                Ensure(retained.Any(file => File.ReadAllText(file).Contains("\"Expected\":false"))
                    && retained.Any(file => File.ReadAllText(file).Contains("\"Expected\":true")), "不同退出条件被错误合并。");
                string corrupted = retained.Single(file => File.ReadAllText(file).Contains("step2"));
                File.WriteAllText(corrupted, "损坏");
                bool reusedCorrupt = false;
                try { repeated.Save(buildRequest(1001, 2)); reusedCorrupt = true; } catch (IOException) { }
                Ensure(!reusedCorrupt && File.ReadAllText(corrupted) == "损坏", "已有记录损坏后错误复用或覆盖原始证据。");
                repeated.ClearSafeSession();

                for (int iteration = 0; iteration < 10; iteration++)
                {
                    var cancelledJournal = new TaskResourceJournal(root, "M1");
                    var cancelled = new TaskResourceCoordinator(new[] { "Zone:Weld" }, cancelledJournal);
                    var waiting = cancelled.Enqueue("run", "task", "焊接", "step", "退离", "call", new[] { "Zone:Weld" });
                    cancelled.TryAcquire(waiting, out _);
                    cancelled.Release(waiting);
                    cancelled.SettleJournalWritesAsync().GetAwaiter().GetResult();
                    Ensure(cancelled.Read().Count == 0, "取消写入中的申请后未完成队列收尾。");
                    cancelledJournal.ClearSafeSession();
                    Ensure(TaskResourceJournal.Inspect(root, "M1") == null, "取消收尾之后仍有迟到记录。");
                }

                string bad = Path.Combine(root, "Runtime", "ResourceGuards", "broken");
                Directory.CreateDirectory(bad);
                File.WriteAllText(Path.Combine(bad, "bad.guard"), "{");
                bool rejected = false;
                try { TaskResourceJournal.Inspect(root, "M1"); } catch { rejected = true; }
                Ensure(rejected, "损坏的资源记录被当作空设备。");
                Directory.Delete(bad, true);

                string blockedRoot = Path.Combine(root, "write-failure");
                Directory.CreateDirectory(blockedRoot);
                File.WriteAllText(Path.Combine(blockedRoot, "Runtime"), "不是目录");
                var failed = new TaskResourceCoordinator(new[] { "Zone:Weld" }, new TaskResourceJournal(blockedRoot, "M1"));
                var denied = failed.Enqueue("run", "task", "焊接", "step", "退离", "call", new[] { "Zone:Weld" });
                try { failed.TryAcquire(denied, out _); } catch { }
                Ensure(SpinWait.SpinUntil(() => failed.Read().Any(x => x.State == TaskResourceState.Blocked), 5000), "写失败没有隔离资源。");
                failed.Release(denied);
                Ensure(!failed.Recover(denied) && failed.Read().Count == 1, "写失败可被普通恢复绕过。");
            }
            finally { Directory.Delete(root, true); }
        }

        /// <summary>关闭时实际设备按顺序核对资源和清理失败，并允许故障排除后重试。</summary>
        private static void ResourceJournalCloseChecks()
        {
            string root = Path.Combine(Path.GetTempPath(), "mcf-guard-close-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var journal = new TaskResourceJournal(root, "M1");
                var device = CreateDevice(new FakeAxis("X"), new FakeLogicalIo(), out _, out var runtime, journal: journal);
                var task = new TaskDefinition { TaskId = "T", Name = "任务", Root = new SequenceNode {
                    Id = "root", Name = "步骤", Children = new List<TaskNode> { new DelayNode { Id = "delay", Name = "延时", Milliseconds = 1 } } } };
                var configuration = new TaskMachineConfiguration { MachineId = "M1", Name = "设备", Stations = new List<StationTaskConfiguration> {
                    new StationTaskConfiguration { StationId = "S", Name = "工位", TaskId = "T", Enabled = true, Mode = StationTaskMode.Once } } };
                var controller = new TaskMachineController(configuration, new[] { task }, device, runtime.AlarmService);
                var request = device.Resources.Enqueue("run", "T", "任务", "node", "步骤", "call", new[] { "Axis:X" });
                Ensure(SpinWait.SpinUntil(() => device.Resources.TryAcquire(request, out _), 5000), "测试资源未取得。");
                ExpectMessage(() => controller.CloseAsync().GetAwaiter().GetResult(), "尚未全部收尾");
                Ensure(runtime.IsTaskEngineControlClaimed && TaskResourceJournal.Inspect(root, "M1") != null, "占用时错误清理或归还控制权。");
                device.Resources.Release(request);
                string session = Directory.GetDirectories(Path.Combine(root, "Runtime", "ResourceGuards")).Single();
                string obstacle = Path.Combine(session, "obstacle");
                Directory.CreateDirectory(obstacle);
                ExpectMessage(() => controller.CloseAsync().GetAwaiter().GetResult(), "不是空");
                Ensure(runtime.IsTaskEngineControlClaimed, "清理失败时错误归还控制权。");
                Directory.Delete(obstacle);
                controller.CloseAsync().GetAwaiter().GetResult();
                Ensure(!runtime.IsTaskEngineControlClaimed && TaskResourceJournal.Inspect(root, "M1") == null,
                    "排除清理故障后没有安全关闭并清理记录。");
            }
            finally { Directory.Delete(root, true); }
        }

        /// <summary>指令已进入驱动后，写入或回读错误都不能释放未知状态的输出。</summary>
        private static void FailedOutputIsIsolated()
        {
            foreach (bool failRead in new[] { false, true })
            foreach (bool parentScope in new[] { false, true })
            {
                var io = new FakeLogicalIo();
                if (failRead) io.OutputReadError = new InvalidOperationException("读取失败");
                else io.WriteError = new InvalidOperationException("写入失败");
                var device = CreateDevice(new FakeAxis("X"), io, out _, out _);
                device.ClaimMachine(null);
                TaskNode output = new SetOutputNode { Id = "out", Name = "执行", OutputName = "Vacuum", Value = true };
                if (parentScope) output = new ResourceScopeNode { Id = "scope", Name = "区域",
                    Resources = new List<string> { "Output:Vacuum" }, Body = new SequenceNode {
                        Id = "body", Name = "正文", Children = new List<TaskNode> { output } } };
                var task = new TaskDefinition { TaskId = "Output", Name = "输出", Root = new SequenceNode {
                    Id = "root", Name = "步骤", Children = new List<TaskNode> { output } } };
                var result = WaitRun(new TaskExecutionEngine().Start(task, device));
                Ensure(io.OutputValue && result.State == TaskRunState.Failed && device.ReadResources().Single().State == TaskResourceState.Blocked,
                    "输出异常未隔离实际状态未知的资源。");
                ExpectMessage(() => device.ReleaseMachine(null), "待确认");
            }
        }

        /// <summary>真实工位节点故障等待复位时，父范围必须已经隔离并阻止再次下发。</summary>
        private static void StationOutputFaultBlocksBeforeRecovery()
        {
            foreach (bool failRead in new[] { false, true })
            {
                var io = new FakeLogicalIo();
                if (failRead) io.OutputReadError = new InvalidOperationException("读取失败");
                else io.WriteError = new InvalidOperationException("写入失败");
                var device = CreateDevice(new FakeAxis("X"), io, out _, out var runtime);
                var output = new SetOutputNode { Id = "out", Name = "执行", OutputName = "Vacuum", Value = true };
                var scope = new ResourceScopeNode { Id = "scope", Name = "区域", Resources = new List<string> { "Output:Vacuum" },
                    Body = new SequenceNode { Id = "body", Name = "正文", Children = new List<TaskNode> { output } } };
                var task = new TaskDefinition { TaskId = "Output", Name = "输出", Root = new SequenceNode {
                    Id = "root", Name = "步骤", Children = new List<TaskNode> { scope } } };
                var configuration = new TaskMachineConfiguration { MachineId = "M1", Name = "设备", Stations = new List<StationTaskConfiguration> {
                    new StationTaskConfiguration { StationId = "S", Name = "工位", TaskId = "Output", Enabled = true, Mode = StationTaskMode.Once } } };
                var controller = new TaskMachineController(configuration, new[] { task }, device, runtime.AlarmService);
                try
                {
                    controller.Reset(); controller.Start();
                    Ensure(SpinWait.SpinUntil(() => controller.GetSnapshot().State == TaskMachineState.Faulted, 5000), "工位未进入故障复位等待。");
                    Ensure(io.OutputValue && io.OutputWrites == 1 && device.ReadResources().Single().State == TaskResourceState.Blocked,
                        "工位等待复位时父范围尚未隔离，或输出未按预期执行一次。");
                    io.WriteError = null; io.OutputReadError = null;
                    ExpectMessage(() => controller.Reset(), "待确认");
                    ExpectMessage(() => controller.Start(), "复位");
                    Ensure(io.OutputWrites == 1, "未确认现场即重新下发输出命令。");
                }
                finally
                {
                    controller.AbortAsync().GetAwaiter().GetResult();
                    string reason;
                    var blocked = device.ReadResources().SingleOrDefault();
                    if (blocked != null) Ensure(device.TryRecoverResources(blocked.RequestId, "测试已核对", out reason), reason);
                    controller.CloseAsync().GetAwaiter().GetResult();
                }
            }
        }
    }
}
