using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Equipment.Demo;
using MotionControl.Framework.Contracts.IO;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Contracts.Safety;
using MotionControl.Framework.Runtime.Alarms;
using MotionControl.Framework.Runtime.Machine;

namespace MotionControl.ZMotionSimulatorCheck
{
    /// <summary>用同一 Runtime 和逻辑 IO 替身验证示例工位的正常、超时与取消路径。</summary>
    internal static class DemoStationFlowChecks
    {
        /// <summary>依次执行闭环检查；仅在内存中模拟轴和 IO，不发送厂商 SDK 指令。</summary>
        public static async Task RunAsync()
        {
            await SuccessfulCycleUsesMappedResourcesAsync().ConfigureAwait(false);
            await TimeoutRaisesAlarmAndCanRetryAsync().ConfigureAwait(false);
            await PausingOutputTurnsItOffAsync().ConfigureAwait(false);
        }

        /// <summary>确认轴目标、传感器 DI、输出 DO 和手动联锁都使用同一运行时。</summary>
        private static async Task SuccessfulCycleUsesMappedResourcesAsync()
        {
            var context = CreateContext(500, 60);
            await context.Runtime.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
            await context.Runtime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
            await context.Runtime.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await WaitUntilAsync(() => context.Axis.MoveCount == 1
                && context.Flow.CurrentStepName.Contains("等待传感器"), "轴定位完成后等待传感器").ConfigureAwait(false);

            Ensure(context.Axis.LastTargetPosition == 7.5, "工位没有以工程单位调用逻辑轴定位。");
            Ensure(context.Io.LastReadInputName == "PartReady", "工位没有读取配置的逻辑 DI。");
            Ensure(await context.Runtime.GetManualOperationBlockReasonAsync(CancellationToken.None)
                .ConfigureAwait(false) == ManualOperationBlockReason.AutomaticFlowActive,
                "自动工位运行期间没有阻止轴手动与 IO 手动操作。");
            Ensure(!context.Io.OutputValues.Any(), "传感器有效前提前写入了 DO。");

            context.Io.SensorActive = true;
            await WaitUntilAsync(() => context.Io.OutputValues.Count >= 2,
                "传感器有效后输出脉冲打开并关闭").ConfigureAwait(false);
            Ensure(context.Io.OutputValues.SequenceEqual(new[] { true, false }),
                "输出顺序不是逻辑 DO 打开后关闭。");
            Ensure(context.Io.LastWrittenOutputName == "ActionOutput", "工位没有写入配置的逻辑 DO。");
            Ensure(context.Runtime.AlarmService.Alarms.Count == 0, "正常循环产生了报警。");

            await context.Runtime.PauseAsync(CancellationToken.None).ConfigureAwait(false);
            Ensure(context.Runtime.State == MachineRunState.Paused, "正常流程暂停后整机状态不正确。");
            await context.Runtime.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>确认 DI 一直无效时停机报警锁存、输出不动作，并可从等待步骤复位重试。</summary>
        private static async Task TimeoutRaisesAlarmAndCanRetryAsync()
        {
            var context = CreateContext(70, 40);
            await context.Runtime.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
            await context.Runtime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
            await context.Runtime.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await WaitUntilAsync(() => context.Runtime.State == MachineRunState.Alarmed,
                "等待传感器超时后进入报警").ConfigureAwait(false);

            var alarm = context.Runtime.AlarmService.Alarms.SingleOrDefault();
            Ensure(alarm != null && alarm.Definition.Code == "DEMO_SENSOR_TIMEOUT"
                && alarm.SourceName == "流程验证工位" && !alarm.ConditionActive,
                "超时未产生可复位的工位停机报警。");
            Ensure(context.Flow.CurrentStepName.Contains("等待传感器"), "超时后工位没有保留等待步骤。");
            Ensure(context.Io.OutputValues.Count == 0, "超时后仍写入了输出。");

            await context.Runtime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
            Ensure(context.Runtime.AlarmService.Alarms.Count == 0, "复位后未清除已结束的超时报警。");
            context.Io.SensorActive = true;
            await context.Runtime.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await WaitUntilAsync(() => context.Io.OutputValues.Count >= 2,
                "复位后从传感器等待步骤继续输出").ConfigureAwait(false);
            Ensure(context.Axis.MoveCount == 1, "超时复位后错误地重新执行了已完成定位。");

            await context.Runtime.PauseAsync(CancellationToken.None).ConfigureAwait(false);
            await context.Runtime.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>确认输出保持期间暂停会先关闭 DO，当前输出步骤留待复位后重试。</summary>
        private static async Task PausingOutputTurnsItOffAsync()
        {
            var context = CreateContext(500, 500);
            context.Io.SensorActive = true;
            await context.Runtime.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
            await context.Runtime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
            await context.Runtime.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await WaitUntilAsync(() => context.Io.OutputValues.Contains(true), "输出脉冲打开").ConfigureAwait(false);

            await context.Runtime.PauseAsync(CancellationToken.None).ConfigureAwait(false);
            Ensure(context.Io.OutputValues.Last() == false, "暂停后示例工位没有关闭 DO。");
            Ensure(context.Flow.CurrentStepName.Contains("触发输出"), "输出期间暂停没有保留当前步骤。");
            await context.Runtime.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>建立共用逻辑轴、逻辑 IO 与报警服务的最小设备上下文。</summary>
        private static DemoContext CreateContext(int sensorTimeoutMilliseconds, int outputPulseMilliseconds)
        {
            var axis = new DemoAxis("AxisX");
            var io = new DemoIo();
            var alarmService = new MachineAlarmService();
            var settings = new DemoStationSettings
            {
                Enabled = true,
                AxisName = "AxisX",
                SensorInputName = "PartReady",
                OutputName = "ActionOutput",
                TargetPosition = 7.5,
                Speed = 2,
                Acceleration = 5,
                Deceleration = 5,
                SensorTimeoutMilliseconds = sensorTimeoutMilliseconds,
                SensorPollMilliseconds = 10,
                OutputPulseMilliseconds = outputPulseMilliseconds
            };
            var flow = new DemoStationFlow(axis, io, alarmService, settings);
            var runtime = new MachineRuntime(
                new MotionCardGroup(new IMotionCard[] { new DemoCard(axis) }),
                new[] { flow },
                alarmService,
                new DemoSafetyStatus(),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMilliseconds(10));
            return new DemoContext(axis, io, flow, runtime);
        }

        /// <summary>有界等待后台调度到达目标状态，避免检查无限挂起。</summary>
        private static async Task WaitUntilAsync(Func<bool> condition, string operationName)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(3);
            while (!condition() && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10).ConfigureAwait(false);
            }

            Ensure(condition(), "等待 " + operationName + " 超时。");
        }

        /// <summary>报告可以直接定位的闭环检查失败。</summary>
        private static void Ensure(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        /// <summary>打包本次检查共享的轴、IO、工位和 Runtime 引用。</summary>
        private sealed class DemoContext
        {
            /// <summary>保存一个检查场景的全部对象。</summary>
            public DemoContext(DemoAxis axis, DemoIo io, DemoStationFlow flow, MachineRuntime runtime)
            {
                Axis = axis;
                Io = io;
                Flow = flow;
                Runtime = runtime;
            }

            /// <summary>记录定位指令的内存轴。</summary>
            public DemoAxis Axis { get; private set; }

            /// <summary>记录传感器和输出的内存 IO。</summary>
            public DemoIo Io { get; private set; }

            /// <summary>实际运行的设备示例工位。</summary>
            public DemoStationFlow Flow { get; private set; }

            /// <summary>被调试页面与工位共同使用的运行时。</summary>
            public MachineRuntime Runtime { get; private set; }
        }

        /// <summary>仅在内存中记录定位位置的逻辑轴替身。</summary>
        private sealed class DemoAxis : IAxis
        {
            /// <summary>建立指定名称的逻辑轴。</summary>
            public DemoAxis(string name) { Name = name; }

            /// <summary>逻辑轴名称。</summary>
            public string Name { get; private set; }

            /// <summary>已经接收的定位次数。</summary>
            public int MoveCount { get; private set; }

            /// <summary>最近一次定位的工程单位目标。</summary>
            public double LastTargetPosition { get; private set; }

            /// <summary>返回可启动自动流程的停稳、已回零及无报警状态。</summary>
            public Task<AxisStatus> ReadStatusAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(new AxisStatus(
                    LastTargetPosition, 0, true, false, true, false, false,
                    false, false, false, null, false));
            }

            /// <summary>模拟伺服使能。</summary>
            public Task EnableServoAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }

            /// <summary>模拟伺服关闭。</summary>
            public Task DisableServoAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }

