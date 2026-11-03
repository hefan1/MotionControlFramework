using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.IO;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Contracts.Clearance;
using MotionControl.Framework.Runtime.Machine;

namespace MotionControl.ClearanceExecutor.Checks
{
    internal static class Program
    {
        private static int Main()
        {
            try { RunAsync().GetAwaiter().GetResult(); Console.WriteLine("受控清料执行器检查通过（无硬件或正式 Host 验收）。"); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }

        private static async Task RunAsync()
        {
            var success = new Fixture();
            var plan = success.Plan();
            Require(plan.RequiredResources.OrderBy(x => x).SequenceEqual(new[] { "Axis:X", "Output:夹具", "旋转区" }), "资源声明缺失。");
            bool rejected = false;
            try { new ControlledClearancePlan(plan.Actions, new[] { "旋转区", "旋转区" }); } catch (ArgumentException) { rejected = true; }
            Require(rejected, "重复区域未被拒绝。");
            rejected = false;
            try { new ControlledClearancePlan(plan.Actions, new[] { "Axis:伪造" }); } catch (ArgumentException) { rejected = true; }
            Require(rejected, "共享区域可伪造轴资源前缀。");
            rejected = false;
            try { new ClearanceSetOutput("动作", "输出", false, true, new[] { new ClearanceInputCondition("安全门", true) },
                new[] { new ClearanceAxisCondition("X", 0, 1) }, new ClearanceInputCondition[0], TimeSpan.FromSeconds(1),
                maintainInputs: new[] { new ClearanceInputCondition("运行许可", true) }); }
            catch (ArgumentException) { rejected = true; }
            Require(rejected, "没有独立完成反馈的输出动作被接受。");
            rejected = false;
            try { new ClearanceSetOutput("动作", "夹具", true, true, new[] { new ClearanceInputCondition("门关闭", true) },
                new[] { new ClearanceAxisCondition("X", 0, 0) }, new[] { new ClearanceInputCondition("夹紧到位", true) },
                TimeSpan.FromSeconds(1), maintainInputs: new[] { new ClearanceInputCondition("运行许可", true) }); }
            catch (ArgumentException) { rejected = true; }
            Require(rejected, "原状态与目标相同的清料输出动作被接受。");
            var outputSequence = new Fixture().OutputOnlyPlan().Actions[0];
            rejected = false;
            try { new ControlledClearancePlan(new[] { outputSequence, outputSequence }); }
            catch (ArgumentException) { rejected = true; }
            Require(rejected, "连续输出动作的原状态与上一步目标冲突却被接受。");
            var reverseOutput = new ClearanceSetOutput("松开", "夹具", true, false,
                new[] { new ClearanceInputCondition("门关闭", true) }, new[] { new ClearanceAxisCondition("X", 0, 0) },
                new[] { new ClearanceInputCondition("松开到位", true) }, TimeSpan.FromSeconds(1),
                maintainInputs: new[] { new ClearanceInputCondition("运行许可", true) });
            _ = new ControlledClearancePlan(new ControlledClearanceAction[] { outputSequence, reverseOutput });
            var conflictingGuard = new ClearanceAbsoluteMove("搬运", "X", 1, 0.01, new AxisMotionProfile(1, 2, 2),
                new[] { new ClearanceInputCondition("门关闭", true) }, new[] { new ClearanceAxisCondition("X", 0, 0) },
                TimeSpan.FromSeconds(1), maintainInputs: new[] { new ClearanceInputCondition("运行许可", true) },
                maintainOutputs: new[] { new ClearanceOutputCondition("夹具", false) });
            rejected = false;
            try { new ControlledClearancePlan(new ControlledClearanceAction[] { outputSequence, conflictingGuard }); }
            catch (ArgumentException) { rejected = true; }
            Require(rejected, "步骤间未执行输出切换却接受相反维持状态。");
            foreach (int changeAt in new[] { 1, 2, 3 })
            {
                var changedTarget = new Fixture(); changedTarget.Io.FlipTargetAt = changeAt;
                await Reject(() => changedTarget.Executor.ExecuteAsync(changedTarget.OutputOnlyPlan(), CancellationToken.None));
                Require(changedTarget.Io.Writes == 0, "目标输出原状态不符或变化后仍下发写入，采样=" + changeAt);
            }
            var unreadableTarget = new Fixture(); unreadableTarget.Io.FailTargetAt = 2;
            await Reject(() => unreadableTarget.Executor.ExecuteAsync(unreadableTarget.OutputOnlyPlan(), CancellationToken.None));
            Require(unreadableTarget.Io.Writes == 0, "目标输出原状态不可读仍下发写入。");
            await success.Executor.ExecuteAsync(plan, CancellationToken.None);
            Require(success.Axis.MoveCount == 1 && success.Io.Writes == 1 && success.Io.Output && !success.Executor.HasUnsettledAction, "完整动作或反馈未确认。");
            await Reject(() => success.Executor.ExecuteAsync(plan, CancellationToken.None));

            var changed = new Fixture(); changed.Io.ChangeSecondRead = true;
            await Reject(() => changed.Executor.ExecuteAsync(changed.Plan(), CancellationToken.None));
            Require(changed.Axis.MoveCount == 0 && changed.Io.Writes == 0, "前置输入变化仍下发动作。");
            var unknownAxis = new Fixture(); unknownAxis.Axis.Velocity = double.NaN;
            await Reject(() => unknownAxis.Executor.ExecuteAsync(unknownAxis.Plan(), CancellationToken.None));
            Require(unknownAxis.Axis.MoveCount == 0, "轴状态未知仍下发动作。");
            var unknownLimit = new Fixture(); unknownLimit.Axis.UnknownLimit = true;
            await Reject(() => unknownLimit.Executor.ExecuteAsync(unknownLimit.Plan(), CancellationToken.None));
            Require(unknownLimit.Axis.MoveCount == 0, "限位未知仍下发动作。");

            var outputFail = new Fixture(); outputFail.Io.FailWriteAfterAccept = true;
            await Reject(() => outputFail.Executor.ExecuteAsync(outputFail.Plan(), CancellationToken.None));
            Require(outputFail.Io.Output && outputFail.Executor.HasUnsettledAction, "输出下发失败后丢失不确定状态。");
            await Reject(() => outputFail.Executor.ExecuteAsync(outputFail.Plan(), CancellationToken.None));
            foreach (bool unreadable in new[] { false, true })
            {
                var droppedTarget = new Fixture(); droppedTarget.Io.NoFeedback = true;
                Task waiting = droppedTarget.Executor.ExecuteAsync(droppedTarget.OutputOnlyPlan(), CancellationToken.None);
                await Until(() => droppedTarget.Io.Writes == 1 && droppedTarget.Io.TargetReads >= 5);
                if (unreadable) droppedTarget.Io.FailAllTargetReads = true;
                else droppedTarget.Io.Output = false;
                Exception error = await Capture(() => waiting);
                Require(error.ToString().Contains(unreadable ? "目标输出读取失败" : "目标输出状态失效")
                    && droppedTarget.Executor.HasUnsettledAction && droppedTarget.Io.Writes == 1,
                    "等待独立完成输入期间目标 DO 异常未失败并保持隔离。");
            }
            var staleFeedback = new Fixture(); staleFeedback.Io.DropOnFeedback = true; staleFeedback.Io.ForceFeedbackCompleted = true;
            await Reject(() => staleFeedback.Executor.ExecuteAsync(staleFeedback.OutputOnlyPlan(), CancellationToken.None));
            Require(staleFeedback.Io.Writes == 1 && staleFeedback.Executor.HasUnsettledAction,
                "目标 DO 掉电但完成 DI 仍有效时错误完成。");
            var wrongReadback = new Fixture(); wrongReadback.Io.WrongReadback = true;
            await Reject(() => wrongReadback.Executor.ExecuteAsync(wrongReadback.Plan(), CancellationToken.None));
            Require(wrongReadback.Executor.HasUnsettledAction, "输出回读失败时错误释放。");
            var noFeedback = new Fixture(); noFeedback.Io.NoFeedback = true;
            await Reject(() => noFeedback.Executor.ExecuteAsync(noFeedback.Plan(TimeSpan.FromMilliseconds(150)), CancellationToken.None));
            Require(noFeedback.Executor.HasUnsettledAction, "独立到位反馈超时后错误完成。");
            var conflict = new Fixture(); conflict.Io.Conflict = true;
            await Reject(() => conflict.Executor.ExecuteAsync(conflict.Plan(), CancellationToken.None));
            Require(conflict.Executor.HasUnsettledAction, "双到位冲突后错误完成。");
            var readTimeout = new Fixture(); readTimeout.Io.HangFeedback = true;
            await Reject(() => readTimeout.Executor.ExecuteAsync(readTimeout.Plan(TimeSpan.FromMilliseconds(150)), CancellationToken.None));
            Require(readTimeout.Executor.HasUnsettledAction, "反馈读取超时后错误完成。");
            var wrongPosition = new Fixture(); wrongPosition.Axis.WrongPosition = true;
            await Reject(() => wrongPosition.Executor.ExecuteAsync(wrongPosition.Plan(), CancellationToken.None));
            Require(wrongPosition.Axis.StopCount == 1 && wrongPosition.Executor.HasUnsettledAction && wrongPosition.Io.Writes == 0,
                "位置反馈不符后未停止、未锁存或继续输出。");
            var cancelFailure = new Fixture(); cancelFailure.Axis.WrongPosition = true; cancelFailure.Axis.CancelCallbackThrows = true;
            Exception cancelError = await Capture(() => cancelFailure.Executor.ExecuteAsync(cancelFailure.Plan(), CancellationToken.None));
            Require(cancelError.ToString().Contains("取消回调失败") && cancelFailure.Executor.HasUnsettledAction,
                "同步取消回调失败未进入诊断或未保留隔离。 ");

            var outputCancel = new Fixture(); outputCancel.Io.BlockWrite = true;
            using (var source = new CancellationTokenSource())
            {
                Task run = outputCancel.Executor.ExecuteAsync(outputCancel.Plan(), source.Token);
                await Until(() => outputCancel.Io.Writes == 1); source.Cancel();
                await Task.Delay(50);
                Require(!run.IsCompleted && !outputCancel.Executor.InFlightSettlement.IsCompleted && outputCancel.Executor.HasUnsettledAction,
                    "输出原始写入仍在途时丢弃任务或取消锁存。");
                outputCancel.Io.ReleaseWrite(); await Reject(() => run);
                Require(outputCancel.Io.Output && outputCancel.Executor.HasUnsettledAction, "迟到输出被误认为未执行。");
            }

            var canceled = new Fixture(); canceled.Axis.BlockMove = true;
            using (var source = new CancellationTokenSource())
            {
                Task run = canceled.Executor.ExecuteAsync(canceled.Plan(), source.Token);
                await Until(() => canceled.Axis.MoveCount == 1);
                source.Cancel(); await Until(() => canceled.Axis.StopCount == 1);
                Require(!run.IsCompleted && !canceled.Executor.InFlightSettlement.IsCompleted && canceled.Executor.HasUnsettledAction,
                    "原轴调用仍在途时过早结束或丢弃凭证。");
                canceled.Axis.Release();
                await Reject(() => run);
                Require(canceled.Executor.InFlightSettlement.IsCompleted && canceled.Executor.HasUnsettledAction, "取消后未锁存现场待确认。");
            }
            var failedStop = new Fixture(); failedStop.Axis.BlockMove = true; failedStop.Axis.FailStop = true;
            using (var source = new CancellationTokenSource())
            {
                Task run = failedStop.Executor.ExecuteAsync(failedStop.Plan(), source.Token);
                await Until(() => failedStop.Axis.MoveCount == 1); source.Cancel();
                await Until(() => failedStop.Axis.StopCount == 1); failedStop.Axis.Release();
                await Reject(() => run);
                Require(failedStop.Executor.HasUnsettledAction && failedStop.Executor.InFlightSettlement.IsFaulted, "停轴失败未保留故障。");
            }
            var lostPermit = new Fixture(); lostPermit.Axis.BlockMove = true;
            Task monitored = lostPermit.Executor.ExecuteAsync(lostPermit.Plan(), CancellationToken.None);
            await Until(() => lostPermit.Axis.MoveCount == 1); lostPermit.Io.DropPermit = true;
            await Until(() => lostPermit.Axis.StopCount == 1);
            Require(!monitored.IsCompleted && lostPermit.Executor.HasUnsettledAction, "维持许可失效后未等待实际动作退出。");
            lostPermit.Axis.Release();
            Exception permitError = await Capture(() => monitored);
            Require(permitError.ToString().Contains("维持输入失效") && lostPermit.Io.Writes == 0 && lostPermit.Executor.HasUnsettledAction,
                "动作中许可失效未停轴、报警或阻止后续输出。");
            var initialMonitorFailure = new Fixture(); initialMonitorFailure.Io.FailPermitAtRead = 2;
            await Reject(() => initialMonitorFailure.Executor.ExecuteAsync(initialMonitorFailure.Plan(), CancellationToken.None));
            Require(initialMonitorFailure.Axis.MoveCount == 0, "监控启动即失败时仍下发运动。");
            var completionRace = new Fixture(); completionRace.Axis.BeforeCompletion = () => completionRace.Io.DropPermit = true;
            Exception raceError = await Capture(() => completionRace.Executor.ExecuteAsync(completionRace.Plan(), CancellationToken.None));
            Require(raceError.ToString().Contains("维持输入失效") && completionRace.Axis.MoveCount == 1
                && completionRace.Io.Writes == 0 && completionRace.Executor.HasUnsettledAction,
                "运动完成与许可失效并发时错误进入下一步骤。");
            var canceledRead = new Fixture();
            canceledRead.Axis.BlockMove = true; canceledRead.Axis.SetTargetAfterBlock = true; canceledRead.Io.PendPermitAtRead = 2;
            Task cancelRace = canceledRead.Executor.ExecuteAsync(canceledRead.Plan(), CancellationToken.None);
            await Until(() => canceledRead.Axis.MoveCount == 1 && canceledRead.Io.PermitReads >= 2);
            canceledRead.Axis.Release(); await Task.Delay(50);
            Require(!cancelRace.IsCompleted && canceledRead.Io.Writes == 0, "在途维持读取未结束时错误完成动作。");
            canceledRead.Io.CancelPendingPermit();
            await Reject(() => cancelRace);
            Require(canceledRead.Io.Writes == 0 && canceledRead.Executor.HasUnsettledAction,
                "底层自行取消的维持读取被当作正常监控结束。");
            var lostCommunication = new Fixture(); lostCommunication.Axis.BlockMove = true;
            Task blocked = lostCommunication.Executor.ExecuteAsync(lostCommunication.Plan(), CancellationToken.None);
            await Until(() => lostCommunication.Axis.MoveCount == 1); lostCommunication.Io.HangPermit = true;
            await Until(() => lostCommunication.Axis.StopCount == 1);
            lostCommunication.Axis.Release(); await Reject(() => blocked);
            Require(lostCommunication.Executor.HasUnsettledAction && lostCommunication.Io.Writes == 0,
                "动作中维持输入读取卡住未触发停轴和隔离。");

            var outputGuard = new Fixture(); outputGuard.Io.GuardOutput = true;
            var outputGuardPlan = outputGuard.GuardedPlan();
            Require(outputGuardPlan.RequiredResources.Contains("Output:焊接启动"), "维持输出没有加入清料资源。");
            await Reject(() => outputGuard.Executor.ExecuteAsync(outputGuardPlan, CancellationToken.None));
            Require(outputGuard.Axis.MoveCount == 0, "焊接输出已有效时仍下发清料运动。");
            var outputChanged = new Fixture(); outputChanged.Axis.BlockMove = true;
            Task outputMonitored = outputChanged.Executor.ExecuteAsync(outputChanged.GuardedPlan(), CancellationToken.None);
            await Until(() => outputChanged.Axis.MoveCount == 1); outputChanged.Io.GuardOutput = true;
            await Until(() => outputChanged.Axis.StopCount == 1); outputChanged.Axis.Release();
            Exception outputError = await Capture(() => outputMonitored);
            Require(outputError.ToString().Contains("维持输出状态失效") && outputChanged.Executor.HasUnsettledAction,
                "清料期间工艺输出变化未停轴并保持隔离。");
            var outputReadFailure = new Fixture(); outputReadFailure.Axis.BlockMove = true;
            Task outputReadRun = outputReadFailure.Executor.ExecuteAsync(outputReadFailure.GuardedPlan(), CancellationToken.None);
            await Until(() => outputReadFailure.Axis.MoveCount == 1); outputReadFailure.Io.FailGuardRead = true;
            await Until(() => outputReadFailure.Axis.StopCount == 1); outputReadFailure.Axis.Release();
            await Reject(() => outputReadRun);
            Require(outputReadFailure.Executor.HasUnsettledAction, "动作期间输出读取失败未保持隔离。");
            rejected = false;
            try { new ClearanceSetOutput("动作", "夹具", false, true, new[] { new ClearanceInputCondition("门关闭", true) },
                new[] { new ClearanceAxisCondition("X", 0, 1) }, new[] { new ClearanceInputCondition("夹紧到位", true) },
                TimeSpan.FromSeconds(1), maintainInputs: new[] { new ClearanceInputCondition("运行许可", true) },
                maintainOutputs: new[] { new ClearanceOutputCondition("夹具", false) }); }
            catch (ArgumentException) { rejected = true; }
            Require(rejected, "动作输出错误地同时允许作为固定维持条件。");

            var movingAxis = new FakeAxis { BlockMove = true }; var guardedAxis = new FakeAxis(); var io = new FakeIo();
            var executor = new ControlledClearanceExecutor(new Dictionary<string, IAxis> { ["X"] = movingAxis, ["Y"] = guardedAxis }, io);
            var guardedPlan = new ControlledClearancePlan(new ControlledClearanceAction[] {
                new ClearanceAbsoluteMove("搬运", "X", 1, 0.01, new AxisMotionProfile(1, 2, 2),
                    new[] { new ClearanceInputCondition("门关闭", true) }, new[] { new ClearanceAxisCondition("X", 0, 0) },
                    TimeSpan.FromSeconds(2), new[] { new ClearanceInputCondition("运行许可", true) },
                    new[] { new ClearanceAxisCondition("Y", 0, 0) }) });
            Require(guardedPlan.RequiredResources.Contains("Axis:Y"), "维持轴未加入资源占用。");
            Task guarded = executor.ExecuteAsync(guardedPlan, CancellationToken.None);
            await Until(() => movingAxis.MoveCount == 1); guardedAxis.Position = 1;
            await Until(() => movingAxis.StopCount == 1); movingAxis.Release();
            Exception guardError = await Capture(() => guarded);
            Require(guardError.ToString().Contains("维持轴状态失效") && executor.HasUnsettledAction,
                "运动中维持轴位置变化未触发停轴及隔离。");
        }

        private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        private static async Task Reject(Func<Task> action) { try { await action(); } catch { return; } throw new InvalidOperationException("操作应被拒绝。"); }
        private static async Task<Exception> Capture(Func<Task> action) { try { await action(); } catch (Exception error) { return error; } throw new InvalidOperationException("操作应被拒绝。"); }
        private static async Task Until(Func<bool> condition)
        { for (int i = 0; i < 200 && !condition(); i++) await Task.Delay(10); Require(condition(), "等待测试事件超时。"); }

        private sealed class Fixture
        {
            public readonly FakeAxis Axis = new FakeAxis(); public readonly FakeIo Io = new FakeIo();
            public ControlledClearanceExecutor Executor { get; }
            public Fixture() { Executor = new ControlledClearanceExecutor(new Dictionary<string, IAxis> { ["X"] = Axis }, Io); }
            public ControlledClearancePlan Plan(TimeSpan? timeout = null)
            {
                var inputs = new[] { new ClearanceInputCondition("门关闭", true) };
                var before = new[] { new ClearanceAxisCondition("X", -0.1, 0.1) };
                var after = new[] { new ClearanceAxisCondition("X", 0.9, 1.1) };
                var feedback = new[] { new ClearanceInputCondition("夹紧到位", true), new ClearanceInputCondition("松开到位", false) };
                var maintain = new[] { new ClearanceInputCondition("运行许可", true) };
                return new ControlledClearancePlan(new ControlledClearanceAction[] {
                    new ClearanceAbsoluteMove("移动", "X", 1, 0.01, new AxisMotionProfile(1, 2, 2), inputs, before, TimeSpan.FromSeconds(2), maintain),
                    new ClearanceSetOutput("夹紧", "夹具", false, true, inputs, after, feedback, timeout ?? TimeSpan.FromSeconds(2),
                        new[] { new ClearanceInputConflict("夹紧到位", "松开到位") }, maintain) }, new[] { "旋转区" });
            }
            public ControlledClearancePlan GuardedPlan()
            {
                return new ControlledClearancePlan(new ControlledClearanceAction[] {
                    new ClearanceAbsoluteMove("搬运", "X", 1, 0.01, new AxisMotionProfile(1, 2, 2),
                        new[] { new ClearanceInputCondition("门关闭", true) }, new[] { new ClearanceAxisCondition("X", 0, 0) },
                        TimeSpan.FromSeconds(2), maintainInputs: new[] { new ClearanceInputCondition("运行许可", true) },
                        maintainOutputs: new[] { new ClearanceOutputCondition("焊接启动", false) }) });
            }
            public ControlledClearancePlan OutputOnlyPlan()
            {
                return new ControlledClearancePlan(new ControlledClearanceAction[] {
                    new ClearanceSetOutput("夹紧", "夹具", false, true,
                        new[] { new ClearanceInputCondition("门关闭", true) }, new[] { new ClearanceAxisCondition("X", 0, 0) },
                        new[] { new ClearanceInputCondition("夹紧到位", true), new ClearanceInputCondition("松开到位", false) },
                        TimeSpan.FromSeconds(2), maintainInputs: new[] { new ClearanceInputCondition("运行许可", true) }) });
            }
        }

        private sealed class FakeAxis : IAxis
        {
            private readonly TaskCompletionSource<bool> _release = new TaskCompletionSource<bool>();
            public bool BlockMove, SetTargetAfterBlock, FailStop, WrongPosition, UnknownLimit, CancelCallbackThrows; public int MoveCount, StopCount; public double Velocity, Position;
            public Action BeforeCompletion;
            public string Name { get { return "X"; } }
            public Task<AxisStatus> ReadStatusAsync(CancellationToken token)
            { token.ThrowIfCancellationRequested(); return Task.FromResult(new AxisStatus(Position, Velocity, true, false, true, UnknownLimit ? (bool?)null : false, false, false, false, false, null, false)); }
            public async Task MoveAbsoluteAsync(double position, AxisMotionProfile profile, CancellationToken token)
            { MoveCount++; if (CancelCallbackThrows) token.Register(() => { throw new InvalidOperationException("取消回调失败"); }); if (BlockMove) { await _release.Task; if (SetTargetAfterBlock) Position = position; } else if (!WrongPosition) Position = position; token.ThrowIfCancellationRequested(); BeforeCompletion?.Invoke(); }
            public Task StopAsync(AxisStopMode mode, CancellationToken token)
            { StopCount++; if (FailStop) throw new InvalidOperationException("停止失败"); return Task.CompletedTask; }
            public void Release() { _release.TrySetResult(true); }
            public Task EnableServoAsync(CancellationToken token) { throw new NotSupportedException(); }
            public Task DisableServoAsync(CancellationToken token) { throw new NotSupportedException(); }
            public Task HomeAsync(CancellationToken token) { throw new NotSupportedException(); }
            public Task MoveRelativeAsync(double value, AxisMotionProfile profile, CancellationToken token) { throw new NotSupportedException(); }
            public Task StartJogAsync(MotionDirection direction, AxisMotionProfile profile, CancellationToken token) { throw new NotSupportedException(); }
            public Task ResetAlarmAsync(CancellationToken token) { throw new NotSupportedException(); }
        }

        private sealed class FakeIo : ILogicalIo
        {
            private readonly TaskCompletionSource<bool> _release = new TaskCompletionSource<bool>();
            private readonly TaskCompletionSource<bool> _pendingPermit = new TaskCompletionSource<bool>();
            public bool ChangeSecondRead, FailWriteAfterAccept, WrongReadback, BlockWrite, NoFeedback, Conflict, HangFeedback, DropOnFeedback, ForceFeedbackCompleted; public volatile bool Output, DropPermit, HangPermit, GuardOutput, FailGuardRead, FailAllTargetReads; public int Reads, Writes, PermitReads, FailPermitAtRead, PendPermitAtRead, TargetReads, FlipTargetAt, FailTargetAt;
            public Task<bool> ReadInputAsync(string name, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                if (name == "门关闭") { Reads++; return Task.FromResult(!(ChangeSecondRead && Reads == 2)); }
                if (name == "运行许可")
                {
                    PermitReads++;
                    if (FailPermitAtRead == PermitReads) throw new InvalidOperationException("维持读取失败");
                    if (PendPermitAtRead == PermitReads) return _pendingPermit.Task;
                    return HangPermit ? new TaskCompletionSource<bool>().Task : Task.FromResult(!DropPermit);
                }
                if (HangFeedback) return new TaskCompletionSource<bool>().Task;
                if (Conflict) return Task.FromResult(true);
                if (NoFeedback) return Task.FromResult(false);
                if (DropOnFeedback && name == "夹紧到位") Output = false;
                if (ForceFeedbackCompleted) return Task.FromResult(name == "夹紧到位");
                return Task.FromResult(name == "夹紧到位" ? Output : !Output);
            }
            public Task<bool> ReadOutputAsync(string name, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                if (name == "焊接启动")
                {
                    if (FailGuardRead) throw new InvalidOperationException("焊接输出读取失败");
                    return Task.FromResult(GuardOutput);
                }
                TargetReads++;
                if (FailAllTargetReads || TargetReads == FailTargetAt) throw new InvalidOperationException("目标输出读取失败");
                if (TargetReads == FlipTargetAt) Output = true;
                return Task.FromResult(WrongReadback && Output ? !Output : Output);
            }
            public async Task WriteOutputAsync(string name, bool value, CancellationToken token)
            { token.ThrowIfCancellationRequested(); Writes++; if (BlockWrite) await _release.Task; Output = value; if (FailWriteAfterAccept) throw new InvalidOperationException("写入结果未知"); }
            public void ReleaseWrite() { _release.TrySetResult(true); }
            public void CancelPendingPermit() { _pendingPermit.TrySetCanceled(); }
        }
    }
}
