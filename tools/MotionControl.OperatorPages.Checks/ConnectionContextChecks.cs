using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using MotionControl.Framework.Contracts.Configuration;
using MotionControl.Framework.Contracts.IO;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.Framework.Runtime.Alarms;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.Framework.Runtime.Machine;
using MotionControl.Framework.Runtime.Variables;
using MotionControl.Framework.Wpf.Configuration;
using MotionControl.Framework.Wpf.Variables;
using MotionControl.Host.Wpf;
using MotionControl.Host.Wpf.Recovery;

namespace MotionControl.OperatorPages.Checks
{
    /// <summary>只供连接来源检查使用的真实 App；启动配置由检查显式装配，不启动正式 Host 或控制器。</summary>
    internal sealed class ConnectionContextCheckApplication : App
    {
        /// <summary>阻止 Dispatcher 首次泵送时读取用户正式配置；测试仍调用继承的真实连接同步方法。</summary>
        protected override void OnStartup(StartupEventArgs e) { }

        /// <summary>各检查已等待真实任务与日志收尾；内部检查进程退出时不再次执行正式 Host 退出流程。</summary>
        protected override void OnExit(ExitEventArgs e) { }
    }

    /// <summary>复用既有无轴连接设施，检查真实 App 和任务入口的来源归属；不代表正常 UI 或运动 API 验收。</summary>
    internal static partial class BoundTaskLoadChecks
    {
        /// <summary>内部检查读取私有组合字段的固定反射范围，不进入生产代码。</summary>
        private const BindingFlags ConnectionCheckFlags = BindingFlags.Instance | BindingFlags.NonPublic;

        /// <summary>完整检查通过独立进程运行真实 App，保持其他检查的普通 Application 宿主不变。</summary>
        internal static async Task RunConnectionContextProcessAsync()
        {
            var start = new ProcessStartInfo
            {
                FileName = Assembly.GetExecutingAssembly().Location,
                Arguments = "--connection-context",
                WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true
            };
            using (var process = Process.Start(start))
            {
                if (process == null) throw new InvalidOperationException("无法启动连接来源专项检查进程。");
                Task<string> output = process.StandardOutput.ReadToEndAsync();
                Task<string> error = process.StandardError.ReadToEndAsync();
                bool finished = await Task.Run(() => process.WaitForExit(60000));
                if (!finished)
                {
                    // 这里只终止本方法刚创建的无硬件内部检查进程，不涉及正式 Host 或控制器。
                    process.Kill();
                    if (!await Task.Run(() => process.WaitForExit(10000)))
                        throw new TimeoutException("连接来源专项检查超时，内部检查进程仍未退出。");
                }
                Console.Write(await output);
                Console.Error.Write(await error);
                if (!finished) throw new TimeoutException("连接来源专项检查超过 60 秒，未取得有效回归结果。");
                Require(process.ExitCode == 0, "连接来源专项检查失败，退出码：" + process.ExitCode + "。请查看上述具体断言。");
            }
        }

        /// <summary>逐组执行受控离线时序，保留全部失败；只有专项进程使用真实 App 子类。</summary>
        internal static async Task RunConnectionContextChecksAsync()
        {
            var app = Application.Current as ConnectionContextCheckApplication;
            Require(app != null, "连接来源检查必须在 --connection-context 独立 App 进程中运行。");
            Console.WriteLine("[范围] 以下为真实 App/任务入口的离线通知与生命周期故障注入，没有轴或 IO 动作，不代表正式界面验收。");
            var failures = new List<Exception>();
            await RunConnectionCheckAsync("同一已发布连接通知保持运行时与任务身份", () => CheckRepeatedConnectionAsync(app), failures);
            await RunConnectionCheckAsync("任务上下文等待后不得撤回失联状态", () => CheckContextSetterRevokedAsync(app), failures);
            await RunConnectionCheckAsync("旧断连监控迟到不得清空新连接", () => CheckLateDisconnectAsync(app), failures);
            if (failures.Count != 0) throw new AggregateException("连接来源离线回归未通过。", failures);
        }

        /// <summary>让每个场景独立报告结果，前一组红态不跳过后续竞态证据。</summary>
        private static async Task RunConnectionCheckAsync(string name, Func<Task> run, List<Exception> failures)
        {
            Console.WriteLine("[开始] " + name);
            try { await run(); Console.WriteLine("[通过] " + name); }
            catch (Exception error)
            {
                failures.Add(new InvalidOperationException(name, error));
                Console.Error.WriteLine("[失败] " + name + "\n" + error);
            }
        }

