using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Adapter.MotionCard.ZMotion;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Framework.Contracts.Configuration;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Runtime.Alarms;
using MotionControl.Framework.Runtime.Machine;
using MotionControl.Framework.Runtime.Safety;
using Newtonsoft.Json;

namespace MotionControl.ZMotionSimulatorCheck
{
    /// <summary>通过正运动仿真卡验证四个独立工位同时驱动四根轴。</summary>
    internal static class LiveParallelStationCheck
    {
        /// <summary>轴状态轮询周期；运行期间每秒输出一次，供现场对照 ZDevelop 观察。</summary>
        private static readonly TimeSpan StatusPollInterval = TimeSpan.FromMilliseconds(100);

        /// <summary>四根轴均需在该时限内完成目标定位。</summary>
        private static readonly TimeSpan MotionTimeout = TimeSpan.FromSeconds(30);

        /// <summary>启动四个仿真工位；只有识别为 VPLC5xx-Simu 后才允许发送运动指令。</summary>
        public static int Run()
        {
            try
            {
                return RunAsync().GetAwaiter().GetResult();
            }
            catch (ZMotionApiException error) when (error.ErrorCode == 20008)
            {
                Console.Error.WriteLine(
                    "[连接未建立] 四工位检查器没有连上仿真控制器，SDK 错误码为 20008；"
                    + "本次没有执行运动，不能据此判定多工位流程或轴运动失败。请确认 ZDevelop 中的 VPLC5xx-Simu 仍在运行。");
                return 2;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("[失败] 四工位四轴仿真检查：" + error);
                return 1;
            }
        }

