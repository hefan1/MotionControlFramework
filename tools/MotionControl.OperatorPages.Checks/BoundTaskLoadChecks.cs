using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.IO;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Contracts.Preparation;
using MotionControl.Framework.Contracts.Safety;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.Framework.Runtime.Alarms;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.Framework.Runtime.Machine;
using MotionControl.Framework.Runtime.Variables;
using MotionControl.Host.Wpf;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Configuration;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Machine;

namespace MotionControl.OperatorPages.Checks
{
    /// <summary>驱动正式 WPF 装载方法、真实 Runtime 生命周期和临时机器配置，完全使用内存卡。</summary>
    internal static class BoundTaskLoadChecks
    {
        /// <summary>检查同 Runtime 重连、准备订阅者错误、并发版本更换及解析失败后关闭可重试。</summary>
        internal static async Task RunAsync()
        {
            const string id = "ReadCheck";
            var root = Path.Combine(Path.GetTempPath(), "BoundLoad-" + Guid.NewGuid().ToString("N"));
            var store = new MachineConfigurationStore(root);
            var window = new MainWindow();
            var card = new LoadCard();
            var cards = new MotionCardGroup(new[] { card });
            var runtime = new MachineRuntime(cards, new IStationFlow[0], new MachineAlarmService(), new SafeCheckSafety());
            var variables = new DeviceVariableStore(new DeviceVariableDefinition[0]);
            var io = new NoIo();
            var oldPreparation = new CheckPreparation("old");
            var replacement = new CheckPreparation("replacement");
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var type = typeof(MainWindow).Assembly.GetType("MotionControl.Host.Wpf.Tasks.TaskMonitorLauncher", true);
            var page = Activator.CreateInstance(type, flags, null, new object[] { store, id, window, null }, null);
            // 发起同一正式 WPF 命令，等待 UI 上下文的实际续体。
            Task Invoke(string name, params object[] arguments)
                => (Task)type.GetMethod(name, flags).Invoke(page, arguments);
            object Published() => type.GetField("_session", flags).GetValue(page);
            var token = CancellationToken.None;
            Exception testError = null;
            Exception cleanupError = null;
            try
            {
                WriteMachine(store, id);
                string ioMappingPath = Path.Combine(store.GetMachineDirectory(id), "HardwareConfig/IoMapping.json");
                await runtime.ConnectAsync(token);
                runtime.ConfigurePreparations(new[] { oldPreparation });
                await Invoke("SetMachineContextAsync", runtime, cards, io, variables, new[] { oldPreparation });
                card.BlockWorkerRead = true;
                Task stale = Invoke("LoadTaskSessionAsync");
                try
                {
                    if (await Task.WhenAny(card.WorkerEntered.Task, Task.Delay(4000)) != card.WorkerEntered.Task)
                        throw new InvalidOperationException("后台装载未进入连接检查。");
                    await runtime.DisconnectAsync(token);
                    await runtime.ConnectAsync(token);
                }
                finally { card.Resume.Set(); }
                await Reject(stale, "连接");
                Require(Published() == null && !runtime.IsTaskEngineControlClaimed, "旧连接装载发布到了新会话。");

                // 安装时抛错必须撤回旧安装；若订阅者已提交另一版本，严禁覆盖它。
                foreach (bool replace in new[] { false, true })
                {
                    EventHandler notify = null;
                    notify = (sender, args) =>
                    {
                        runtime.PreparationChanged -= notify;
                        if (replace) runtime.ConfigurePreparations(new[] { replacement });
                        throw new InvalidOperationException("准备定义通知测试失败");
                    };
                    runtime.PreparationChanged += notify;
                    try { await Reject(Invoke("LoadTaskSessionAsync"), "准备定义通知"); }
                    finally { runtime.PreparationChanged -= notify; }
                    Require(Published() == null && !runtime.IsTaskEngineControlClaimed
                        && runtime.PreparationStations[0].StationId == (replace ? "replacement" : "old"),
                        "装载回滚覆盖了其他准备定义或遗漏归还设备。");
                }
                // 通知正常返回但替换了安装代次，也不能把另一份定义发布为任务准备。
                EventHandler replaceWithoutError = null;
                replaceWithoutError = (sender, args) =>
                {
                    runtime.PreparationChanged -= replaceWithoutError;
                    runtime.ConfigurePreparations(new[] { replacement });
                };
                runtime.PreparationChanged += replaceWithoutError;
                try { await Reject(Invoke("LoadTaskSessionAsync"), "准备"); }
                finally { runtime.PreparationChanged -= replaceWithoutError; }
                Require(Published() == null && !runtime.IsTaskEngineControlClaimed
                    && runtime.PreparationStations[0].StationId == "replacement",
                    "装载发布了不属于任务的准备定义，或回滚覆盖了新定义。");
                // 同一 Runtime 在配置通知中失联。清理可操作该失败装载自身，不能发布已失效的任务。
                EventHandler lost = null;
                lost = (sender, args) => { runtime.PreparationChanged -= lost; card.Online = false; };
                runtime.PreparationChanged += lost;
                try { await Reject(Invoke("LoadTaskSessionAsync"), "连接"); }
                finally { runtime.PreparationChanged -= lost; card.Online = true; }
                Require(Published() == null && !runtime.IsTaskEngineControlClaimed, "配置通知中的掉线未禁止发布。");

                string invalid = Path.Combine(store.GetMachineDirectory(id), "Parameters", "StationPreparation.json");
                File.WriteAllText(invalid, "{}");
                Task failed = Invoke("LoadTaskSessionAsync");
                Require(SpinWait.SpinUntil(() => runtime.IsTaskEngineControlClaimed, 4000), "装载没有获得预期设备控制权。");
                var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                Task holding = runtime.RunConnectedConfigurationChangeAsync(() => release.Task, token);
                try
                {
                    try { await failed; throw new InvalidOperationException("无效准备配置没有拒绝装载。"); }
                    catch (AggregateException error)
                    {
                        Require(error.InnerExceptions.Count == 2 && error.InnerExceptions[0].ToString().Contains("schemaVersion")
                            && error.InnerExceptions[1].ToString().Contains("设备命令"), "装载或收尾原始故障丢失。");
                    }
                    Require(Published() == null && runtime.IsTaskEngineControlClaimed, "收尾失败后丢失设备 owner 或发布了旧任务。");
                }
                finally { release.TrySetResult(true); }
                await holding;
                await Invoke("CloseTaskSessionAsync", false);
                Require(!runtime.IsTaskEngineControlClaimed, "重试关闭未归还失败装载的实际控制权。");
                File.Delete(invalid);
                store.SaveJson(id, "HardwareConfig/IoMapping.json", new
                {
                    schemaVersion = 1,
                    digitalInputs = new object[0],
                    digitalOutputs = new object[0]
                });
                await Invoke("LoadTaskSessionAsync");
                Require(Published() != null && runtime.IsTaskEngineControlClaimed, "当前连接没有正常发布任务。");

                // 任务发布后若另有新安装，页面不能把它当作当前任务，关闭时也不能覆盖它。
                var isPublished = type.GetMethod("HasPublishedPreparationSession", flags);
                Require((bool)isPublished.Invoke(page, new object[] { runtime, runtime.ConnectionSessionId }),
                    "已发布任务没有绑定实际安装的准备配置。");
                Guid? lostSession = runtime.ConnectionSessionId;
                long lostPreparationVersion = runtime.PreparationConfigurationVersion;
                long lostSafetyVersion = runtime.SafetyObservationVersion;
                type.GetMethod("MarkControllerConnectionLost", flags).Invoke(page, null);
                Require(card.Online && !runtime.IsConnectionSessionCurrent(lostSession)
                    && !(bool)isPublished.Invoke(page, new object[] { runtime, lostSession }),
                    "连接丢失已锁存但底层反馈恢复时，旧任务范围仍被当成有效会话。");
                await Reject(Invoke("EnsureTaskSessionAsync"), "连接");
                await Reject(runtime.PrepareAsync(null, lostSession, lostPreparationVersion, runtime.ControlOwner,
                    lostSafetyVersion, token), "变化");
                await Invoke("CloseTaskSessionAsync", false);
                await runtime.DisconnectAsync(token);
                await runtime.ConnectAsync(token);
                await Invoke("SetMachineContextAsync", runtime, cards, io, variables, new[] { oldPreparation });
                await Invoke("LoadTaskSessionAsync");
                Require((bool)isPublished.Invoke(page, new object[] { runtime, runtime.ConnectionSessionId }),
                    "重新连接并读取任务后没有建立新的准备会话。");
                // 两个关闭入口同时等待同一底层收尾时，只能恢复一次配置，两个调用均应得到一致结果。
                object concurrentSession = Published();
                object concurrentMachine = concurrentSession.GetType().GetProperty("Machine").GetValue(concurrentSession);
                var concurrentCloseField = concurrentMachine.GetType().GetField("_closeTask", flags);
                var delayedClose = new TaskCompletionSource<bool>();
                concurrentCloseField.SetValue(concurrentMachine, delayedClose.Task);
                Task closeFirst = Invoke("CloseTaskSessionAsync", false);
                Task closeSecond = Invoke("CloseTaskSessionAsync", false);
                concurrentCloseField.SetValue(concurrentMachine, null);
                await (Task)concurrentMachine.GetType().GetMethod("CloseAsync").Invoke(concurrentMachine, null);
                delayedClose.SetResult(true);
                await Task.WhenAll(closeFirst, closeSecond);
                Require(Published() == null && !runtime.IsTaskEngineControlClaimed,
                    "并发关闭后会话或设备控制权没有安全释放。");
                await Invoke("LoadTaskSessionAsync");
                object closingSession = Published();
                object closingMachine = closingSession.GetType().GetProperty("Machine").GetValue(closingSession);
                var closeTaskField = closingMachine.GetType().GetField("_closeTask", flags);
                var closeFailure = new TaskCompletionSource<bool>();
                closeTaskField.SetValue(closingMachine, closeFailure.Task);
                Task closing = Invoke("CloseTaskSessionAsync", false);
                closeFailure.SetException(new IOException("simulated close failure"));
                await Reject(closing, "simulated close failure");
                Require(ReferenceEquals(Published(), closingSession)
                    && !(bool)isPublished.Invoke(page, new object[] { runtime, runtime.ConnectionSessionId }),
                    "关闭失败后仍将未确认会话作为可提交的准备范围。");
                await Invoke("CloseTaskSessionAsync", false);
                await Invoke("LoadTaskSessionAsync");
                Require((bool)isPublished.Invoke(page, new object[] { runtime, runtime.ConnectionSessionId }),
                    "安全关闭并重新读取后未恢复任务准备范围。");
                runtime.ConfigurePreparations(new[] { replacement });
                Require(!(bool)isPublished.Invoke(page, new object[] { runtime, runtime.ConnectionSessionId }),
                    "已发布任务错误地认领了其他准备配置代次。");
                await Reject(Invoke("CloseTaskSessionAsync", false), "设备准备配置未能恢复");
                Require(Published() == null && !runtime.IsTaskEngineControlClaimed
                    && runtime.PreparationStations[0].StationId == "replacement",
                    "关闭任务时覆盖了其他准备配置或遗留了已关闭会话。");
                await Invoke("LoadTaskSessionAsync");
                Require(Published() != null && runtime.IsTaskEngineControlClaimed, "保留新定义后无法重新装载任务。");

                object loadedSession = Published();
                object loadedDevice = loadedSession.GetType().GetProperty("TaskDevice").GetValue(loadedSession);
                var sessionCatalog = (TaskResourceCatalog)loadedDevice.GetType().GetProperty("Catalog").GetValue(loadedDevice);
                store.SaveJson(id, "HardwareConfig/IoMapping.json", new
                {
                    schemaVersion = 1,
                    digitalInputs = new[] { new { name = "DI_Later", controllerId = "Memory", channel = 0, activeHigh = true } },
                    digitalOutputs = new object[0]
                });
                type.GetMethod("RefreshCatalog", flags).Invoke(page, null);
                var displayedCatalog = (TaskResourceCatalog)type.GetField("_catalog", flags).GetValue(page);
                var catalogDetails = (TextBlock)type.GetField("_catalogDetails", flags).GetValue(page);
                Require(ReferenceEquals(displayedCatalog, sessionCatalog) && displayedCatalog.Inputs.Count == 0
                    && catalogDetails.Text.Contains("已加载会话") && !catalogDetails.Text.Contains("DI_Later"),
                    "已加载任务会话的目录详情被磁盘新配置替换。");
                await Invoke("CloseTaskSessionAsync", false);
                displayedCatalog = (TaskResourceCatalog)type.GetField("_catalog", flags).GetValue(page);
                Require(displayedCatalog.Inputs.Contains("DI_Later") && catalogDetails.Text.Contains("尚未装入任务会话"),
                    "关闭会话后没有自动显示当前保存目录或仍把它标为运行会话目录。");

                await Invoke("LoadTaskSessionAsync");
                File.WriteAllText(ioMappingPath, "{ invalid json");
                var runCommand = type.GetMethod("RunCommandAsync", flags);
                var closeWithSuccessText = new Func<Task>(async () =>
                {
                    await Invoke("CloseTaskSessionAsync", false);
                    type.GetMethod("SetSuccessText", flags).Invoke(page, new object[] { "任务执行已停止" });
                });
                await (Task)runCommand.Invoke(page, new object[] { closeWithSuccessText, "任务停止失败", "任务运行复位建议" });
                var lastError = (Exception)type.GetField("_lastError", flags).GetValue(page);
                var status = (TextBlock)type.GetField("_status", flags).GetValue(page);
                var diagnostics = (TextBox)type.GetField("_diagnosticText", flags).GetValue(page);
                Require(lastError != null && status.Text.Contains("无法读取任务关联配置") && diagnostics.Text.Contains("IoMapping.json"),
                    "停止任务后保存目录错误未保留。错误=" + (lastError?.ToString() ?? "null") + "；状态=" + status.Text);

                type.GetMethod("ShowError", flags).Invoke(page, new object[]
                {
                    new InvalidOperationException("上一条顶部操作错误"), "任务启动失败", "任务运行复位建议"
                });
                await Reject(Invoke("RunFromHostAsync", true), "请先连接运动控制卡");
                lastError = (Exception)type.GetField("_lastError", flags).GetValue(page);
                Require(lastError == null && status.Text.Length == 0 && diagnostics.Text.Length == 0,
                    "顶部启停命令开始前没有清除旧页面错误，成功操作时会遗留旧提示。");

                var setMachineContext = type.GetMethod("SetMachineContextAsync", flags);
                await (Task)setMachineContext.Invoke(page, new[]
                {
                    type.GetField("_runtime", flags).GetValue(page),
                    type.GetField("_motionCards", flags).GetValue(page),
                    type.GetField("_logicalIo", flags).GetValue(page),
                    type.GetField("_variables", flags).GetValue(page),
                    type.GetField("_equipmentPreparations", flags).GetValue(page)
                });
                lastError = (Exception)type.GetField("_lastError", flags).GetValue(page);
                status = (TextBlock)type.GetField("_status", flags).GetValue(page);
                Require(lastError != null && status.Text.Contains("无法读取任务关联配置"),
                    "控制器上下文更新清除了仍未解决的保存目录错误。");

                store.SaveJson(id, "HardwareConfig/IoMapping.json", new
                {
                    schemaVersion = 1,
                    digitalInputs = new object[0],
                    digitalOutputs = new object[0]
                });
                type.GetMethod("RefreshCatalog", flags).Invoke(page, null);
                await Invoke("LoadTaskSessionAsync");
                type.GetMethod("ShowError", flags).Invoke(page, new object[]
                {
                    new InvalidOperationException("顶部启动前的旧错误"), "任务启动失败", "任务运行复位建议"
                });
                await Invoke("ResetFromHostAsync", true);
                await Invoke("RunFromHostAsync", true);
                lastError = (Exception)type.GetField("_lastError", flags).GetValue(page);
                status = (TextBlock)type.GetField("_status", flags).GetValue(page);
                Require(lastError == null && status.Text == window.TryFindResource("任务执行已启动") as string
                    && diagnostics.Text.Length == 0,
                    "顶部启动成功后仍显示旧错误，或没有显示新的成功状态。错误="
                        + (lastError?.Message ?? "无") + "；状态=" + status.Text + "；诊断=" + diagnostics.Text);
                type.GetMethod("ShowError", flags).Invoke(page, new object[]
                {
                    new InvalidOperationException("顶部暂停前的旧错误"), "任务暂停失败", "任务运行复位建议"
                });
                await Invoke("RunFromHostAsync", false);
                lastError = (Exception)type.GetField("_lastError", flags).GetValue(page);
                status = (TextBlock)type.GetField("_status", flags).GetValue(page);
                Require(lastError == null && status.Text == window.TryFindResource("任务执行已暂停") as string
                    && diagnostics.Text.Length == 0,
                    "顶部暂停成功后仍显示旧错误，或没有显示新的成功状态。错误="
                        + (lastError?.Message ?? "无") + "；状态=" + status.Text + "；诊断=" + diagnostics.Text);
            }
            catch (Exception error) { testError = error; }
            finally
            {
                card.Resume.Set();
                try { await Invoke("StopTaskSessionForShutdownAsync"); }
                catch (Exception error) { cleanupError = error; }
                try { await Invoke("CloseTaskSessionAsync", false); }
                catch (Exception error) { cleanupError = cleanupError ?? error; }
                window.Close();
                try
                {
                    if (runtime.State != MachineRunState.Disconnected)
                        await runtime.StopStationFlowsForShutdownAsync(token);
                    if (runtime.State != MachineRunState.Disconnected) await runtime.DisconnectAsync(token);
                }
                catch (Exception error) { cleanupError = cleanupError ?? error; }
                finally
                {
                    try { if (Directory.Exists(root)) Directory.Delete(root, true); }
                    catch (Exception error) { cleanupError = cleanupError ?? error; }
                }
            }
            if (testError != null && cleanupError != null)
                throw new AggregateException("检查失败且测试收尾也未完全成功。", testError, cleanupError);
            if (testError != null) ExceptionDispatchInfo.Capture(testError).Throw();
            if (cleanupError != null) throw new InvalidOperationException("测试收尾未完成。", cleanupError);
        }