        /// <summary>同一卡和 IO 再次通知时，真实 App 不得替换 Runtime、关闭已发布任务或换掉报警历史。</summary>
        private static async Task CheckRepeatedConnectionAsync(App app)
        {
            var fixture = new ConnectionContextFixture(app);
            var errors = new List<Exception>();
            try
            {
                fixture.Initialize();
                await fixture.ConnectNewCardAsync();
                await fixture.InvokeLauncherAsync("LoadTaskSessionAsync");
                MachineRuntime original = fixture.CurrentRuntime;
                Guid? connection = original.ConnectionSessionId;
                object task = fixture.LauncherField("_session");
                object alarms = fixture.AppField("_currentAlarmService");
                object history = fixture.AppField("_alarmHistoryJournal");
                object monitor = ReadConnectionCheckField(original, "_controlInputMonitorTask");
                Require(task != null && original.IsTaskEngineControlClaimed,
                    "幂等检查前未实际装载任务并取得控制权，不能判断重复通知行为。");

                await fixture.SynchronizeAsync();

                Require(ReferenceEquals(fixture.CurrentRuntime, original)
                    && ReferenceEquals(fixture.LauncherField("_runtime"), original)
                    && ReferenceEquals(ReadConnectionCheckField(fixture.Host, "_machineRuntime"), original)
                    && connection == original.ConnectionSessionId
                    && ReferenceEquals(fixture.LauncherField("_session"), task)
                    && original.IsTaskEngineControlClaimed
                    && ReferenceEquals(fixture.AppField("_currentAlarmService"), alarms)
                    && ReferenceEquals(fixture.AppField("_alarmHistoryJournal"), history)
                    && ReferenceEquals(ReadConnectionCheckField(original, "_controlInputMonitorTask"), monitor),
                    "ROC19_重复连接重建上下文：同一卡和 IO 的通知替换了运行时、任务、连接身份或报警历史。");
            }
            catch (Exception error) { errors.Add(error); }
            finally { await fixture.CleanupAsync(errors); }
            if (errors.Count != 0) throw new AggregateException("重复连接通知检查或收尾失败。", errors);
        }

        /// <summary>用既有后台读取屏障挂起真实 setter，再发送真实失联通知；旧 setter 不得复活其请求。</summary>
        private static async Task CheckContextSetterRevokedAsync(App app)
        {
            var fixture = new ConnectionContextFixture(app);
            var errors = new List<Exception>();
            LoadCard originalCard = null;
            Task loading = null;
            Task setting = null;
            try
            {
                fixture.Initialize();
                originalCard = await fixture.ConnectNewCardAsync();
                MachineRuntime original = fixture.CurrentRuntime;
                originalCard.BlockWorkerRead = true;
                loading = fixture.InvokeLauncherAsync("LoadTaskSessionAsync");
                await WaitForConnectionCheckAsync(originalCard.WorkerEntered.Task, "任务后台读取未进入受控等待窗口。");

                var replacementCard = new LoadCard();
                var replacementCards = new MotionCardGroup(new[] { replacementCard });
                var replacement = new MachineRuntime(replacementCards,
                    new MotionControl.Framework.Contracts.Flow.IStationFlow[0], new MachineAlarmService(), new SafeCheckSafety());
                fixture.TrackRuntime(replacement);
                await replacement.ConnectAsync(CancellationToken.None);
                setting = fixture.InvokeLauncherAsync("SetMachineContextAsync", replacement, replacementCards,
                    new NoIo(), fixture.Variables, null);
                Require(!setting.IsCompleted, "setter 没有等待真实在途装载，本次竞态检查无效。");
                fixture.InvokeLauncher("MarkControllerConnectionLost");
                originalCard.Resume.Set();

                Exception loadError = await ObserveConnectionCheckAsync(loading);
                if (!(loadError is InvalidOperationException))
                    throw new InvalidOperationException("被撤销的任务装载没有以连接/上下文失效结束。", loadError);
                Exception settingError = await ObserveConnectionCheckAsync(setting);
                if (settingError != null && !(settingError is InvalidOperationException))
                    throw new InvalidOperationException("任务上下文撤销返回了非预期异常。", settingError);
                Require((bool)fixture.LauncherField("_controllerConnectionLost")
                    && !ReferenceEquals(fixture.LauncherField("_runtime"), replacement)
                    && fixture.LauncherField("_session") == null
                    && !original.IsTaskEngineControlClaimed && !replacement.IsTaskEngineControlClaimed,
                    "ROC19_旧上下文撤回失联：等待装载结束的 setter 覆盖了后来失联通知，或残留了任务控制权。");
            }
            catch (Exception error) { errors.Add(error); }
            finally
            {
                originalCard?.Resume.Set();
                await FinishConnectionCheckTaskAsync(loading, true, errors);
                await FinishConnectionCheckTaskAsync(setting, true, errors);
                await fixture.CleanupAsync(errors);
            }
            if (errors.Count != 0) throw new AggregateException("任务上下文撤销检查或收尾失败。", errors);
        }

