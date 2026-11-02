using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
            try { RunAsync().GetAwaiter().GetResult(); Console.WriteLine("设备准备：8 组检查通过（仅内部逻辑检查）。"); return 0; }
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
            fixture.Runtime.ConfigurePreparations(new[] { new Preparation("Slow", fixture) { WaitUntilStopped = true } });
            await fixture.ConnectAsync();
            Task run = fixture.Runtime.PrepareAsync(null, CancellationToken.None);
            await WaitAsync(() => fixture.Axis.Moving);
            fixture.Axis.StopFails = true;
            await RejectAsync(() => fixture.Runtime.StopPreparationAsync(CancellationToken.None), "停轴错误被忽略");
            await RejectAsync(() => run, "停止失败未反映到准备结果");
            Ensure(fixture.Runtime.IsPreparing && !fixture.Runtime.IsPrepared, "停止未确认却归还了设备占用");
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
            internal Fixture() { Runtime = new MachineRuntime(new MotionCardGroup(new[] { new Card(Axis) }), new[] { Flow },
                new MachineAlarmService(), Safety, pauseTimeout: TimeSpan.FromSeconds(1), safetyPollInterval: TimeSpan.FromMilliseconds(10)); }
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
            { StopCount++; if (StopFails) throw new InvalidOperationException("测试停止失败"); Moving = false; return Task.CompletedTask; }
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
