using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.IO;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Contracts.Preparation;
using MotionControl.Framework.Contracts.Safety;
using MotionControl.Framework.Runtime.Alarms;
using MotionControl.Framework.Runtime.Machine;

namespace MotionControl.Preparation.Checks
{
    /// <summary>通过独立假设备检查通用准备的命令权、依赖、停止与启动闸门，不连接真实设备。</summary>
    internal static class Program
    {
        /// <summary>执行全部检查并返回失败退出码。</summary>
        private static int Main()
        {
            try { RunAsync().GetAwaiter().GetResult(); Console.WriteLine("设备准备：19 组检查通过（仅内部逻辑检查）。"); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }

        /// <summary>逐组验证依赖、启动、取消、急停、未知状态与任务入口。</summary>
        private static async Task RunAsync()
        {
            await StartupAndInvalidationAsync();
            await StopAndExclusionAsync();
            await EmergencyStopAsync();
            await FailureDoesNotPrepareAsync();
            await UnknownStateAndDependencyAsync();
            await TaskEntryGuardAsync();
            await StopFailureRemainsExclusiveAsync();
            await CompletionCancellationAndLateProgressAsync();
            await UntrustedRecoveryBlocksAutomationAsync();
            await RunMarkerFailureBlocksBothOwnersAsync();
            await PreparationMarkerLifecycleAsync();
            await StopAlarmCannotBeOverwrittenByTaskPauseAsync();
            await PreparationAlarmGateAsync();
            await StationFlowStartAlarmGateAsync();
            await StationFlowSettlementAndConnectionAsync();
            await FailedDisconnectInvalidatesPreparationAsync();
            await OldAlarmTimeoutDoesNotAffectNewRunAsync();
            await OldPreparationStopDoesNotAffectNewRunAsync();
            await PreparationPreflightMarkerAsync();
        }

        /// <summary>只读前提拒绝不能创建运行标记；标记后的条件变化仍保留保护。</summary>
        private static async Task PreparationPreflightMarkerAsync()
        {
            var fixture = new Fixture();
            int markers = 0;
            var preparation = new Preparation("Preflight", fixture) { RejectValidationAt = 1 };
            fixture.Runtime.SetPreparationRecordCallbacks(() => markers++, () => { });
            fixture.Runtime.ConfigurePreparations(new[] { preparation });
            await fixture.ConnectAsync();
            await RejectAsync(() => fixture.Runtime.PrepareAsync(null, CancellationToken.None), "未满足前提仍进入准备。");
            Ensure(markers == 0 && fixture.Log.Count == 0 && !fixture.Runtime.IsPreparing,
                "只读前提失败建立了运行标记或执行了动作。");
            preparation.RejectValidationAt = 0;
            await fixture.Runtime.PrepareAsync(null, CancellationToken.None);
            Ensure(markers == 1 && fixture.Log.Count == 1 && fixture.Runtime.IsPrepared, "修正前提后无法安全准备。");
            await fixture.Runtime.DisconnectAsync(CancellationToken.None);

            var cancelledBeforeMarker = new Fixture();
            var cancellation = new CancellationTokenSource();
            int cancelledMarkers = 0;
            cancelledBeforeMarker.Runtime.SetPreparationRecordCallbacks(() => cancelledMarkers++, () => { });
            cancelledBeforeMarker.Runtime.ConfigurePreparations(new[] { new Preparation("CancelledPreflight", cancelledBeforeMarker) { OnValidate = () => cancellation.Cancel() } });
            await cancelledBeforeMarker.ConnectAsync();
            await RejectAsync(() => cancelledBeforeMarker.Runtime.PrepareAsync(null, cancellation.Token), "预检期间取消仍进入准备。");
            Ensure(cancelledMarkers == 0 && cancelledBeforeMarker.Log.Count == 0, "预检取消后错误写入标记或执行动作。");
            await cancelledBeforeMarker.Runtime.DisconnectAsync(CancellationToken.None);
            cancellation.Dispose();

            var alarmedPreflight = new Fixture();
            int alarmedMarkers = 0;
            alarmedPreflight.Runtime.SetPreparationRecordCallbacks(() => alarmedMarkers++, () => { });
            alarmedPreflight.Runtime.ConfigurePreparations(new[] { new Preparation("AlarmPreflight", alarmedPreflight)
            {
                OnValidate = () => alarmedPreflight.Runtime.AlarmService.ReportCondition(
                    new MachineAlarmDefinition("PREFLIGHT_STOP", "预检停机", MachineAlarmKind.StopMachine), "检查", true, "预检期间报警")
            } });
            await alarmedPreflight.ConnectAsync();
            await RejectAsync(() => alarmedPreflight.Runtime.PrepareAsync(null, CancellationToken.None), "预检报警仍执行准备。");
            Ensure(alarmedMarkers == 0 && alarmedPreflight.Log.Count == 0, "预检报警仍建立标记或执行动作。");
            await alarmedPreflight.Runtime.DisconnectAsync(CancellationToken.None);

            var changed = new Fixture();
            int changedMarkers = 0;
            var changing = new Preparation("Changed", changed) { RejectValidationAt = 2 };
            changed.Runtime.SetPreparationRecordCallbacks(() => changedMarkers++, () => { });
            changed.Runtime.ConfigurePreparations(new[] { changing });
            await changed.ConnectAsync();
            await RejectAsync(() => changed.Runtime.PrepareAsync(null, CancellationToken.None), "预检后条件改变未被拒绝。");
            Ensure(changedMarkers == 1 && changed.Log.Count == 0 && !changed.Runtime.IsPreparationRecordReusable,
                "标记后的条件变化错误释放了运行保护。");
            await changed.Runtime.DisconnectAsync(CancellationToken.None);

            var failed = new Fixture();
            int failedMarkers = 0;
            failed.Runtime.SetPreparationRecordCallbacks(() => failedMarkers++, () => { });
            failed.Runtime.ConfigurePreparations(new[] { new Preparation("ExecutionFailure", failed) { ReadyPose = false } });
            await failed.ConnectAsync();
            await RejectAsync(() => failed.Runtime.PrepareAsync(null, CancellationToken.None), "执行后的姿态失败未被拒绝。");
            Ensure(failedMarkers == 1 && failed.Log.Count == 1 && !failed.Runtime.IsPreparationRecordReusable,
                "实际执行失败错误释放了运行保护。");
            await failed.Runtime.DisconnectAsync(CancellationToken.None);
        }

        /// <summary>旧准备的报警停止请求迟到时，不得停止新的准备周期。</summary>
        private static async Task OldPreparationStopDoesNotAffectNewRunAsync()
        {
            var fixture = new Fixture();
            fixture.Runtime.ConfigurePreparations(new[] { new Preparation("Slow", fixture) { WaitUntilStopped = true } });
            await fixture.ConnectAsync();
            Task first = fixture.Runtime.PrepareAsync(null, CancellationToken.None);
            await WaitAsync(() => fixture.Axis.Moving);
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var oldToken = (CancellationTokenSource)typeof(MachineRuntime).GetField("_preparationCancellation", flags).GetValue(fixture.Runtime);
            await fixture.Runtime.StopPreparationAsync(CancellationToken.None);
            try { await first; } catch (OperationCanceledException) { }
            Task second = fixture.Runtime.PrepareAsync(null, CancellationToken.None);
            await WaitAsync(() => fixture.Axis.Moving);
            try
            {
                var stop = typeof(MachineRuntime).GetMethod("StopPreparationAsync", flags, null,
                    new[] { typeof(CancellationToken), typeof(CancellationTokenSource) }, null);
                await (Task)stop.Invoke(fixture.Runtime, new object[] { CancellationToken.None, oldToken });
                Ensure(fixture.Runtime.IsPreparing && fixture.Axis.Moving && !second.IsCompleted,
                    "旧准备的迟到停止请求影响了新的准备周期。");
            }
            finally
            {
                await fixture.Runtime.StopPreparationAsync(CancellationToken.None);
                try { await second; } catch (OperationCanceledException) { }
            }
        }

        /// <summary>旧一轮报警的超时任务不能把下一轮正在受控暂停的生产误报为超时。</summary>
        private static async Task OldAlarmTimeoutDoesNotAffectNewRunAsync()
        {
            var flow = new DelayedSecondStopFlow();
            var runtime = new MachineRuntime(new MotionCardGroup(new[] { new Card(new Axis()) }), new IStationFlow[] { flow },
                new MachineAlarmService(), new Safety(), pauseTimeout: TimeSpan.FromMilliseconds(1000), safetyPollInterval: TimeSpan.FromMilliseconds(10));
            await runtime.ConnectAsync(CancellationToken.None);
            await runtime.ResetAsync(CancellationToken.None);
            await runtime.StartAsync(CancellationToken.None);
            await WaitAsync(() => flow.Executions == 1);
            var alarm = new MachineAlarmDefinition("OLD_TIMER", "第一轮停机", MachineAlarmKind.StopMachine);
            runtime.AlarmService.ReportCondition(alarm, "测试", true, "第一轮报警");
            runtime.AlarmService.ReportCondition(alarm, "测试", false, "条件恢复");
            await WaitAsync(() => runtime.State == MachineRunState.Alarmed);
            await Task.Delay(700);
            await runtime.ResetAsync(CancellationToken.None);
            await runtime.StartAsync(CancellationToken.None);
            await WaitAsync(() => flow.Executions == 2);
            Task pause = runtime.PauseAsync(CancellationToken.None);
            try
            {
                await flow.SecondStopEntered.Task;
                await Task.Delay(450);
                Ensure(runtime.State == MachineRunState.Pausing, "旧一轮报警超时错误影响了新一轮暂停。");
            }
            finally { flow.ReleaseSecondStop.TrySetResult(true); }
            await pause;
        }

        /// <summary>仅同次控制器连接且工位、观察器及报警交接均已正常结束时可确认 C# 流程收尾。</summary>
        private static async Task StationFlowSettlementAndConnectionAsync()
        {
            var fixture = new Fixture();
            await fixture.ConnectAsync();
            Guid? firstConnection = fixture.Runtime.ConnectionSessionId;
            Ensure(firstConnection.HasValue, "控制器连接后未生成连接周期标识。");
            await fixture.Runtime.ResetAsync(CancellationToken.None);
            Ensure(fixture.Runtime.IsStationFlowSettlementConfirmed, "首次运行前工位应处于正常空闲状态。");
            await fixture.Runtime.StartAsync(CancellationToken.None);
            await WaitAsync(() => fixture.Flow.Executions > 0);
            Ensure(!fixture.Runtime.IsStationFlowSettlementConfirmed, "生产运行中不能确认已收尾。");
            var alarm = new MachineAlarmDefinition("FLOW_SETTLE", "检查停机", MachineAlarmKind.StopMachine);
            fixture.Runtime.AlarmService.ReportCondition(alarm, "测试", true, "暂停流程");
            fixture.Runtime.AlarmService.ReportCondition(alarm, "测试", false, "条件恢复");
            await WaitAsync(() => fixture.Runtime.State == MachineRunState.Alarmed);
            await fixture.Runtime.ResetAsync(CancellationToken.None);
            await WaitAsync(() => fixture.Runtime.IsStationFlowSettlementConfirmed);
            Ensure(fixture.Runtime.ConnectionSessionId == firstConnection, "同次连接的暂停恢复改变了连接标识。");
            await fixture.Runtime.DisconnectAsync(CancellationToken.None);
            Ensure(fixture.Runtime.ConnectionSessionId == null && !fixture.Runtime.IsStationFlowSettlementConfirmed,
                "断开连接后不能沿用旧工位收尾凭据。");
            await fixture.ConnectAsync();
            Ensure(fixture.Runtime.ConnectionSessionId.HasValue && fixture.Runtime.ConnectionSessionId != firstConnection,
                "控制器重连后没有生成新的连接周期标识。");

            var disconnected = new Fixture();
            await disconnected.ConnectAsync();
            await disconnected.Runtime.ResetAsync(CancellationToken.None);
            disconnected.Card.ForceOffline();
            Ensure(disconnected.Runtime.ConnectionSessionId.HasValue && !disconnected.Runtime.IsStationFlowSettlementConfirmed,
                "底层控制卡意外断线时不能沿用 C# 流程收尾凭据。");

            var alarms = new MachineAlarmService();
            var notificationEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseNotification = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            alarms.StopAlarmRaised += (sender, args) => { notificationEntered.TrySetResult(true); releaseNotification.Task.GetAwaiter().GetResult(); };
            var other = new MachineRuntime(new MotionCardGroup(new[] { new Card(new Axis()) }), new[] { new Flow() }, alarms, new Safety());
            await other.ConnectAsync(CancellationToken.None);
            await other.ResetAsync(CancellationToken.None);
            Task report = Task.Run(() => alarms.ReportCondition(new MachineAlarmDefinition("PENDING_NOTIFY", "通知未交接", MachineAlarmKind.StopMachine), "测试", true, "测试"));
            try
            {
                await notificationEntered.Task;
                Ensure(other.State == MachineRunState.Ready && !other.IsStationFlowSettlementConfirmed,
                    "报警已锁存但回调尚未交接时错误确认工位收尾。");
            }
            finally { releaseNotification.TrySetResult(true); await report; }

            var failedFlow = new ThrowingStopFlow();
            var failed = new MachineRuntime(new MotionCardGroup(new[] { new Card(new Axis()) }), new IStationFlow[] { failedFlow },
                new MachineAlarmService(), new Safety());
            await failed.ConnectAsync(CancellationToken.None);
            await failed.ResetAsync(CancellationToken.None);
            await failed.StartAsync(CancellationToken.None);
            await failedFlow.Entered.Task;
            await RejectAsync(() => failed.PauseAsync(CancellationToken.None), "停止回调抛错却允许确认安全暂停");
            Ensure(failedFlow.StopCallbackCount == 1, "检查未实际触发预期的失败停止回调。");
            await failed.ResetAsync(CancellationToken.None);
            Ensure(!failed.IsStationFlowSettlementConfirmed, "停止回调失败但后台任务退出后错误确认收尾。");
        }

        /// <summary>断卡失败但卡仍连接、复位回到 Ready 时拒绝设备准备；真实重连后重新执行。</summary>
        private static async Task FailedDisconnectInvalidatesPreparationAsync()
        {
            await CheckDisconnectProbeFailureAsync();
            await CheckQueuedSessionCommandsAsync();
            await CheckAxisAlarmResetDisconnectAsync();
            await CheckConnectionLossDuringCallbacksAsync();
            foreach (var owner in new[] { MachineControlOwner.StationFlows, MachineControlOwner.TaskEngine })
            {
                var buttons = new MemoryButtons();
                var fixture = new Fixture(owner, buttons);
                int validations = 0, markers = 0;
                var item = new Preparation("Home", fixture) { OnValidate = () => validations++ };
                fixture.Runtime.ConfigurePreparations(new[] { item });
                fixture.Runtime.SetPreparationRecordCallbacks(() => markers++, () => { });
                await fixture.ConnectAsync();
                Guid? original = fixture.Runtime.ConnectionSessionId;
                await fixture.Runtime.PrepareAsync(null, CancellationToken.None);
                Ensure(fixture.Runtime.IsPrepared, "测试未建立有效准备资格。");
                int notifications = 0;
                int disconnectCallsAtNotification = 0;
                EventHandler throwingSubscriber = (sender, args) =>
                {
                    notifications++;
                    disconnectCallsAtNotification = fixture.Card.DisconnectCalls;
                    throw new InvalidOperationException("模拟准备订阅者失败");
                };
                fixture.Runtime.PreparationChanged += throwingSubscriber;
                var preparationStates = new List<MachineRunState>();
                EventHandler observingPreparation = (sender, args) => preparationStates.Add(fixture.Runtime.State);
                fixture.Runtime.PreparationChanged += observingPreparation;
                int failedStatusNotifications = 0;
                var observedStates = new List<MachineRunState>();
                var observedErrors = new List<Exception>();
                EventHandler throwingStatus = (sender, args) =>
                {
                    failedStatusNotifications++;
                    throw new InvalidOperationException("模拟状态订阅者失败");
                };
                EventHandler observingStatus = (sender, args) =>
                {
                    observedStates.Add(fixture.Runtime.State);
                    observedErrors.Add(fixture.Runtime.LastError);
                };
                fixture.Runtime.StatusChanged += throwingStatus;
                fixture.Runtime.StatusChanged += observingStatus;
                fixture.Card.FailDisconnect = true;
                bool invalidatedBeforeDriver = false;
                fixture.Card.OnDisconnect = () => invalidatedBeforeDriver =
                    !fixture.Runtime.ConnectionSessionId.HasValue && !fixture.Runtime.IsPrepared;
                Exception disconnectError = null;
                try { await fixture.Runtime.DisconnectAsync(CancellationToken.None); }
                catch (Exception error) { disconnectError = error; }
                finally
                {
                    fixture.Runtime.PreparationChanged -= throwingSubscriber;
                    fixture.Runtime.PreparationChanged -= observingPreparation;
                    fixture.Runtime.StatusChanged -= throwingStatus;
                    fixture.Runtime.StatusChanged -= observingStatus;
                }
                Ensure(disconnectError != null && disconnectError.ToString().Contains("模拟断开失败")
                    && ReferenceEquals(disconnectError, fixture.Runtime.LastError) && notifications == 1
                    && disconnectCallsAtNotification == 1 && invalidatedBeforeDriver,
                    "准备资格没有在驱动断卡前失效，或订阅者阻断了断卡/覆盖了驱动错误。");
                Ensure(fixture.Card.IsConnected && fixture.Runtime.ConnectionSessionId == null
                    && fixture.Runtime.State == MachineRunState.Alarmed && !fixture.Runtime.IsPrepared,
                    "断开失败后未使连接及准备资格失效。");
                Ensure(failedStatusNotifications == 1 && observedStates.SequenceEqual(new[] { MachineRunState.Alarmed })
                    && ReferenceEquals(observedErrors[0], disconnectError), "失败状态被订阅异常覆盖，或后续状态订阅者没有收到原始故障。");
                int readsAfterFailure = Volatile.Read(ref buttons.Reads);
                await WaitAsync(() => Volatile.Read(ref buttons.Reads) > readsAfterFailure);
                int previousAxisResets = fixture.Axis.ResetCount, previousFlowResets = fixture.Flow.ResetCount;
                await RejectAsync(() => fixture.Runtime.ResetAsync(CancellationToken.None), "失效会话仍允许复位。");
                Ensure(fixture.Runtime.State == MachineRunState.Alarmed && ReferenceEquals(fixture.Runtime.LastError, disconnectError)
                    && fixture.Axis.ResetCount == previousAxisResets && fixture.Flow.ResetCount == previousFlowResets,
                    "失效会话复位清掉了原断卡故障或执行了轴/工位复位。");
                int manualCalls = 0;
                // 代理只计数，不执行任何设备动作。
                Task CountManual() { manualCalls++; return Task.CompletedTask; }
                Ensure(await fixture.Runtime.RunManualOperationAsync(CountManual, CancellationToken.None) == ManualOperationBlockReason.MotionCardDisconnected
                    && await fixture.Runtime.RunManualRecoveryOperationAsync(CountManual, CancellationToken.None) == ManualOperationBlockReason.MotionCardDisconnected
                    && await fixture.Runtime.RunManualAxisAlarmResetAsync(CountManual, CancellationToken.None) == ManualOperationBlockReason.MotionCardDisconnected
                    && manualCalls == 0, "连接失效后手动动作、示教或轴报警复位被放行。");
                Ensure(await fixture.Runtime.RunManualStopAsync(CountManual, CancellationToken.None) == ManualOperationBlockReason.None
                    && manualCalls == 1, "失效会话内仍连接的控制器无法受控停止。");
                var taskOwner = new PauseOwner();
                await fixture.Runtime.AcquireTaskEngineControlAsync(taskOwner, CancellationToken.None);
                await RejectAsync(() => fixture.Runtime.BeginTaskEngineRunAsync(CancellationToken.None), "失效会话仍能启动任务。");
                fixture.Runtime.ReleaseTaskEngineControl(taskOwner);
                int previousValidations = validations, previousMarkers = markers, previousActions = fixture.Log.Count;
                await RejectAsync(() => fixture.Runtime.PrepareAsync(null, CancellationToken.None), "失效连接仍可准备。");
                Ensure(validations == previousValidations && markers == previousMarkers && fixture.Log.Count == previousActions,
                    "会话失效后仍执行准备预检、持久标记或动作。");
                fixture.Card.FailDisconnect = false;
                fixture.Runtime.PreparationChanged += throwingSubscriber;
                fixture.Runtime.PreparationChanged += observingPreparation;
                fixture.Runtime.StatusChanged += throwingStatus;
                fixture.Runtime.StatusChanged += observingStatus;
                try { await fixture.Runtime.DisconnectAsync(CancellationToken.None); }
                finally
                {
                    fixture.Runtime.PreparationChanged -= throwingSubscriber;
                    fixture.Runtime.PreparationChanged -= observingPreparation;
                    fixture.Runtime.StatusChanged -= throwingStatus;
                    fixture.Runtime.StatusChanged -= observingStatus;
                }
                Ensure(failedStatusNotifications == 2 && observedStates.SequenceEqual(new[] { MachineRunState.Alarmed, MachineRunState.Disconnected })
                    && observedErrors[1] == null && fixture.Runtime.LastError == null,
                    "成功断卡被状态订阅异常误判为失败，或后续订阅者未获知成功。");
                Ensure(notifications == 2 && disconnectCallsAtNotification == 2 && !fixture.Card.IsConnected
                    && fixture.Runtime.State == MachineRunState.Disconnected, "准备通知异常改变了断开重试的成功结果。");
                Ensure(preparationStates.SequenceEqual(new[] { MachineRunState.Alarmed, MachineRunState.Disconnected }),
                    "抛错的准备订阅者跳过了后续正常订阅者。");
                await fixture.ConnectAsync();
                Ensure(fixture.Runtime.ConnectionSessionId.HasValue && fixture.Runtime.ConnectionSessionId != original,
                    "重连未产生新会话。");
                await RejectAsync(() => fixture.Runtime.PrepareAsync(null, original, CancellationToken.None),
                    "重连后接受了原会话的准备请求。");
                await fixture.Runtime.PrepareAsync(null, CancellationToken.None);
                Ensure(fixture.Log.Count == previousActions + 1 && fixture.Runtime.IsPrepared, "重连后未重新执行工位准备。");
                await fixture.Runtime.DisconnectAsync(CancellationToken.None);
            }
        }

        /// <summary>在设备回调及持久标记中丢失通信，禁止后续动作、复位成功或准备资格。</summary>
        private static async Task CheckConnectionLossDuringCallbacksAsync()
        {
            var token = CancellationToken.None;
            foreach (string phase in new[] { "预检", "标记", "工位预检", "完成核查" })
            {
                var fixture = new Fixture();
                var item = new Preparation("Home", fixture);
                int validations = 0, markers = 0, completions = 0;
                item.OnValidate = () =>
                {
                    validations++;
                    if ((phase == "预检" && validations == 1) || (phase == "工位预检" && validations == 2))
                        fixture.Card.ForceOffline();
                };
                if (phase == "完成核查") item.OnReady = fixture.Card.ForceOffline;
                fixture.Runtime.SetPreparationRecordCallbacks(() =>
                {
                    markers++;
                    if (phase == "标记") fixture.Card.ForceOffline();
                }, () => completions++);
                fixture.Runtime.ConfigurePreparations(new[] { item });
                await fixture.ConnectAsync();
                await RejectAsync(() => fixture.Runtime.PrepareAsync(null, token), "回调后掉线仍报告准备完成：" + phase);
                Ensure(markers == (phase == "预检" ? 0 : 1) && completions == 0 && !fixture.Runtime.IsPrepared
                    && fixture.Log.Count == (phase == "完成核查" ? 1 : 0), "掉线后续动作或标记错误：" + phase);
                await fixture.Runtime.DisconnectAsync(token);
            }
            foreach (bool dropDuringAxis in new[] { true, false })
            {
                var fixture = new Fixture();
                if (dropDuringAxis) fixture.Axis.OnReset = fixture.Card.ForceOffline;
                else fixture.Flow.OnReset = fixture.Card.ForceOffline;
                await fixture.ConnectAsync();
                await RejectAsync(() => fixture.Runtime.ResetAsync(token), "复位回调掉线后仍报告 Ready。");
                Ensure(fixture.Axis.ResetCount == 1 && fixture.Flow.ResetCount == (dropDuringAxis ? 0 : 1)
                    && fixture.Runtime.State == MachineRunState.Alarmed, "掉线复位后执行了后续回调或发布就绪。");
                await fixture.Runtime.DisconnectAsync(token);
            }
            foreach (bool task in new[] { false, true })
            {
                var fixture = new Fixture();
                int markers = 0;
                fixture.Runtime.SetBeforeAutomaticRun(() => { markers++; fixture.Card.ForceOffline(); });
                await fixture.ConnectAsync();
                await fixture.Runtime.ResetAsync(token);
                var owner = new PauseOwner();
                if (task) await fixture.Runtime.AcquireTaskEngineControlAsync(owner, token);
                await RejectAsync(() => task ? fixture.Runtime.BeginTaskEngineRunAsync(token) : fixture.Runtime.StartAsync(token),
                    "生产标记回调期间掉线后仍放行。");
                Ensure(markers == 1 && fixture.Flow.Executions == 0 && fixture.Runtime.State != MachineRunState.Running,
                    "生产标记后的掉线未阻止运行许可。");
                if (task) fixture.Runtime.ReleaseTaskEngineControl(owner);
                await fixture.Runtime.DisconnectAsync(token);
            }
            foreach (bool probeFails in new[] { false, true })
            {
                var lostDuringRunning = new Fixture();
                await lostDuringRunning.ConnectAsync();
                await lostDuringRunning.Runtime.ResetAsync(token);
                var taskOwner = new PauseOwner();
                var probeError = new InvalidOperationException("测试厂商连接状态无法读取");
                await lostDuringRunning.Runtime.AcquireTaskEngineControlAsync(taskOwner, token);
                lostDuringRunning.Runtime.StatusChanged += (sender, args) =>
                {
                    if (lostDuringRunning.Runtime.State != MachineRunState.Running) return;
                    if (probeFails) lostDuringRunning.Card.ConnectionProbeError = probeError;
                    else lostDuringRunning.Card.ForceOffline();
                };
                InvalidOperationException rejected = null;
                try { await lostDuringRunning.Runtime.BeginTaskEngineRunAsync(token); }
                catch (InvalidOperationException error) { rejected = error; }
                Ensure(rejected != null && (probeFails ? ReferenceEquals(rejected, probeError) : rejected.Message.Contains("连接会话"))
                    && lostDuringRunning.Runtime.State == MachineRunState.Alarmed
                    && ReferenceEquals(lostDuringRunning.Runtime.LastError, rejected),
                    "Running 通知期间连接失效/未知却回到 Ready，或原启动错误被覆盖。");
                lostDuringRunning.Card.ConnectionProbeError = null;
                lostDuringRunning.Runtime.ReleaseTaskEngineControl(taskOwner);
                await lostDuringRunning.Runtime.DisconnectAsync(token);
            }
        }

        /// <summary>急停条件已恢复但报警仍锁存时，轴复位特批也必须核对慢读结束后的卡连接。</summary>
        private static async Task CheckAxisAlarmResetDisconnectAsync()
        {
            var fixture = new Fixture();
            var token = CancellationToken.None;
            await fixture.ConnectAsync();
            fixture.Safety.Active = true;
            Ensure(await fixture.Runtime.GetManualOperationBlockReasonAsync(token) == ManualOperationBlockReason.EmergencyStopInputActive
                && fixture.Runtime.AlarmService.HasLatchedStopAlarm, "未通过实际手动检查建立急停报警。");
            fixture.Safety.Active = false;
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Safety.NextRead = release;
            int calls = 0;
            Task<ManualOperationBlockReason> reset = fixture.Runtime.RunManualAxisAlarmResetAsync(
                () => { calls++; return Task.CompletedTask; }, token);
            try
            {
                Ensure(!reset.IsCompleted && fixture.Safety.NextRead == null, "复位未进入异步急停读取。");
                fixture.Card.ForceOffline();
            }
            finally { release.TrySetResult(false); }
            Ensure(await reset == ManualOperationBlockReason.MotionCardDisconnected && calls == 0
                && fixture.Runtime.AlarmService.HasLatchedStopAlarm, "掉线前的急停释放反馈让轴报警复位越过了连接门禁。");
            await fixture.Runtime.DisconnectAsync(token);
        }

        /// <summary>驱动断卡异常后若连线状态查询也失败，两份故障均保留且不盲目重启按钮监控。</summary>
        private static async Task CheckDisconnectProbeFailureAsync()
        {
            var buttons = new MemoryButtons();
            var fixture = new Fixture(buttons: buttons);
            await fixture.ConnectAsync();
            fixture.Card.FailDisconnect = true;
            var probeError = new InvalidOperationException("模拟卡连接反馈失败");
            fixture.Card.OnDisconnect = () => fixture.Card.ConnectionProbeError = probeError;
            AggregateException combined = null;
            try { await fixture.Runtime.DisconnectAsync(CancellationToken.None); }
            catch (AggregateException error) { combined = error; }
            Ensure(combined != null && combined.InnerExceptions.Count == 2
                && ReferenceEquals(combined.InnerExceptions[0], fixture.Runtime.LastError)
                && combined.InnerExceptions[0].ToString().Contains("模拟断开失败")
                && ReferenceEquals(combined.InnerExceptions[1], probeError), "恢复探测异常掩盖了驱动原始断卡故障。");
            var monitor = typeof(MachineRuntime).GetField("_controlInputMonitorCancellation",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Ensure(monitor.GetValue(fixture.Runtime) == null, "连接反馈未知时启动了按钮轮询。");
            fixture.Card.OnDisconnect = null;
            fixture.Card.ConnectionProbeError = null;
            fixture.Card.FailDisconnect = false;
            await fixture.Runtime.DisconnectAsync(CancellationToken.None);
        }

        /// <summary>同一 Runtime 的命令依序排在断开、重连、复位之后，旧提交不可进入新连接。</summary>
        private static async Task CheckQueuedSessionCommandsAsync()
        {
            var fixture = new Fixture();
            var token = CancellationToken.None;
            await fixture.ConnectAsync();
            await fixture.Runtime.ResetAsync(token);
            int axisResets = fixture.Axis.ResetCount, flowResets = fixture.Flow.ResetCount;
            await RejectAsync(() => fixture.Runtime.ResetAsync(new CancellationToken(true)), "已取消的复位仍执行。");
            Ensure(axisResets == fixture.Axis.ResetCount && flowResets == fixture.Flow.ResetCount, "已取消复位调用了设备。");
            Guid? originalSession = fixture.Runtime.ConnectionSessionId;
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task held = fixture.Runtime.RunConnectedConfigurationChangeAsync(() => release.Task, token);
            Task disconnect = fixture.Runtime.DisconnectAsync(token);
            Task connect = fixture.Runtime.ConnectAsync(token);
            // 使用计数器代表设备动作；原请求均在连接 A 有效时提交。
            int manualCalls = 0, configurationCalls = 0;
            Task CountManual() { manualCalls++; return Task.CompletedTask; }
            var manual = fixture.Runtime.RunManualOperationAsync(CountManual, token);
            var recovery = fixture.Runtime.RunManualRecoveryOperationAsync(CountManual, token);
            var axisReset = fixture.Runtime.RunManualAxisAlarmResetAsync(CountManual, token);
            Task oldReset = fixture.Runtime.ResetAsync(token);
            Task oldConfiguration = fixture.Runtime.RunConnectedConfigurationChangeAsync(
                () => { configurationCalls++; return Task.CompletedTask; }, token);
            Task oldStationStart = fixture.Runtime.StartAsync(token);
            var taskOwner = new PauseOwner();
            Task claim = fixture.Runtime.AcquireTaskEngineControlAsync(taskOwner, token);
            Task oldTaskStart = fixture.Runtime.BeginTaskEngineRunAsync(token);
            release.SetResult(true);
            await held;
            await disconnect;
            await connect;
            Ensure(originalSession != fixture.Runtime.ConnectionSessionId && fixture.Runtime.ConnectionSessionId.HasValue,
                "测试没有先完成真实断开和重连。");
            Ensure(await manual == ManualOperationBlockReason.MotionCardDisconnected
                && await recovery == ManualOperationBlockReason.MotionCardDisconnected
                && await axisReset == ManualOperationBlockReason.MotionCardDisconnected && manualCalls == 0,
                "旧连接排队的手动命令进入了新的连接会话。");
            await RejectAsync(() => oldReset, "排队的旧复位进入新连接。");
            await RejectAsync(() => oldConfiguration, "排队的旧配置进入新连接。");
            await claim;
            foreach (var oldStart in new[] { oldStationStart, oldTaskStart })
            {
                string reason = null;
                try { await oldStart; } catch (InvalidOperationException error) { reason = error.Message; }
                Ensure(reason != null && reason.Contains("连接会话"), "旧生产启动未按会话失效拒绝。");
            }
            fixture.Runtime.ReleaseTaskEngineControl(taskOwner);
            Ensure(axisResets == fixture.Axis.ResetCount && flowResets == fixture.Flow.ResetCount && configurationCalls == 0,
                "拒绝旧命令前已经调用设备。");
            await fixture.Runtime.ResetAsync(token);
            await fixture.Runtime.DisconnectAsync(token);
        }

        /// <summary>C# 流程许可前报警禁止启动；许可后报警须取消并完成工位收尾。</summary>
        private static async Task StationFlowStartAlarmGateAsync()
        {
            var before = new Fixture();
            await before.ConnectAsync();
            await before.Runtime.ResetAsync(CancellationToken.None);
            int reported = 0;
            before.Runtime.StatusChanged += (sender, args) =>
            {
                if (before.Runtime.State == MachineRunState.Running && Interlocked.Exchange(ref reported, 1) == 0)
                    before.Runtime.AlarmService.ReportCondition(new MachineAlarmDefinition("FLOW_BEFORE_GATE", "启动时停机", MachineAlarmKind.StopMachine), "测试", true, "许可前");
            };
            await RejectAsync(() => before.Runtime.StartAsync(CancellationToken.None), "启动许可前已报警却成功启动");
            Ensure(reported == 1 && before.Flow.Executions == 0, "报警已锁存时 C# 工位仍执行了步骤。");

            var alarm = new GatedAlarmService();
            var flow = new Flow();
            var runtime = new MachineRuntime(new MotionCardGroup(new[] { new Card(new Axis()) }), new[] { flow }, alarm, new Safety());
            await runtime.ConnectAsync(CancellationToken.None);
            await runtime.ResetAsync(CancellationToken.None);
            alarm.ReportImmediatelyAfterStartGate = true;
            await runtime.StartAsync(CancellationToken.None);
            await WaitAsync(() => runtime.State == MachineRunState.Alarmed);
            Ensure(runtime.State == MachineRunState.Alarmed, "许可提交后报警未使工位流程停止并进入报警。");
        }

        /// <summary>报警在准备许可提交前后发生时均不得覆盖报警或执行准备动作。</summary>
        private static async Task PreparationAlarmGateAsync()
        {
            var before = new Fixture();
            before.Runtime.ConfigurePreparations(new[] { new Preparation("A", before) });
            var markerEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseMarker = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int markerCount = 0;
            int safeRecordCount = 0;
            before.Runtime.SetPreparationRecordCallbacks(() => { markerCount++; markerEntered.TrySetResult(true); releaseMarker.Task.GetAwaiter().GetResult(); }, () => safeRecordCount++);
            await before.ConnectAsync();
            Task attempt = Task.Run(() => before.Runtime.PrepareAsync(null, CancellationToken.None));
            try
            {
                await markerEntered.Task;
                var alarm = new MachineAlarmDefinition("PREP_BEFORE_GATE", "准备前停机", MachineAlarmKind.StopMachine);
                before.Runtime.AlarmService.ReportCondition(alarm, "测试", true, "准备提交前");
                releaseMarker.TrySetResult(true);
                await RejectAsync(() => attempt, "报警后设备准备未被拒绝");
                Ensure(before.Runtime.State == MachineRunState.Alarmed && before.Log.Count == 0 && !before.Runtime.IsPreparing && markerCount == 1 && safeRecordCount == 0 && !before.Runtime.IsPreparationRecordReusable,
                    "准备前报警被覆盖，或准备动作已执行。");
            }
            finally { releaseMarker.TrySetResult(true); }

            var after = new Fixture();
            after.Runtime.ConfigurePreparations(new[] { new Preparation("A", after) });
            await after.ConnectAsync();
            int reported = 0;
            after.Runtime.StatusChanged += (sender, args) =>
            {
                if (after.Runtime.State == MachineRunState.Preparing && Interlocked.Exchange(ref reported, 1) == 0)
                    after.Runtime.AlarmService.ReportCondition(new MachineAlarmDefinition("PREP_AFTER_GATE", "准备启动停机", MachineAlarmKind.StopMachine), "测试", true, "准备许可已发布");
            };
            try { await after.Runtime.PrepareAsync(null, CancellationToken.None); }
            catch (OperationCanceledException) { }
            Ensure(reported == 1 && after.Log.Count == 0 && after.Runtime.State == MachineRunState.Alarmed,
                "准备许可刚发布便收到报警时仍执行了准备动作或覆盖报警状态。");

            var completing = new Fixture();
            completing.Runtime.ConfigurePreparations(new[] { new Preparation("A", completing) });
            completing.Runtime.SetPreparationRecordCallbacks(() => { }, () =>
                completing.Runtime.AlarmService.ReportCondition(
                    new MachineAlarmDefinition("PREP_FINISH_STOP", "准备结束停机", MachineAlarmKind.StopMachine), "测试", true, "结束记录提交期间"));
            await completing.ConnectAsync();
            await RejectAsync(() => completing.Runtime.PrepareAsync(null, CancellationToken.None), "准备结束期间报警却报告成功");
            Ensure(completing.Log.Count == 1 && completing.Runtime.State == MachineRunState.Alarmed,
                "准备结束期间的停机报警被覆盖成就绪。");
            Ensure(!completing.Runtime.IsPreparationRecordReusable && !completing.Runtime.IsPrepared,
                "准备结束记录保存期间发生报警，不能授予复用标记或准备完成资格。");
        }

        /// <summary>暂停提交与已报警状态使用同一锁；并发停机报警不能被覆盖成普通暂停。</summary>
        private static async Task StopAlarmCannotBeOverwrittenByTaskPauseAsync()
        {
            var alarm = new GatedAlarmService();
            var axis = new Axis();
            var runtime = new MachineRuntime(new MotionCardGroup(new[] { new Card(axis) }), new[] { new Flow() },
                alarm, new Safety(), controlOwner: MachineControlOwner.TaskEngine);
            object owner = new PauseOwner();
            await runtime.ConnectAsync(CancellationToken.None);
            await runtime.AcquireTaskEngineControlAsync(owner, CancellationToken.None);
            await runtime.ResetAsync(CancellationToken.None);
            await runtime.BeginTaskEngineRunAsync(CancellationToken.None);
            alarm.Arm();
            Task finish = Task.Run(() => runtime.FinishTaskEngineRunAsync(false, null));
            Task report = null;
            try
            {
                await WaitAsync(() => alarm.CheckEntered.IsSet);
                var definition = new MachineAlarmDefinition("CONCURRENT_STOP", "并发停机", MachineAlarmKind.StopMachine);
                report = Task.Run(() => alarm.ReportCondition(definition, "检查", true, "并发报警"));
                await WaitAsync(() => alarm.StopObserved.IsSet);
                await report;
                Ensure(runtime.State == MachineRunState.Alarmed, "暂停检查期间停机报警没有生效。");
            }
            finally { alarm.ReleaseCheck.Set(); }
            await finish;
            if (report != null) await report;
            Ensure(runtime.State == MachineRunState.Alarmed, "并发停机报警被普通暂停覆盖。");
        }

        /// <summary>提供可门控的报警读取，用于安排暂停检查与停机事件交错。</summary>
        private sealed class GatedAlarmService : IMachineAlarmService, IMachineAlarmStartGate, IMachineStopAlarmNotificationDrain
        {
            /// <summary>实际的内存报警实现。</summary>
            private readonly MachineAlarmService _inner = new MachineAlarmService();
            /// <summary>首次受控读取的启用标记。</summary>
            private int _armed;
            /// <summary>暂停流程已进入报警读取。</summary>
            internal readonly ManualResetEventSlim CheckEntered = new ManualResetEventSlim(false);
            /// <summary>允许暂停流程结束本次读取。</summary>
            internal readonly ManualResetEventSlim ReleaseCheck = new ManualResetEventSlim(false);
            /// <summary>停机事件处理已读取有效报警。</summary>
            internal readonly ManualResetEventSlim StopObserved = new ManualResetEventSlim(false);
            /// <summary>启动门禁提交后立即上报报警，用于检查调度令牌已登记。</summary>
            internal bool ReportImmediatelyAfterStartGate;
            /// <summary>只阻塞下一次报警读取。</summary>
            internal void Arm() { Interlocked.Exchange(ref _armed, 1); }
            /// <summary>当前报警快照。</summary>
            public IReadOnlyCollection<MachineAlarmRecord> Alarms => _inner.Alarms;
            /// <summary>转发报警变化事件。</summary>
            public event EventHandler AlarmsChanged { add { _inner.AlarmsChanged += value; } remove { _inner.AlarmsChanged -= value; } }
            /// <summary>转发停机报警事件。</summary>
            public event EventHandler<MachineAlarmRaisedEventArgs> StopAlarmRaised { add { _inner.StopAlarmRaised += value; } remove { _inner.StopAlarmRaised -= value; } }
            /// <summary>按测试门控截取报警读取，其他调用如实返回当前报警。</summary>
            public bool HasLatchedStopAlarm
            {
                get
                {
                    if (Interlocked.CompareExchange(ref _armed, 0, 1) == 1)
                    {
                        bool value = _inner.HasLatchedStopAlarm;
                        CheckEntered.Set(); ReleaseCheck.Wait(); return value;
                    }
                    bool active = _inner.HasLatchedStopAlarm;
                    if (active) StopObserved.Set();
                    return active;
                }
            }
            /// <summary>报告模拟报警条件。</summary>
            public void ReportCondition(MachineAlarmDefinition definition, string source, bool active, string detail)
                => _inner.ReportCondition(definition, source, active, detail);
            /// <summary>复位模拟报警。</summary>
            public void ResetStopAlarms() => _inner.ResetStopAlarms();
            /// <summary>检查报警条件。</summary>
            public void EnsureStopAlarmConditionsRecovered() => _inner.EnsureStopAlarmConditionsRecovered();
            /// <summary>等待实际报警服务中的通知交接。</summary>
            public Task WaitForStopNotificationDispatchAsync() => _inner.WaitForStopNotificationDispatchAsync();
            /// <summary>转发原子门禁，并按需在退出门禁后同步注入停机报警。</summary>
            public void ExecuteWhenNoStopAlarm(Action commit)
            {
                _inner.ExecuteWhenNoStopAlarm(commit);
                if (ReportImmediatelyAfterStartGate)
                {
                    ReportImmediatelyAfterStartGate = false;
                    _inner.ReportCondition(new MachineAlarmDefinition("FLOW_AFTER_GATE", "启动后停机", MachineAlarmKind.StopMachine), "测试", true, "许可后");
                }
            }
        }

        /// <summary>准备记录先于动作；成功或受控停稳后结束，执行/停轴/保存失败时保持。</summary>
        private static async Task PreparationMarkerLifecycleAsync()
        {
            var success = new Fixture();
            success.Runtime.ConfigurePreparations(new[] { new Preparation("A", success) });
            int begins = 0, ends = 0;
            success.Runtime.SetPreparationRecordCallbacks(() => { Ensure(success.Log.Count == 0, "动作先于准备标记"); begins++; }, () => ends++);
            await success.ConnectAsync();
            await success.Runtime.PrepareAsync(null, CancellationToken.None);
            Ensure(begins == 1 && ends == 1 && success.Runtime.IsPrepared && success.Runtime.IsPreparationRecordReusable,
                "准备成功未提交安全结束记录或未授予本会话标记沿用资格");
            await success.Runtime.DisconnectAsync(CancellationToken.None);
            Ensure(!success.Runtime.IsPreparationRecordReusable, "控制器断开后保留了旧准备标记沿用资格。");
            await success.ConnectAsync();
            Ensure(!success.Runtime.IsPreparationRecordReusable, "控制器重新连接后沿用了旧准备标记资格。");

            var cancelled = new Fixture();
            cancelled.Runtime.ConfigurePreparations(new[] { new Preparation("Slow", cancelled) { WaitUntilStopped = true } });
            bool marked = false, cleared = false;
            cancelled.Runtime.SetPreparationRecordCallbacks(() => marked = true, () => { Ensure(!cancelled.Axis.Moving, "轴未停稳就清标记"); cleared = true; });
            await cancelled.ConnectAsync();
            Task run = cancelled.Runtime.PrepareAsync(null, CancellationToken.None);
            await WaitAsync(() => cancelled.Axis.Moving);
            Ensure(marked && !cleared, "运行中的准备未保留标记");
            await cancelled.Runtime.StopPreparationAsync(CancellationToken.None);
            try { await run; } catch (OperationCanceledException) { }
            Ensure(cleared && !cancelled.Runtime.IsPreparing && cancelled.Runtime.IsPreparationRecordReusable,
                "受控停止后未保存安全结束记录或未允许重新准备");

            var concurrent = new Fixture();
            concurrent.Runtime.ConfigurePreparations(new[] { new Preparation("Slow", concurrent) { WaitUntilStopped = true } });
            concurrent.Axis.StopGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int clearCount = 0;
            concurrent.Runtime.SetPreparationRecordCallbacks(() => { }, () => Interlocked.Increment(ref clearCount));
            await concurrent.ConnectAsync();
            Task concurrentRun = concurrent.Runtime.PrepareAsync(null, CancellationToken.None);
            await WaitAsync(() => concurrent.Axis.Moving);
            Task stopOne = concurrent.Runtime.StopPreparationAsync(CancellationToken.None);
            Task stopTwo = concurrent.Runtime.StopPreparationAsync(CancellationToken.None);
            await WaitAsync(() => concurrent.Axis.StopCount > 0);
            Ensure(clearCount == 0 && concurrent.Runtime.IsPreparing, "并发停止仍在收尾时提前清除标记。");
            concurrent.Axis.StopGate.TrySetResult(true);
            await Task.WhenAll(stopOne, stopTwo);
            try { await concurrentRun; } catch (OperationCanceledException) { }
            Ensure(clearCount == 1 && !concurrent.Runtime.IsPreparing, "并发停止收尾后标记没有恰好清除一次。");

            var failed = new Fixture();
            failed.Runtime.ConfigurePreparations(new[] { new Preparation("A", failed) { ReadyPose = false } });
            bool failedMarked = false, incorrectlyCleared = false;
            failed.Runtime.SetPreparationRecordCallbacks(() => failedMarked = true, () => incorrectlyCleared = true);
            await failed.ConnectAsync();
            await RejectAsync(() => failed.Runtime.PrepareAsync(null, CancellationToken.None), "准备失败未报错");
            Ensure(failedMarked && !incorrectlyCleared && failed.Runtime.State == MachineRunState.Alarmed, "准备失败错误清除了记录");

            var markError = new Fixture();
            markError.Runtime.ConfigurePreparations(new[] { new Preparation("A", markError) });
            markError.Runtime.SetPreparationRecordCallbacks(() => { throw new InvalidOperationException("磁盘错误"); }, () => { });
            await markError.ConnectAsync();
            await RejectAsync(() => markError.Runtime.PrepareAsync(null, CancellationToken.None), "标记写失败允许准备");
            Ensure(markError.Log.Count == 0 && !markError.Runtime.IsPreparing, "标记写失败后设备已执行准备");

            var clearError = new Fixture();
            clearError.Runtime.ConfigurePreparations(new[] { new Preparation("A", clearError) });
            clearError.Runtime.SetPreparationRecordCallbacks(() => { }, () => { throw new InvalidOperationException("记录保存失败"); });
            await clearError.ConnectAsync();
            await RejectAsync(() => clearError.Runtime.PrepareAsync(null, CancellationToken.None), "结束记录写失败未报错");
            Ensure(!clearError.Runtime.IsPrepared && clearError.Runtime.State == MachineRunState.Alarmed, "结束记录失败仍显示已准备");
        }

        /// <summary>运行标记写入失败必须发生在两个执行入口下达生产动作之前。</summary>
        private static async Task RunMarkerFailureBlocksBothOwnersAsync()
        {
            foreach (var owner in new[] { MachineControlOwner.StationFlows, MachineControlOwner.TaskEngine })
            {
                var fixture = new Fixture(owner);
                int attempts = 0;
                fixture.Runtime.SetBeforeAutomaticRun(() => { attempts++; throw new InvalidOperationException("运行记录写入失败"); });
                await fixture.ConnectAsync();
                await fixture.Runtime.ResetAsync(CancellationToken.None);
                object taskOwner = owner == MachineControlOwner.TaskEngine ? new PauseOwner() : null;
                if (taskOwner != null) await fixture.Runtime.AcquireTaskEngineControlAsync(taskOwner, CancellationToken.None);
                if (owner == MachineControlOwner.StationFlows)
                    await RejectAsync(() => fixture.Runtime.StartAsync(CancellationToken.None), "标记写入失败仍允许 C# 生产");
                else
                    await RejectAsync(() => fixture.Runtime.BeginTaskEngineRunAsync(CancellationToken.None), "标记写入失败仍允许任务生产");
                Ensure(attempts == 1 && fixture.Flow.Executions == 0 && fixture.Runtime.State != MachineRunState.Running,
                    "标记写入失败后状态或工位被启动。");
                if (taskOwner != null) fixture.Runtime.ReleaseTaskEngineControl(taskOwner);
                await fixture.Runtime.DisconnectAsync(CancellationToken.None);
            }
        }

        /// <summary>不可信恢复数据应阻止两个执行入口和准备，但不阻止手动排查。</summary>
        private static async Task UntrustedRecoveryBlocksAutomationAsync()
        {
            foreach (var owner in new[] { MachineControlOwner.StationFlows, MachineControlOwner.TaskEngine })
            {
                var fixture = new Fixture(owner);
                fixture.Runtime.ConfigurePreparations(new[] { new Preparation("A", fixture) });
                fixture.Runtime.SetRecoveryBlockReason("测试恢复记录损坏");
                await fixture.ConnectAsync();
                await fixture.Runtime.ResetAsync(CancellationToken.None);
                object taskOwner = owner == MachineControlOwner.TaskEngine ? new PauseOwner() : null;
                if (taskOwner != null) await fixture.Runtime.AcquireTaskEngineControlAsync(taskOwner, CancellationToken.None);
                Ensure((await fixture.Runtime.GetAutomaticStartBlockReasonAsync(CancellationToken.None)).Contains("恢复记录损坏"), "启动提示遗漏恢复原因");
                await RejectAsync(() => fixture.Runtime.PrepareAsync(null, CancellationToken.None), "恢复损坏时允许回零");
                if (owner == MachineControlOwner.StationFlows)
                    await RejectAsync(() => fixture.Runtime.StartAsync(CancellationToken.None), "恢复损坏时允许C#启动");
                else
                    await RejectAsync(() => fixture.Runtime.BeginTaskEngineRunAsync(CancellationToken.None), "恢复损坏时允许任务启动");
                if (taskOwner != null) fixture.Runtime.ReleaseTaskEngineControl(taskOwner);
                bool manual = false;
                var result = await fixture.Runtime.RunManualOperationAsync(() => { manual = true; return Task.CompletedTask; }, CancellationToken.None);
                Ensure(result == ManualOperationBlockReason.None && manual, "恢复门禁错误阻断手动排查");
                await fixture.Runtime.DisconnectAsync(CancellationToken.None);
            }
        }

        /// <summary>按依赖准备后不自动生产，正常暂停不回初始位，手动操作撤销准备资格。</summary>
        private static async Task StartupAndInvalidationAsync()
        {
            var fixture = new Fixture();
            var first = new Preparation("First", fixture);
            var second = new Preparation("Second", fixture, "First");
            fixture.Runtime.ConfigurePreparations(new[] { second, first });
            await fixture.ConnectAsync();
            await fixture.Runtime.ResetAsync(CancellationToken.None);
            await RejectAsync(() => fixture.Runtime.StartAsync(CancellationToken.None), "未准备启动没有被拦截");
            await fixture.Runtime.PrepareAsync(null, CancellationToken.None);
            Ensure(fixture.Log.SequenceEqual(new[] { "First", "Second" }), "没有按工位依赖顺序准备");
            Ensure(fixture.Runtime.State == MachineRunState.Ready && fixture.Flow.Executions == 0, "准备结束自动进入了生产");
            await fixture.Runtime.StartAsync(CancellationToken.None);
            await WaitAsync(() => fixture.Flow.Executions > 0);
            await fixture.Runtime.PauseAsync(CancellationToken.None);
            await fixture.Runtime.ResetAsync(CancellationToken.None);
            await RejectAsync(() => fixture.Runtime.PrepareAsync(null, CancellationToken.None), "生产开始后允许误执行回零");
            Ensure(fixture.Runtime.IsPrepared && fixture.Runtime.State == MachineRunState.Ready,
                "生产中误点回零破坏了原准备资格或续跑状态");
            first.ReadyPose = false;
            await fixture.Runtime.StartAsync(CancellationToken.None);
            await fixture.Runtime.PauseAsync(CancellationToken.None);
            await fixture.Runtime.ResetAsync(CancellationToken.None);
            await fixture.Runtime.RunManualOperationAsync(() => Task.CompletedTask, CancellationToken.None);
            Ensure(!fixture.Runtime.IsPrepared, "手动操作后仍保留准备资格");
            await RejectAsync(() => fixture.Runtime.StartAsync(CancellationToken.None), "手动改变后仍允许自动启动");
            await RejectAsync(() => fixture.Runtime.PrepareAsync(null, CancellationToken.None), "生产后手动操作清掉生产历史并允许重回零");
            await fixture.Runtime.DisconnectAsync(CancellationToken.None);
        }

        /// <summary>准备期间阻止手动、另一会话和复位，停止后等待轴停稳且不标完成。</summary>
        private static async Task StopAndExclusionAsync()
        {
            var fixture = new Fixture();
            var preparation = new Preparation("Slow", fixture) { WaitUntilStopped = true };
            fixture.Runtime.ConfigurePreparations(new[] { preparation });
            await fixture.ConnectAsync();
            Task run = fixture.Runtime.PrepareAsync(null, CancellationToken.None);
            await WaitAsync(() => fixture.Axis.Moving);
            bool manualCalled = false;
            ManualOperationBlockReason reason = await fixture.Runtime.RunManualOperationAsync(() => { manualCalled = true; return Task.CompletedTask; }, CancellationToken.None);
            Ensure(reason != ManualOperationBlockReason.None && !manualCalled, "准备期间仍允许手动动作");
            await RejectAsync(() => fixture.Runtime.AcquireTaskEngineControlAsync(new object(), CancellationToken.None), "准备期间可抢占任务控制权");
            await RejectAsync(() => fixture.Runtime.ResetAsync(CancellationToken.None), "准备期间可复位");
            await fixture.Runtime.StopPreparationAsync(CancellationToken.None);
            try { await run; } catch (OperationCanceledException) { }
            Ensure(!fixture.Axis.Moving && fixture.Axis.StopCount > 0 && !fixture.Runtime.IsPrepared, "停止准备没有停轴或误标完成");
            Ensure(fixture.Runtime.State == MachineRunState.ResetRequired && !fixture.Runtime.IsPreparing, "停止准备后未解除安全收尾状态");
            await fixture.Runtime.DisconnectAsync(CancellationToken.None);
        }

        /// <summary>设备准备也持续监控急停，触发后取消步骤和受控停轴。</summary>
        private static async Task EmergencyStopAsync()
        {
            var fixture = new Fixture();
            object taskOwner = new object();
            fixture.Runtime.ConfigurePreparations(new[] { new Preparation("Slow", fixture) { WaitUntilStopped = true } });
            await fixture.ConnectAsync();
            await fixture.Runtime.AcquireTaskEngineControlAsync(taskOwner, CancellationToken.None);
            Task run = fixture.Runtime.PrepareAsync(null, CancellationToken.None);
            await WaitAsync(() => fixture.Axis.Moving);
            await RejectAsync(() => fixture.Runtime.BeginTaskEngineRunAsync(CancellationToken.None), "准备期间允许任务生产启动");
            fixture.Safety.Active = true;
            await WaitAsync(() => !fixture.Runtime.IsPreparing);
            try { await run; } catch (OperationCanceledException) { }
            Ensure(fixture.Runtime.State == MachineRunState.Alarmed && !fixture.Axis.Moving && !fixture.Runtime.IsPrepared,
                "准备期间急停没有停止或错误放行");
            fixture.Safety.Active = false;
            await fixture.Runtime.ResetAsync(CancellationToken.None);
            fixture.Runtime.ReleaseTaskEngineControl(taskOwner);
            await fixture.Runtime.DisconnectAsync(CancellationToken.None);
        }

        /// <summary>动作完成而最终姿态不合格时仍失败，报警复位不能制造准备完成。</summary>
        private static async Task FailureDoesNotPrepareAsync()
        {
            var fixture = new Fixture();
            fixture.Runtime.ConfigurePreparations(new[] { new Preparation("Bad", fixture) { ReadyPose = false } });
            await fixture.ConnectAsync();
            await RejectAsync(() => fixture.Runtime.PrepareAsync(null, CancellationToken.None), "最终姿态不合格却成功");
            Ensure(!fixture.Runtime.IsPrepared && fixture.Runtime.AlarmService.Alarms.Any(a => a.Definition.Code == "PREPARATION_FAILED"), "准备失败未上报警或误标完成");
            await fixture.Runtime.ResetAsync(CancellationToken.None);
            Ensure(!fixture.Runtime.IsPrepared, "清报警错误地完成了准备");
            await fixture.Runtime.DisconnectAsync(CancellationToken.None);
        }

        /// <summary>读不到轴状态不发送动作，单工位自动包含前置，循环依赖必须拒绝。</summary>
        private static async Task UnknownStateAndDependencyAsync()
        {
            var fixture = new Fixture();
            fixture.Runtime.ConfigurePreparations(new[] { new Preparation("A", fixture), new Preparation("B", fixture, "A") { RequireDependenciesExecuted = true }, new Preparation("C", fixture, "B") { RequireDependenciesExecuted = true }, new Preparation("D", fixture, "A", "C") { RequireDependenciesExecuted = true } });
            await fixture.ConnectAsync();
            fixture.Axis.ReadError = true;
            await RejectAsync(() => fixture.Runtime.PrepareAsync(null, CancellationToken.None), "轴状态未知却开始准备");
            Ensure(fixture.Log.Count == 0, "轴读取失败后已执行准备动作");
            fixture.Axis.ReadError = false;
            await fixture.Runtime.PrepareAsync("D", CancellationToken.None);
            Ensure(fixture.Log.SequenceEqual(new[] { "A", "B", "C", "D" }), "所选工位没有依次准备其前置工位");
            var fork = new Fixture();
            fork.Runtime.ConfigurePreparations(new[]
            {
                new Preparation("C", fork, "A", "B") { RequireDependenciesExecuted = true },
                new Preparation("A", fork),
                new Preparation("B", fork)
            });
            await fork.ConnectAsync();
            await fork.Runtime.PrepareAsync("C", CancellationToken.None);
            Ensure(fork.Log.SequenceEqual(new[] { "A", "B", "C" }), "独立前序工位没有全部执行后再准备汇合工位。");
            await fork.Runtime.DisconnectAsync(CancellationToken.None);
            bool cycleRejected = false;
            try { fixture.Runtime.ConfigurePreparations(new[] { new Preparation("A", fixture, "B"), new Preparation("B", fixture, "A") { RequireDependenciesExecuted = true }, new Preparation("C", fixture, "B") { RequireDependenciesExecuted = true }, new Preparation("D", fixture, "A", "C") { RequireDependenciesExecuted = true } }); }
            catch (ArgumentException) { cycleRejected = true; }
            Ensure(cycleRejected, "循环依赖未被拒绝");
            await fixture.Runtime.DisconnectAsync(CancellationToken.None);
        }

        /// <summary>任务入口也受同一准备闸门约束，准备不另抢会话控制权。</summary>
        private static async Task TaskEntryGuardAsync()
        {
            var fixture = new Fixture();
            object owner = new PauseOwner();
            fixture.Runtime.ConfigurePreparations(new[] { new Preparation("TaskPreparation", fixture) });
            await fixture.ConnectAsync();
            await fixture.Runtime.AcquireTaskEngineControlAsync(owner, CancellationToken.None);
            await fixture.Runtime.ResetAsync(CancellationToken.None);
            await RejectAsync(() => fixture.Runtime.BeginTaskEngineRunAsync(CancellationToken.None), "任务入口绕过准备");
            await fixture.Runtime.PrepareAsync(null, CancellationToken.None);
            Ensure(fixture.Runtime.IsTaskEngineControlClaimed && fixture.Runtime.ControlOwner == MachineControlOwner.TaskEngine,
                "准备改变了任务会话控制权");
            await fixture.Runtime.BeginTaskEngineRunAsync(CancellationToken.None);
            await fixture.Runtime.FinishTaskEngineRunAsync(false, null);
            await fixture.Runtime.ResetAsync(CancellationToken.None);
            await fixture.Runtime.PrepareAsync(null, CancellationToken.None);
            await fixture.Runtime.BeginTaskEngineRunAsync(CancellationToken.None);
            fixture.Runtime.ConfirmTaskEngineProductionStarted();
            await fixture.Runtime.FinishTaskEngineRunAsync(false, null);
            fixture.Runtime.ReleaseTaskEngineControl(owner);
            await fixture.Runtime.DisconnectAsync(CancellationToken.None);
        }

        /// <summary>停轴失败后保持占用，必须再次确认停止成功才能复位或手动操作。</summary>
        private static async Task StopFailureRemainsExclusiveAsync()
        {
            var fixture = new Fixture();
            bool recordCleared = false;
            fixture.Runtime.SetPreparationRecordCallbacks(() => { }, () => recordCleared = true);
            fixture.Runtime.ConfigurePreparations(new[] { new Preparation("Slow", fixture) { WaitUntilStopped = true } });
            await fixture.ConnectAsync();
            Task run = fixture.Runtime.PrepareAsync(null, CancellationToken.None);
            await WaitAsync(() => fixture.Axis.Moving);
            fixture.Axis.StopFails = true;
            await RejectAsync(() => fixture.Runtime.StopPreparationAsync(CancellationToken.None), "停轴错误被忽略");
            await RejectAsync(() => run, "停止失败未反映到准备结果");
            Ensure(fixture.Runtime.IsPreparing && !fixture.Runtime.IsPrepared, "停止未确认却归还了设备占用");
            Ensure(!recordCleared, "停稳未确认却清除了准备记录");
            await RejectAsync(() => fixture.Runtime.ResetAsync(CancellationToken.None), "停止未确认时仍可复位");
            fixture.Axis.StopFails = false;
            await fixture.Runtime.StopPreparationAsync(CancellationToken.None);
            Ensure(!fixture.Runtime.IsPreparing, "重新确认停稳成功后仍无法解除占用");
            await fixture.Runtime.ResetAsync(CancellationToken.None);
            await fixture.Runtime.DisconnectAsync(CancellationToken.None);
        }

        /// <summary>完成边界收到取消不留下准备资格，完成后迟到进度不能覆盖最终结果。</summary>
        private static async Task CompletionCancellationAndLateProgressAsync()
        {
            var fixture = new Fixture();
            var preparation = new Preparation("Fast", fixture);
            fixture.Runtime.ConfigurePreparations(new[] { preparation });
            await fixture.ConnectAsync();
            using (var cancel = new CancellationTokenSource())
            {
                EventHandler handler = (sender, args) =>
                {
                    if (fixture.Runtime.IsPreparing && fixture.Runtime.PreparationStations[0].IsPrepared) cancel.Cancel();
                };
                fixture.Runtime.PreparationChanged += handler;
                try { await fixture.Runtime.PrepareAsync(null, cancel.Token); }
                catch (OperationCanceledException) { }
                fixture.Runtime.PreparationChanged -= handler;
                Ensure(!fixture.Runtime.IsPrepared && !fixture.Runtime.IsPreparing, "完成边界取消仍错误标记准备完成");
            }
            await fixture.Runtime.PrepareAsync(null, CancellationToken.None);
            preparation.CapturedProgress.Report(new StationPreparationProgress("迟到的旧步骤"));
            Ensure(fixture.Runtime.PreparationStations[0].State == StationPreparationState.Ready, "迟到进度覆盖了准备完成状态");
            await fixture.Runtime.DisconnectAsync(CancellationToken.None);
        }

        /// <summary>等待可观察事件，超过上限即认为检查失败。</summary>
        private static async Task WaitAsync(Func<bool> predicate)
        { for (int i = 0; i < 200; i++) { if (predicate()) return; await Task.Delay(10); } throw new TimeoutException("内部检查等待超时"); }
        /// <summary>期望具体操作被拒绝。</summary>
        private static async Task RejectAsync(Func<Task> operation, string message)
        { try { await operation(); } catch (Exception) { return; } throw new InvalidOperationException(message); }
        /// <summary>断言用户可见安全语义。</summary>
        private static void Ensure(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

        /// <summary>为准备检查提供受控暂停能力，不连接任务设备。</summary>
        private sealed class PauseOwner : IAutomaticProductionPause
        {
            /// <summary>测试替身没有真实在途动作。</summary>
            public bool IsAutomaticPauseConfirmed { get { return true; } }
            /// <summary>测试替身的暂停不访问硬件。</summary>
            public Task PauseAutomaticProductionAsync() { return Task.CompletedTask; }
        }

        /// <summary>集中保存准备测试使用的卡、轴、工位和运行时。</summary>
        private sealed class Fixture
        {
            /// <summary>可控轴替身。</summary>
            internal readonly Axis Axis = new Axis();
            /// <summary>用于控制测试连接状态的运动卡替身。</summary>
            internal readonly Card Card;
            /// <summary>可控安全输入。</summary>
            internal readonly Safety Safety = new Safety();
            /// <summary>记录准备的工位顺序。</summary>
            internal readonly List<string> Log = new List<string>();
            /// <summary>检查是否误启动生产。</summary>
            internal readonly Flow Flow = new Flow();
            /// <summary>被检查的通用运行时。</summary>
            internal readonly MachineRuntime Runtime;
            /// <summary>创建无硬件环境。</summary>
            internal Fixture(MachineControlOwner owner = MachineControlOwner.StationFlows, ILogicalIo buttons = null) { Card = new Card(Axis); Runtime = new MachineRuntime(new MotionCardGroup(new[] { Card }), new[] { Flow },
                new MachineAlarmService(), Safety, pauseTimeout: TimeSpan.FromSeconds(1), safetyPollInterval: TimeSpan.FromMilliseconds(10),
                controlOwner: owner, controlInputIo: buttons, startInputName: buttons == null ? null : "Start"); }
            /// <summary>连接内存卡，不做物理动作。</summary>
            internal Task ConnectAsync() { return Runtime.ConnectAsync(CancellationToken.None); }
        }

        /// <summary>按钮始终释放，只计数以验证断开失败后的现场按钮采集已恢复。</summary>
        private sealed class MemoryButtons : ILogicalIo
        {
            /// <summary>累计输入采样次数。</summary>
            internal int Reads;
            /// <summary>只提供释放状态，不发起设备命令。</summary>
            public Task<bool> ReadInputAsync(string name, CancellationToken token)
            {
                Interlocked.Increment(ref Reads);
                return Task.FromResult(false);
            }
            /// <summary>此用例禁止读取输出。</summary>
            public Task<bool> ReadOutputAsync(string name, CancellationToken token) { throw new NotSupportedException(); }
            /// <summary>此用例禁止写输出。</summary>
            public Task WriteOutputAsync(string name, bool value, CancellationToken token) { throw new NotSupportedException(); }
        }

        /// <summary>用于验证依赖和取消的工位准备替身。</summary>
        private sealed class Preparation : IStationPreparation
        {
            /// <summary>所在测试设备。</summary>
            private readonly Fixture _fixture;
            /// <summary>设置测试工位与依赖。</summary>
            internal Preparation(string id, Fixture fixture, params string[] dependencies) { StationId = id; _fixture = fixture; Dependencies = dependencies; }
            /// <summary>工位标识。</summary>
            public string StationId { get; }
            /// <summary>工位显示名。</summary>
            public string StationName { get { return StationId; } }
            /// <summary>测试依赖列表。</summary>
            public IReadOnlyCollection<string> Dependencies { get; }
            /// <summary>为真时等待取消以检查停轴。</summary>
            internal bool WaitUntilStopped;
            /// <summary>控制最终姿态检查。</summary>
            internal bool ReadyPose = true;
            /// <summary>指定第几次只读校验拒绝；零表示不拒绝。</summary>
            internal int RejectValidationAt;
            /// <summary>模拟前序工位动作完成后才建立的物理互锁。</summary>
            internal bool RequireDependenciesExecuted;
            /// <summary>在只读预检中注入取消等并发现场变化。</summary>
            internal Action OnValidate;
            /// <summary>在最终姿态校验期间注入底层掉线。</summary>
            internal Action OnReady;
            /// <summary>统计校验次数以模拟预检后现场条件变化。</summary>
            private int _validationCount;
            /// <summary>保留本次进度接收器，检查迟到回调不会破坏结果。</summary>
            internal IProgress<StationPreparationProgress> CapturedProgress;
            /// <summary>按测试设置模拟只读准备前提检查。</summary>
            public Task ValidateStartAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); OnValidate?.Invoke(); if (RequireDependenciesExecuted && Dependencies.Any(id => !_fixture.Log.Contains(id))) throw new InvalidOperationException("前序工位尚未完成动作"); if (++_validationCount == RejectValidationAt) throw new InvalidOperationException("测试轴尚未使能"); return Task.CompletedTask; }
            /// <summary>记录顺序并在需要时保持运动直到取消。</summary>
            public async Task ExecuteAsync(IProgress<StationPreparationProgress> progress, CancellationToken token)
            {
                CapturedProgress = progress;
                _fixture.Log.Add(StationId); progress.Report(new StationPreparationProgress("测试准备"));
                if (!WaitUntilStopped) return;
                _fixture.Axis.Moving = true;
                try { await Task.Delay(Timeout.Infinite, token); } finally { _fixture.Axis.Moving = false; }
            }
            /// <summary>根据测试安排接受或拒绝最终姿态。</summary>
            public Task ValidateReadyAsync(CancellationToken token)
            { token.ThrowIfCancellationRequested(); OnReady?.Invoke(); if (!ReadyPose) throw new InvalidOperationException("最终姿态不合格"); return Task.CompletedTask; }
        }

        /// <summary>从不连接真实卡的控制器替身。</summary>
        private sealed class Card : IMotionCard
        {
            /// <summary>安装测试轴。</summary>
            internal Card(IAxis axis) { Axes = new[] { axis }; }
            /// <summary>测试卡标识。</summary>
            public string ControllerId { get { return "TestCard"; } }
            /// <summary>内存连接状态。</summary>
            public bool IsConnected { get { if (ConnectionProbeError != null) throw ConnectionProbeError; return _connected; } }
            /// <summary>卡的底层连接缓存。</summary>
            private bool _connected;
            /// <summary>仅在设备断开失败后模拟连接检测本身抛错。</summary>
            internal Exception ConnectionProbeError;
            /// <summary>测试轴列表。</summary>
            public IReadOnlyCollection<IAxis> Axes { get; }
            /// <summary>标记连接。</summary>
            public Task ConnectAsync(CancellationToken token) { _connected = true; return Task.CompletedTask; }
            /// <summary>模拟断开动作失败且卡仍报告连接。</summary>
            internal bool FailDisconnect;
            /// <summary>记录驱动断卡调用次数，和准备通知的发生顺序对照。</summary>
            internal int DisconnectCalls;
            /// <summary>在内存卡断开入口观察准备资格是否已失效。</summary>
            internal Action OnDisconnect;
            /// <summary>失败时保留连接状态，成功时标记断开。</summary>
            public Task DisconnectAsync(CancellationToken token)
            {
                DisconnectCalls++;
                OnDisconnect?.Invoke();
                if (FailDisconnect) throw new InvalidOperationException("模拟断开失败");
                _connected = false;
                return Task.CompletedTask;
            }
            /// <summary>模拟底层通信突然断开，而不经过 Runtime 的主动断卡流程。</summary>
            internal void ForceOffline() { _connected = false; }
        }

        /// <summary>提供运动/停止和未知状态的轴替身。</summary>
        private sealed class Axis : IAxis
        {
            /// <summary>测试逻辑轴名称。</summary>
            public string Name { get { return "TestAxis"; } }
            /// <summary>模拟当前运动。</summary>
            internal volatile bool Moving;
            /// <summary>模拟读取失败。</summary>
            internal bool ReadError;
            /// <summary>收到的停止次数。</summary>
            internal int StopCount;
            /// <summary>模拟驱动停止命令失败。</summary>
            internal bool StopFails;
            /// <summary>延迟停止完成，验证多个停止请求的收尾。</summary>
            internal TaskCompletionSource<bool> StopGate;
            /// <summary>返回可靠状态或明确抛出读取错误。</summary>
            public Task<AxisStatus> ReadStatusAsync(CancellationToken token)
            { if (ReadError) throw new InvalidOperationException("轴状态不可读"); return Task.FromResult(new AxisStatus(0, 0, true, Moving, true, false, false, false, false, false, null, false)); }
            /// <summary>无需物理使能。</summary>
            public Task EnableServoAsync(CancellationToken token) { return Task.CompletedTask; }
            /// <summary>无需物理关闭。</summary>
            public Task DisableServoAsync(CancellationToken token) { return Task.CompletedTask; }
            /// <summary>不测试厂商回零算法。</summary>
            public Task HomeAsync(CancellationToken token) { return Task.CompletedTask; }
            /// <summary>不执行物理绝对定位。</summary>
            public Task MoveAbsoluteAsync(double position, AxisMotionProfile profile, CancellationToken token) { return Task.CompletedTask; }
            /// <summary>不执行物理相对定位。</summary>
            public Task MoveRelativeAsync(double distance, AxisMotionProfile profile, CancellationToken token) { return Task.CompletedTask; }
            /// <summary>不执行物理点动。</summary>
            public Task StartJogAsync(MotionDirection direction, AxisMotionProfile profile, CancellationToken token) { return Task.CompletedTask; }
            /// <summary>记录停止并使运动反馈变为停止。</summary>
            public Task StopAsync(AxisStopMode mode, CancellationToken token)
            { StopCount++; if (StopFails) throw new InvalidOperationException("测试停止失败"); Moving = false; return StopGate == null ? Task.CompletedTask : StopGate.Task; }
            /// <summary>没有实际驱动报警。</summary>
            public Task ResetAlarmAsync(CancellationToken token) { ResetCount++; OnReset?.Invoke(); return Task.CompletedTask; }
            /// <summary>在轴复位中模拟通信丢失。</summary>
            internal Action OnReset;
            /// <summary>复位调用次数，核对拒绝路径无驱动调用。</summary>
            internal int ResetCount;
        }

        /// <summary>可控急停输入。</summary>
        private sealed class Safety : IMachineSafetyStatus
        {
            /// <summary>可将下一次安全读取挂起，用于测试命令末端的连接复核。</summary>
            internal TaskCompletionSource<bool> NextRead;
            /// <summary>是否触发急停。</summary>
            internal volatile bool Active;
            /// <summary>读取当前模拟急停。</summary>
            public Task<bool> ReadEmergencyStopActiveAsync(CancellationToken token)
            {
                return Interlocked.Exchange(ref NextRead, null)?.Task ?? Task.FromResult(Active);
            }
        }

        /// <summary>等待取消的生产流程，用于证明准备不会启动生产。</summary>
        private sealed class Flow : IStationFlow
        {
            /// <summary>进入生产步的次数。</summary>
            internal int Executions;
            /// <summary>生产工位名。</summary>
            public string StationName { get { return "Production"; } }
            /// <summary>固定生产步骤名。</summary>
            public string CurrentStepName { get { return "等待生产停止"; } }
            /// <summary>进入生产后一直等待暂停令牌。</summary>
            public async Task ExecuteCurrentStepAsync(CancellationToken token) { Interlocked.Increment(ref Executions); await Task.Delay(Timeout.Infinite, token); }
            /// <summary>不重放生产步骤。</summary>
            public Task ResetAsync(CancellationToken token) { ResetCount++; OnReset?.Invoke(); return Task.CompletedTask; }
            /// <summary>在工位复位中模拟通信丢失。</summary>
            internal Action OnReset;
            /// <summary>工位复位次数，用于验证无会话时不调用设备工艺。</summary>
            internal int ResetCount;
        }

        /// <summary>第一轮快速响应取消，第二轮受控延迟收尾以复现跨代定时器。</summary>
        private sealed class DelayedSecondStopFlow : IStationFlow
        {
            /// <summary>已经开始执行的周期次数。</summary>
            internal int Executions;
            /// <summary>第二轮已收到停止。</summary>
            internal readonly TaskCompletionSource<bool> SecondStopEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>允许第二轮结束。</summary>
            internal readonly TaskCompletionSource<bool> ReleaseSecondStop = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>工位名称。</summary>
            public string StationName => "延迟停止工位";
            /// <summary>当前步骤说明。</summary>
            public string CurrentStepName => "等待停止";
            /// <summary>取消第二轮时先等待测试许可，再完成退出。</summary>
            public async Task ExecuteCurrentStepAsync(CancellationToken token)
            {
                int run = Interlocked.Increment(ref Executions);
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) when (run == 2 && token.IsCancellationRequested)
                {
                    SecondStopEntered.TrySetResult(true);
                    await ReleaseSecondStop.Task;
                    throw;
                }
            }
            /// <summary>测试流程没有额外复位动作。</summary>
            public Task ResetAsync(CancellationToken token) { return Task.CompletedTask; }
        }

        /// <summary>取消回调异常的 C# 工位替身，用于验证失败不会被正常任务退出掩盖。</summary>
        private sealed class ThrowingStopFlow : IStationFlow
        {
            /// <summary>验证模拟的停止异常实际由唯一一次取消回调抛出。</summary>
            internal int StopCallbackCount;
            /// <summary>工位已经注册取消回调。</summary>
            internal readonly TaskCompletionSource<bool> Entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>工位名称。</summary>
            public string StationName => "停止失败工位";
            /// <summary>当前步骤说明。</summary>
            public string CurrentStepName => "等待停止";
            /// <summary>模拟停止回调返回异常，同时允许后台任务退出。</summary>
            public async Task ExecuteCurrentStepAsync(CancellationToken token)
            {
                var stopping = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (token.Register(() =>
                {
                    Interlocked.Increment(ref StopCallbackCount);
                    stopping.TrySetResult(true);
                    throw new InvalidOperationException("测试停止回调失败");
                }))
                {
                    Entered.TrySetResult(true);
                    await stopping.Task.ConfigureAwait(false);
                }
            }
            /// <summary>测试工位没有额外复位动作。</summary>
            public Task ResetAsync(CancellationToken token) { return Task.CompletedTask; }
        }
    }
}