        /// <summary>生成仅有一个等待步骤的合法设备；没有硬件通道或动作。</summary>
        private static void WriteMachine(MachineConfigurationStore store, string id)
        {
            store.SaveJson(id, "HardwareConfig/AxisParameters.json", new { schemaVersion = 1, axes = new object[0] });
            store.SaveJson(id, "HardwareConfig/IoMapping.json", new { schemaVersion = 1, digitalInputs = new object[0], digitalOutputs = new object[0] });
            var definition = new TaskDefinition { TaskId = "Idle", Name = "等待", Revision = 1,
                Root = new SequenceNode { Id = "root", Name = "等待", TimeoutMs = 2000,
                    Children = new List<TaskNode> { new DelayNode { Id = "step", Name = "等待", TimeoutMs = 1000, Milliseconds = 50 } } } };
            string tasks = Path.Combine(store.GetMachineDirectory(id), "Tasks");
            Directory.CreateDirectory(tasks);
            File.WriteAllText(Path.Combine(tasks, "Idle.json"), TaskDefinitionJson.Serialize(definition));
            var config = new TaskMachineConfiguration { MachineId = id, Name = "任务绑定检查", Revision = 1,
                Stations = new List<StationTaskConfiguration> { new StationTaskConfiguration { StationId = "Idle", Name = "等待", TaskId = "Idle", Mode = StationTaskMode.Cyclic } } };
            new TaskMachineConfigurationStore(store).Save(id, config, new[] { definition }, new TaskResourceCatalogLoader(store).Load(id));
        }

