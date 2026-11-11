using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.Framework.Runtime.Machine;
using MotionControl.Host.Wpf;

namespace MotionControl.OperatorPages.Checks
{
    /// <summary>实际调用 App 关窗入口，核对断卡和连接同步等待后的来源；仅为无轴离线故障注入。</summary>
    internal static partial class BoundTaskLoadChecks
    {
        /// <summary>分别核对正常关窗、断卡等待换源和同步等待换源，不以退出协调对象的单体检查代替 App 回归。</summary>
        internal static async Task RunConnectionShutdownChecksAsync()
        {
            var app = Application.Current as ConnectionContextCheckApplication;
            Require(app != null, "关窗来源检查必须在 --connection-context 独立 App 进程中运行。");
            var failures = new List<Exception>();
            await RunConnectionCheckAsync("真实 App 正常断卡同步后允许关窗",
                () => CheckConnectionShutdownAsync(app, false, false), failures);
            await RunConnectionCheckAsync("真实 App 断卡等待后拒绝关闭后来连接",
                () => CheckConnectionShutdownAsync(app, true, false), failures);
            await RunConnectionCheckAsync("真实 App 同步等待后拒绝关闭后来连接",
                () => CheckConnectionShutdownAsync(app, true, true), failures);
            if (failures.Count != 0) throw new AggregateException("真实 App 关窗来源离线回归未通过。", failures);
        }

