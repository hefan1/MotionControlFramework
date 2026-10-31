using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Equipment.Demo;
using MotionControl.Framework.Contracts.Alarms;
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
        /// <summary>依次执行单工位和多工位闭环；只在内存中模拟轴和 IO。</summary>
        public static async Task RunAsync()
        {
            SharedInputCanBeReadByMultipleStations();
            await SuccessfulCycleUsesMappedResourcesAsync().ConfigureAwait(false);
            await FourStationsRunConcurrentCyclesAsync().ConfigureAwait(false);
            await PausingPartialCycleKeepsLastCompletedDurationAsync().ConfigureAwait(false);
            await TimeoutRaisesAlarmAndCanRetryAsync().ConfigureAwait(false);
            await TimeoutReportsMultipleLinkedAlarmsAsync().ConfigureAwait(false);
            await PausingOutputTurnsItOffAsync().ConfigureAwait(false);
            await RestartRestoresCompletedStepWithoutRepeatingMoveAsync().ConfigureAwait(false);
        }

        /// <summary>共享只读 DI 合法；运动轴和写入 DO 仍必须按并行工位独占。</summary>
        private static void SharedInputCanBeReadByMultipleStations()
        {
            var first = CreateStationSettings("上料定位工位", "Load_X", "公共工件到位", "夹具夹紧");
            var second = CreateStationSettings("视觉检测工位", "Camera_X", "公共工件到位", "相机触发");
            var set = new DemoStationSetSettings
            {
                Enabled = true,
                Stations = new List<DemoStationSettings> { first, second }
            };

            set.Validate();
            Console.WriteLine("[工位配置通过] 多个工位可读取同一逻辑 DI。");

            second.AxisName = first.AxisName;
            Ensure(RejectedFor(set, "同一逻辑轴"), "共享运动轴没有被拒绝。");
            second.AxisName = "Camera_X";
            second.OutputName = first.OutputName;
            Ensure(RejectedFor(set, "同一逻辑 DO"), "共享写入 DO 没有被拒绝。");
        }

        /// <summary>验证整个工位组仍拒绝会造成控制争用的配置。</summary>
        private static bool RejectedFor(DemoStationSetSettings settings, string reason)
        {
            try { settings.Validate(); return false; }
            catch (InvalidOperationException error)
            { return error.Message.IndexOf(reason, StringComparison.OrdinalIgnoreCase) >= 0; }
        }

        /// <summary>验证四个独立工位同时定位、分别等待各自 DI，并连续完成三个生产循环。</summary>
        private static async Task FourStationsRunConcurrentCyclesAsync()
        {
            var axes = new[]
            {
                new DemoAxis("Load_X"),
                new DemoAxis("Press_Z"),
                new DemoAxis("Camera_X"),
                new DemoAxis("Unload_X")
            };
            var settings = new[]
            {
                CreateStationSettings("上料定位工位", "Load_X", "上料到位", "夹具夹紧"),
                CreateStationSettings("压装工位", "Press_Z", "压装完成", "压头动作"),
                CreateStationSettings("视觉检测工位", "Camera_X", "检测完成", "相机触发"),
                CreateStationSettings("下料工位", "Unload_X", "下料到位", "下料电磁阀")
            };
            var io = new MultiDemoIo(
                settings.Select(station => station.SensorInputName),
                settings.Select(station => station.OutputName));
            var alarms = new MachineAlarmService();
            var flows = settings.Select(station => new DemoStationFlow(
                axes.Single(axis => string.Equals(axis.Name, station.AxisName, StringComparison.OrdinalIgnoreCase)),
                io,
                alarms,
                station)).ToArray();
            for (int index = 0; index < flows.Length; index++)
            {
                Ensure(flows[index].CurrentStepName.Contains(settings[index].AxisName),
                    settings[index].StationName + " 的待启动步骤没有显示对应逻辑轴。");
            }
            Ensure(flows.Select(flow => flow.CurrentStepName).Distinct().Count() == flows.Length,
                "四工位的待启动步骤仍使用相同文字，操作员无法区分当前轴动作。");
            var runtime = new MachineRuntime(
                new MotionCardGroup(new IMotionCard[] { new DemoCard(axes) }),
                flows,
                alarms,
                new DemoSafetyStatus(),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMilliseconds(10));

            await runtime.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
            await runtime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
            await runtime.StartAsync(CancellationToken.None).ConfigureAwait(false);

            for (int cycle = 1; cycle <= 3; cycle++)
            {
                await WaitUntilAsync(() => flows.All(flow => flow.CompletedCycles == cycle - 1)
                    && flows.All(flow => flow.CurrentStepName.Contains("等待传感器"))
                    && axes.All(axis => axis.MoveCount >= cycle),
                    "四工位同时到达第 " + cycle + " 轮传感器等待").ConfigureAwait(false);

                foreach (DemoStationSettings station in settings)
                    io.SetInput(station.SensorInputName, true);

                await WaitUntilAsync(() => settings.All(station =>
                    io.ReadOutputHistory(station.OutputName).Count >= cycle * 2),
                    "四工位第 " + cycle + " 轮输出脉冲完成").ConfigureAwait(false);

                foreach (DemoStationSettings station in settings)
                    io.SetInput(station.SensorInputName, false);

                await WaitUntilAsync(() => flows.All(flow => flow.CompletedCycles >= cycle),
                    "四工位第 " + cycle + " 个完整周期").ConfigureAwait(false);
            }

            Ensure(runtime.AlarmService.Alarms.Count == 0, "正常连续生产期间出现了意外报警。");
            foreach (DemoStationSettings station in settings)
            {
                IReadOnlyList<bool> outputHistory = io.ReadOutputHistory(station.OutputName);
                Ensure(outputHistory.Count >= 6 && outputHistory.Take(6).SequenceEqual(
                    new[] { true, false, true, false, true, false }),
                    station.StationName + " 的三次输出没有按开、关顺序完成。");
            }

            await runtime.PauseAsync(CancellationToken.None).ConfigureAwait(false);
            Ensure(runtime.State == MachineRunState.Paused, "四工位连续生产后整机未按暂停命令收尾。");
            Ensure(flows.All(flow => flow.CompletedCycles == 3), "整机暂停时四工位的完整周期计数不一致。");
            await runtime.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>为一个独立并行工位生成短周期的内存检查参数。</summary>
        private static DemoStationSettings CreateStationSettings(
            string stationName,
            string axisName,
            string inputName,
            string outputName)
        {
            return new DemoStationSettings
            {
                Enabled = true,
                StationName = stationName,
                AxisName = axisName,
                SensorInputName = inputName,
                OutputName = outputName,
                TargetPosition = 1,
                Speed = 1,
                Acceleration = 10,
                Deceleration = 10,
                SensorTimeoutMilliseconds = 3000,
                SensorPollMilliseconds = 10,
                OutputPulseMilliseconds = 20
            };
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

            context.Io.SensorActive = false;
            await WaitUntilAsync(() => context.Flow.CompletedCycles == 1,
                "传感器复位后记录一个完整循环").ConfigureAwait(false);
            Ensure(context.Flow.LastCycleDuration.HasValue && context.Flow.LastCycleDuration.Value > TimeSpan.Zero,
                "完整循环没有记录有效的最近周期。");

            await context.Runtime.PauseAsync(CancellationToken.None).ConfigureAwait(false);
            Ensure(context.Runtime.State == MachineRunState.Paused, "正常流程暂停后整机状态不正确。");
            await context.Runtime.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>确认暂停中断下一循环后保留上一个完整周期时长。</summary>
        private static async Task PausingPartialCycleKeepsLastCompletedDurationAsync()
        {
            var context = CreateContext(500, 40);
            await context.Runtime.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
            await context.Runtime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
            await context.Runtime.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await WaitUntilAsync(() => context.Axis.MoveCount == 1
                && context.Flow.CurrentStepName.Contains("等待传感器"), "首循环定位后等待传感器").ConfigureAwait(false);

            context.Io.SensorActive = true;
            await WaitUntilAsync(() => context.Io.OutputValues.Count >= 2,
                "首循环输出脉冲完成").ConfigureAwait(false);
            context.Io.SensorActive = false;
            await WaitUntilAsync(() => context.Flow.CompletedCycles == 1,
                "首循环完成并开始下一循环").ConfigureAwait(false);
            Ensure(context.Flow.LastCycleDuration.HasValue, "首循环完成后没有周期时间。");
            TimeSpan lastCompletedDuration = context.Flow.LastCycleDuration.Value;

            await WaitUntilAsync(() => context.Axis.MoveCount >= 2
                && context.Flow.CurrentStepName.Contains("等待传感器"), "下一循环定位后等待传感器").ConfigureAwait(false);
            await context.Runtime.PauseAsync(CancellationToken.None).ConfigureAwait(false);

            Ensure(context.Runtime.State == MachineRunState.Paused, "下一循环暂停后整机状态不正确。");
            Ensure(context.Flow.LastCycleDuration == lastCompletedDuration,
                "暂停中断下一循环后没有保留最近一次完整周期时长。");
            Ensure(context.Flow.CompletedCycles == 1, "未完成的循环被错误计入产量。");
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

            context.Io.SensorActive = false;
            await WaitUntilAsync(() => context.Flow.CompletedCycles == 1,
                "恢复步骤完成后计入一个循环").ConfigureAwait(false);
            Ensure(!context.Flow.LastCycleDuration.HasValue,
                "传感器超时后恢复的半周期错误保留了超时前的计时。");

            await WaitUntilAsync(() => context.Axis.MoveCount >= 2
                && context.Flow.CurrentStepName.Contains("等待传感器"),
                "完成恢复周期后开始下一轮定位").ConfigureAwait(false);
            context.Io.SensorActive = true;
            await WaitUntilAsync(() => context.Io.OutputValues.Count >= 4,
                "下一轮传感器有效后执行输出脉冲").ConfigureAwait(false);
            context.Io.SensorActive = false;
            await WaitUntilAsync(() => context.Flow.CompletedCycles == 2,
                "下一轮传感器复位后完成循环").ConfigureAwait(false);
            Ensure(context.Flow.LastCycleDuration.HasValue && context.Flow.LastCycleDuration.Value > TimeSpan.Zero,
                "恢复后的新完整周期没有从定位步骤重新计时。");

            await context.Runtime.PauseAsync(CancellationToken.None).ConfigureAwait(false);
            await context.Runtime.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>验证一次超时可同时报告停机与非停机报警，且非停机报警随 DI 恢复清除。</summary>
        private static async Task TimeoutReportsMultipleLinkedAlarmsAsync()
        {
            var axis = new DemoAxis("AxisX");
            var io = new DemoIo();
            var alarms = new MachineAlarmService();
            DemoStationSettings settings = CreateStationSettings("流程验证工位", "AxisX", "PartReady", "ActionOutput");
            settings.SensorTimeoutMilliseconds = 70;
            settings.SensorPollMilliseconds = 10;
            settings.TimeoutAlarmCodes = new List<string> { "DEMO_SENSOR_TIMEOUT", "LOAD_MATERIAL_MISSING" };
            var definitions = new[]
            {
                new MachineAlarmDefinition("DEMO_SENSOR_TIMEOUT", "等待传感器超时", MachineAlarmKind.StopMachine),
                new MachineAlarmDefinition("LOAD_MATERIAL_MISSING", "上料缺少物料", MachineAlarmKind.NonStop)
            };
            var flow = new DemoStationFlow(axis, io, alarms, settings, definitions);
            var runtime = new MachineRuntime(
                new MotionCardGroup(new IMotionCard[] { new DemoCard(axis) }),
                new[] { flow },
                alarms,
                new DemoSafetyStatus(),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMilliseconds(10));

            await runtime.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
            await runtime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
            await runtime.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await WaitUntilAsync(() => runtime.State == MachineRunState.Alarmed,
                "关联停机报警后整机进入报警状态").ConfigureAwait(false);

            IReadOnlyList<MachineAlarmRecord> raised = alarms.Alarms.ToList();
            Ensure(raised.Count == 2, "一次超时没有同时报告全部已关联报警。");
            Ensure(raised.All(alarm => alarm.RelatedInputName == "PartReady"), "关联报警没有保留引发超时的逻辑输入。");
            var stopAlarm = raised.SingleOrDefault(alarm => alarm.Definition.Code == "DEMO_SENSOR_TIMEOUT");
            var nonStopAlarm = raised.SingleOrDefault(alarm => alarm.Definition.Code == "LOAD_MATERIAL_MISSING");
            Ensure(stopAlarm != null && stopAlarm.Definition.Kind == MachineAlarmKind.StopMachine
                && !stopAlarm.ConditionActive, "停机报警未保留为可复位的已恢复锁存记录。");
            Ensure(nonStopAlarm != null && nonStopAlarm.Definition.Kind == MachineAlarmKind.NonStop
                && nonStopAlarm.ConditionActive, "非停机报警未保持活动状态等待 DI 恢复。");
            Ensure(io.OutputValues.Count == 0, "关联报警触发期间仍写入了 DO。");

            await runtime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
            Ensure(alarms.Alarms.Count == 1 && alarms.Alarms.Single().Definition.Code == "LOAD_MATERIAL_MISSING",
                "整机复位未只清除已恢复的停机报警，或错误清除了非停机报警。");

            io.SensorActive = true;
            await runtime.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await WaitUntilAsync(() => io.OutputValues.Count >= 2,
                "DI 恢复后清除非停机报警并完成输出").ConfigureAwait(false);
            Ensure(alarms.Alarms.Count == 0, "DI 恢复后非停机报警仍留在当前报警列表。");
            io.SensorActive = false;
            await WaitUntilAsync(() => flow.CompletedCycles == 1,
                "多报警恢复后完成一个生产周期").ConfigureAwait(false);

            await runtime.PauseAsync(CancellationToken.None).ConfigureAwait(false);
            await runtime.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
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

        /// <summary>模拟进程重建后恢复等待步骤，确认不会重复执行已完成的轴定位。</summary>
        private static async Task RestartRestoresCompletedStepWithoutRepeatingMoveAsync()
        {
            var beforeRestart = CreateContext(500, 50);
            await beforeRestart.Runtime.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
            await beforeRestart.Runtime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
            await beforeRestart.Runtime.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await WaitUntilAsync(
                () => beforeRestart.Axis.MoveCount == 1
                    && beforeRestart.Flow.CurrentStepName.Contains("等待传感器"),
                "重启前完成轴定位并等待传感器").ConfigureAwait(false);

            await beforeRestart.Runtime.PauseAsync(CancellationToken.None).ConfigureAwait(false);
            int savedStep = beforeRestart.Flow.RecoveryStepNumber;
            Ensure(savedStep == 20, "暂停时没有保存到轴定位之后的等待步骤。");
            await beforeRestart.Runtime.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);

            var afterRestart = CreateContext(500, 50);
            afterRestart.Io.SensorActive = true;
            afterRestart.Flow.RestoreStep(savedStep);
            await afterRestart.Runtime.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
            await afterRestart.Runtime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
            await afterRestart.Runtime.StartAsync(CancellationToken.None).ConfigureAwait(false);
            await WaitUntilAsync(
                () => afterRestart.Io.OutputValues.Count >= 2,
                "恢复等待步骤后执行输出脉冲").ConfigureAwait(false);

            Ensure(afterRestart.Axis.MoveCount == 0, "恢复后重复执行了重启前已完成的轴定位。");
            Ensure(afterRestart.Flow.RecoveryStepNumber == 40, "恢复流程没有从传感器等待转到后续步骤。");
            await afterRestart.Runtime.PauseAsync(CancellationToken.None).ConfigureAwait(false);
            await afterRestart.Runtime.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
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

        /// <summary>按逻辑名称隔离多个并行工位的 DI 状态和 DO 历史。</summary>
        private sealed class MultiDemoIo : ILogicalIo
        {
            /// <summary>保护并行工位共享的内存 IO 状态。</summary>
            private readonly object _sync = new object();

            /// <summary>每个逻辑 DI 当前的有效状态。</summary>
            private readonly Dictionary<string, bool> _inputValues;

            /// <summary>每个逻辑 DO 已写入的状态历史。</summary>
            private readonly Dictionary<string, List<bool>> _outputValues;

            /// <summary>建立彼此独立的逻辑 DI/DO 通道。</summary>
            public MultiDemoIo(IEnumerable<string> inputNames, IEnumerable<string> outputNames)
            {
                _inputValues = inputNames.ToDictionary(name => name, name => false, StringComparer.OrdinalIgnoreCase);
                _outputValues = outputNames.ToDictionary(name => name, name => new List<bool>(), StringComparer.OrdinalIgnoreCase);
            }

            /// <summary>切换指定工位的逻辑输入，不影响其他工位信号。</summary>
            public void SetInput(string inputName, bool active)
            {
                lock (_sync)
                {
                    if (!_inputValues.ContainsKey(inputName))
                        throw new InvalidOperationException("未知逻辑 DI：" + inputName);
                    _inputValues[inputName] = active;
                }
            }

            /// <summary>返回指定工位输出的不可变写入历史。</summary>
            public IReadOnlyList<bool> ReadOutputHistory(string outputName)
            {
                lock (_sync)
                {
                    List<bool> history;
                    if (!_outputValues.TryGetValue(outputName, out history))
                        throw new InvalidOperationException("未知逻辑 DO：" + outputName);
                    return history.ToArray();
                }
            }

            /// <summary>读取指定逻辑 DI 的当前状态。</summary>
            public Task<bool> ReadInputAsync(string inputName, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_sync)
                {
                    bool active;
                    if (!_inputValues.TryGetValue(inputName, out active))
                        throw new InvalidOperationException("未知逻辑 DI：" + inputName);
                    return Task.FromResult(active);
                }
            }

            /// <summary>读取指定逻辑 DO 最近写入的状态。</summary>
            public Task<bool> ReadOutputAsync(string outputName, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_sync)
                {
                    List<bool> history;
                    if (!_outputValues.TryGetValue(outputName, out history))
                        throw new InvalidOperationException("未知逻辑 DO：" + outputName);
                    return Task.FromResult(history.Count > 0 && history[history.Count - 1]);
                }
            }

            /// <summary>记录指定逻辑 DO 状态，供每个工位单独核对脉冲顺序。</summary>
            public Task WriteOutputAsync(string outputName, bool value, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_sync)
                {
                    List<bool> history;
                    if (!_outputValues.TryGetValue(outputName, out history))
                        throw new InvalidOperationException("未知逻辑 DO：" + outputName);
                    history.Add(value);
                }
                return Task.CompletedTask;
            }
        }

        /// <summary>保存单根测试轴的内存运动卡。</summary>
        private sealed class DemoCard : IMotionCard
        {
            /// <summary>创建仅含一根逻辑轴的卡。</summary>
            public DemoCard(IAxis axis) : this(new[] { axis }) { }

            /// <summary>创建含多根独立逻辑轴的内存卡。</summary>
            public DemoCard(IEnumerable<IAxis> axes) { Axes = axes.ToArray(); }

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
