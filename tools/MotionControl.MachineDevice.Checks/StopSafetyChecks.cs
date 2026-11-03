using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Device;
using MotionControl.TaskEngine.Execution;
using MotionControl.TaskEngine.Machine;
using MotionControl.TaskEngine.Resources;

namespace MotionControl.MachineDevice.Checks
{
    internal static partial class Program
    {
        /// <summary>读状态失败或令牌已取消时也要先请求停止，之后继续报告无法确认的错误。</summary>
        private static void StopBeforeStatusRead()
        {
            var axis = new FakeAxis("X") { IgnoresCancellation = true };
            var device = CreateDevice(axis, new FakeLogicalIo(), out _, out _);
            var move = device.BeginMove("X", 10, false, false, Profile());
            axis.ReadError = new InvalidOperationException("状态读取故障");
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                ExpectMessage(() => device.EndMoveAsync(move, false, cancellation.Token).GetAwaiter().GetResult(), "状态读取故障");
            }
            Ensure(axis.StopCalls == 1 && !axis.Moving, "停止前错误地等待了状态读取，或受取消令牌阻止。");
            var unknown = new FakeAxis("X") { IgnoresCancellation = true };
            var unknownDevice = CreateDevice(unknown, new FakeLogicalIo(), out _, out _);
            var unknownMove = unknownDevice.BeginMove("X", 10, false, false, Profile());
            unknown.ReportedVelocity = double.NaN;
            ExpectMessage(() => unknownDevice.EndMoveAsync(unknownMove, false, CancellationToken.None).GetAwaiter().GetResult(), "速度状态未知");
            Ensure(unknown.StopCalls == 1, "速度未知时没有先发送停止。");
            var completedAxis = new FakeAxis("X");
            var completedDevice = CreateDevice(completedAxis, new FakeLogicalIo(), out _, out _);
            completedDevice.ClaimMachine(null);
            var completedMove = completedDevice.BeginMove("X", 10, false, false, Profile());
            completedAxis.Complete();
            completedAxis.ReportedVelocity = 0.1;
            Ensure(!completedDevice.IsMoveComplete(completedMove, 1), "速度仍非零却报告动作已停稳。");
            completedAxis.ReportedVelocity = double.NaN;
            ExpectMessage(() => completedDevice.EndMoveAsync(completedMove, true, CancellationToken.None).GetAwaiter().GetResult(), "速度状态未知");
            ExpectMessage(() => completedDevice.ReleaseMachine(null), "收尾");
        }

        /// <summary>即使没有任务运行登记，原动作仍在途时设备也必须禁止资源恢复和控制权归还。</summary>
        private static void PendingAxisBlocksRecovery()
        {
            PendingCancellationBlocksRecovery();
            PendingStopBlocksRecovery();
        }

