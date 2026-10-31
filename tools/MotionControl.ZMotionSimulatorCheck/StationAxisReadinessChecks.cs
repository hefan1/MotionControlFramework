using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Contracts.Safety;
using MotionControl.Framework.Runtime.Alarms;
using MotionControl.Framework.Runtime.Machine;

namespace MotionControl.ZMotionSimulatorCheck
{
    /// <summary>验证工位声明的回零要求会阻止启动，并且不会误拦截未使用轴。</summary>
    internal static class StationAxisReadinessChecks
    {
        /// <summary>运行工位轴回零要求检查并返回失败原因。</summary>
        internal static async Task<IReadOnlyList<string>> RunAsync()
        {
            var failures = new List<string>();
            try
            {
                await AllRequiredStationAxesMustBeHomedBeforeStartAsync().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                failures.Add("工位轴回零启动检查失败：" + error.Message);
            }

            return failures.AsReadOnly();
        }

        /// <summary>一次列出所有未回零工位轴；逐根回零后才允许并行工位启动。</summary>
        private static async Task AllRequiredStationAxesMustBeHomedBeforeStartAsync()
        {
            var loadingAxis = new ReadinessAxis("Load_X", false);
            var pressAxis = new ReadinessAxis("Press_Z", false);
            var unusedAxis = new ReadinessAxis("Spare_X", false);
            var loadingFlow = new ReadinessStationFlow("上料定位工位", "Load_X");
            var pressFlow = new ReadinessStationFlow("压装工位", "Press_Z");
            // 物理轴顺序故意与工位流程顺序相反，避免启动原因按卡轴号乱序显示。
            var card = new ReadinessMotionCard("ReadinessCard", new IAxis[] { pressAxis, loadingAxis, unusedAxis });
            var runtime = new MachineRuntime(
                new MotionCardGroup(new[] { card }),
                new IStationFlow[] { loadingFlow, pressFlow },
                new MachineAlarmService(),
                new ReadinessSafetyStatus());

            try
            {
                await runtime.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
                await runtime.ResetAsync(CancellationToken.None).ConfigureAwait(false);

                InvalidOperationException blocked = await CaptureInvalidOperationAsync(
                    () => runtime.StartAsync(CancellationToken.None)).ConfigureAwait(false);
                Ensure(blocked.Message.Contains("Load_X"), "拦截原因没有指出未回零逻辑轴。");
                Ensure(blocked.Message.Contains("Press_Z"), "拦截原因没有一次列出全部未回零轴。");
                Ensure(blocked.Message.Contains("上料定位工位") && blocked.Message.Contains("压装工位"),
                    "拦截原因没有列出全部受影响工位。");
                Ensure(blocked.Message.IndexOf("Load_X", StringComparison.Ordinal)
                        < blocked.Message.IndexOf("Press_Z", StringComparison.Ordinal),
                    "启动拦截原因没有按设备工位的显示顺序排列逻辑轴。");
                Ensure(blocked.Message.Contains("请在轴手动页面逐个完成"),
                    "没有设备初始化流程时仍给出了无法执行的处理建议。");
                Ensure(runtime.State == MachineRunState.Ready, "未回零拒绝启动后错误地把设备锁存为整机报警。");
                Ensure(loadingFlow.StartCount == 0 && pressFlow.StartCount == 0, "未回零时工位仍然启动。");
                string readinessReason = await runtime
                    .GetAutomaticStartBlockReasonAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                Ensure(!string.IsNullOrWhiteSpace(readinessReason)
                    && readinessReason.Contains("Load_X")
                    && readinessReason.Contains("Press_Z")
                    && readinessReason.Contains("上料定位工位")
                    && readinessReason.Contains("压装工位")
                    && readinessReason.IndexOf("Load_X", StringComparison.Ordinal)
                        < readinessReason.IndexOf("Press_Z", StringComparison.Ordinal),
                    "总览启动就绪查询没有列出全部未回零轴和受影响工位。");

                loadingAxis.SetHomed(true);
                string remainingReason = await runtime
                    .GetAutomaticStartBlockReasonAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                Ensure(!string.IsNullOrWhiteSpace(remainingReason)
                    && remainingReason.Contains("Press_Z")
                    && !remainingReason.Contains("Load_X"),
                    "回零一根轴后没有只保留剩余工位轴的启动拦截原因。");

                pressAxis.SetHomed(true);
                string readyReason = await runtime
                    .GetAutomaticStartBlockReasonAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                Ensure(string.IsNullOrWhiteSpace(readyReason), "全部所需轴回零后总览仍报告启动受阻。");
                await runtime.StartAsync(CancellationToken.None).ConfigureAwait(false);
                await Task.WhenAll(loadingFlow.WaitUntilStartedAsync(), pressFlow.WaitUntilStartedAsync())
                    .ConfigureAwait(false);
                Ensure(runtime.State == MachineRunState.Running, "全部所需轴回零后没有允许并行工位启动。");
                Ensure(!unusedAxis.IsHomed, "测试前置条件错误：未使用轴应保持未回零。");

                await runtime.PauseAsync(CancellationToken.None).ConfigureAwait(false);
                Ensure(runtime.State == MachineRunState.Paused, "回零检查结束后无法安全暂停测试流程。");
            }
            finally
            {
                if (runtime.State == MachineRunState.Running || runtime.State == MachineRunState.Pausing)
                {
                    await runtime.PauseAsync(CancellationToken.None).ConfigureAwait(false);
                }

                if (runtime.State != MachineRunState.Disconnected)
                {
                    await runtime.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
        }

        /// <summary>等待流程报告启动，避免仅凭 StartAsync 返回就假定后台工位已运行。</summary>
        private static async Task WaitForStartAsync(Task started)
        {
            Task completed = await Task.WhenAny(started, Task.Delay(TimeSpan.FromSeconds(3))).ConfigureAwait(false);
            if (completed != started)
            {
                throw new TimeoutException("等待自动工位进入首步骤超时。");
            }

            await started.ConfigureAwait(false);
        }

        /// <summary>捕获启动时的明确拦截异常，未抛出时让回归检查失败。</summary>
        private static async Task<InvalidOperationException> CaptureInvalidOperationAsync(Func<Task> action)
        {
            try
            {
                await action().ConfigureAwait(false);
            }
            catch (InvalidOperationException error)
            {
                return error;
            }

            throw new InvalidOperationException("未回零的工位轴未能拦截自动启动。");
        }

        /// <summary>断言指定条件成立，否则让检查输出可读原因。</summary>
        private static void Ensure(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        /// <summary>提供可控回零状态的测试逻辑轴。</summary>
        private sealed class ReadinessAxis : IAxis
        {
            /// <summary>创建指定逻辑名称和回零状态的轴替身。</summary>
            internal ReadinessAxis(string name, bool homed)
            {
                Name = name;
                IsHomed = homed;
            }

            /// <summary>轴在设备流程中的逻辑名称。</summary>
            public string Name { get; private set; }

            /// <summary>轴当前是否已通过测试回零。</summary>
            public bool IsHomed { get; private set; }

            /// <summary>设置测试轴回零状态。</summary>
            internal void SetHomed(bool homed)
            {
                IsHomed = homed;
            }

            /// <summary>返回无报警、停稳且限位已知的测试状态。</summary>
            public Task<AxisStatus> ReadStatusAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(new AxisStatus(0, 0, true, false, IsHomed,
                    false, false, false, false, false, string.Empty, false));
            }

            /// <summary>测试替身接受轴使能请求。</summary>
            public Task EnableServoAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }

            /// <summary>测试替身接受轴失能请求。</summary>
            public Task DisableServoAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }

            /// <summary>模拟完成回零。</summary>
            public Task HomeAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); IsHomed = true; return Task.CompletedTask; }