        /// <summary>只注入旧监控的退出屏障，调用真实 App 断连与新连接入口，检查旧收尾不能修改 B。</summary>
        private static async Task CheckLateDisconnectAsync(App app)
        {
            var fixture = new ConnectionContextFixture(app);
            var errors = new List<Exception>();
            var monitorFinished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationTokenSource monitorCancellation = null;
            Task disconnecting = null;
            Task connecting = null;
            try
            {
                fixture.Initialize();
                LoadCard originalCard = await fixture.ConnectNewCardAsync();
                MachineRuntime original = fixture.CurrentRuntime;
                await original.StopControlInputMonitoringAsync();
                monitorCancellation = new CancellationTokenSource();
                SetConnectionCheckField(original, "_controlInputMonitorCancellation", monitorCancellation);
                SetConnectionCheckField(original, "_controlInputMonitorTask", monitorFinished.Task);
                originalCard.Online = false;
                fixture.Connection.Card = null;
                fixture.Connection.LogicalIo = null;
                disconnecting = fixture.SynchronizeAsync();
                Require(!disconnecting.IsCompleted && monitorCancellation.IsCancellationRequested,
                    "App 断连没有进入旧监控的受控退出窗口，本次检查无效。");

                LoadCard replacementCard = await fixture.InstallNewCardAsync();
                connecting = fixture.SynchronizeAsync();
                // 修复可以先等 A 监控退出再发布 B；只记录旧实现已经提前发布的 B，不要求这种微时序。
                MachineRuntime earlyReplacement = fixture.CurrentRuntime;
                if (ReferenceEquals(earlyReplacement, original)) earlyReplacement = null;
                object earlyHistory = earlyReplacement == null ? null : fixture.AppField("_alarmHistoryJournal");
                monitorFinished.TrySetResult(true);
                await connecting;
                await disconnecting;

                MachineRuntime replacement = earlyReplacement ?? fixture.CurrentRuntime;
                Require(replacement != null && !ReferenceEquals(original, replacement)
                    && replacement.MotionCards.Cards.Any(card => ReferenceEquals(card, replacementCard)),
                    "ROC19_旧断连清空新连接：两个同步完成后，没有保留连接 B 的真实 Runtime。");
                object history = fixture.AppField("_alarmHistoryJournal");
                object replacementMonitor = ReadConnectionCheckField(replacement, "_controlInputMonitorTask");
                Require(ReferenceEquals(fixture.CurrentRuntime, replacement)
                    && ReferenceEquals(fixture.LauncherField("_runtime"), replacement)
                    && ReferenceEquals(ReadConnectionCheckField(fixture.Host, "_machineRuntime"), replacement)
                    && ReferenceEquals(ReadConnectionCheckField(fixture.VariablePage, "_machineRuntime"), replacement)
                    && ReferenceEquals(fixture.AppField("_currentAlarmService"), replacement.AlarmService)
                    && history != null && (earlyHistory == null || ReferenceEquals(history, earlyHistory))
                    && !(bool)ReadConnectionCheckField(history, "_closing")
                    && replacementMonitor != null
                    && !((Task)replacementMonitor).IsCompleted,
                    "ROC19_旧断连清空新连接：旧监控退出后清除了 B 的运行时、页面、报警历史或监控。");
            }
            catch (Exception error) { errors.Add(error); }
            finally
            {
                monitorFinished.TrySetResult(true);
                await FinishConnectionCheckTaskAsync(connecting, false, errors);
                await FinishConnectionCheckTaskAsync(disconnecting, false, errors);
                await fixture.CleanupAsync(errors);
                monitorCancellation?.Dispose();
            }
            if (errors.Count != 0) throw new AggregateException("旧断连迟到检查或收尾失败。", errors);
        }