        /// <summary>只接受指定原因造成的装载拒绝。</summary>
        private static async Task Reject(Task task, string expected)
        {
            try { await task; }
            catch (Exception error) { Require(error.ToString().Contains(expected), error.ToString()); return; }
            throw new InvalidOperationException("预期装载失败：" + expected);
        }
        /// <summary>报告未满足的装载边界。</summary>
        private static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }

        /// <summary>内存连接反馈，仅为首个后台读提供可控窗口。</summary>
        private sealed class LoadCard : IMotionCard
        {
            /// <summary>模拟底层连接。</summary>
            internal volatile bool Online;
            /// <summary>仅挂起一次后台线程的读取。</summary>
            internal volatile bool BlockWorkerRead;
            /// <summary>已进入受控读取。</summary>
            internal readonly TaskCompletionSource<bool> WorkerEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>释放后台读取。</summary>
            internal readonly ManualResetEventSlim Resume = new ManualResetEventSlim(false);
            /// <summary>内存卡标识。</summary>
            public string ControllerId => "Memory";
            /// <summary>返回状态；不访问硬件。</summary>
            public bool IsConnected
            {
                get
                {
                    if (BlockWorkerRead && Thread.CurrentThread.IsThreadPoolThread)
                    {
                        BlockWorkerRead = false;
                        WorkerEntered.TrySetResult(true);
                        if (!Resume.Wait(5000)) throw new TimeoutException("内存装载读未按时释放。");
                    }
                    return Online;
                }
            }
            /// <summary>没有轴。</summary>
            public IReadOnlyCollection<IAxis> Axes => new IAxis[0];
            /// <summary>内存连接。</summary>
            public Task ConnectAsync(CancellationToken token) { Online = true; return Task.CompletedTask; }
            /// <summary>内存断开。</summary>
            public Task DisconnectAsync(CancellationToken token) { Online = false; return Task.CompletedTask; }
        }
        /// <summary>测试不允许读写 IO。</summary>
        private sealed class NoIo : ILogicalIo
        {
            /// <summary>禁止读取输入。</summary>
            public Task<bool> ReadInputAsync(string name, CancellationToken token) { throw new NotSupportedException(); }
            /// <summary>禁止读取输出。</summary>
            public Task<bool> ReadOutputAsync(string name, CancellationToken token) { throw new NotSupportedException(); }
            /// <summary>禁止写入输出。</summary>
            public Task WriteOutputAsync(string name, bool value, CancellationToken token) { throw new NotSupportedException(); }
        }
        /// <summary>用于识别安装及撤回顺序的设备准备定义。</summary>
        private sealed class CheckPreparation : IStationPreparation
        {
            /// <summary>创建指定标识的定义。</summary>
            internal CheckPreparation(string id) { StationId = id; }
            /// <summary>稳定标识。</summary>
            public string StationId { get; }
            /// <summary>测试名称。</summary>
            public string StationName => StationId;
            /// <summary>无依赖。</summary>
            public IReadOnlyCollection<string> Dependencies => new string[0];
            /// <summary>禁止设备预检。</summary>
            public Task ValidateStartAsync(CancellationToken token) { throw new NotSupportedException(); }
            /// <summary>禁止执行准备。</summary>
            public Task ExecuteAsync(IProgress<StationPreparationProgress> progress, CancellationToken token) { throw new NotSupportedException(); }
            /// <summary>禁止设备校验。</summary>
            public Task ValidateReadyAsync(CancellationToken token) { throw new NotSupportedException(); }
        }

        /// <summary>为启动/暂停检查提供显式正常的内存急停输入，不连接任何安全硬件。</summary>
        private sealed class SafeCheckSafety : IMachineSafetyStatus
        {
            public Task<bool> ReadEmergencyStopActiveAsync(CancellationToken token)
                => Task.FromResult(false);
        }
    }
}
