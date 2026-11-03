using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Contracts.Preparation;
using MotionControl.Framework.Runtime.Alarms;
using MotionControl.Framework.Runtime.Machine;

namespace MotionControl.ZMotionSimulatorCheck
{
    internal static partial class RuntimeRegressionChecks
    {
        /// <summary>取消回调阻塞时仍先发停轴，结束前保持准备占用。</summary>
        private static async Task PreparationCancellationDoesNotBlockStopAsync()
        {
            var axis = new PreparationStopAxis();
            var preparation = new BlockingPreparationCancellation();
            var runtime = new MachineRuntime(new MotionCardGroup(new IMotionCard[] { new FakeMotionCard("CardA", axis) }),
                new IStationFlow[] { new WaitingStationFlow("工位", "等待") }, new MachineAlarmService(), new FakeSafetyStatus(),
                TimeSpan.FromMilliseconds(250));
            runtime.ConfigurePreparations(new IStationPreparation[] { preparation });
            await runtime.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
            Task running = runtime.PrepareAsync(null, CancellationToken.None);
            try
            {
                Ensure(await Task.WhenAny(preparation.Started.Task, Task.Delay(3000)).ConfigureAwait(false) == preparation.Started.Task, "准备未启动。");
                Task stopping = runtime.StopPreparationAsync(CancellationToken.None);
                Ensure(await Task.WhenAny(preparation.CallbackStarted.Task, Task.Delay(3000)).ConfigureAwait(false) == preparation.CallbackStarted.Task, "取消回调未启动。");
                Ensure(await Task.WhenAny(axis.Stopped.Task, Task.Delay(3000)).ConfigureAwait(false) == axis.Stopped.Task, "取消回调阻塞了停轴。");
                Ensure(await Task.WhenAny(stopping, Task.Delay(3000)).ConfigureAwait(false) == stopping, "取消回调阻塞了停止超时。");
                await ExpectExceptionAsync<TimeoutException>(() => stopping).ConfigureAwait(false);
                Ensure(runtime.IsPreparing, "取消回调尚未结束时错误地释放设备。");
                await ExpectExceptionAsync<InvalidOperationException>(() => runtime.PrepareAsync(null, CancellationToken.None)).ConfigureAwait(false);
                preparation.Release.Set();
                try { await running.ConfigureAwait(false); } catch (OperationCanceledException) { }
                await runtime.StopPreparationAsync(CancellationToken.None).ConfigureAwait(false);
                Ensure(!runtime.IsPreparing, "取消和停轴确认完成后未释放设备。");
            }
            finally { preparation.Release.Set(); }
        }

        /// <summary>用受控回调模拟设备准备的取消处理迟迟不返回。</summary>
        private sealed class BlockingPreparationCancellation : IStationPreparation
        {
            /// <summary>准备动作已开始的通知。</summary>
            internal readonly TaskCompletionSource<bool> Started = new TaskCompletionSource<bool>();
            /// <summary>取消回调已开始的通知。</summary>
            internal readonly TaskCompletionSource<bool> CallbackStarted = new TaskCompletionSource<bool>();
            /// <summary>测试结束阻塞的信号。</summary>
            internal readonly ManualResetEventSlim Release = new ManualResetEventSlim(false);
            /// <summary>测试工位标识。</summary>
            public string StationId => "P";
            /// <summary>测试工位名称。</summary>
            public string StationName => "准备工位";
            /// <summary>测试没有其他准备依赖。</summary>
            public IReadOnlyCollection<string> Dependencies => new string[0];
            /// <summary>开始前检查调用方取消状态。</summary>
            public Task ValidateStartAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
            /// <summary>完成前检查调用方取消状态。</summary>
            public Task ValidateReadyAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
            /// <summary>等待取消，并模拟被测试控制的慢取消回调。</summary>
            public async Task ExecuteAsync(IProgress<StationPreparationProgress> progress, CancellationToken token)
            {
                using (token.Register(() => { CallbackStarted.TrySetResult(true); Release.Wait(); }))
                {
                    Started.TrySetResult(true);
                    await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                }
            }
        }

        /// <summary>复用已有假轴并通知测试何时发出了停轴。</summary>
        private sealed class PreparationStopAxis : IAxis
        {
            /// <summary>提供基础轴反馈和模拟动作。</summary>
            private readonly FakeAxis _axis = new FakeAxis("X");
            /// <summary>停轴命令已送达的通知。</summary>
            internal readonly TaskCompletionSource<bool> Stopped = new TaskCompletionSource<bool>();
            /// <summary>返回逻辑轴名称。</summary>
            public string Name => _axis.Name;
            /// <summary>读取模拟轴状态。</summary>
            public Task<AxisStatus> ReadStatusAsync(CancellationToken t) => _axis.ReadStatusAsync(t);
            /// <summary>使能模拟轴。</summary>
            public Task EnableServoAsync(CancellationToken t) => _axis.EnableServoAsync(t);
            /// <summary>关闭模拟轴使能。</summary>
            public Task DisableServoAsync(CancellationToken t) => _axis.DisableServoAsync(t);
            /// <summary>执行模拟回零。</summary>
            public Task HomeAsync(CancellationToken t) => _axis.HomeAsync(t);
            /// <summary>执行模拟绝对定位。</summary>
            public Task MoveAbsoluteAsync(double p, AxisMotionProfile profile, CancellationToken t) => _axis.MoveAbsoluteAsync(p, profile, t);
            /// <summary>执行模拟相对定位。</summary>
            public Task MoveRelativeAsync(double p, AxisMotionProfile profile, CancellationToken t) => _axis.MoveRelativeAsync(p, profile, t);
            /// <summary>执行模拟点动。</summary>
            public Task StartJogAsync(MotionDirection d, AxisMotionProfile profile, CancellationToken t) => _axis.StartJogAsync(d, profile, t);
            /// <summary>记录停轴已发送并停止模拟轴。</summary>
            public Task StopAsync(AxisStopMode mode, CancellationToken t) { Stopped.TrySetResult(true); return _axis.StopAsync(mode, t); }
            /// <summary>清除模拟轴报警。</summary>
            public Task ResetAlarmAsync(CancellationToken t) => _axis.ResetAlarmAsync(t);
        }
    }
}