        /// <summary>等待已声明的进入屏障；超时属于测试未建立场景，不当作产品红态。</summary>
        private static async Task WaitForConnectionCheckAsync(Task task, string failure)
        {
            if (await Task.WhenAny(task, Task.Delay(4000)) != task) throw new TimeoutException(failure);
            await task;
        }

        /// <summary>观察实际任务的完成或原异常，供场景分别判断预期撤销和错误。</summary>
        private static async Task<Exception> ObserveConnectionCheckAsync(Task task)
        {
            try { await task; return null; }
            catch (Exception error) { return error; }
        }

        /// <summary>屏障已释放后仍等待实际任务；只允许场景明确预期的上下文撤销，其余收尾错误保留。</summary>
        private static async Task FinishConnectionCheckTaskAsync(Task task, bool allowRevokedContext, List<Exception> errors)
        {
            if (task == null) return;
            Exception error = await ObserveConnectionCheckAsync(task);
            if (error != null && !(allowRevokedContext && error is InvalidOperationException)) errors.Add(error);
        }

        /// <summary>读取本检查涉及的真实私有字段；App 子类仍从声明字段的 App 类型查找。</summary>
        private static object ReadConnectionCheckField(object target, string name)
        {
            Type type = target is App ? typeof(App) : target.GetType();
            FieldInfo field = type.GetField(name, ConnectionCheckFlags);
            if (field == null) throw new InvalidOperationException("连接检查找不到字段：" + type.FullName + "." + name);
            return field.GetValue(target);
        }

        /// <summary>仅在内部检查设置组合引用或明确声明的监控完成屏障，不替换生产方法。</summary>
        private static void SetConnectionCheckField(object target, string name, object value)
        {
            Type type = target is App ? typeof(App) : target.GetType();
            FieldInfo field = type.GetField(name, ConnectionCheckFlags);
            if (field == null) throw new InvalidOperationException("连接检查找不到字段：" + type.FullName + "." + name);
            field.SetValue(target, value);
        }

        /// <summary>显式装配本组临时配置、真实页面和任务入口，并登记所有已发布 Runtime 以等待收尾。</summary>
        private sealed class ConnectionContextFixture
        {
            /// <summary>本进程唯一的真实 App 检查实例。</summary>
            private readonly App _app;
            /// <summary>当前场景自己的临时设备目录，不接触用户配置。</summary>
            private readonly string _root = Path.Combine(Path.GetTempPath(), "Roc19Connection-" + Guid.NewGuid().ToString("N"));
            /// <summary>真实任务入口的内部类型。</summary>
            private readonly Type _launcherType = typeof(App).Assembly.GetType("MotionControl.Host.Wpf.Tasks.TaskMonitorLauncher", true);
            /// <summary>已创建的全部真实 Runtime，包括被错误替换的旧实例。</summary>
            private readonly List<MachineRuntime> _runtimes = new List<MachineRuntime>();
            /// <summary>真实任务入口实例。</summary>
            private object _launcher;
            /// <summary>测试源只提供无轴连接身份，不模拟运动 API。</summary>
            internal readonly ConnectionContextCardConnection Connection = new ConnectionContextCardConnection();
            /// <summary>本场景的真实主窗口，不调用正式 App 启动。</summary>
            internal MainWindow Host;
            /// <summary>本场景的真实变量页，用于核对页面绑定来源。</summary>
            internal DeviceVariableView VariablePage;
            /// <summary>真实任务装载使用的设备变量服务。</summary>
            internal DeviceVariableStore Variables;
            /// <summary>App 当前实际安装的 Runtime。</summary>
            internal MachineRuntime CurrentRuntime { get { return (MachineRuntime)AppField("_machineRuntime"); } }

            /// <summary>保存唯一 App；设备与页面随后在可收尾的检查范围内创建。</summary>
            internal ConnectionContextFixture(App app) { _app = app; }

