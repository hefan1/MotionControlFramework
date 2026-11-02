using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Framework.Contracts.Flow;
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
            try { RunAsync().GetAwaiter().GetResult(); Console.WriteLine("设备准备：12 组检查通过（仅内部逻辑检查）。"); return 0; }
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
        }

        /// <summary>暂停提交与已报警状态使用同一锁；并发停机报警不能被覆盖成普通暂停。</summary>
        private static async Task StopAlarmCannotBeOverwrittenByTaskPauseAsync()
        {
            var alarm = new GatedAlarmService();
            var axis = new Axis();
            var runtime = new MachineRuntime(new MotionCardGroup(new[] { new Card(axis) }), new[] { new Flow() },
                alarm, new Safety(), controlOwner: MachineControlOwner.TaskEngine);
            object owner = new object();
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
        private sealed class GatedAlarmService : IMachineAlarmService
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
            Ensure(begins == 1 && ends == 1 && success.Runtime.IsPrepared, "准备成功未提交安全结束记录");

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
            Ensure(cleared && !cancelled.Runtime.IsPreparing, "受控停止后未保存安全结束记录");

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
                if (owner == MachineControlOwner.StationFlows)
                    await RejectAsync(() => fixture.Runtime.StartAsync(CancellationToken.None), "标记写入失败仍允许 C# 生产");
                else
                    await RejectAsync(() => fixture.Runtime.BeginTaskEngineRunAsync(CancellationToken.None), "标记写入失败仍允许任务生产");
                Ensure(attempts == 1 && fixture.Flow.Executions == 0 && fixture.Runtime.State != MachineRunState.Running,
                    "标记写入失败后状态或工位被启动。");
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
                Ensure((await fixture.Runtime.GetAutomaticStartBlockReasonAsync(CancellationToken.None)).Contains("恢复记录损坏"), "启动提示遗漏恢复原因");
                await RejectAsync(() => fixture.Runtime.PrepareAsync(null, CancellationToken.None), "恢复损坏时允许回零");
                if (owner == MachineControlOwner.StationFlows)
                    await RejectAsync(() => fixture.Runtime.StartAsync(CancellationToken.None), "恢复损坏时允许C#启动");
                else
                    await RejectAsync(() => fixture.Runtime.BeginTaskEngineRunAsync(CancellationToken.None), "恢复损坏时允许任务启动");
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
            fixture.Runtime.ConfigurePreparations(new[] { new Preparation("A", fixture), new Preparation("B", fixture, "A") });
            await fixture.ConnectAsync();
            fixture.Axis.ReadError = true;
            await RejectAsync(() => fixture.Runtime.PrepareAsync(null, CancellationToken.None), "轴状态未知却开始准备");
            Ensure(fixture.Log.Count == 0, "轴读取失败后已执行准备动作");
            fixture.Axis.ReadError = false;
            await fixture.Runtime.PrepareAsync("B", CancellationToken.None);
            Ensure(fixture.Log.SequenceEqual(new[] { "A", "B" }), "所选工位没有依次准备其前置工位");
            bool cycleRejected = false;
            try { fixture.Runtime.ConfigurePreparations(new[] { new Preparation("A", fixture, "B"), new Preparation("B", fixture, "A") }); }
            catch (ArgumentException) { cycleRejected = true; }
            Ensure(cycleRejected, "循环依赖未被拒绝");
            await fixture.Runtime.DisconnectAsync(CancellationToken.None);
        }

        /// <summary>任务入口也受同一准备闸门约束，准备不另抢会话控制权。</summary>
        private static async Task TaskEntryGuardAsync()
        {
            var fixture = new Fixture();
            object owner = new object();
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

        /// <summary>一个完全独立的内存设备环境。</summary>
        private sealed class Fixture
        {
            /// <summary>可控轴替身。</summary>
            internal readonly Axis Axis = new Axis();
            /// <summary>可控安全输入。</summary>
            internal readonly Safety Safety = new Safety();
            /// <summary>记录准备的工位顺序。</summary>
            internal readonly List<string> Log = new List<string>();
            /// <summary>检查是否误启动生产。</summary>
            internal readonly Flow Flow = new Flow();
            /// <summary>被检查的通用运行时。</summary>
            internal readonly MachineRuntime Runtime;
            /// <summary>创建无硬件环境。</summary>
            internal Fixture(MachineControlOwner owner = MachineControlOwner.StationFlows) { Runtime = new MachineRuntime(new MotionCardGroup(new[] { new Card(Axis) }), new[] { Flow },
                new MachineAlarmService(), Safety, pauseTimeout: TimeSpan.FromSeconds(1), safetyPollInterval: TimeSpan.FromMilliseconds(10), controlOwner: owner); }
            /// <summary>连接内存卡，不做物理动作。</summary>
            internal Task ConnectAsync() { return Runtime.ConnectAsync(CancellationToken.None); }
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
            /// <summary>保留本次进度接收器，检查迟到回调不会破坏结果。</summary>
            internal IProgress<StationPreparationProgress> CapturedProgress;
            /// <summary>本替身没有产品占用。</summary>
            public Task ValidateStartAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
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
            { token.ThrowIfCancellationRequested(); if (!ReadyPose) throw new InvalidOperationException("最终姿态不合格"); return Task.CompletedTask; }
        }

        /// <summary>从不连接真实卡的控制器替身。</summary>
        private sealed class Card : IMotionCard
        {
            /// <summary>安装测试轴。</summary>
            internal Card(IAxis axis) { Axes = new[] { axis }; }
            /// <summary>测试卡标识。</summary>
            public string ControllerId { get { return "TestCard"; } }
            /// <summary>内存连接状态。</summary>
            public bool IsConnected { get; private set; }
            /// <summary>测试轴列表。</summary>
            public IReadOnlyCollection<IAxis> Axes { get; }
            /// <summary>标记连接。</summary>
            public Task ConnectAsync(CancellationToken token) { IsConnected = true; return Task.CompletedTask; }
            /// <summary>标记断开。</summary>
            public Task DisconnectAsync(CancellationToken token) { IsConnected = false; return Task.CompletedTask; }
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
            public Task ResetAlarmAsync(CancellationToken token) { return Task.CompletedTask; }
        }

        /// <summary>可控急停输入。</summary>
        private sealed class Safety : IMachineSafetyStatus
        {
            /// <summary>是否触发急停。</summary>
            internal volatile bool Active;
            /// <summary>读取当前模拟急停。</summary>
            public Task<bool> ReadEmergencyStopActiveAsync(CancellationToken token) { return Task.FromResult(Active); }
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
            public Task ResetAsync(CancellationToken token) { return Task.CompletedTask; }
        }
    }
}