        /// <summary>在真实关窗已经完成停止和保存后控制等待位置，检查后来连接与先前保存的恢复证据。</summary>
        private static async Task CheckConnectionShutdownAsync(App app, bool replaceConnection, bool waitForSynchronization)
        {
            var fixture = new ConnectionContextFixture(app);
            var errors = new List<Exception>();
            var monitorFinished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var synchronizationEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationTokenSource monitorCancellation = null;
            CancellationTokenRegistration monitorRegistration = default(CancellationTokenRegistration);
            Task connecting = null;
            bool closingStarted = false;
            int closeRequests = 0;
            CancelEventHandler observeClose = (sender, args) =>
            {
                closeRequests++;
                // 记录生产代码实际调用 Close；保留检查窗口，以便同一独立进程继续后续场景。
                args.Cancel = true;
            };
            using (var dialogs = new ConnectionCheckDialogScope("暂不能关闭软件"))
            {
                try
                {
                    fixture.Initialize();
                    await fixture.ConnectNewCardAsync();
                    MachineRuntime original = fixture.CurrentRuntime;
                    new WindowInteropHelper(fixture.Host).EnsureHandle();
                    fixture.Host.Closing += observeClose;
                    fixture.Connection.BlockDisconnect = true;
                    SetConnectionCheckField(app, "_taskEditsCloseConfirmed", true);
                    MethodInfo close = typeof(App).GetMethod("MainWindow_Closing", ConnectionCheckFlags);
                    Require(close != null, "找不到真实 App 关窗入口。");
                    var closeArgs = new CancelEventArgs();
                    closingStarted = true;
                    close.Invoke(app, new object[] { fixture.Host, closeArgs });
                    Require(closeArgs.Cancel, "连接中的首次关窗没有取消，未等待真实设备收尾。");
                    await WaitForConnectionCheckAsync(fixture.Connection.DisconnectEntered.Task,
                        "真实 App 没有完成停止、保存并进入连接服务的断开屏障，本次关窗场景未建立。");
                    Require((bool)fixture.AppField("_shutdownDisconnectInProgress") && closeRequests == 0,
                        "真实 App 尚在断卡等待时已请求关闭，或没有保留关窗等待状态。");

                    var store = (MachineConfigurationStore)fixture.AppField("_configurationStore");
                    string recoveryFile = Path.Combine(store.GetMachineDirectory("Machine01"), "Runtime", "RecoverySnapshot.json");
                    Require(File.Exists(recoveryFile), "真实关窗没有保存恢复快照，不能核对迟到结果是否覆盖证据。");
                    byte[] savedRecovery = File.ReadAllBytes(recoveryFile);
                    LoadCard replacementCard = null;
                    if (waitForSynchronization)
                    {
                        // Runtime 的真实断开已经结束。这里只重放一个尚未结束的监控收尾，定位 App 随后的同步 await。
                        monitorCancellation = new CancellationTokenSource();
                        monitorRegistration = monitorCancellation.Token.Register(() => synchronizationEntered.TrySetResult(true));
                        SetConnectionCheckField(original, "_controlInputMonitorCancellation", monitorCancellation);
                        SetConnectionCheckField(original, "_controlInputMonitorTask", monitorFinished.Task);
                        fixture.Connection.ResumeDisconnect.TrySetResult(true);
                        await WaitForConnectionCheckAsync(synchronizationEntered.Task,
                            "真实关窗的连接同步没有进入原监控退出等待，本次同步换源场景未建立。");
                        Require((bool)fixture.AppField("_shutdownDisconnectInProgress") && closeRequests == 0,
                            "真实 App 在连接同步仍等待原监控时已经请求关闭。");
                    }
                    if (replaceConnection)
                    {
                        replacementCard = await fixture.InstallNewCardAsync();
                        connecting = fixture.SynchronizeAsync();
                        if (!waitForSynchronization)
                        {
                            await connecting;
                            Require(fixture.CurrentRuntime != null && !ReferenceEquals(original, fixture.CurrentRuntime),
                                "连接服务断开屏障内没有建立后来 Runtime，不能检查旧关窗是否越界。");
                        }
                    }

                    // 新实现可以先等 A 监控结束再发布 B；不把 B 必须提前发布当作检查条件。
                    monitorFinished.TrySetResult(true);
                    fixture.Connection.ResumeDisconnect.TrySetResult(true);
                    if (connecting != null) await connecting;
                    await WaitForConnectionShutdownCompletionAsync(fixture);
                    Require(File.ReadAllBytes(recoveryFile).SequenceEqual(savedRecovery),
                        "ROC19_关窗迟到改写恢复证据：等待之后用后来上下文覆盖了本次已保存的恢复快照。");

                    if (!replaceConnection)
                    {
                        Require(closeRequests == 1 && dialogs.DismissedCount == 0
                            && !fixture.Connection.IsConnected && fixture.Connection.ConnectedMotionCard == null
                            && fixture.CurrentRuntime == null,
                            "ROC19_正常关窗被误拒绝：真实断卡及同步清空 Runtime 后没有请求关闭，或误报上下文变化。");
                    }
                    else
                    {
                        string phase = waitForSynchronization ? "同步等待" : "断卡等待";
                        Require(closeRequests == 0 && dialogs.DismissedCount == 1,
                            "ROC19_关窗" + phase + "越界：旧关闭请求没有明确拒绝，或仍对后来连接请求 Close。");
                        MachineRuntime replacement = fixture.CurrentRuntime;
                        Require(replacement != null && !ReferenceEquals(original, replacement)
                            && replacement.MotionCards.Cards.Any(card => ReferenceEquals(card, replacementCard))
                            && fixture.Connection.IsConnected && ReferenceEquals(fixture.Connection.Card, replacementCard)
                            && !replacement.IsShutdownInProgress
                            && ReferenceEquals(fixture.LauncherField("_runtime"), replacement)
                            && ReferenceEquals(ReadConnectionCheckField(fixture.Host, "_machineRuntime"), replacement)
                            && ReferenceEquals(ReadConnectionCheckField(fixture.VariablePage, "_machineRuntime"), replacement)
                            && ReferenceEquals(fixture.AppField("_currentAlarmService"), replacement.AlarmService),
                            "ROC19_关窗" + phase + "清理新连接：旧退出改写了 B 的卡、运行时、页面、任务或报警来源。");
                        object history = fixture.AppField("_alarmHistoryJournal");
                        var monitor = (Task)ReadConnectionCheckField(replacement, "_controlInputMonitorTask");
                        Require(history != null && !(bool)ReadConnectionCheckField(history, "_closing")
                            && monitor != null && !monitor.IsCompleted,
                            "ROC19_关窗" + phase + "结束新监控：旧退出关闭了 B 的报警历史或现场按钮监控。");
                    }
                }
                catch (Exception error) { errors.Add(error); }
                finally
                {
                    // 失败也先放行原等待并观察真实退出结束，不能带着 async void 续体拆除下一组要复用的 App。
                    monitorFinished.TrySetResult(true);
                    fixture.Connection.ResumeDisconnect.TrySetResult(true);
                    await FinishConnectionCheckTaskAsync(connecting, false, errors);
                    if (closingStarted)
                    {
                        try { await WaitForConnectionShutdownCompletionAsync(fixture); }
                        catch (Exception error) { errors.Add(error); }
                    }
                    if (fixture.Host != null) fixture.Host.Closing -= observeClose;
                    try
                    {
                        typeof(App).GetMethod("ReleaseShutdownProjectGate", ConnectionCheckFlags).Invoke(app, null);
                        SetConnectionCheckField(app, "_taskEditsCloseConfirmed", false);
                        SetConnectionCheckField(app, "_shutdownRetainedBlockedEvidence", false);
                    }
                    catch (Exception error) { errors.Add(error); }
                    await fixture.CleanupAsync(errors);
                    monitorRegistration.Dispose();
                    monitorCancellation?.Dispose();
                }
            }
            if (errors.Count != 0) throw new AggregateException("真实 App 关窗来源检查或收尾失败。", errors);
        }

        /// <summary>等待真实 async void 关窗 finally 完成；超时是未收尾，不能当作成功拒绝。</summary>
        private static async Task WaitForConnectionShutdownCompletionAsync(ConnectionContextFixture fixture)
        {
            var timer = Stopwatch.StartNew();
            while ((bool)fixture.AppField("_shutdownDisconnectInProgress") && timer.Elapsed < TimeSpan.FromSeconds(8))
                await Task.Delay(10);
            Require(!(bool)fixture.AppField("_shutdownDisconnectInProgress"),
                "真实 App 关窗异步处理尚未收尾，不能将等待中状态当作来源拒绝。");
        }
    }
}