            /// <summary>创建合法空轴、空 IO、无工位的设备配置，避免初始化异常弹窗掩盖来源断言。</summary>
            internal void Initialize()
            {
                Require(CurrentRuntime == null && AppField("_mainWindow") == null,
                    "上一组连接检查尚未完成收尾，不能把遗留状态当成本组竞态证据。");
                var store = new MachineConfigurationStore(_root);
                WriteMachine(store, "Machine01");
                var catalog = new MachineAlarmCatalogStore(store);
                catalog.LoadOrCreate("Machine01", new MachineAlarmCatalogDocument());
                Variables = new DeviceVariableStore(new DeviceVariableDefinition[0]);
                Host = new MainWindow();
                VariablePage = new DeviceVariableView(Variables, store, "Machine01");
                _launcher = Activator.CreateInstance(_launcherType, ConnectionCheckFlags, null,
                    new object[] { store, "Machine01", Host, null }, null);
                SetConnectionCheckField(_app, "_mainWindow", Host);
                SetConnectionCheckField(_app, "_configurationStore", store);
                SetConnectionCheckField(_app, "_alarmCatalogStore", catalog);
                SetConnectionCheckField(_app, "_cardConnection", Connection);
                SetConnectionCheckField(_app, "_axisMappingPage", new AxisMappingConfigurationView(store, "Machine01", Connection));
                SetConnectionCheckField(_app, "_deviceVariableStore", Variables);
                SetConnectionCheckField(_app, "_deviceVariablePage", VariablePage);
                SetConnectionCheckField(_app, "_taskMonitorLauncher", _launcher);
                SetConnectionCheckField(_app, "_alarmContinuity", new AlarmContinuityStore(store, "Machine01"));
                SetConnectionCheckField(_app, "_machineRecoveryCoordinator", new MachineRecoveryCoordinator(
                    new MachineRecoveryStore(store, "Machine01"), Variables, "Machine01"));
            }

            /// <summary>安装独立的无轴卡和 IO 身份后调用真实 App 同步，不替换 Runtime 工厂。</summary>
            internal async Task<LoadCard> ConnectNewCardAsync()
            {
                LoadCard card = await InstallNewCardAsync();
                await SynchronizeAsync();
                Require(CurrentRuntime != null, "连接检查的合法配置未产生 Runtime，不能进入来源回归。");
                return card;
            }

            /// <summary>只改变底层连接来源，调用方自行安排真实 App 同步与旧监控退出的先后顺序。</summary>
            internal async Task<LoadCard> InstallNewCardAsync()
            {
                var card = new LoadCard();
                await card.ConnectAsync(CancellationToken.None);
                Connection.Card = card;
                Connection.LogicalIo = new NoIo();
                return card;
            }

            /// <summary>调用生产 App 的真实同步入口，并记录其已发布 Runtime 供失败后清理。</summary>
            internal async Task SynchronizeAsync()
            {
                try { await (Task)typeof(App).GetMethod("SynchronizeCardConnectionAsync", ConnectionCheckFlags).Invoke(_app, null); }
                finally { TrackRuntime(CurrentRuntime); }
            }

            /// <summary>记录真实实例一次，避免重复收尾隐藏遗留 Runtime。</summary>
            internal void TrackRuntime(MachineRuntime runtime)
            {
                if (runtime != null && !_runtimes.Contains(runtime)) _runtimes.Add(runtime);
            }

            /// <summary>读取 App 当前组合结果。</summary>
            internal object AppField(string name) { return ReadConnectionCheckField(_app, name); }

            /// <summary>读取真实任务入口的上下文和会话身份。</summary>
            internal object LauncherField(string name) { return ReadConnectionCheckField(_launcher, name); }

            /// <summary>调用真实任务入口的同步失联通知。</summary>
            internal void InvokeLauncher(string name) { _launcherType.GetMethod(name, ConnectionCheckFlags).Invoke(_launcher, null); }

            /// <summary>调用真实任务装载、关闭或上下文设置，不替换这些方法的完成结果。</summary>
            internal Task InvokeLauncherAsync(string name, params object[] arguments)
            { return (Task)_launcherType.GetMethod(name, ConnectionCheckFlags).Invoke(_launcher, arguments); }