            /// <summary>测试替身接受绝对定位请求。</summary>
            public Task MoveAbsoluteAsync(double position, AxisMotionProfile profile, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }

            /// <summary>测试替身接受相对定位请求。</summary>
            public Task MoveRelativeAsync(double distance, AxisMotionProfile profile, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }

            /// <summary>测试替身接受连续点动请求。</summary>
            public Task StartJogAsync(MotionDirection direction, AxisMotionProfile profile, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }

            /// <summary>测试替身接受停止请求。</summary>
            public Task StopAsync(AxisStopMode stopMode, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }

            /// <summary>测试替身接受报警复位请求。</summary>
            public Task ResetAlarmAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        }

        /// <summary>提供需要明确回零轴的暂停等待工位。</summary>
        private sealed class ReadinessStationFlow : IStationFlow, IStationAxisRequirements
        {
            /// <summary>通知后台工位已进入当前测试步骤。</summary>
            private readonly TaskCompletionSource<bool> _started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            /// <summary>创建工位及需要回零的逻辑轴要求。</summary>
            internal ReadinessStationFlow(string stationName, string requiredAxis)
            {
                StationName = stationName;
                RequiredHomedAxes = new[] { requiredAxis };
            }

            /// <summary>测试工位的显示名称。</summary>
            public string StationName { get; private set; }

            /// <summary>当前测试等待步骤。</summary>
            public string CurrentStepName { get { return "等待回归检查"; } }

            /// <summary>本工位声明的回零轴。</summary>
            public IReadOnlyCollection<string> RequiredHomedAxes { get; private set; }

            /// <summary>记录工位启动并等待 Runtime 受控暂停。</summary>
            public async Task ExecuteCurrentStepAsync(CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref StartCount);
                _started.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }

            /// <summary>模拟工位复位，不改变当前步骤。</summary>
            public Task ResetAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }

            /// <summary>工位执行次数，供回归检查确认拦截和放行结果。</summary>
            internal int StartCount;

            /// <summary>等待流程确实进入后台首步骤。</summary>
            internal Task WaitUntilStartedAsync() { return WaitForStartAsync(_started.Task); }
        }

        /// <summary>为 Runtime 提供已经配置且当前未触发的测试急停状态。</summary>
        private sealed class ReadinessSafetyStatus : IConfigurableMachineSafetyStatus
        {
            /// <summary>测试用急停来源始终可读取。</summary>
            public bool IsConfigured { get { return true; } }

            /// <summary>报告急停当前处于释放状态。</summary>
            public Task<bool> ReadEmergencyStopActiveAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(false);
            }
        }

        /// <summary>提供测试用逻辑轴集合的运动卡。</summary>
        private sealed class ReadinessMotionCard : IMotionCard
        {
            /// <summary>控制卡包含的逻辑轴。</summary>
            private readonly IReadOnlyCollection<IAxis> _axes;

            /// <summary>创建拥有指定逻辑轴的运动卡替身。</summary>
            internal ReadinessMotionCard(string controllerId, IEnumerable<IAxis> axes)
            {
                ControllerId = controllerId;
                _axes = axes.ToList().AsReadOnly();
            }

            /// <summary>返回测试控制卡稳定标识。</summary>
            public string ControllerId { get; private set; }

            /// <summary>报告测试控制卡连接状态。</summary>
            public bool IsConnected { get; private set; }

            /// <summary>返回本卡测试逻辑轴。</summary>
            public IReadOnlyCollection<IAxis> Axes { get { return _axes; } }

            /// <summary>模拟建立控制卡连接。</summary>
            public Task ConnectAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); IsConnected = true; return Task.CompletedTask; }

            /// <summary>模拟断开控制卡连接。</summary>
            public Task DisconnectAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); IsConnected = false; return Task.CompletedTask; }
        }
    }
}
