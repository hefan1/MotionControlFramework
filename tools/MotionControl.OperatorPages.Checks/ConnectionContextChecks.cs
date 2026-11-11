using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
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
            await RunConnectionCheckAsync("旧连接完成后不得覆盖新组合 C", () => CheckLateConnectionSuccessAsync(app), failures);
            await RunConnectionCheckAsync("旧连接失败收尾不得清空新组合 C", () => CheckLateConnectionFailureAsync(app), failures);
            await RunConnectionCheckAsync("旧连接晚于断连不得重挂运行时", () => CheckLateConnectionAfterLossAsync(app, true), failures);
            await RunConnectionCheckAsync("失联通知不再同步时不得发布旧连接", () => CheckLateConnectionAfterLossAsync(app, false), failures);
            await RunConnectionCheckAsync("未发布监控失败必须保留真实收尾", () => CheckUnpublishedMonitorCleanupAsync(app), failures);
            await RunConnectionCheckAsync("未发布监控同步收尾后必须安全重新关窗", () => CheckUnpublishedMonitorCleanupAsync(app, true), failures);
            await RunConnectionCheckAsync("真实 App 关窗的连接来源", RunConnectionShutdownChecksAsync, failures);
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

        /// <summary>以真实在途装载挂起 B 的上下文设置，再替换组合并发布 C，核对旧 B 完成不能覆盖主窗口。</summary>
        private static async Task CheckLateConnectionSuccessAsync(App app)
        {
            var fixture = new ConnectionContextFixture(app);
            var errors = new List<Exception>();
            LoadCard originalCard = null;
            Task loading = null;
            Task oldConnecting = null;
            try
            {
                fixture.Initialize();
                originalCard = await fixture.ConnectNewCardAsync();
                object originalLauncher = fixture.AppField("_taskMonitorLauncher");
                originalCard.BlockWorkerRead = true;
                loading = fixture.InvokeLauncherAsync("LoadTaskSessionAsync");
                await WaitForConnectionCheckAsync(originalCard.WorkerEntered.Task, "旧组合 A 的任务读取未进入受控等待窗口。");
                long beforeSetting = (long)ReadConnectionCheckField(originalLauncher, "_machineContextVersion");
                await fixture.InstallNewCardAsync();
                oldConnecting = fixture.SynchronizeAsync();
                await WaitForAppContextSettingAsync(oldConnecting, originalLauncher, beforeSetting);
                // 旧基线已发布 B 时登记它；修复先清旧上下文时，这里仍可能是 A 或空值。
                fixture.TrackRuntime(fixture.CurrentRuntime);

                fixture.ReplaceLauncher();
                LoadCard newestCard = await fixture.ConnectNewCardAsync();
                MachineRuntime newest = fixture.CurrentRuntime;
                Guid? newestSession = newest.ConnectionSessionId;
                object newestHistory = fixture.AppField("_alarmHistoryJournal");
                Require(newest.MotionCards.Cards.Any(card => ReferenceEquals(card, newestCard)),
                    "组合故障注入中的 C 没有通过真实 App 入口发布，不能继续检查旧结果。");
                originalCard.Resume.Set();
                Exception loadError = await ObserveConnectionCheckAsync(loading);
                if (!(loadError is InvalidOperationException))
                    throw new InvalidOperationException("旧连接替换没有撤销原组合的任务装载。", loadError);
                await oldConnecting;

                RequireCurrentConnection(fixture, newest, newestSession, newestHistory,
                    "ROC19_旧连接完成覆盖新组合：B 的迟到结果改变了 C 的 App、主窗口、页面、任务入口或报警历史。");
            }
            catch (Exception error) { errors.Add(error); }
            finally
            {
                originalCard?.Resume.Set();
                await FinishConnectionCheckTaskAsync(loading, true, errors);
                await FinishConnectionCheckTaskAsync(oldConnecting, false, errors);
                await fixture.CleanupAsync(errors);
            }
            if (errors.Count != 0) throw new AggregateException("旧连接完成迟到检查或收尾失败。", errors);
        }

        /// <summary>只给旧 launcher 的关闭任务注入迟到异常，实际经过 App 初始化 catch，核对其不能清除已发布 C。</summary>
        private static async Task CheckLateConnectionFailureAsync(App app)
        {
            var fixture = new ConnectionContextFixture(app);
            var errors = new List<Exception>();
            var closeFinished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            LoadCard originalCard = null;
            object originalLauncher = null;
            Task originalClose = null;
            Task loading = null;
            Task oldConnecting = null;
            ConnectionCheckDialogScope dialogs = null;
            try
            {
                fixture.Initialize();
                originalCard = await fixture.ConnectNewCardAsync();
                originalLauncher = fixture.AppField("_taskMonitorLauncher");
                originalCard.BlockWorkerRead = true;
                loading = fixture.InvokeLauncherAsync("LoadTaskSessionAsync");
                await WaitForConnectionCheckAsync(originalCard.WorkerEntered.Task, "旧组合 A 的任务读取未进入失败场景的受控等待窗口。");
                originalClose = (Task)ReadConnectionCheckField(originalLauncher, "_sessionCloseTask");
                SetConnectionCheckField(originalLauncher, "_sessionCloseTask", closeFinished.Task);
                long beforeSetting = (long)ReadConnectionCheckField(originalLauncher, "_machineContextVersion");
                await fixture.InstallNewCardAsync();
                oldConnecting = fixture.SynchronizeAsync();
                await WaitForAppContextSettingAsync(oldConnecting, originalLauncher, beforeSetting);
                fixture.TrackRuntime(fixture.CurrentRuntime);

                fixture.ReplaceLauncher();
                LoadCard newestCard = await fixture.ConnectNewCardAsync();
                MachineRuntime newest = fixture.CurrentRuntime;
                Guid? newestSession = newest.ConnectionSessionId;
                object newestHistory = fixture.AppField("_alarmHistoryJournal");
                Require(newest.MotionCards.Cards.Any(card => ReferenceEquals(card, newestCard)),
                    "旧关闭异常释放前，C 没有通过真实 App 入口发布。");
                dialogs = new ConnectionCheckDialogScope("设备运行时");
                closeFinished.SetException(new IOException("ROC19_注入旧任务关闭迟到失败"));
                // setter 已持有注入的等待任务；恢复入口字段并放行实际装载，允许 App 的失败收尾等待真实回滚。
                SetConnectionCheckField(originalLauncher, "_sessionCloseTask", originalClose);
                originalCard.Resume.Set();
                await oldConnecting;
                Console.WriteLine("[内部提示] 仅关闭本检查线程的“设备运行时”提示：" + dialogs.DismissedCount + " 次；不计为正式 UI 验收。");
                Exception loadError = await ObserveConnectionCheckAsync(loading);
                if (!(loadError is InvalidOperationException))
                    throw new InvalidOperationException("旧失败场景的真实在途装载没有按上下文撤销结束。", loadError);
                RequireCurrentConnection(fixture, newest, newestSession, newestHistory,
                    "ROC19_旧连接失败清空新组合：B 的迟到失败收尾清除了 C 的运行时、页面、任务入口或报警历史。");
            }
            catch (Exception error) { errors.Add(error); }
            finally
            {
                originalCard?.Resume.Set();
                closeFinished.TrySetResult(true);
                await FinishConnectionCheckTaskAsync(oldConnecting, false, errors);
                dialogs?.Dispose();
                try
                {
                    if (originalLauncher != null) SetConnectionCheckField(originalLauncher, "_sessionCloseTask", originalClose);
                }
                catch (Exception error) { errors.Add(error); }
                await FinishConnectionCheckTaskAsync(loading, true, errors);
                // App 可能在新来源核对后直接放弃旧等待，仍须观察本检查主动注入的异常。
                await ObserveConnectionCheckAsync(closeFinished.Task);
                await fixture.CleanupAsync(errors);
            }
            if (errors.Count != 0) throw new AggregateException("旧连接失败迟到检查或收尾失败。", errors);
        }

        /// <summary>共用真实装载等待，分别检查后来离线同步和不再同步的失联通知能撤销旧 App 连接。</summary>
        private static async Task CheckLateConnectionAfterLossAsync(App app, bool synchronizeDisconnect)
        {
            var fixture = new ConnectionContextFixture(app);
            var errors = new List<Exception>();
            LoadCard originalCard = null;
            Task loading = null;
            Task oldConnecting = null;
            Task disconnecting = null;
            try
            {
                fixture.Initialize();
                originalCard = await fixture.ConnectNewCardAsync();
                MachineRuntime original = fixture.CurrentRuntime;
                Guid? originalSession = original.ConnectionSessionId;
                object originalLauncher = fixture.AppField("_taskMonitorLauncher");
                originalCard.BlockWorkerRead = true;
                loading = fixture.InvokeLauncherAsync("LoadTaskSessionAsync");
                await WaitForConnectionCheckAsync(originalCard.WorkerEntered.Task, "断连前的任务读取未进入受控等待窗口。");
                long beforeSetting = (long)ReadConnectionCheckField(originalLauncher, "_machineContextVersion");
                LoadCard pendingCard = await fixture.InstallNewCardAsync();
                oldConnecting = fixture.SynchronizeAsync();
                await WaitForAppContextSettingAsync(oldConnecting, originalLauncher, beforeSetting);
                fixture.TrackRuntime(fixture.CurrentRuntime);

                if (synchronizeDisconnect)
                {
                    fixture.ReplaceLauncher();
                    fixture.Connection.Card = null;
                    fixture.Connection.LogicalIo = null;
                    disconnecting = fixture.SynchronizeAsync();
                    await disconnecting;
                    Require(fixture.CurrentRuntime == null && fixture.LauncherField("_runtime") == null,
                        "后来离线同步没有实际清空当前上下文，不能检查旧连接迟到。");
                }
                else
                {
                    // 对应心跳路径的失联锁存边界：只发真实失联通知，故意不调用第二次 App 同步。
                    pendingCard.Online = false;
                    fixture.InvokeLauncher("MarkControllerConnectionLost");
                    Require((bool)fixture.LauncherField("_controllerConnectionLost") && original.ConnectionSessionId == null,
                        "真实失联通知没有撤销原连接身份，本次无同步场景未建立。");
                }

                originalCard.Resume.Set();
                Exception loadError = await ObserveConnectionCheckAsync(loading);
                if (!(loadError is InvalidOperationException))
                    throw new InvalidOperationException("连接失效后，原任务装载没有按上下文撤销结束。", loadError);
                await oldConnecting;

                object flow = fixture.AppField("_flowStatusPage");
                object manual = fixture.AppField("_manualMotionDebugPage");
                object hostFlow = ReadConnectionCheckField(fixture.Host, "_flowStatusPage");
                object hostManual = ReadConnectionCheckField(fixture.Host, "_manualMotionDebugPage");
                if (synchronizeDisconnect)
                {
                    Require(!fixture.Connection.IsConnected && fixture.CurrentRuntime == null
                        && fixture.LauncherField("_runtime") == null && fixture.LauncherField("_session") == null
                        && (bool)fixture.LauncherField("_controllerConnectionLost")
                        && ReadConnectionCheckField(fixture.Host, "_machineRuntime") == null
                        && ReadConnectionCheckField(fixture.VariablePage, "_machineRuntime") == null
                        && flow == null && manual == null && hostFlow == null && hostManual == null
                        && fixture.AppField("_currentAlarmService") == null && fixture.AppField("_alarmHistoryJournal") == null,
                        "ROC19_旧连接晚于断连重挂运行时：离线同步完成后，旧 B 把运行时或页面重新挂回当前组合。");
                }
                else
                {
                    // 保留已失效的 A 等待收尾是合法结果；任何入口都不能把迟到 B 安装为当前对象。
                    var boundRuntimes = new object[]
                    {
                        fixture.CurrentRuntime, fixture.LauncherField("_runtime"),
                        ReadConnectionCheckField(fixture.Host, "_machineRuntime"),
                        ReadConnectionCheckField(fixture.VariablePage, "_machineRuntime"),
                        flow == null ? null : ReadConnectionCheckField(flow, "_runtime"),
                        manual == null ? null : ReadConnectionCheckField(manual, "_machineRuntime"),
                        hostFlow == null ? null : ReadConnectionCheckField(hostFlow, "_runtime"),
                        hostManual == null ? null : ReadConnectionCheckField(hostManual, "_machineRuntime")
                    };
                    Require(!fixture.Connection.IsConnected && original.ConnectionSessionId == null
                        && !original.IsConnectionSessionCurrent(originalSession)
                        && (bool)fixture.LauncherField("_controllerConnectionLost")
                        && fixture.LauncherField("_session") == null && !original.IsTaskEngineControlClaimed
                        && boundRuntimes.All(runtime => runtime == null || ReferenceEquals(runtime, original)),
                        "ROC19_无同步失联后重挂旧连接：未再同步的失联通知被旧 B 覆盖，或页面仍安装 B，或原 A 身份未失效。");
                }
            }
            catch (Exception error) { errors.Add(error); }
            finally
            {
                originalCard?.Resume.Set();
                await FinishConnectionCheckTaskAsync(loading, true, errors);
                await FinishConnectionCheckTaskAsync(oldConnecting, false, errors);
                await FinishConnectionCheckTaskAsync(disconnecting, false, errors);
                await fixture.CleanupAsync(errors);
            }
            if (errors.Count != 0) throw new AggregateException("旧连接晚于失联检查或收尾失败。", errors);
        }

        /// <summary>在未发布运行时收尾时注入取消失败，分别核对同步重试和真实关窗的原对象收尾。</summary>
        private static async Task CheckUnpublishedMonitorCleanupAsync(App app, bool closeAfterMonitor = false)
        {
            var fixture = new ConnectionContextFixture(app);
            var errors = new List<Exception>();
            var monitorFinished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancellationEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            MachineRuntime unpublished = null;
            AlarmHistoryJournal history = null;
            CancellationTokenSource monitorCancellation = null;
            CancellationTokenRegistration monitorRegistration = default(CancellationTokenRegistration);
            Task connecting = null;
            Task retrying = null;
            using (var dialogs = new ConnectionCheckDialogScope("设备运行时"))
            {
                try
                {
                    fixture.Initialize();
                    await fixture.ConnectNewCardAsync();
                    MachineRuntime original = fixture.CurrentRuntime;
                    LoadCard pendingCard = await fixture.InstallNewCardAsync();
                    fixture.Connection.ObserveLogicalIo = () =>
                    {
                        var candidate = fixture.LauncherField("_runtime") as MachineRuntime;
                        if (candidate == null || ReferenceEquals(candidate, original)
                            || ReferenceEquals(candidate, fixture.CurrentRuntime) || unpublished != null) return;
                        // 只按真实 Runtime 身份触发一次，不依赖 getter 访问次数或替换 App 方法。
                        unpublished = candidate;
                        fixture.TrackRuntime(candidate);
                        fixture.Connection.ObserveLogicalIo = null;
                        try
                        {
                            var recorded = ReadConnectionCheckField(candidate.AlarmService, "HistoryRecorded") as Delegate;
                            history = recorded?.GetInvocationList().Select(handler => handler.Target)
                                .OfType<AlarmHistoryJournal>().SingleOrDefault();
                            Require(history != null, "未发布 B 的真实报警历史订阅未找到，不能建立监控收尾检查。");
                            var originalMonitor = (Task)ReadConnectionCheckField(candidate, "_controlInputMonitorTask");
                            // 空 IO 的真实监控及其停止均不捕获 UI 上下文；先等它结束，不能泄漏原后台任务。
                            candidate.StopControlInputMonitoringAsync().GetAwaiter().GetResult();
                            Require(originalMonitor != null && originalMonitor.IsCompleted,
                                "未发布 B 的原监控尚未结束，不能安装受控退出屏障。");
                            monitorCancellation = new CancellationTokenSource();
                            monitorRegistration = monitorCancellation.Token.Register(() =>
                            {
                                cancellationEntered.TrySetResult(true);
                                throw new IOException("ROC19_注入未发布监控取消回调失败");
                            });
                            SetConnectionCheckField(candidate, "_controlInputMonitorCancellation", monitorCancellation);
                            SetConnectionCheckField(candidate, "_controlInputMonitorTask", monitorFinished.Task);
                            fixture.Connection.LogicalIo = new NoIo();
                        }
                        catch (Exception error)
                        {
                            cancellationEntered.TrySetException(new InvalidOperationException("未发布监控检查的注入条件未建立。", error));
                        }
                    };

                    connecting = fixture.SynchronizeAsync();
                    await WaitForConnectionCheckAsync(cancellationEntered.Task,
                        "未进入补丁候选的未发布 Runtime 收尾；不能把旧生产或场景未成立当作本组 RED。");
                    Require(unpublished != null && history != null && monitorCancellation.IsCancellationRequested
                        && !monitorFinished.Task.IsCompleted,
                        "真实 App finally 没有取消并保留受控监控等待，本次场景未建立。");
                    var closingHistories = (HashSet<AlarmHistoryJournal>)fixture.AppField("_closingAlarmHistories");
                    bool historyRetained = !(bool)ReadConnectionCheckField(history, "_closing") && !closingHistories.Contains(history);

                    if (closeAfterMonitor)
                    {
                        Require(historyRetained, "原监控失败后没有保留历史，不能建立真实关窗的同步收尾前提。");
                        monitorRegistration.Dispose();
                        monitorFinished.TrySetResult(true);
                        await connecting;
                        await pendingCard.DisconnectAsync(CancellationToken.None);
                        fixture.Connection.Card = null;
                        fixture.Connection.LogicalIo = null;
                        await CheckSynchronousUnpublishedClosingAsync(app, fixture, unpublished, history);
                    }
                    else
                    {
                        retrying = fixture.SynchronizeAsync();
                        // 给真实重试一次调度机会；唯一监控屏障仍未释放，不能把正在调度当作收尾已经完成。
                        await Task.Yield();
                        var currentBindings = new[]
                        {
                            fixture.CurrentRuntime,
                            (MachineRuntime)ReadConnectionCheckField(fixture.Host, "_machineRuntime"),
                            (MachineRuntime)ReadConnectionCheckField(fixture.VariablePage, "_machineRuntime")
                        };
                        bool retryWaited = !retrying.IsCompleted && currentBindings.All(runtime => runtime == null
                            || ((ReferenceEquals(runtime, original) || ReferenceEquals(runtime, unpublished))
                                && runtime.ConnectionSessionId == null));
                        historyRetained = historyRetained && !(bool)ReadConnectionCheckField(history, "_closing")
                            && !closingHistories.Contains(history);
                        monitorRegistration.Dispose();
                        monitorFinished.TrySetResult(true);
                        await connecting;
                        await retrying;

                        var writer = (Thread)ReadConnectionCheckField(history, "_writer");
                        bool writerStopped = await Task.Run(() => writer.Join(4000));
                        MachineRuntime replacement = fixture.CurrentRuntime;
                        Console.WriteLine("[未发布收尾] 屏障内保留历史=" + historyRetained + "，重试等待原监控=" + retryWaited
                            + "，放行后历史线程结束=" + writerStopped + "。");
                        const string failure = "ROC19_未发布监控失败丢失收尾：B 的监控未结束就关闭了历史、允许重试发布 C，或放行后未完成原历史收尾。";
                        Require(historyRetained && retryWaited && writerStopped && (bool)ReadConnectionCheckField(history, "_closing")
                            && replacement != null && !ReferenceEquals(replacement, unpublished)
                            && replacement.MotionCards.Cards.Any(card => ReferenceEquals(card, pendingCard)), failure);
                        RequireCurrentConnection(fixture, replacement, replacement.ConnectionSessionId,
                            fixture.AppField("_alarmHistoryJournal"), failure);
                    }
                }
                catch (Exception error) { errors.Add(error); }
                finally
                {
                    fixture.Connection.ObserveLogicalIo = null;
                    monitorRegistration.Dispose();
                    monitorFinished.TrySetResult(true);
                    await FinishConnectionCheckTaskAsync(connecting, false, errors);
                    await FinishConnectionCheckTaskAsync(retrying, false, errors);
                    // 产品断言之后才兜底回收捕获对象，不能用测试清理冒充 App 已保留或完成原收尾。
                    if (unpublished != null)
                    {
                        try { await unpublished.StopControlInputMonitoringAsync(); }
                        catch (Exception error) { errors.Add(error); }
                    }
                    if (history != null)
                    {
                        try
                        {
                            AlarmHistoryCloseResult result = await Task.Run(() => history.Close(TimeSpan.FromSeconds(4)));
                            Require(result.IsComplete, "未发布监控检查的原报警历史兜底收尾失败：" + result.Error);
                        }
                        catch (Exception error) { errors.Add(error); }
                    }
                    await fixture.CleanupAsync(errors);
                    monitorCancellation?.Dispose();
                }
            }
            if (errors.Count != 0) throw new AggregateException("未发布监控失败检查或收尾失败。", errors);
        }

        /// <summary>通过真实 Window.Close 进入 WPF Closing，核对同步完成的未发布收尾不会在原关窗事件中递归 Close。</summary>
        private static async Task CheckSynchronousUnpublishedClosingAsync(App app, ConnectionContextFixture fixture,
            MachineRuntime runtime, AlarmHistoryJournal history)
        {
            var errors = new List<Exception>();
            var dispatcherErrors = new List<Exception>();
            var closingObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int closingCalls = 0;
            bool firstCancelled = false;
            bool closingStarted = false;
            CancelEventHandler productionClosing = null;
            CancelEventHandler observeClosing = (sender, args) =>
            {
                closingCalls++;
                if (closingCalls == 1) firstCancelled = args.Cancel;
                // 此观察者在生产处理器之后执行；始终保留内部窗口，避免真正退出专项进程。
                args.Cancel = true;
                if (closingCalls >= 2) closingObserved.TrySetResult(true);
            };
            DispatcherUnhandledExceptionEventHandler observeException = (sender, args) =>
            {
                dispatcherErrors.Add(args.Exception);
                Console.Error.WriteLine("[真实关窗原异常] " + args.Exception);
                args.Handled = true;
                closingObserved.TrySetResult(true);
            };
            using (var dialogs = new ConnectionCheckDialogScope("暂不能关闭软件"))
            using (var recoveryDialogs = new ConnectionCheckDialogScope("恢复状态保存失败"))
            {
                try
                {
                    Require(app.ShutdownMode == ShutdownMode.OnExplicitShutdown,
                        "真实关窗检查必须使用显式退出模式，不能让关闭一个内部窗口提前结束专项进程。");
                    var pending = (System.Collections.IList)fixture.AppField("_unfinishedConnectionCleanups");
                    Require(pending.Count == 1 && ReferenceEquals(ReadConnectionCheckField(pending[0], "Runtime"), runtime)
                        && ReferenceEquals(ReadConnectionCheckField(pending[0], "History"), history),
                        "App 没有保留本组真实未发布对象，不能建立同步关窗收尾场景。");
                    object cleanup = pending[0];
                    var previousStop = (Task)ReadConnectionCheckField(cleanup, "StopTask");
                    var monitor = (Task)ReadConnectionCheckField(runtime, "_controlInputMonitorTask");
                    Require(previousStop != null && previousStop.IsFaulted && monitor != null && monitor.IsCompleted
                        && !fixture.Connection.IsConnected && fixture.Connection.Card == null && fixture.Connection.LogicalIo == null
                        && fixture.CurrentRuntime == null && !(bool)fixture.AppField("_shutdownDisconnectInProgress"),
                        "原取消失败、已结束监控和离线来源没有同时成立，不能检查同步完成的 Closing。");
                    Require(new WindowInteropHelper(fixture.Host).EnsureHandle() != IntPtr.Zero,
                        "真实关窗检查没有建立窗口 HWND，不能以直接调用事件代替 WPF Closing。");
                    MethodInfo closing = typeof(App).GetMethod("MainWindow_Closing", ConnectionCheckFlags);
                    Require(closing != null, "找不到真实 App 关窗处理器。");
                    productionClosing = (CancelEventHandler)Delegate.CreateDelegate(typeof(CancelEventHandler), app, closing);
                    app.DispatcherUnhandledException += observeException;
                    fixture.Host.Closing += productionClosing;
                    fixture.Host.Closing += observeClosing;
                    SetConnectionCheckField(app, "_taskEditsCloseConfirmed", true);
                    closingStarted = true;
                    fixture.Host.Close();

                    await WaitForConnectionCheckAsync(closingObserved.Task,
                        "真实 Closing 没有再次进入，也未取得本组原始异常，不能确认关窗结果。");
                    await WaitForConnectionShutdownCompletionAsync(fixture);
                    // 排空本次 Close 已投递的异常；完成依据仍是真实 Closing 事件与 App 关闭状态。
                    await app.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle).Task;
                    Console.WriteLine("[真实 Closing] 次数=" + closingCalls + "，首次取消=" + firstCancelled
                        + "，调度异常=" + dispatcherErrors.Count + "，待收尾对象=" + pending.Count + "。");
                    Require(closingCalls >= 1 && firstCancelled && pending.Count == 0
                        && ((Task)ReadConnectionCheckField(cleanup, "StopTask")).Status == TaskStatus.RanToCompletion,
                        "真实关窗没有经过原 pending 收尾分支，不能把其他初始化或关闭错误当作本组 RED。");
                    Require(dialogs.DismissedCount == 0 && recoveryDialogs.DismissedCount == 0,
                        "真实关窗被恢复或来源提示拒绝，本次同步重入场景未完成，请查看原提示正文。");
                    if (dispatcherErrors.Count == 0)
                        Require(closingCalls == 2 && !(bool)fixture.AppField("_shutdownDisconnectInProgress"),
                            "ROC19_未发布收尾同步关窗重入：原收尾完成后没有在 WPF 允许的时机再次进入 Closing。");
                }
                catch (Exception error) { errors.Add(error); }
                finally
                {
                    if (closingStarted)
                    {
                        try { await WaitForConnectionShutdownCompletionAsync(fixture); }
                        catch (Exception error)
                        {
                            errors.Add(error);
                            // 未完成的 async void 仍持有窗口；继续观察到实际结束，不能拆掉 handler 后污染下一组。
                            while ((bool)fixture.AppField("_shutdownDisconnectInProgress")) await Task.Delay(10);
                        }
                        try { await app.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle).Task; }
                        catch (Exception error) { errors.Add(error); }
                    }
                    fixture.Host.Closing -= observeClosing;
                    if (productionClosing != null) fixture.Host.Closing -= productionClosing;
                    app.DispatcherUnhandledException -= observeException;
                    try
                    {
                        typeof(App).GetMethod("ReleaseShutdownProjectGate", ConnectionCheckFlags).Invoke(app, null);
                        SetConnectionCheckField(app, "_taskEditsCloseConfirmed", false);
                        SetConnectionCheckField(app, "_shutdownRetainedBlockedEvidence", false);
                    }
                    catch (Exception error) { errors.Add(error); }
                }
            }
            if (dispatcherErrors.Count != 0)
                errors.Add(new AggregateException("ROC19_未发布收尾同步关窗重入：真实 Closing 的原处理尚未返回就再次 Close，产生调度异常。", dispatcherErrors));
            if (errors.Count != 0) throw new AggregateException("未发布收尾的真实 WPF 关窗检查或收尾失败。", errors);
        }

        /// <summary>确认旧 App 调用已进入真实 setter 的关闭等待，不要求 Runtime 必须在等待前创建或发布。</summary>
        private static async Task WaitForAppContextSettingAsync(Task connecting, object launcher, long beforeSetting)
        {
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(4))
            {
                long version = (long)ReadConnectionCheckField(launcher, "_machineContextVersion");
                var closing = ReadConnectionCheckField(launcher, "_sessionCloseTask") as Task;
                if (version > beforeSetting && closing != null && !closing.IsCompleted && !connecting.IsCompleted) return;
                if (connecting.IsCompleted) break;
                await Task.Delay(10);
            }
            throw new InvalidOperationException("旧 App 连接没有进入任务上下文关闭等待，本次组合时序检查无效。");
        }

        /// <summary>核对同一个已发布 Runtime、连接代次、页面与报警历史，不能只凭某个 App 字段仍存在判定成功。</summary>
        private static void RequireCurrentConnection(ConnectionContextFixture fixture, MachineRuntime runtime,
            Guid? connection, object history, string message)
        {
            object flow = fixture.AppField("_flowStatusPage");
            object manual = fixture.AppField("_manualMotionDebugPage");
            var monitor = ReadConnectionCheckField(runtime, "_controlInputMonitorTask") as Task;
            Require(ReferenceEquals(fixture.CurrentRuntime, runtime)
                && runtime.IsConnectionSessionCurrent(connection)
                && ReferenceEquals(fixture.LauncherField("_runtime"), runtime)
                && ReferenceEquals(ReadConnectionCheckField(fixture.Host, "_machineRuntime"), runtime)
                && ReferenceEquals(ReadConnectionCheckField(fixture.VariablePage, "_machineRuntime"), runtime)
                && flow != null && ReferenceEquals(ReadConnectionCheckField(flow, "_runtime"), runtime)
                && ReferenceEquals(ReadConnectionCheckField(fixture.Host, "_flowStatusPage"), flow)
                && manual != null && ReferenceEquals(ReadConnectionCheckField(manual, "_machineRuntime"), runtime)
                && ReferenceEquals(ReadConnectionCheckField(fixture.Host, "_manualMotionDebugPage"), manual)
                && ReferenceEquals(fixture.AppField("_currentAlarmService"), runtime.AlarmService)
                && history != null && ReferenceEquals(fixture.AppField("_alarmHistoryJournal"), history)
                && !(bool)ReadConnectionCheckField(history, "_closing")
                && monitor != null && !monitor.IsCompleted, message);
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

        /// <summary>仅在内部检查设置组合引用或明确声明的生命周期任务屏障，不替换生产方法。</summary>
        private static void SetConnectionCheckField(object target, string name, object value)
        {
            Type type = target is App ? typeof(App) : target.GetType();
            FieldInfo field = type.GetField(name, ConnectionCheckFlags);
            if (field == null) throw new InvalidOperationException("连接检查找不到字段：" + type.FullName + "." + name);
            field.SetValue(target, value);
        }

        /// <summary>仅在本检查 UI 线程关闭指定标题的系统提示框；不操作正式 Host、其他线程或其他进程。</summary>
        private sealed class ConnectionCheckDialogScope : IDisposable
        {
            /// <summary>当前内部检查所在的原生 UI 线程编号。</summary>
            private readonly uint _thread = GetCurrentThreadId();
            /// <summary>本场景允许关闭的唯一提示框标题。</summary>
            private readonly string _expectedCaption;
            /// <summary>在 MessageBox 的嵌套调度循环中查找本场景提示框。</summary>
            private readonly DispatcherTimer _timer;
            /// <summary>本范围向匹配提示框成功投递关闭消息的次数，仅用于内部诊断。</summary>
            internal int DismissedCount { get; private set; }

            /// <summary>开始只针对当前检查线程和指定标题的提示框观察，不改变生产提示方法。</summary>
            internal ConnectionCheckDialogScope(string expectedCaption)
            {
                if (string.IsNullOrWhiteSpace(expectedCaption)) throw new ArgumentException("检查提示框标题不能为空。", nameof(expectedCaption));
                _expectedCaption = expectedCaption;
                _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
                _timer.Tick += DismissMatchingDialog;
                _timer.Start();
            }

            /// <summary>同时核对原生类名和完整标题，只关闭本检查声明的系统提示框。</summary>
            private void DismissMatchingDialog(object sender, EventArgs e)
            {
                EnumThreadWindows(_thread, (window, parameter) =>
                {
                    var className = new StringBuilder(64);
                    GetClassName(window, className, className.Capacity);
                    if (className.ToString() != "#32770") return true;
                    var caption = new StringBuilder(256);
                    GetWindowText(window, caption, caption.Capacity);
                    if (caption.ToString() != _expectedCaption) return true;
                    // 只读取已经匹配的本检查提示框正文，保留原始拒绝原因，不扫描其他窗口。
                    EnumChildWindows(window, (child, childParameter) =>
                    {
                        var childClass = new StringBuilder(64);
                        GetClassName(child, childClass, childClass.Capacity);
                        if (childClass.ToString() != "Static") return true;
                        var body = new StringBuilder(4096);
                        GetWindowText(child, body, body.Capacity);
                        if (!string.IsNullOrWhiteSpace(body.ToString()))
                            Console.WriteLine("[内部提示正文] " + _expectedCaption + "：" + body);
                        return true;
                    }, IntPtr.Zero);
                    if (PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero)) DismissedCount++;
                    return false;
                }, IntPtr.Zero);
            }

            /// <summary>停止本范围的提示框观察，后续测试窗口不受影响。</summary>
            public void Dispose()
            {
                _timer.Stop();
                _timer.Tick -= DismissMatchingDialog;
            }

            /// <summary>只接收指定原生 UI 线程所属的窗口。</summary>
            private delegate bool WindowEnumerator(IntPtr window, IntPtr parameter);
            /// <summary>枚举当前内部检查线程的窗口，不扫描其他进程。</summary>
            [DllImport("user32.dll")]
            private static extern bool EnumThreadWindows(uint threadId, WindowEnumerator callback, IntPtr parameter);
            /// <summary>只枚举已确认属于本检查的提示框子控件，取得拒绝正文。</summary>
            [DllImport("user32.dll")]
            private static extern bool EnumChildWindows(IntPtr parent, WindowEnumerator callback, IntPtr parameter);
            /// <summary>读取系统提示框的窗口类名。</summary>
            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            private static extern int GetClassName(IntPtr window, StringBuilder name, int maximum);
            /// <summary>读取提示框标题，避免关闭本线程其他类型的提醒。</summary>
            [DllImport("user32.dll", CharSet = CharSet.Unicode)]
            private static extern int GetWindowText(IntPtr window, StringBuilder title, int maximum);
            /// <summary>仅向已经匹配的内部检查提示框发送关闭消息。</summary>
            [DllImport("user32.dll")]
            private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
            /// <summary>取得构造本范围时的检查线程编号。</summary>
            [DllImport("kernel32.dll")]
            private static extern uint GetCurrentThreadId();
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
            /// <summary>包括被替换的旧任务入口，所有实际装载与持权都须安全结束。</summary>
            private readonly List<object> _launchers = new List<object>();
            /// <summary>本组入口共享的临时设备配置服务。</summary>
            private MachineConfigurationStore _store;
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
                _store = store;
                WriteMachine(store, "Machine01");
                var catalog = new MachineAlarmCatalogStore(store);
                catalog.LoadOrCreate("Machine01", new MachineAlarmCatalogDocument());
                Variables = new DeviceVariableStore(new DeviceVariableDefinition[0]);
                Host = new MainWindow();
                VariablePage = new DeviceVariableView(Variables, store, "Machine01");
                _launcher = CreateLauncher();
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

            /// <summary>为同一隔离设备创建真实任务入口，并登记其完整收尾责任。</summary>
            private object CreateLauncher()
            {
                object launcher = Activator.CreateInstance(_launcherType, ConnectionCheckFlags, null,
                    new object[] { _store, "Machine01", Host, null }, null);
                _launchers.Add(launcher);
                return launcher;
            }

            /// <summary>只在组合故障注入中替换 App 当前任务入口，旧入口仍保留并由 CleanupAsync 收尾。</summary>
            internal void ReplaceLauncher()
            {
                _launcher = CreateLauncher();
                SetConnectionCheckField(_app, "_taskMonitorLauncher", _launcher);
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
                Connection.ResumeDisconnect.TrySetResult(true);
                TrackRuntime(CurrentRuntime);
                foreach (object launcher in _launchers)
                {
                    try
                    {
                        await (Task)_launcherType.GetMethod("CloseTaskSessionAsync", ConnectionCheckFlags).Invoke(launcher, new object[] { false });
                        await (Task)_launcherType.GetMethod("SetMachineContextAsync", ConnectionCheckFlags).Invoke(launcher,
                            new object[] { null, null, null, null, null });
                    }
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
                if (errors.Count == 0)
                {
                    try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
                    catch (Exception error) { errors.Add(error); }
                }
                else Console.Error.WriteLine("[检查证据] " + _root);
            }
        }

        /// <summary>仅把既有无轴 LoadCard 与禁止 IO 的服务交给真实 App，不提供运动或参数写入能力。</summary>
        private sealed class ConnectionContextCardConnection : IMotionCardHostConnection
        {
            /// <summary>本轮对外可见的无轴卡引用，测试据此控制来源变化。</summary>
            internal IMotionCard Card;
            /// <summary>本轮逻辑 IO 身份；NoIo 对所有硬件调用明确拒绝。</summary>
            internal ILogicalIo LogicalIo;
            /// <summary>仅在专项故障注入观察真实 IO 来源读取；默认为空，不改变其他连接检查。</summary>
            internal Action ObserveLogicalIo;
            /// <summary>只挂起下一次 provider 断开，默认不阻塞任何现有场景。</summary>
            internal bool BlockDisconnect;
            /// <summary>下一次被挂起的 provider 断开已经捕获原卡对象。</summary>
            internal readonly TaskCompletionSource<bool> DisconnectEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>释放本连接源的单次 provider 断开等待。</summary>
            internal readonly TaskCompletionSource<bool> ResumeDisconnect = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
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
            /// <summary>返回与当前卡同时安装的 IO 身份；专项可在真实来源核对处注入生命周期变化。</summary>
            public ILogicalIo ConnectedLogicalIo
            {
                get { ObserveLogicalIo?.Invoke(); return LogicalIo; }
            }
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
                IMotionCard card = Card;
                if (BlockDisconnect)
                {
                    BlockDisconnect = false;
                    DisconnectEntered.TrySetResult(true);
                    await ResumeDisconnect.Task;
                }
                token.ThrowIfCancellationRequested();
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