            /// <summary>先结束任务和所有 Runtime，再等 App 历史收尾；收尾失败保留原错误与临时证据。</summary>
            internal async Task CleanupAsync(List<Exception> errors)
            {
                int errorCount = errors.Count;
                TrackRuntime(CurrentRuntime);
                if (_launcher != null)
                {
                    try { await InvokeLauncherAsync("CloseTaskSessionAsync", false); }
                    catch (Exception error) { errors.Add(error); }
                }
                foreach (MachineRuntime runtime in _runtimes)
                {
                    try { await runtime.StopControlInputMonitoringAsync(); await runtime.DisconnectAsync(CancellationToken.None); }
                    catch (Exception error) { errors.Add(error); }
                }
                Connection.Card = null;
                Connection.LogicalIo = null;
                if (AppField("_mainWindow") != null && AppField("_deviceVariablePage") != null)
                {
                    try { await SynchronizeAsync(); }
                    catch (Exception error) { errors.Add(error); }
                }
                try
                {
                    typeof(App).GetMethod("CloseAlarmHistory", ConnectionCheckFlags).Invoke(_app, null);
                    var closing = (HashSet<AlarmHistoryJournal>)AppField("_closingAlarmHistories");
                    var timer = Stopwatch.StartNew();
                    while (closing.Count != 0 && timer.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(10);
                    Require(closing.Count == 0 && ((List<AlarmHistoryJournal>)AppField("_unfinishedAlarmHistories")).Count == 0,
                        "连接检查的报警历史未确认收尾，保留临时目录：" + _root);
                }
                catch (Exception error) { errors.Add(error); }
                try { Host?.Close(); }
                catch (Exception error) { errors.Add(error); }
                foreach (string name in new[] { "_mainWindow", "_axisMappingPage", "_deviceVariablePage", "_taskMonitorLauncher",
                    "_configurationStore", "_alarmCatalogStore", "_cardConnection", "_deviceVariableStore", "_alarmContinuity", "_machineRecoveryCoordinator" })
                    SetConnectionCheckField(_app, name, null);
                if (errors.Count == errorCount)
                {
                    try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
                    catch (Exception error) { errors.Add(error); }
                }
                else Console.Error.WriteLine("[收尾证据] " + _root);
            }
        }

        /// <summary>仅把既有无轴 LoadCard 与禁止 IO 的服务交给真实 App，不提供运动或参数写入能力。</summary>
        private sealed class ConnectionContextCardConnection : IMotionCardHostConnection
        {
            /// <summary>本轮对外可见的无轴卡引用，测试据此控制来源变化。</summary>
            internal LoadCard Card;
            /// <summary>本轮逻辑 IO 身份；NoIo 对所有硬件调用明确拒绝。</summary>
            internal ILogicalIo LogicalIo;
            /// <summary>不声明任何可由轴映射页连接的硬件。</summary>
            public IReadOnlyList<MotionCardSelectionOption> SupportedMotionCards => new MotionCardSelectionOption[0];
            /// <summary>连接状态始终来自当前无轴卡。</summary>
            public bool IsConnected => Card != null && Card.IsConnected;
            /// <summary>明确标识内部检查后端。</summary>
            public string ActualControllerType => "无轴连接检查";
            /// <summary>本检查没有控制器固件版本。</summary>
            public string ActualControllerVersion => string.Empty;
            /// <summary>返回当前实际参与 Runtime 的卡对象。</summary>
            public IMotionCard ConnectedMotionCard => Card;
            /// <summary>返回与当前卡同时安装的 IO 身份。</summary>
            public ILogicalIo ConnectedLogicalIo => LogicalIo;
            /// <summary>本检查没有轴映射。</summary>
            public IReadOnlyList<AxisHardwareDefinition> AxisHardwareDefinitions => new AxisHardwareDefinition[0];
            /// <summary>本检查没有输入映射。</summary>
            public IReadOnlyList<DigitalIoHardwareDefinition> DigitalInputs => new DigitalIoHardwareDefinition[0];
            /// <summary>本检查没有输出映射。</summary>
            public IReadOnlyList<DigitalIoHardwareDefinition> DigitalOutputs => new DigitalIoHardwareDefinition[0];
            /// <summary>连接身份由测试显式安装，不允许页面发起硬件连接。</summary>
            public Task ConnectAsync(MotionCardSettingsDocument settings, CancellationToken token) { throw new NotSupportedException("内部检查不提供页面连接。"); }
            /// <summary>断开当前无轴卡；只清理仍属于本次调用的源。</summary>
            public async Task DisconnectAsync(CancellationToken token)
            {
                LoadCard card = Card;
                if (card != null) await card.DisconnectAsync(token);
                if (ReferenceEquals(Card, card)) { Card = null; LogicalIo = null; }
            }
            /// <summary>本检查不提供自动重连。</summary>
            public Task ReconnectAsync(MotionCardSettingsDocument settings, CancellationToken token) { throw new NotSupportedException("内部检查不提供重连。"); }
            /// <summary>本检查没有轴，参数读取必须明确拒绝。</summary>
            public Task<AxisMappingCardParameters> ReadAxisParametersAsync(int axis, CancellationToken token) { throw new NotSupportedException("内部检查没有轴参数。"); }
            /// <summary>本检查不声明回零能力。</summary>
            public IReadOnlyList<AxisHomeModeOption> GetSupportedHomeModes(MotionCardSelectionOption card, int axis) => new AxisHomeModeOption[0];
        }
    }
}
