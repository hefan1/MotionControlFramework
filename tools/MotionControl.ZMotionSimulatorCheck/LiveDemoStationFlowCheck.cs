using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Adapter.MotionCard.ZMotion;
using MotionControl.Equipment.Demo;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Runtime.Alarms;
using MotionControl.Framework.Runtime.Machine;
using MotionControl.Framework.Runtime.Safety;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MotionControl.ZMotionSimulatorCheck
{
    /// <summary>通过正运动仿真器验证真实 Demo 工位流程的轴、传感器、输出、暂停和恢复。</summary>
    internal static class LiveDemoStationFlowCheck
    {
        /// <summary>轴位置、IO 状态和流程步骤的轮询间隔。</summary>
        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

        /// <summary>单个工艺步骤的最长等待时间。</summary>
        private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(45);

        /// <summary>启动仿真工艺流程检查；仅允许连接 VPLC5xx-Simu 控制器。</summary>
        public static int Run()
        {
            try
            {
                RunAsync().GetAwaiter().GetResult();
                return 0;
            }
            catch (ZMotionApiException error) when (error.ErrorCode == 20008)
            {
                Console.Error.WriteLine(
                    "[连接未建立] Demo 工艺流程检查没有建立独立 SDK 会话，错误码 20008；"
                    + "此结果不代表 ZDevelop 中的仿真控制器或流程功能失败。本次没有确认流程结果。"
                    + "请保持 ZDevelop/ZSimu 会话，不要为重试而断开控制器。");
                return 2;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("[失败] Demo 工艺流程仿真检查：" + error);
                return 1;
            }
        }

        /// <summary>连接仿真卡、执行一次真实工位流程，并在退出前恢复轴和输出状态。</summary>
        private static async Task RunAsync()
        {
            string configurationDirectory = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "Samples",
                "ZDevelopSimulation");
            JObject cardDocument = LoadJsonObject(configurationDirectory, "MotionCards.json");
            JObject cardDefinition = (JObject)cardDocument["motionCards"]?.First;
            if (cardDefinition == null)
            {
                throw new InvalidOperationException("仿真配置没有定义运动控制卡。");
            }

            string controllerId = (string)cardDefinition["controllerId"];
            string connectionFileName = (string)cardDefinition["connectionConfigurationFile"];
            ZMotionCardConfiguration cardConfiguration = JsonConvert.DeserializeObject<ZMotionCardConfiguration>(
                File.ReadAllText(GetConfigurationFilePath(configurationDirectory, connectionFileName)));
            JObject axisDocument = LoadJsonObject(configurationDirectory, "AxisParameters.json");
            JObject ioDocument = LoadJsonObject(configurationDirectory, "IoMapping.json");
            List<ZMotionAxisConfiguration> axisConfigurations =
                axisDocument["axes"].ToObject<List<ZMotionAxisConfiguration>>();
            List<ZMotionIoPointConfiguration> inputs =
                ioDocument["digitalInputs"].ToObject<List<ZMotionIoPointConfiguration>>();
            List<ZMotionIoPointConfiguration> outputs =
                ioDocument["digitalOutputs"].ToObject<List<ZMotionIoPointConfiguration>>();
            string emergencyInputName = (string)LoadJsonObject(configurationDirectory, "SafetyInterlock.json")
                ["emergencyStopInputName"];

            ZMotionAxisConfiguration axisConfiguration =
                axisConfigurations.Single(axisItem => axisItem.AxisNumber == 0);
            ZMotionIoPointConfiguration sensorInput = inputs.Single(input => input.Channel == 0);
            ZMotionIoPointConfiguration emergencyInput =
                inputs.Single(input => input.Name == emergencyInputName);
            ZMotionIoPointConfiguration actionOutput = outputs.Single(output => output.Channel == 0);
            sensorInput.Name = "PartReady";
            actionOutput.Name = "ActionOutput";

            var card = new ZMotionCard(
                controllerId,
                cardConfiguration,
                new[] { axisConfiguration },
                inputs,
                outputs);
            var motionCards = new MotionCardGroup(new IMotionCard[] { card });
            IAxis axis = motionCards.GetAxis(axisConfiguration.Name);
            var alarms = new MachineAlarmService();
            var settings = new DemoStationSettings
            {
                Enabled = true,
                StationName = "上料工位",
                AxisName = axisConfiguration.Name,
                SensorInputName = sensorInput.Name,
                OutputName = actionOutput.Name,
                Speed = 5,
                Acceleration = 10,
                Deceleration = 10,
                SensorTimeoutMilliseconds = 45000,
                SensorPollMilliseconds = 100,
                OutputPulseMilliseconds = 3000
            };
            var flow = new DemoStationFlow(axis, card, alarms, settings);
            var runtime = new MachineRuntime(
                motionCards,
                new IStationFlow[] { flow },
                alarms,
                new LogicalIoEmergencyStopStatus(card, emergencyInput.Name),
                TimeSpan.FromSeconds(10),
                TimeSpan.FromMilliseconds(100));

            AxisStatus originalAxisStatus = null;
            bool? originalOutputValue = null;
            bool runtimeConnected = false;
            try
            {
                await runtime.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
                runtimeConnected = true;
                EnsureControllerIsSimulator(card);
                await card.ApplyAxisParametersAsync(CancellationToken.None).ConfigureAwait(false);

                originalAxisStatus = await axis.ReadStatusAsync(CancellationToken.None).ConfigureAwait(false);
                originalOutputValue = await card.ReadOutputAsync(actionOutput.Name, CancellationToken.None)
                    .ConfigureAwait(false);
                bool sensorInitiallyActive =
                    await card.ReadInputAsync(sensorInput.Name, CancellationToken.None).ConfigureAwait(false);
                bool emergencyStopActive =
                    await card.ReadInputAsync(emergencyInput.Name, CancellationToken.None).ConfigureAwait(false);
                ValidateInitialState(
                    originalAxisStatus,
                    sensorInitiallyActive,
                    emergencyStopActive,
                    originalOutputValue.Value);

                settings.TargetPosition = GetSafeTargetPosition(
                    axisConfiguration,
                    originalAxisStatus.Position);
                settings.Validate();
                flow.RestoreStep(10);
                Console.WriteLine(
                    "[准备] 控制器=" + card.ActualControllerType
                    + "；工位=" + flow.StationName
                    + "；轴=" + axis.Name
                    + "；起始位置=" + originalAxisStatus.Position.ToString("F3")
                    + "；本次目标=" + settings.TargetPosition.ToString("F3")
                    + "；DI0=OFF；DO0=OFF。");

                await runtime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
                await runtime.StartAsync(CancellationToken.None).ConfigureAwait(false);
                await WaitForStepAsync(flow, axis, 20, settings.TargetPosition, "轴定位后等待传感器", true)
                    .ConfigureAwait(false);

                Console.WriteLine("[暂停检查] 流程停在步骤 20，正在暂停并检查步骤保留。");
                await runtime.PauseAsync(CancellationToken.None).ConfigureAwait(false);
                Ensure(runtime.State == MachineRunState.Paused, "步骤 20 暂停后，Runtime 没有进入 Paused 状态。");
                Ensure(flow.RecoveryStepNumber == 20, "步骤 20 暂停后流程步骤没有保留。");
                Console.WriteLine("[通过] 步骤 20 暂停后保留；复位并再次启动，流程将从等待传感器继续。");

                await runtime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
                await runtime.StartAsync(CancellationToken.None).ConfigureAwait(false);
                Ensure(flow.RecoveryStepNumber == 20, "复位并再次启动后，流程步骤没有保留在 20。");
                Console.WriteLine("[等待输入] 请在 ZSimu 中将输入仿真 DI0 置为 ON；流程会触发仿真 DO0 三秒。");

                await WaitForInputActiveAsync(card, sensorInput.Name).ConfigureAwait(false);
                await WaitForOutputValueAsync(card, actionOutput.Name, true, "输出脉冲有效").ConfigureAwait(false);
                Console.WriteLine("[输出动作] 工艺流程已实际写入 DO0=ON，正在等待三秒脉冲完成。");
                await WaitForOutputValueAsync(card, actionOutput.Name, false, "输出脉冲关闭").ConfigureAwait(false);
                await WaitForStepAsync(flow, axis, 40, settings.TargetPosition, "输出关闭后等待传感器复位", false)
                    .ConfigureAwait(false);

                Console.WriteLine("[等待复位] DO0 已关闭，流程停在步骤 40；请在 ZSimu 中将 DI0 置为 OFF。");
                await WaitForInputInactiveAsync(card, sensorInput.Name).ConfigureAwait(false);
                await runtime.PauseAsync(CancellationToken.None).ConfigureAwait(false);
                Ensure(runtime.State == MachineRunState.Paused, "传感器复位后，Runtime 没有进入 Paused 状态。");
                Console.WriteLine(
                    "[通过] Demo 工位完成轴定位、步骤 20 暂停/复位/续跑、DI0 触发、DO0 脉冲和步骤 40 传感器复位。");
            }
            finally
            {
                if (runtimeConnected)
                {
                    if (runtime.State == MachineRunState.Running
                        || runtime.State == MachineRunState.Pausing
                        || runtime.State == MachineRunState.Alarmed)
                    {
                        await runtime.PauseAsync(CancellationToken.None).ConfigureAwait(false);
                    }

                    await RestoreOutputsAndAxisAsync(
                        card,
                        axis,
                        actionOutput.Name,
                        originalOutputValue,
                        originalAxisStatus).ConfigureAwait(false);
                    await runtime.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
        }

        /// <summary>确认控制器身份、轴状态及仿真器初始 DI/DO，拒绝在未知状态启动测试。</summary>
        private static void ValidateInitialState(
            AxisStatus axisStatus,
            bool sensorActive,
            bool emergencyStopActive,
            bool outputActive)
        {
            if (axisStatus == null)
            {
                throw new InvalidOperationException("读取仿真轴初始状态失败。");
            }

            if (!axisStatus.ServoEnabled || axisStatus.Moving || axisStatus.AlarmActive
                || axisStatus.PositiveLimitActive != false || axisStatus.NegativeLimitActive != false
                || axisStatus.PositiveSoftLimitActive == true || axisStatus.NegativeSoftLimitActive == true)
            {
                throw new InvalidOperationException(
                    "仿真轴初始条件不安全：要求伺服已使能、轴停稳、无报警且硬限位未触发。位置="
                    + axisStatus.Position + "；报警=" + axisStatus.AlarmMessage);
            }

            if (sensorActive || emergencyStopActive || outputActive)
            {
                throw new InvalidOperationException(
                    "仿真 IO 初始条件不安全：要求 DI0、急停 DI 和 DO0 均为 OFF。"
                    + " DI0=" + sensorActive + "；急停=" + emergencyStopActive + "；DO0=" + outputActive + "。");
            }
        }

        /// <summary>计算距离当前位置十个工程单位且处于已配置软限位内的测试目标。</summary>
        private static double GetSafeTargetPosition(ZMotionAxisConfiguration axis, double currentPosition)
        {
            const double moveDistance = 10;
            double positiveTarget = currentPosition + moveDistance;
            if (!axis.SoftLimitEnabled || positiveTarget < axis.MaximumPosition - 1)
            {
                return positiveTarget;
            }

            double negativeTarget = currentPosition - moveDistance;
            if (!axis.SoftLimitEnabled || negativeTarget > axis.MinimumPosition + 1)
            {
                return negativeTarget;
            }

            throw new InvalidOperationException("仿真轴当前位置距离软限位过近，不能执行十个工程单位的验证移动。");
        }

        /// <summary>轮询轴和流程步骤，直至到达预期步骤，并在运动期间记录轴状态。</summary>
        private static async Task WaitForStepAsync(
            DemoStationFlow flow,
            IAxis axis,
            int expectedStep,
            double targetPosition,
            string operationName,
            bool showMotionStatus)
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            int lastPrintedSecond = -1;
            while (elapsed.Elapsed < StepTimeout)
            {
                AxisStatus status = await axis.ReadStatusAsync(CancellationToken.None).ConfigureAwait(false);
                if (flow.RecoveryStepNumber == expectedStep)
                {
                    if (expectedStep == 20)
                    {
                        EnsureStoppedAtTarget(axis.Name, status, targetPosition);
                    }

                    Console.WriteLine("[步骤] " + flow.CurrentStepName);
                    return;
                }

                int currentSecond = (int)elapsed.Elapsed.TotalSeconds;
                if (showMotionStatus && currentSecond != lastPrintedSecond)
                {
                    Console.WriteLine(
                        "[运动 " + currentSecond + " 秒] 位置=" + status.Position.ToString("F3")
                        + "；速度=" + status.Velocity.ToString("F3")
                        + "；运动=" + status.Moving
                        + "；报警=" + status.AlarmActive
                        + "；流程=" + flow.CurrentStepName);
                    lastPrintedSecond = currentSecond;
                }

                if (status.AlarmActive)
                {
                    throw new InvalidOperationException(
                        "等待流程步骤时逻辑轴 '" + axis.Name + "' 发生报警：" + status.AlarmMessage);
                }

                await Task.Delay(PollInterval).ConfigureAwait(false);
            }

            throw new TimeoutException(
                "等待工位 '" + flow.StationName + "' " + operationName + " 超时。当前=" + flow.CurrentStepName);
        }

        /// <summary>等待仿真 DI 变为有效，超时后提示需要检查的输入通道。</summary>
        private static async Task WaitForInputActiveAsync(ZMotionCard card, string inputName)
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed < StepTimeout)
            {
                if (await card.ReadInputAsync(inputName, CancellationToken.None).ConfigureAwait(false))
                {
                    Console.WriteLine("[输入] " + inputName + " 已变为 ON。");
                    return;
                }

                await Task.Delay(PollInterval).ConfigureAwait(false);
            }

            throw new TimeoutException("等待 ZSimu 仿真 DI0 置 ON 超时。");
        }

        /// <summary>等待工艺流程将指定仿真 DO 写成目标状态，并回读状态。</summary>
        private static async Task WaitForOutputValueAsync(
            ZMotionCard card,
            string outputName,
            bool expectedValue,
            string operationName)
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed < StepTimeout)
            {
                bool outputValue = await card.ReadOutputAsync(outputName, CancellationToken.None)
                    .ConfigureAwait(false);
                if (outputValue == expectedValue)
                {
                    Console.WriteLine(
                        "[输出读回] " + outputName + "=" + (outputValue ? "ON" : "OFF")
                        + "；" + operationName + "。");
                    return;
                }

                await Task.Delay(PollInterval).ConfigureAwait(false);
            }

            throw new TimeoutException("工艺流程没有在时限内完成操作：" + operationName + "。");
        }

        /// <summary>等待仿真 DI 恢复无效状态，供工位完成传感器复位步骤。</summary>
        private static async Task WaitForInputInactiveAsync(ZMotionCard card, string inputName)
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed < StepTimeout)
            {
                if (!await card.ReadInputAsync(inputName, CancellationToken.None).ConfigureAwait(false))
                {
                    Console.WriteLine("[输入] " + inputName + " 已恢复 OFF。");
                    return;
                }

                await Task.Delay(PollInterval).ConfigureAwait(false);
            }

            throw new TimeoutException("等待 ZSimu 仿真 DI0 恢复 OFF 超时。");
        }

        /// <summary>确认连接的控制器身份严格为已授权的正运动本机仿真卡。</summary>
        private static void EnsureControllerIsSimulator(ZMotionCard card)
        {
            if (!string.Equals(
                card.ActualControllerType,
                ZMotionCard.SimulationControllerType,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "安全检查已阻止工艺流程验证：当前控制器不是 VPLC5xx-Simu，而是 '"
                    + card.ActualControllerType + "'。");
            }
        }

        /// <summary>确认轴已停稳且实际位置等于流程目标。</summary>
        private static void EnsureStoppedAtTarget(string axisName, AxisStatus status, double targetPosition)
        {
            if (status.Moving || Math.Abs(status.Velocity) > 0.01
                || Math.Abs(status.Position - targetPosition) > 0.05)
            {
                throw new InvalidOperationException(
                    "逻辑轴 '" + axisName + "' 没有停稳到流程目标位置 " + targetPosition
                    + "；当前=" + status.Position + "；运动=" + status.Moving + "；速度=" + status.Velocity + "。");
            }
        }

        /// <summary>流程退出后恢复测试前 DO 和轴位置，并读回确认。</summary>
        private static async Task RestoreOutputsAndAxisAsync(
            ZMotionCard card,
            IAxis axis,
            string outputName,
            bool? originalOutputValue,
            AxisStatus originalAxisStatus)
        {
            if (!card.IsConnected)
            {
                return;
            }

            if (originalOutputValue.HasValue)
            {
                await card.WriteOutputAsync(outputName, originalOutputValue.Value, CancellationToken.None)
                    .ConfigureAwait(false);
                bool restoredOutput = await card.ReadOutputAsync(outputName, CancellationToken.None)
                    .ConfigureAwait(false);
                Ensure(restoredOutput == originalOutputValue.Value, "仿真 DO 未恢复到测试前状态。");
            }

            if (originalAxisStatus != null)
            {
                AxisStatus currentStatus = await axis.ReadStatusAsync(CancellationToken.None).ConfigureAwait(false);
                if (currentStatus.Moving)
                {
                    await axis.StopAsync(AxisStopMode.Controlled, CancellationToken.None).ConfigureAwait(false);
                }

                await axis.MoveAbsoluteAsync(
                    originalAxisStatus.Position,
                    new AxisMotionProfile(5, 10, 10),
                    CancellationToken.None).ConfigureAwait(false);

                AxisStatus restoredAxisStatus = await axis.ReadStatusAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                Ensure(!restoredAxisStatus.Moving, "恢复测试前位置后仿真轴仍在运动。");
                Ensure(
                    Math.Abs(restoredAxisStatus.Position - originalAxisStatus.Position) <= 0.05,
                    "仿真轴没有恢复到测试前位置。原位置=" + originalAxisStatus.Position
                    + "；恢复后=" + restoredAxisStatus.Position + "。");
            }

            Console.WriteLine("[恢复] 输出已恢复；仿真轴已停稳并回到测试前位置。");
        }

        /// <summary>读取仿真配置 JSON，并在文件路径或格式不正确时停止本次验证。</summary>
        private static JObject LoadJsonObject(string directory, string fileName)
        {
            string path = GetConfigurationFilePath(directory, fileName);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("Demo 工艺流程仿真检查缺少配置文件。", path);
            }

            return JObject.Parse(File.ReadAllText(path));
        }

        /// <summary>限制厂商连接配置文件只能从检查器的 Samples 目录读取。</summary>
        private static string GetConfigurationFilePath(string directory, string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName) || Path.IsPathRooted(fileName))
            {
                throw new ArgumentException("仿真配置文件必须是相对文件名。", nameof(fileName));
            }

            string fullPath = Path.GetFullPath(Path.Combine(directory, fileName));
            string directoryPrefix = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(directoryPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("仿真配置文件必须位于检查器的 Samples 目录中。", nameof(fileName));
            }

            return fullPath;
        }

        /// <summary>在验证前置条件或状态恢复读回值不符时报告明确故障。</summary>
        private static void Ensure(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