        /// <summary>取消分支未退出时，即使停止命令已报错也不得恢复或归还。</summary>
        private static void PendingCancellationBlocksRecovery()
        {
            var axis = new FakeAxis("X") { StopError = new InvalidOperationException("停止失败"),
                CancellationGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously) };
            var device = CreateDevice(axis, new FakeLogicalIo(), out _, out _);
            device.ClaimMachine(null);
            var request = device.Resources.Enqueue("run", "task", "工位", "node", "步骤", "call", new[] { "Axis:X" });
            string reason;
            Ensure(device.Resources.TryAcquire(request, out reason), reason);
            var move = device.BeginMove("X", 10, false, false, Profile());
            TaskMoveCleanupPendingException failure = null;
            try { device.EndMoveAsync(move, false, CancellationToken.None).GetAwaiter().GetResult(); }
            catch (TaskMoveCleanupPendingException error) { failure = error; }
            Ensure(failure != null && !failure.PendingCleanup.IsCompleted, "未报告仍在途的原动作。");
            device.Resources.Block(request, "停止失败");
            string id = device.ReadResources().Single().RequestId;
            Ensure(!device.TryRecoverResources(id, "现场已核实", out reason) && reason.Contains("收尾"), "原动作仍在途却允许恢复。");
            ExpectMessage(() => device.ReleaseMachine(null), "收尾");
            axis.CancellationGate.TrySetResult(true);
            Ensure(failure.PendingCleanup.Wait(TimeSpan.FromSeconds(5)), "原动作迟到收尾未结束。");
            Ensure(device.TryRecoverResources(id, "检查已确认轴停止", out reason), reason);
            device.ReleaseMachine(null);
        }

        /// <summary>原动作已结束但显式停止仍在途时，仍禁止恢复和归还控制权。</summary>
        private static void PendingStopBlocksRecovery()
        {
            var axis = new FakeAxis("X") { StopGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously) };
            var device = CreateDevice(axis, new FakeLogicalIo(), out _, out _);
            device.ClaimMachine(null);
            var request = device.Resources.Enqueue("run", "task", "工位", "node", "步骤", "call", new[] { "Axis:X" });
            string reason;
            Ensure(device.Resources.TryAcquire(request, out reason), reason);
            var move = device.BeginMove("X", 10, false, false, Profile());
            Task ending = device.EndMoveAsync(move, false, CancellationToken.None);
            Ensure(axis.StopCalls == 1 && !axis.Moving && !ending.IsCompleted, "未建立动作已取消但显式停止仍在途的交错。");
            device.Resources.Block(request, "停止仍在途");
            string id = device.ReadResources().Single().RequestId;
            Ensure(!device.TryRecoverResources(id, "现场已核实", out reason) && reason.Contains("收尾"), "停止仍在途却允许恢复。");
            ExpectMessage(() => device.ReleaseMachine(null), "收尾");
            axis.StopGate.TrySetResult(true);
            Ensure(ending.Wait(TimeSpan.FromSeconds(5)), "显式停止未收尾。");
            Ensure(device.TryRecoverResources(id, "检查已确认轴停止", out reason), reason);
            device.ReleaseMachine(null);
        }

        /// <summary>同轴动作需结束凭证收尾；旧凭证及其他设备不能停止新动作。</summary>
        private static void DeviceMoveTokenOwnership()
        {
            var axis = new FakeAxis("X") { IgnoresCancellation = true };
            var device = CreateDevice(axis, new FakeLogicalIo(), out _, out _);
            var otherAxis = new FakeAxis("X");
            var otherDevice = CreateDevice(otherAxis, new FakeLogicalIo(), out _, out _);
            device.ClaimMachine(null);
            var old = device.BeginMove("X", 10, false, false, Profile());
            ExpectMessage(() => otherDevice.EndMoveAsync(old, false, CancellationToken.None).GetAwaiter().GetResult(), "不属于");
            Ensure(axis.Moving && axis.StopCalls == 0 && otherAxis.StopCalls == 0, "跨设备调用影响了轴。");
            axis.Complete();
            ExpectMessage(() => device.ReleaseMachine(null), "收尾");
            ExpectMessage(() => device.BeginMove("X", 20, false, false, Profile()), "尚未完成收尾");
            Ensure(axis.MoveCalls == 1, "旧凭证未收尾却接受了新动作。");
            device.EndMoveAsync(old, true, CancellationToken.None).GetAwaiter().GetResult();
            device.ReleaseMachine(null);
            device.ClaimMachine(null);
            var current = device.BeginMove("X", 20, false, false, Profile());
            device.EndMoveAsync(old, false, CancellationToken.None).GetAwaiter().GetResult();
            Ensure(axis.Moving && axis.StopCalls == 0, "旧凭证迟到收尾停止了新动作。");
            device.EndMoveAsync(current, false, CancellationToken.None).GetAwaiter().GetResult();
            Ensure(!axis.Moving && axis.StopCalls == 1, "当前动作未正确停止。");
            device.ReleaseMachine(null);

            var failedAxis = new FakeAxis("X") { StopError = new InvalidOperationException("停止失败") };
            var failedDevice = CreateDevice(failedAxis, new FakeLogicalIo(), out _, out _);
            failedDevice.ClaimMachine(null);
            var failedMove = failedDevice.BeginMove("X", 10, false, false, Profile());
            ExpectMessage(() => failedDevice.EndMoveAsync(failedMove, false, CancellationToken.None).GetAwaiter().GetResult(), "停止失败");
            ExpectMessage(() => failedDevice.BeginMove("X", 20, false, false, Profile()), "尚未完成收尾");
            ExpectMessage(() => failedDevice.ReleaseMachine(null), "收尾");
            Ensure(failedDevice.GetResourceRecoveryState() == TaskResourceRecoveryState.Held,
                "公开调用停止失败却错误宣称资源已清除。");

            var raceAxis = new FakeAxis("X") { IgnoresCancellation = true };
            var raceDevice = CreateDevice(raceAxis, new FakeLogicalIo(), out _, out _);
            raceDevice.ClaimMachine(null);
            using (var start = new ManualResetEventSlim(false))
            {
                Func<ITaskAxisMove> attempt = () => { start.Wait(); try { return raceDevice.BeginMove("X", 10, false, false, Profile()); }
                    catch (TaskMoveRejectedBeforeCommandException) { return null; } };
                Task<ITaskAxisMove> a = Task.Run(attempt), b = Task.Run(attempt);
                start.Set();
                Task.WaitAll(a, b);
                Ensure((a.Result == null) != (b.Result == null) && raceAxis.MoveCalls == 1, "并发申请同轴时接受了多个动作。");
                raceDevice.EndMoveAsync(a.Result ?? b.Result, false, CancellationToken.None).GetAwaiter().GetResult();
            }
            raceDevice.ReleaseMachine(null);

            var readingAxis = new FakeAxis("X") { IgnoresCancellation = true,
                StatusGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously) };
            var readingDevice = CreateDevice(readingAxis, new FakeLogicalIo(), out _, out _);
            readingDevice.ClaimMachine(null);
            Task<ITaskAxisMove> reading = Task.Run(() => readingDevice.BeginMove("X", 5, true, false, Profile()));
            try
            {
                Ensure(readingAxis.StatusEntered.Wait(TimeSpan.FromSeconds(5)), "前置状态读取没有进入。");
                ExpectMessage(() => readingDevice.BeginMove("X", 7, true, false, Profile()), "尚未完成收尾");
                ExpectMessage(() => readingDevice.ReleaseMachine(null), "收尾");
                Ensure(readingAxis.StatusReadCount == 1, "并发同轴动作在预留期间读取了过期位置。");
            }
            finally { readingAxis.StatusGate.TrySetResult(true); }
            Ensure(reading.Wait(TimeSpan.FromSeconds(5)), "前置状态读取未完成。");
            readingDevice.EndMoveAsync(reading.Result, false, CancellationToken.None).GetAwaiter().GetResult();
            readingDevice.ReleaseMachine(null);
        }

        /// <summary>设备只在命令入口之前明确拒绝；入口内抛出任何异常均隔离。</summary>
        private static void PreflightDoesNotMaskStartedMotion()
        {
            CheckPreflight(false);
            CheckPreflight(true);
            var axis = new FakeAxis("X") { IgnoresCancellation = true,
                StartError = new TaskMoveRejectedBeforeCommandException("伪装的前置拒绝", null) };
            var device = CreateDevice(axis, new FakeLogicalIo(), out _, out _);
            device.ClaimMachine(null);
            TaskRun run = new TaskExecutionEngine().Start(MoveTask(false), device);
            var result = WaitRun(run);
            Ensure(result.State == TaskRunState.Failed && axis.MoveCalls == 1 && axis.Moving
                && device.ReadResources().Single().State == TaskResourceState.Blocked, "已进入命令入口的错误被当成安全前置拒绝。");
            axis.StopAsync(AxisStopMode.Controlled, CancellationToken.None).GetAwaiter().GetResult();
            ExpectMessage(() => device.ReleaseMachine(null), "收尾");
        }

        /// <summary>轴未使能时定位及回零都在设备命令调用前拒绝，不隔离空闲资源。</summary>
        private static void CheckPreflight(bool home)
        {
            var axis = new FakeAxis("X") { ServoEnabled = false };
            var device = CreateDevice(axis, new FakeLogicalIo(), out _, out _);
            device.ClaimMachine(null);
            TaskDefinition task = home
                ? new TaskDefinition { TaskId = "HomePreflight", Name = "回零前置", Root = new SequenceNode { Id = "root", Name = "步骤",
                    Children = new List<TaskNode> { new AxisCommandNode { Id = "home", Name = "回零", AxisName = "X", Command = TaskAxisCommand.Home } } } }
                : MoveTask(false);
            var result = WaitRun(new TaskExecutionEngine().Start(task, device));
            Ensure(result.State == TaskRunState.Failed && result.Error.Message.Contains("未使能") && axis.MoveCalls == 0
                && device.ReadResources().Count == 0, "命令未下发却被隔离，或错误发出了命令。");
            device.ReleaseMachine(null);
        }

        /// <summary>接收命令后抛错或丢失凭证，临时轴均须隔离。</summary>
        private static void BeginFailureIsIsolated()
        {
            CheckBeginFailure(1);
            CheckBeginFailure(2);
            CheckBeginFailure(3);
        }

        /// <summary>构造一个已发动作后异常或丢失凭证的设备。</summary>
        private static void CheckBeginFailure(int mode)
        {
            var axis = new FakeAxis("X") { IgnoresCancellation = true };
            var inner = CreateDevice(axis, new FakeLogicalIo(), out _, out _);
            var device = new FaultingDevice(inner) { BeginResult = mode };
            device.ClaimMachine(null);
            TaskRun run = new TaskExecutionEngine().Start(MoveTask(false), device);
            var result = WaitRun(run);
            Ensure(result.State == TaskRunState.Failed && device.ReadResources().Single().State == TaskResourceState.Blocked,
                "已发命令但凭证未知时，轴资源被放行。");
            Ensure(result.Error.Message.Contains(mode == 2 ? "凭证" : "接收故障"), "未保留接收动作错误。");
            axis.StopAsync(AxisStopMode.Controlled, CancellationToken.None).GetAwaiter().GetResult();
            string reason;
            Ensure(!device.TryRecoverResources(device.ReadResources().Single().RequestId, "现场已核实", out reason)
                && reason.Contains("收尾"), "已丢失的动作凭证尚未收尾却允许恢复。");
            ExpectMessage(() => device.ReleaseMachine(null), "收尾");
        }

        /// <summary>停止失败应立即隔离临时轴，也应隔离父范围。</summary>
        private static void EndFailureIsIsolated()
        {
            CheckEndFailure(false);
            CheckEndFailure(true);
        }

        /// <summary>停止失败不释放轴，并保留故障原文。</summary>
        private static void CheckEndFailure(bool parentScope)
        {
            var axis = new FakeAxis("X") { StopError = new InvalidOperationException("停止命令失败"),
                CancellationGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously) };
            var inner = CreateDevice(axis, new FakeLogicalIo(), out _, out _);
            var device = new FaultingDevice(inner);
            device.ClaimMachine(null);
            TaskRun run = new TaskExecutionEngine().Start(MoveTask(parentScope), device);
            WaitFor(() => axis.Moving, "动作没有开始。");
            run.Cancel();
            WaitFor(() => device.ReadResources().Any(item => item.State == TaskResourceState.Blocked), "停止失败没有立即隔离。");
            Ensure(!run.Completion.IsCompleted, "原动作仍会迟到停止，运行却已宣称收尾。");
            string reason;
            Ensure(!device.TryRecoverResources(device.ReadResources().Single().RequestId, "现场已核实", out reason), "原动作仍未结束却允许恢复资源。");
            ExpectMessage(() => device.ReleaseMachine(null), "未收尾");
            axis.CancellationGate.TrySetResult(true);
            var result = WaitRun(run);
            Ensure(result.State == TaskRunState.Failed && result.Error.ToString().Contains("停止命令失败"), "停止故障没有保留。");
            Ensure(device.ReadResources().Single().State == TaskResourceState.Blocked, "停止故障释放了临时轴或父范围。");
            Ensure(!axis.Moving && axis.StopCalls == 2, "未等待假动作的迟到停止完成。");
            axis.StopError = null;
            RecoverAndRelease(device);
        }

        /// <summary>模拟暂停回调取出后迟到；旧回调只能等待旧动作收尾，不能影响新动作。</summary>
        private static void LateStopDoesNotTouchNextMove()
        {
            var axis = new FakeAxis("X") { IgnoresCancellation = true };
            var device = new FaultingDevice(CreateDevice(axis, new FakeLogicalIo(), out _, out _));
            device.ClaimMachine(null);
            TaskRun first = new TaskExecutionEngine().Start(MoveTask(false), device);
            WaitFor(() => axis.Moving, "第一动作未启动。");
            Func<Task> oldCallback = null;
            WaitFor(() => { oldCallback = ReadPauseCallback(first); return oldCallback != null; }, "未登记暂停回调。");
            axis.Complete();
            Ensure(WaitRun(first).State == TaskRunState.Completed, "第一动作未正常完成。");
            Ensure(device.EndCalls == 1, "正常动作没有完成一次收尾。");
            TaskRun second = new TaskExecutionEngine().Start(MoveTask(false), device);
            WaitFor(() => axis.Moving && axis.MoveCalls == 2, "第二动作未启动。");
            oldCallback().GetAwaiter().GetResult();
            oldCallback().GetAwaiter().GetResult();
            Ensure(axis.Moving && axis.StopCalls == 0 && device.EndCalls == 1, "旧动作迟到回调影响了新动作。");
            Ensure(second.Pause(), "第二动作未能暂停。");
            Func<Task> callback = ReadPauseCallback(second);
            Ensure(callback != null, "第二动作缺少暂停回调。");
            device.StopEndGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task stop1 = callback();
            Task stop2 = callback();
            WaitFor(() => device.EndCalls == 2, "暂停收尾未开始。");
            Ensure(ReferenceEquals(stop1, stop2), "同一动作的两个回调未共享收尾任务。");
            device.StopEndGate.TrySetResult(true);
            Task.WhenAll(stop1, stop2).GetAwaiter().GetResult();
            second.Cancel();
            WaitRun(second);
            Ensure(!axis.Moving && axis.StopCalls == 1 && device.EndCalls == 2, "第二动作取消没有单次停轴。");
            device.ReleaseMachine(null);
        }

        /// <summary>实际动作已到位但收尾受阻时，暂停不能提前结束；收尾失败隔离该轴。</summary>
        private static void PauseWaitsForNormalEnd()
        {
            PauseWaitsForNormalEndAsync(false).GetAwaiter().GetResult();
            PauseWaitsForNormalEndAsync(true).GetAwaiter().GetResult();
        }

        /// <summary>控制正常收尾、暂停和失败的交错顺序。</summary>
        private static async Task PauseWaitsForNormalEndAsync(bool fail)
        {
            var axis = new FakeAxis("X") { IgnoresCancellation = true };
            var inner = CreateDevice(axis, new FakeLogicalIo(), out _, out var runtime);
            var device = new FaultingDevice(inner) { NormalEndGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
                EndError = fail ? new InvalidOperationException("到位收尾失败") : null };
            var task = MoveTask(false);
            var machine = new TaskMachineController(new TaskMachineConfiguration { MachineId = "EndCheck", Name = "收尾检查",
                Stations = new List<StationTaskConfiguration> { new StationTaskConfiguration { StationId = "s", Name = "工位", TaskId = task.TaskId,
                    Enabled = true, Mode = StationTaskMode.Once } } }, new[] { task }, device, runtime.AlarmService);
            try
            {
                await machine.ResetAsync(); machine.Start();
                await WaitForAsync(() => axis.Moving, "轴未启动。");
                axis.Complete();
                await WaitForAsync(() => device.EndEntered.IsSet, "正常收尾未开始。");
                Task pause = machine.PauseAsync();
                await Task.Delay(50);
                Ensure(!pause.IsCompleted && machine.GetSnapshot().State == TaskMachineState.Pausing, "正常收尾尚未完成，暂停却已返回。");
                device.NormalEndGate.TrySetResult(true);
                if (fail)
                {
                    try { await pause; throw new InvalidOperationException("收尾失败却报告暂停成功。"); }
                    catch (InvalidOperationException error) { Ensure(error.ToString().Contains("到位收尾失败"), "未保留收尾故障。"); }
                    Ensure(device.ReadResources().Single().State == TaskResourceState.Blocked && device.EndCalls == 1,
                        "暂停和正常收尾没有共享结果或失败未隔离。");
                }
                else
                {
                    await pause;
                    await WaitForAsync(() => device.ReadResources().Count == 0, "成功收尾后资源没有释放。");
                    Ensure(machine.GetSnapshot().State == TaskMachineState.Paused && device.EndCalls == 1 && axis.StopCalls == 0,
                        "已停稳动作的正常收尾没有与暂停共享。");
                }
            }
            finally
            {
                device.NormalEndGate.TrySetResult(true);
                await machine.AbortAsync();
                string reason;
                var blocked = device.ReadResources().FirstOrDefault(item => item.State == TaskResourceState.Blocked);
                if (blocked != null)
                {
                    // 测试替身在真正设备收尾前故意抛错；显式处理其保留凭证后才能解除隔离。
                    await inner.EndMoveAsync(device.LastMove, true, CancellationToken.None);
                    Ensure(device.TryRecoverResources(blocked.RequestId, "检查已确认轴停止", out reason), reason);
                }
                if (fail)
                {
                    bool rejected = false;
                    try { await machine.CloseAsync(); }
                    catch (InvalidOperationException error) { rejected = error.Message.Contains("待确认"); }
                    Ensure(rejected, "停轴收尾曾失败时，即使资源已解除隔离，也不能自动归还控制权。");
                }
                else await machine.CloseAsync();
            }
        }

        /// <summary>构造带或不带父范围的单轴任务。</summary>
        private static TaskDefinition MoveTask(bool parentScope)
        {
            TaskNode move = new MoveAxisNode { Id = "move", Name = "轴定位", AxisName = "X", Mode = TaskMoveMode.Absolute,
                Target = new TaskValueBinding { Source = TaskValueSource.Constant, DataType = DeviceVariableType.Number, Constant = "10" },
                Profile = new TaskMotionProfile { Speed = 10, Acceleration = 100, Deceleration = 100 } };
            if (parentScope) move = new ResourceScopeNode { Id = "scope", Name = "工作范围", Resources = new List<string> { "Axis:X" },
                Body = new SequenceNode { Id = "body", Name = "范围内", Children = new List<TaskNode> { move } } };
            return new TaskDefinition { TaskId = "StopCheck", Name = "停止检查", Root = new SequenceNode { Id = "root", Name = "动作",
                Children = new List<TaskNode> { move } } };
        }

        /// <summary>从运行对象锁内取出已经登记的回调，模拟实际调度被延迟。</summary>
        private static Func<Task> ReadPauseCallback(TaskRun run)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            object sync = typeof(TaskRun).GetField("_sync", flags).GetValue(run);
            lock (sync)
                return ((List<Func<Task>>)typeof(TaskRun).GetField("_pauseStopActions", flags).GetValue(run)).FirstOrDefault();
        }

        /// <summary>等待后台条件，避免故障检查卡死。</summary>
        private static void WaitFor(Func<bool> condition, string failure) => WaitForAsync(condition, failure).GetAwaiter().GetResult();
        /// <summary>异步等待后台条件，最多五秒。</summary>
        private static async Task WaitForAsync(Func<bool> condition, string failure)
        {
            var watch = Stopwatch.StartNew();
            while (!condition()) { if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException(failure); await Task.Delay(10); }
        }
        /// <summary>等待独立运行退出。</summary>
        private static TaskRunSnapshot WaitRun(TaskRun run)
        {
            Ensure(run.Completion.Wait(TimeSpan.FromSeconds(5)), "运行未结束。");
            return run.Completion.GetAwaiter().GetResult();
        }
        /// <summary>假设备人工确认停止后解除隔离并归还控制权。</summary>
        private static void RecoverAndRelease(FaultingDevice device)
        {
            string reason;
            var blocked = device.ReadResources().Single();
            Ensure(device.TryRecoverResources(blocked.RequestId, "检查已确认轴停止", out reason), reason);
            device.ReleaseMachine(null);
        }

        /// <summary>为实际设备端口注入明确的接收和收尾异常，仅用于内部并发检查。</summary>
        private sealed class FaultingDevice : ITaskDevice
        {
            /// <summary>真实设备适配器的假硬件实例。</summary>
            private readonly ITaskDevice _inner;
            /// <summary>0 正常、1 发命令后抛错、2 返回空凭证、3 发命令后包装故障。</summary>
            internal int BeginResult;
            /// <summary>保留已接受凭证，供测试中的维护收尾使用。</summary>
            internal ITaskAxisMove LastMove;
            /// <summary>可注入的收尾失败。</summary>
            internal Exception EndError;
            /// <summary>控制正常收尾完成时机。</summary>
            internal TaskCompletionSource<bool> NormalEndGate;
            /// <summary>控制停止收尾完成时机。</summary>
            internal TaskCompletionSource<bool> StopEndGate;
            /// <summary>通知正常收尾已进入。</summary>
            internal readonly ManualResetEventSlim EndEntered = new ManualResetEventSlim(false);
            /// <summary>记录实际发起的收尾次数。</summary>
            internal int EndCalls;
            /// <summary>绑定现有设备。</summary>
            internal FaultingDevice(ITaskDevice inner) { _inner = inner; }
            /// <summary>资源目录。</summary>
            public TaskResourceCatalog Catalog => _inner.Catalog;
            /// <summary>共享资源协调器。</summary>
            public TaskResourceCoordinator Resources => _inner.Resources;
            /// <summary>读取输入。</summary>
            public bool? ReadInput(string name) => _inner.ReadInput(name);
            /// <summary>读取输出。</summary>
            public bool? ReadOutput(string name) => _inner.ReadOutput(name);
            /// <summary>写入输出。</summary>
            public void WriteOutput(string name, bool value) => _inner.WriteOutput(name, value);
            /// <summary>写入变量。</summary>
            public void WriteVariable(string name, object value) => _inner.WriteVariable(name, value);
            /// <summary>读取变量。</summary>
            public IReadOnlyList<DeviceVariableSnapshot> ReadVariables() => _inner.ReadVariables();
            /// <summary>读取轴状态。</summary>
            public TaskAxisState ReadAxis(string name) => _inner.ReadAxis(name);
            /// <summary>检查互锁。</summary>
            public bool CheckInterlock(TaskInterlock i, TaskCondition c, out string reason) => _inner.CheckInterlock(i, c, out reason);
            /// <summary>读取资源。</summary>
            public IReadOnlyList<TaskResourceSnapshot> ReadResources() => _inner.ReadResources();
            /// <summary>恢复资源。</summary>
            public bool TryRecoverResources(string id, string confirmation, out string reason) => _inner.TryRecoverResources(id, confirmation, out reason);
            /// <summary>注入接收动作后异常或空凭证。</summary>
            public ITaskAxisMove BeginMove(string name, double target, bool relative, bool home, AxisMotionProfile profile)
            {
                var move = _inner.BeginMove(name, target, relative, home, profile);
                LastMove = move;
                if (BeginResult == 1) throw new InvalidOperationException("接收故障");
                if (BeginResult == 3) throw new InvalidOperationException("接收故障（包装）", new TaskMoveRejectedBeforeCommandException("内层前置错误", null));
                return BeginResult == 2 ? null : move;
            }
            /// <summary>推进动作。</summary>
            public void AdvanceMove(ITaskAxisMove move, double progress) => _inner.AdvanceMove(move, progress);
            /// <summary>读取动作完成。</summary>
            public bool IsMoveComplete(ITaskAxisMove move, double progress) => _inner.IsMoveComplete(move, progress);
            /// <summary>控制收尾时机并注入错误。</summary>
            public async Task EndMoveAsync(ITaskAxisMove move, bool completed, CancellationToken token)
            {
                Interlocked.Increment(ref EndCalls);
                if (completed && NormalEndGate != null) { EndEntered.Set(); await NormalEndGate.Task; }
                if (!completed && StopEndGate != null) await StopEndGate.Task;
                if (EndError != null) throw EndError;
                await _inner.EndMoveAsync(move, completed, token);
            }
            /// <summary>接受轴组动作。</summary>
            public IReadOnlyList<ITaskAxisGroupMove> BeginGroupMove(IReadOnlyList<TaskAxisGroupTarget> targets) => _inner.BeginGroupMove(targets);
            /// <summary>推进轴组。</summary>
            public bool AdvanceGroupMove(IReadOnlyList<ITaskAxisGroupMove> moves, double elapsed, out string detail) => _inner.AdvanceGroupMove(moves, elapsed, out detail);
            /// <summary>结束轴组。</summary>
            public Task EndGroupMoveAsync(IReadOnlyList<ITaskAxisGroupMove> moves, CancellationToken token) => _inner.EndGroupMoveAsync(moves, token);
            /// <summary>确认轴停止。</summary>
            public Task ConfirmStoppedAsync(string name, CancellationToken token) => _inner.ConfirmStoppedAsync(name, token);
            /// <summary>读取握手。</summary>
            public void ReadHandshake(string request, string complete, out bool? r, out bool? c) => _inner.ReadHandshake(request, complete, out r, out c);
            /// <summary>发布完成。</summary>
            public bool TryPublishCompletion(string request, string complete) => _inner.TryPublishCompletion(request, complete);
            /// <summary>清除完成。</summary>
            public bool TryClearCompletion(string request, string complete) => _inner.TryClearCompletion(request, complete);
            /// <summary>申请控制权。</summary>
            public void ClaimMachine(object owner) => _inner.ClaimMachine(owner);
            /// <summary>归还控制权。</summary>
            public void ReleaseMachine(object owner) => _inner.ReleaseMachine(owner);
            /// <summary>登记运行。</summary>
            public void RegisterRun(string id, object owner) => _inner.RegisterRun(id, owner);
            /// <summary>注销运行。</summary>
            public void UnregisterRun(string id) => _inner.UnregisterRun(id);
        }
    }
}