            /// <summary>模拟回零完成。</summary>
            public Task HomeAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }

            /// <summary>记录绝对定位命令，不触发真实控制卡。</summary>
            public Task MoveAbsoluteAsync(double position, AxisMotionProfile profile, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                LastTargetPosition = position;
                MoveCount++;
                return Task.CompletedTask;
            }

            /// <summary>本场景不使用相对定位。</summary>
            public Task MoveRelativeAsync(double distance, AxisMotionProfile profile, CancellationToken cancellationToken)
            {
                throw new NotSupportedException("闭环示例只使用绝对定位。");
            }

            /// <summary>本场景不使用连续点动。</summary>
            public Task StartJogAsync(MotionDirection direction, AxisMotionProfile profile, CancellationToken cancellationToken)
            {
                throw new NotSupportedException("闭环示例不使用点动。");
            }

            /// <summary>内存轴没有持续运动，停止请求可立即完成。</summary>
            public Task StopAsync(AxisStopMode stopMode, CancellationToken cancellationToken) { return Task.CompletedTask; }

            /// <summary>内存轴没有报警，复位请求可立即完成。</summary>
            public Task ResetAlarmAsync(CancellationToken cancellationToken) { return Task.CompletedTask; }
        }

        /// <summary>记录逻辑传感器状态和 DO 写入顺序的内存 IO。</summary>
        private sealed class DemoIo : ILogicalIo
        {
            /// <summary>保护后台工位写入的输出记录。</summary>
            private readonly object _sync = new object();

            /// <summary>保存输出有效状态变化。</summary>
            private readonly List<bool> _outputValues = new List<bool>();

            /// <summary>模拟传感器当前逻辑有效状态。</summary>
            public bool SensorActive { get; set; }

            /// <summary>最近读取的逻辑输入名称。</summary>
            public string LastReadInputName { get; private set; }

            /// <summary>最近写入的逻辑输出名称。</summary>
            public string LastWrittenOutputName { get; private set; }

            /// <summary>返回不可变快照，供后台检查输出开关顺序。</summary>
            public IReadOnlyList<bool> OutputValues
            {
                get { lock (_sync) { return _outputValues.ToArray(); } }
            }

            /// <summary>读取配置的模拟传感器，错误逻辑名称会明确失败。</summary>
            public Task<bool> ReadInputAsync(string inputName, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (inputName != "PartReady") throw new InvalidOperationException("未知逻辑 DI：" + inputName);
                LastReadInputName = inputName;
                return Task.FromResult(SensorActive);
            }

            /// <summary>读取最近一次模拟输出值。</summary>
            public Task<bool> ReadOutputAsync(string outputName, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (outputName != "ActionOutput") throw new InvalidOperationException("未知逻辑 DO：" + outputName);
                lock (_sync) { return Task.FromResult(_outputValues.LastOrDefault()); }
            }

            /// <summary>记录逻辑输出变化，供正常与暂停路径检查。</summary>
            public Task WriteOutputAsync(string outputName, bool value, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (outputName != "ActionOutput") throw new InvalidOperationException("未知逻辑 DO：" + outputName);
                lock (_sync)
                {
                    LastWrittenOutputName = outputName;
                    _outputValues.Add(value);
                }

                return Task.CompletedTask;
            }
        }

        /// <summary>保存单根测试轴的内存运动卡。</summary>
        private sealed class DemoCard : IMotionCard
        {
            /// <summary>创建仅含一根逻辑轴的卡。</summary>
            public DemoCard(IAxis axis) { Axes = new[] { axis }; }

            /// <summary>内存卡的稳定控制器标识。</summary>
            public string ControllerId { get { return "DemoCard"; } }

            /// <summary>本次内存连接是否已建立。</summary>
            public bool IsConnected { get; private set; }

            /// <summary>内存卡提供的逻辑轴集合。</summary>
            public IReadOnlyCollection<IAxis> Axes { get; private set; }

            /// <summary>模拟连接。</summary>
            public Task ConnectAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); IsConnected = true; return Task.CompletedTask; }

            /// <summary>模拟断开。</summary>
            public Task DisconnectAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); IsConnected = false; return Task.CompletedTask; }
        }

        /// <summary>始终返回急停未触发的内存安全状态。</summary>
        private sealed class DemoSafetyStatus : IConfigurableMachineSafetyStatus
        {
            /// <summary>该检查模拟已配置可读取的急停逻辑 DI。</summary>
            public bool IsConfigured { get { return true; } }

            /// <summary>返回未触发，允许检查自动流程其余路径。</summary>
            public Task<bool> ReadEmergencyStopActiveAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(false);
            }
        }
    }
}