        /// <summary>加载样例配置，连接仿真卡，并在退出前暂停流程、确认四轴停止后断开。</summary>
        private static async Task<int> RunAsync()
        {
            ZMotionCard card = null;
            MachineRuntime runtime = null;
            MotionCardGroup motionCards = null;

            try
            {
                string configurationDirectory = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "Samples",
                    "ZDevelopSimulation");
                MotionCardsDocument cardsDocument = LoadJson<MotionCardsDocument>(configurationDirectory, "MotionCards.json");
                AxisParametersDocument axesDocument = LoadJson<AxisParametersDocument>(configurationDirectory, "AxisParameters.json");
                IoMappingDocument ioDocument = LoadJson<IoMappingDocument>(configurationDirectory, "IoMapping.json");
                SafetyInterlockDocument safetyDocument = LoadJson<SafetyInterlockDocument>(configurationDirectory, "SafetyInterlock.json");

                MotionCardDefinition cardDefinition = cardsDocument.MotionCards.Single();
                ZMotionCardConfiguration cardConfiguration = LoadJson<ZMotionCardConfiguration>(
                    configurationDirectory,
                    cardDefinition.ConnectionConfigurationFile);
                List<ZMotionAxisConfiguration> axisConfigurations = axesDocument.Axes
                    .Where(axis => string.Equals(
                        axis.ControllerId,
                        cardDefinition.ControllerId,
                        StringComparison.OrdinalIgnoreCase))
                    .ToList();
                List<ZMotionIoPointConfiguration> inputs = ioDocument.DigitalInputs
                    .Where(point => string.Equals(
                        point.ControllerId,
                        cardDefinition.ControllerId,
                        StringComparison.OrdinalIgnoreCase))
                    .ToList();
                List<ZMotionIoPointConfiguration> outputs = ioDocument.DigitalOutputs
                    .Where(point => string.Equals(
                        point.ControllerId,
                        cardDefinition.ControllerId,
                        StringComparison.OrdinalIgnoreCase))
                    .ToList();

                AddMissingSimulationAxes(axisConfigurations, cardDefinition.ControllerId);

                card = new ZMotionCard(
                    cardDefinition.ControllerId,
                    cardConfiguration,
                    axisConfigurations,
                    inputs,
                    outputs);
                motionCards = new MotionCardGroup(new IMotionCard[] { card });

                List<ZMotionAxisConfiguration> testedAxisConfigurations = axisConfigurations
                    .Where(configuration => configuration.AxisNumber >= 0 && configuration.AxisNumber < 4)
                    .OrderBy(configuration => configuration.AxisNumber)
                    .ToList();
                if (testedAxisConfigurations.Count != 4)
                {
                    throw new InvalidOperationException("仿真检查未能准备好物理轴 0 到 3，不能开始四工位测试。");
                }

                List<IAxis> axes = testedAxisConfigurations
                    .Select(configuration => motionCards.GetAxis(configuration.Name))
                    .ToList();
                string[] stationNames = { "上料工位", "搬运工位", "检测工位", "组装工位" };
                double[] preferredMoveDistances = { 30, -30, 30, -30 };
                var stationFlows = new List<ParallelAxisMoveStationFlow>();

                for (var index = 0; index < axes.Count; index++)
                {
                    stationFlows.Add(new ParallelAxisMoveStationFlow(
                        stationNames[index],
                        axes[index],
                        motionCards.AxisResources,
                        preferredMoveDistances[index],
                        5,
                        10,
                        10,
                        testedAxisConfigurations[index]));
                }

                runtime = new MachineRuntime(
                    motionCards,
                    stationFlows,
                    new MachineAlarmService(),
                    new LogicalIoEmergencyStopStatus(card, safetyDocument.EmergencyStopInputName));

                await runtime.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
                if (!string.Equals(
                    card.ActualControllerType,
                    ZMotionCard.SimulationControllerType,
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "安全检查已阻止运动：当前控制器身份不是 VPLC5xx-Simu，而是 '"
                        + card.ActualControllerType + "'。本检查器只允许使用仿真卡。");
                }

                await card.ApplyAxisParametersAsync(CancellationToken.None).ConfigureAwait(false);
                Console.WriteLine("[配置] 已为物理轴 0 到 3 应用仿真轴参数；接下来只执行轴定位，不写入 DO。");

                var initialStatuses = new List<AxisStatus>();
                for (var index = 0; index < axes.Count; index++)
                {
                    AxisStatus status = await axes[index]
                        .ReadStatusAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                    initialStatuses.Add(status);
                    stationFlows[index].SetSafeRelativeTargetPosition(status.Position);
                }

                Console.WriteLine(
                    "[准备] 控制器=" + card.ActualControllerType
                    + "；工位数=" + stationFlows.Count + "；轴数=" + axes.Count
                    + "；不写入任何 DO。急停 DI 必须为关闭状态。");
                for (var index = 0; index < axes.Count; index++)
                {
                    Console.WriteLine(
                        "[目标] " + stationFlows[index].StationName + "/" + axes[index].Name
                        + "：" + initialStatuses[index].Position + " -> " + stationFlows[index].TargetPosition);
                }

                await runtime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
                await runtime.StartAsync(CancellationToken.None).ConfigureAwait(false);

                bool allAxesMovedAtTheSameTime = await ObserveParallelMotionAsync(
                    axes,
                    stationFlows).ConfigureAwait(false);

                if (!allAxesMovedAtTheSameTime)
                {
                    throw new InvalidOperationException("没有观察到四根轴同时运动，不能确认四个工位并行执行。");
                }

                await runtime.PauseAsync(CancellationToken.None).ConfigureAwait(false);
                for (var index = 0; index < axes.Count; index++)
                {
                    AxisStatus finalStatus = await axes[index]
                        .ReadStatusAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                    EnsureAxisStoppedAtTarget(axes[index].Name, finalStatus, stationFlows[index].TargetPosition);
                }

                await RestoreOriginalAxisPositionsAsync(axes, initialStatuses).ConfigureAwait(false);

                Console.WriteLine(
                    "[通过] 四个工位已并行启动；四根逻辑轴均在重叠时间内运动并到位；"
                    + "Runtime 已暂停，所有轴已停稳并返回测试前位置。");
                return 0;
            }
            finally
            {
                await StopRuntimeAndDisconnectAsync(runtime, card, motionCards).ConfigureAwait(false);
            }
        }

        /// <summary>四工位仿真验证结束后，将每根轴依次移动回测试前的位置并检查停稳状态。</summary>
        private static async Task RestoreOriginalAxisPositionsAsync(
            IReadOnlyList<IAxis> axes,
            IReadOnlyList<AxisStatus> initialStatuses)
        {
            var restoreProfile = new AxisMotionProfile(5, 10, 10);

            for (var index = 0; index < axes.Count; index++)
            {
                await axes[index].MoveAbsoluteAsync(
                    initialStatuses[index].Position,
                    restoreProfile,
                    CancellationToken.None).ConfigureAwait(false);

                await WaitForStableAxisPositionAsync(
                    axes[index],
                    initialStatuses[index].Position).ConfigureAwait(false);
            }
        }

        /// <summary>连续读取到三次停稳且位置正确后，才确认仿真轴已恢复到测试前的位置。</summary>
        private static async Task WaitForStableAxisPositionAsync(IAxis axis, double targetPosition)
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            var stableSamples = 0;

            while (elapsed.Elapsed < MotionTimeout)
            {
                AxisStatus status = await axis
                    .ReadStatusAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                bool isStable = !status.Moving
                    && Math.Abs(status.Velocity) <= 0.01
                    && Math.Abs(status.Position - targetPosition) <= 0.05;

                stableSamples = isStable ? stableSamples + 1 : 0;
                if (stableSamples >= 3)
                {
                    EnsureAxisStoppedAtTarget(axis.Name, status, targetPosition);
                    return;
                }

                await Task.Delay(StatusPollInterval).ConfigureAwait(false);
            }

            throw new TimeoutException(
                "逻辑轴 '" + axis.Name + "' 未能稳定恢复到测试前位置 " + targetPosition + "。");
        }

        /// <summary>轮询四根逻辑轴，记录同时运动，并在四个工位到位后返回。</summary>
        private static async Task<bool> ObserveParallelMotionAsync(
            IReadOnlyList<IAxis> axes,
            IReadOnlyList<ParallelAxisMoveStationFlow> stationFlows)
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            int lastPrintedSecond = -1;
            bool allAxesMovedAtTheSameTime = false;

            while (elapsed.Elapsed < MotionTimeout)
            {
                var statuses = new List<AxisStatus>();
                for (var index = 0; index < axes.Count; index++)
                {
                    statuses.Add(await axes[index]
                        .ReadStatusAsync(CancellationToken.None)
                        .ConfigureAwait(false));
                }

                bool allAxesMoving = statuses.All(status => status.Moving || Math.Abs(status.Velocity) > 0.01);
                if (allAxesMoving)
                {
                    allAxesMovedAtTheSameTime = true;
                }

                int currentSecond = (int)elapsed.Elapsed.TotalSeconds;
                if (currentSecond != lastPrintedSecond)
                {
                    string[] axisReadings = Enumerable.Range(0, axes.Count)
                        .Select(index => axes[index].Name
                            + " 位置=" + statuses[index].Position.ToString("F3")
                            + " 速度=" + statuses[index].Velocity.ToString("F3")
                            + " 运动=" + (statuses[index].Moving || Math.Abs(statuses[index].Velocity) > 0.01))
                        .ToArray();
                    Console.WriteLine("[运行 " + currentSecond + " 秒] " + string.Join("；", axisReadings));
                    lastPrintedSecond = currentSecond;
                }

                if (stationFlows.All(flow => flow.MoveCompleted))
                {
                    Console.WriteLine("[到位] 四个工位的定位步骤均已完成。");
                    return allAxesMovedAtTheSameTime;
                }

                ParallelAxisMoveStationFlow failedFlow = stationFlows.FirstOrDefault(flow => flow.LastError != null);
                if (failedFlow != null)
                {
                    throw new InvalidOperationException("工位 '" + failedFlow.StationName + "' 执行失败。", failedFlow.LastError);
                }

                await Task.Delay(StatusPollInterval).ConfigureAwait(false);
            }

            throw new TimeoutException("等待四个并行工位的轴定位完成超过 30 秒。");
        }

        /// <summary>为仿真器补齐物理轴 2 和 3 的逻辑映射，不修改项目正式配置文件。</summary>
        private static void AddMissingSimulationAxes(
            List<ZMotionAxisConfiguration> axisConfigurations,
            string controllerId)
        {
            ZMotionAxisConfiguration template = axisConfigurations.Single(
                configuration => configuration.AxisNumber == 0);

            for (var axisNumber = 2; axisNumber < 4; axisNumber++)
            {
                if (axisConfigurations.Any(configuration => configuration.AxisNumber == axisNumber))
                {
                    continue;
                }

                axisConfigurations.Add(new ZMotionAxisConfiguration
                {
                    ControllerId = controllerId,
                    Name = axisNumber == 2 ? "SimulationZ" : "SimulationR",
                    AxisNumber = axisNumber,
                    EngineeringUnit = template.EngineeringUnit,
                    UnitsPerEngineeringUnit = template.UnitsPerEngineeringUnit,
                    MinimumPosition = template.MinimumPosition,
                    MaximumPosition = template.MaximumPosition,
                    SoftLimitEnabled = template.SoftLimitEnabled,
                    ManualSpeed = template.ManualSpeed,
                    PositionTolerance = template.PositionTolerance,
                    HomeMode = template.HomeMode,
                    HomeSearchSpeed = template.HomeSearchSpeed,
                    HomeCreepSpeed = template.HomeCreepSpeed,
                    HomeAcceleration = template.HomeAcceleration,
                    HomeDeceleration = template.HomeDeceleration
                });
            }
        }

        /// <summary>确认轴已停止且反馈位置到达目标位置。</summary>
        private static void EnsureAxisStoppedAtTarget(string axisName, AxisStatus status, double targetPosition)
        {
            if (status.Moving || Math.Abs(status.Velocity) > 0.01)
            {
                throw new InvalidOperationException("暂停后逻辑轴 '" + axisName + "' 仍在运动。");
            }

            if (Math.Abs(status.Position - targetPosition) > 0.05)
            {
                throw new InvalidOperationException(
                    "逻辑轴 '" + axisName + "' 停止位置 " + status.Position
                    + " 与目标位置 " + targetPosition + " 不一致。");
            }
        }

        /// <summary>发生异常时先暂停 Runtime，再确认无运行流程后断开控制器。</summary>
        private static async Task StopRuntimeAndDisconnectAsync(
            MachineRuntime runtime,
            ZMotionCard card,
            MotionCardGroup motionCards)
        {
            if (runtime != null)
            {
                if (runtime.State == MachineRunState.Running || runtime.State == MachineRunState.Pausing)
                {
                    await runtime.PauseAsync(CancellationToken.None).ConfigureAwait(false);
                }

                if (runtime.State != MachineRunState.Disconnected)
                {
                    await runtime.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
                }

                return;
            }

            if (motionCards != null && motionCards.IsConnected)
            {
                await motionCards.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
            }
            else if (card != null && card.IsConnected)
            {
                await card.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }

        /// <summary>读取 JSON 配置文件，并在路径或内容错误时给出明确提示。</summary>
        private static T LoadJson<T>(string configurationDirectory, string fileName)
        {
            string filePath = Path.Combine(configurationDirectory, fileName);
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException("四工位仿真检查缺少配置文件。", filePath);
            }

            T result = JsonConvert.DeserializeObject<T>(File.ReadAllText(filePath));
            if (ReferenceEquals(result, null))
            {
                throw new InvalidOperationException("仿真配置文件内容为空：" + filePath);
            }

            return result;
        }

        /// <summary>保存运动卡清单和配置文件名。</summary>
        private sealed class MotionCardsDocument
        {
            /// <summary>本配置文件的数据版本。</summary>
            public int SchemaVersion { get; set; }

            /// <summary>本设备使用的运动卡。</summary>
            public List<MotionCardDefinition> MotionCards { get; set; }
        }

        /// <summary>描述仿真控制器的适配器和连接配置。</summary>
        private sealed class MotionCardDefinition
        {
            /// <summary>本设备内唯一的控制器标识。</summary>
            public string ControllerId { get; set; }

            /// <summary>所选运动卡适配器类型。</summary>
            public string AdapterType { get; set; }

            /// <summary>连接参数 JSON 文件名。</summary>
            public string ConnectionConfigurationFile { get; set; }
        }

        /// <summary>保存逻辑轴和物理轴参数。</summary>
        private sealed class AxisParametersDocument
        {
            /// <summary>本配置文件的数据版本。</summary>
            public int SchemaVersion { get; set; }

            /// <summary>本设备映射的逻辑轴。</summary>
            public List<ZMotionAxisConfiguration> Axes { get; set; }
        }

        /// <summary>保存仿真器逻辑 DI/DO 映射。</summary>
        private sealed class IoMappingDocument
        {
            /// <summary>本配置文件的数据版本。</summary>
            public int SchemaVersion { get; set; }

            /// <summary>输入点映射。</summary>
            public List<ZMotionIoPointConfiguration> DigitalInputs { get; set; }

            /// <summary>输出点映射。</summary>
            public List<ZMotionIoPointConfiguration> DigitalOutputs { get; set; }
        }

        /// <summary>保存软件急停使用的逻辑输入名称。</summary>
        private sealed class SafetyInterlockDocument
        {
            /// <summary>本配置文件的数据版本。</summary>
            public int SchemaVersion { get; set; }

            /// <summary>软件监控的急停逻辑 DI。</summary>
            public string EmergencyStopInputName { get; set; }
        }

        /// <summary>一个独立工位只负责移动自己的逻辑轴，到位后保持等待以便 Runtime 统一暂停。</summary>
        private sealed class ParallelAxisMoveStationFlow : IStationFlow
        {
            /// <summary>工位使用的逻辑轴。</summary>
            private readonly IAxis _axis;

            /// <summary>不同工位共享的轴资源仲裁器。</summary>
            private readonly IAxisResourceCoordinator _axisResources;

            /// <summary>该工位的轴移动距离，正负号表示方向。</summary>
            private readonly double _moveDistance;

            /// <summary>轴移动使用的速度、加速度和减速度。</summary>
            private readonly AxisMotionProfile _profile;

            /// <summary>目标轴配置，用于阻止越出模拟行程。</summary>
            private readonly ZMotionAxisConfiguration _axisConfiguration;

            /// <summary>当前流程步骤编号，10 为定位，20 为到位待机。</summary>
            private int _stepNumber = 10;

            /// <summary>本工位是否已完成轴定位。</summary>
            private int _moveCompleted;

            /// <summary>本工位最后一次轴移动异常。</summary>
            private Exception _lastError;

            /// <summary>尚未回到设备原点前，由连接后的起始位置计算出的目标位置。</summary>
            private double _targetPosition;

            /// <summary>检查并保存工位轴、资源仲裁和测试运动参数。</summary>
            public ParallelAxisMoveStationFlow(
                string stationName,
                IAxis axis,
                IAxisResourceCoordinator axisResources,
                double moveDistance,
                double speed,
                double acceleration,
                double deceleration,
                ZMotionAxisConfiguration axisConfiguration)
            {
                StationName = stationName ?? throw new ArgumentNullException(nameof(stationName));
                _axis = axis ?? throw new ArgumentNullException(nameof(axis));
                _axisResources = axisResources ?? throw new ArgumentNullException(nameof(axisResources));
                _axisConfiguration = axisConfiguration ?? throw new ArgumentNullException(nameof(axisConfiguration));
                _moveDistance = moveDistance;
                _profile = new AxisMotionProfile(speed, acceleration, deceleration);
                CurrentStepName = "等待整机复位";
            }

            /// <summary>本工位在界面和诊断日志中显示的名称。</summary>
            public string StationName { get; private set; }

            /// <summary>本工位当前正在执行或等待的步骤说明。</summary>
            public string CurrentStepName { get; private set; }

            /// <summary>该工位轴到位后保存的绝对目标位置。</summary>
            public double TargetPosition { get { return _targetPosition; } }

            /// <summary>轴绝对定位是否已完成并收到适配器到位反馈。</summary>
            public bool MoveCompleted { get { return Interlocked.CompareExchange(ref _moveCompleted, 0, 0) == 1; } }

            /// <summary>返回运行期间出现的最后一个轴动作异常。</summary>
            public Exception LastError { get { return _lastError; } }

            /// <summary>设置目标位置并在运动前验证它仍处于逻辑轴的仿真行程内。</summary>
            public void SetTargetPosition(double targetPosition)
            {
                if (targetPosition < _axisConfiguration.MinimumPosition
                    || targetPosition > _axisConfiguration.MaximumPosition)
                {
                    throw new InvalidOperationException(
                        "工位 '" + StationName + "' 的目标位置 " + targetPosition
                        + " 超出轴 '" + _axis.Name + "' 的仿真行程范围。");
                }

                _targetPosition = targetPosition;
            }

            /// <summary>优先按设定方向移动 30 个工程单位；将要越过软限位时改向移动。</summary>
            public void SetSafeRelativeTargetPosition(double currentPosition)
            {
                double targetPosition = currentPosition + _moveDistance;
                if (targetPosition < _axisConfiguration.MinimumPosition
                    || targetPosition > _axisConfiguration.MaximumPosition)
                {
                    targetPosition = currentPosition - _moveDistance;
                }

                SetTargetPosition(targetPosition);
            }

            /// <summary>执行一次受资源仲裁保护的轴定位；到位后保持待机直到 Runtime 请求暂停。</summary>
            public async Task ExecuteCurrentStepAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (_stepNumber == 10)
                {
                    CurrentStepName = "轴定位到 " + _targetPosition;
                    IDisposable axisLease = await _axisResources
                        .AcquireAxisAsync(_axis.Name, cancellationToken)
                        .ConfigureAwait(false);
                    try
                    {
                        await _axis.MoveAbsoluteAsync(_targetPosition, _profile, cancellationToken)
                            .ConfigureAwait(false);
                        Interlocked.Exchange(ref _moveCompleted, 1);
                        _stepNumber = 20;
                        CurrentStepName = "到位后等待 Runtime 暂停";
                    }
                    catch (Exception error)
                    {
                        _lastError = error;
                        throw;
                    }
                    finally
                    {
                        axisLease.Dispose();
                    }

                    return;
                }

                if (_stepNumber == 20)
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                    return;
                }

                throw new InvalidOperationException("未知工位步骤编号：" + _stepNumber + "。");
            }

            /// <summary>复位工位步骤到定位起点，不执行任何运动。</summary>
            public Task ResetAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _stepNumber = 10;
                Interlocked.Exchange(ref _moveCompleted, 0);
                _lastError = null;
                CurrentStepName = "等待启动后轴定位到 " + _targetPosition;
                return Task.CompletedTask;
            }
        }
    }
}
