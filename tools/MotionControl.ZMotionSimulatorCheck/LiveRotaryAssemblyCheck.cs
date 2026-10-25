using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Adapter.MotionCard.ZMotion;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Runtime.Alarms;
using MotionControl.Framework.Runtime.Machine;
using MotionControl.Framework.Runtime.Safety;
using Newtonsoft.Json.Linq;

namespace MotionControl.ZMotionSimulatorCheck
{
    /// <summary>在正运动仿真控制器上验证十二轴转盘、多工位并行、共享轴排队和结果分流。</summary>
    internal static class LiveRotaryAssemblyCheck
    {
        /// <summary>本次仿真使用的物理轴数量。</summary>
        private const int AxisCount = 12;
        /// <summary>仿真轴每个工程单位使用的脉冲数，让仿真分辨率满足到位容差。</summary>
        private const double SimulatorPulsesPerEngineeringUnit = 1000;
        /// <summary>轮询仿真轴状态的时间间隔。</summary>
        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
        /// <summary>等待所有三轮工位流程完成的最长时间。</summary>
        private static readonly TimeSpan FlowTimeout = TimeSpan.FromSeconds(150);
        /// <summary>十二根仿真轴按物理轴 0 到 11 的工艺含义排列。</summary>
        private static readonly string[] AxisNames = {
            "Dial_Index_R", "Load_X", "Load_Z", "Press_Position_X", "Press_Head_Z",
            "Vision_X", "Vision_Y", "Vision_Z", "Unload_X", "Unload_Z",
            "Shared_Transfer_X", "Feeder_Pusher"
        };

        /// <summary>运行转盘多工位仿真检查；连接失败与流程验证失败分开报告。</summary>
        public static int Run()
        {
            try
            {
                return RunAsync().GetAwaiter().GetResult();
            }
            catch (ZMotionApiException error) when (error.ErrorCode == 20008)
            {
                Console.Error.WriteLine("[连接未建立] SDK 返回 20008；本次独立检查器没有连接到 VPLC5xx-Simu。该连接结果不能判定设备流程或轴功能失败；请检查 ZDevelop 与仿真器会话状态。" );
                return 2;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("[失败] 转盘十二轴多工位仿真检查：" + error);
                return 1;
            }
        }

        /// <summary>连接并只读预检十二轴，验证三轮并行工位流程，最后恢复各轴原位置。</summary>
        private static async Task<int> RunAsync()
        {
            ZMotionCard card = null;
            MachineRuntime runtime = null;
            List<IAxis> axes = null;
            AxisStatus[] initial = null;
            bool parametersApplied = false;
            bool baselineCapturedAfterConfiguration = false;
            bool restored = false;

            try
            {
                string folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Samples", "ZDevelopSimulation");
                JObject cardsJson = ReadJson(folder, "MotionCards.json");
                JObject axesJson = ReadJson(folder, "AxisParameters.json");
                JObject ioJson = ReadJson(folder, "IoMapping.json");
                JObject safetyJson = ReadJson(folder, "SafetyInterlock.json");
                JToken cardItem = cardsJson["motionCards"].Single();
                string controllerId = cardItem.Value<string>("controllerId");
                ZMotionCardConfiguration connection = ReadJson(folder,
                    cardItem.Value<string>("connectionConfigurationFile")).ToObject<ZMotionCardConfiguration>();
                ZMotionAxisConfiguration template = axesJson["axes"].First()
                    .ToObject<ZMotionAxisConfiguration>();
                List<ZMotionAxisConfiguration> axisMappings = CreateAxisMappings(template, controllerId);
                List<ZMotionIoPointConfiguration> inputs = ioJson["digitalInputs"]
                    .Select(item => item.ToObject<ZMotionIoPointConfiguration>()).ToList();
                List<ZMotionIoPointConfiguration> outputs = ioJson["digitalOutputs"]
                    .Select(item => item.ToObject<ZMotionIoPointConfiguration>()).ToList();

                card = new ZMotionCard(controllerId, connection, axisMappings, inputs, outputs);
                var cards = new MotionCardGroup(new IMotionCard[] { card });
                axes = axisMappings.Select(mapping => cards.GetAxis(mapping.Name)).ToList();
                initial = new AxisStatus[AxisCount];

                var signals = new RotaryAssemblySignals();
                BuildFlows(axes, cards.AxisResources, signals,
                    out RotaryLoadFlow loadFlow,
                    out RotaryAssemblyFlow assemblyFlow,
                    out RotaryInspectionFlow inspectionFlow,
                    out RotaryUnloadFlow unloadFlow,
                    out RotaryFeederFlow feederFlow,
                    out RotaryIndexFlow indexFlow,
                    out IStationFlow[] stationFlows);

                runtime = new MachineRuntime(cards,
                    stationFlows,
                    new MachineAlarmService(),
                    new LogicalIoEmergencyStopStatus(card, safetyJson.Value<string>("emergencyStopInputName")));

                await runtime.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
                if (!string.Equals(card.ActualControllerType, ZMotionCard.SimulationControllerType,
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("当前控制器不是 VPLC5xx-Simu，仿真检查禁止继续运动。实际控制器："
                        + card.ActualControllerType);
                }

                for (int index = 0; index < AxisCount; index++)
                {
                    initial[index] = await axes[index].ReadStatusAsync(CancellationToken.None).ConfigureAwait(false);
                    EnsureReadyForTest(axes[index].Name, initial[index], axisMappings[index]);
                    Console.WriteLine("[只读预检] 物理轴 " + index + " / " + axes[index].Name
                        + "，位置=" + initial[index].Position.ToString("F3"));
                }
                Console.WriteLine("[预检通过] 十二根轴全部停稳、无报警且未触及软硬限位；不写任何 DO。" );

                await card.ApplyAxisParametersAsync(CancellationToken.None).ConfigureAwait(false);
                parametersApplied = true;
                for (int index = 0; index < AxisCount; index++)
                {
                    initial[index] = await axes[index].ReadStatusAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                    EnsureReadyForTest(axes[index].Name, initial[index], axisMappings[index]);
                    Console.WriteLine("[参数应用后基准] 物理轴 " + index + " / " + axes[index].Name
                        + "，位置=" + initial[index].Position.ToString("F3")
                        + "，UNITS=" + axisMappings[index].UnitsPerEngineeringUnit.ToString("F0"));
                }
                baselineCapturedAfterConfiguration = true;

                double[] expectedAfterFinalCycle = ConfigureFlowTargets(
                    loadFlow, assemblyFlow, inspectionFlow, unloadFlow, feederFlow, indexFlow,
                    axisMappings, initial);

                await runtime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
                await runtime.StartAsync(CancellationToken.None).ConfigureAwait(false);

                int firstPauseMoving = await WaitForLiveMotionProgressAsync(
                    axes, runtime, initial, 7, 3, TimeSpan.FromSeconds(25)).ConfigureAwait(false);
                if (firstPauseMoving < 7)
                {
                    throw new InvalidOperationException("首次运行暂停点没有观察到七根轴同时运动，实际并发峰值="
                        + firstPauseMoving);
                }
                await runtime.PauseAsync(CancellationToken.None).ConfigureAwait(false);
                int stoppedAfterFirstPause = await CountMovingAxesAsync(axes).ConfigureAwait(false);
                int changedAfterFirstPause = await CountAxesMovedFromStartAsync(axes, initial).ConfigureAwait(false);
                if (stoppedAfterFirstPause != 0 || changedAfterFirstPause < 3 || signals.SharedAxisOwners != 0)
                {
                    throw new InvalidOperationException("首轮整机暂停后未能确认轴全部停稳、动作已发生且共享资源已释放。"
                        + "运动轴=" + stoppedAfterFirstPause + "，位置变化轴=" + changedAfterFirstPause
                        + "，共享轴占用=" + signals.SharedAxisOwners);
                }
                Console.WriteLine("[中途暂停通过] 暂停前观察到 " + firstPauseMoving
                    + " 根轴同时运动；暂停后所有轴停稳，已释放共享轴资源。" );

                await runtime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
                await runtime.StartAsync(CancellationToken.None).ConfigureAwait(false);
                await WaitForSharedAxisContentionAsync(loadFlow, unloadFlow, signals).ConfigureAwait(false);
                int contentionMoving = await CountMovingAxesAsync(axes).ConfigureAwait(false);
                await runtime.PauseAsync(CancellationToken.None).ConfigureAwait(false);
                int stoppedDuringContention = await CountMovingAxesAsync(axes).ConfigureAwait(false);
                if (contentionMoving == 0 || stoppedDuringContention != 0 || signals.SharedAxisOwners != 0)
                {
                    throw new InvalidOperationException("共享转运轴争用期间暂停检查未通过。暂停前运动轴="
                        + contentionMoving + "，暂停后运动轴=" + stoppedDuringContention
                        + "，共享轴占用=" + signals.SharedAxisOwners);
                }
                Console.WriteLine("[共享轴暂停通过] 上料和出料工位争用同一根转运轴；暂停后运动停止且占用清零。" );

                await runtime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
                // 前两轮专门验证暂停和资源争用；完整生产循环的统计从这里重新开始。
                loadFlow.ResetResourceWaitTime();
                unloadFlow.ResetResourceWaitTime();
                await runtime.StartAsync(CancellationToken.None).ConfigureAwait(false);
                int peakMoving = await WaitForAllCyclesAsync(axes, runtime, signals,
                    loadFlow, assemblyFlow, inspectionFlow, unloadFlow, feederFlow, indexFlow)
                    .ConfigureAwait(false);

                if (peakMoving < 5)
                {
                    throw new InvalidOperationException("完整工艺循环的并行运动数量不足，至少需要五根轴同时运动，实际并发峰值="
                        + peakMoving);
                }
                if (signals.IndexMoveCount != RotaryAssemblySignals.CycleCount - 1)
                {
                    throw new InvalidOperationException("转盘实际分度次数不符，期望 2 次，实际=" + signals.IndexMoveCount);
                }
                if (signals.MaximumSharedAxisOwners != 1 || signals.SharedAxisOwners != 0)
                {
                    throw new InvalidOperationException("共享转运轴没有保持单一占用者。最大占用="
                        + signals.MaximumSharedAxisOwners + "，结束占用=" + signals.SharedAxisOwners);
                }
                if (loadFlow.ResourceWaitMilliseconds < 250 && unloadFlow.ResourceWaitMilliseconds < 250)
                {
                    throw new InvalidOperationException("上料和出料流程没有观察到共享转运轴的有效排队等待。" );
                }
                if (signals.GoodUnloadCount != 2 || signals.RejectUnloadCount != 1
                    || !signals.WasUnloadPassed(0) || !signals.WasUnloadPassed(1) || signals.WasUnloadPassed(2))
                {
                    throw new InvalidOperationException("视觉结果分流不符合预期：应完成两次良品出料和一次不良品分流。" );
                }

                await runtime.PauseAsync(CancellationToken.None).ConfigureAwait(false);
                for (int index = 0; index < AxisCount; index++)
                {
                    await EnsureStoppedAtAsync(axes[index], expectedAfterFinalCycle[index]).ConfigureAwait(false);
                }
                Console.WriteLine("[多工位流程通过] 三轮工位加工，两个转盘分度；并发峰值=" + peakMoving
                    + "；完整循环共享轴累计排队：上料=" + loadFlow.ResourceWaitMilliseconds + "ms，出料="
                    + unloadFlow.ResourceWaitMilliseconds + "ms；良品=2，不良品=1。" );

                await RestoreAxesAsync(axes, initial).ConfigureAwait(false);
                restored = true;
                Console.WriteLine("[通过] 转盘轴、上料、压装、视觉、出料、送料共十二轴均已验证并恢复原位置；未写入 DO。" );
                return 0;
            }
            finally
            {
                if (runtime != null && (runtime.State == MachineRunState.Running || runtime.State == MachineRunState.Pausing))
                {
                    await runtime.PauseAsync(CancellationToken.None).ConfigureAwait(false);
                }

                if (!restored && parametersApplied && baselineCapturedAfterConfiguration
                    && axes != null && initial != null && initial.All(status => status != null))
                {
                    try
                    {
                        if (runtime != null && runtime.State == MachineRunState.Alarmed)
                        {
                            await StopMovingAxesForCleanupAsync(axes).ConfigureAwait(false);
                        }
                        await RestoreAxesAsync(axes, initial).ConfigureAwait(false);
                        restored = true;
                    }
                    catch (Exception error)
                    {
                        Console.Error.WriteLine("[原位恢复未完成] " + error.Message);
                    }
                }

                if (runtime != null && runtime.State != MachineRunState.Disconnected)
                {
                    await runtime.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
                }
                else if (card != null && card.IsConnected)
                {
                    await card.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
        }

        /// <summary>按十二轴工艺分工创建六个独立工位流程及其安全运动目标。</summary>
        private static void BuildFlows(IReadOnlyList<IAxis> axes, IAxisResourceCoordinator axisResources,
            RotaryAssemblySignals signals, out RotaryLoadFlow loadFlow, out RotaryAssemblyFlow assemblyFlow,
            out RotaryInspectionFlow inspectionFlow, out RotaryUnloadFlow unloadFlow,
            out RotaryFeederFlow feederFlow, out RotaryIndexFlow indexFlow, out IStationFlow[] stationFlows)
        {
            loadFlow = new RotaryLoadFlow(axes[1], axes[2], axes[10], axisResources, signals,
                0, 0, 0, 0, 0, 0, 0);
            assemblyFlow = new RotaryAssemblyFlow(axes[3], axes[4], axisResources, signals,
                0, 0, 0, 0);
            inspectionFlow = new RotaryInspectionFlow(axes[5], axes[6], axes[7], axisResources, signals,
                0, 0, 0, 0, 0, 0, 0);
            unloadFlow = new RotaryUnloadFlow(axes[8], axes[9], axes[10], axisResources, signals,
                0, 0, 0, 0, 0, 0, 0, 0, 0);
            feederFlow = new RotaryFeederFlow(axes[11], axisResources, signals, 0, 0);
            indexFlow = new RotaryIndexFlow(axes[0], axisResources, signals, 0, 0);
            stationFlows = new IStationFlow[] {
                loadFlow, assemblyFlow, inspectionFlow, unloadFlow, feederFlow, indexFlow };
        }

        /// <summary>根据仿真卡实时读取的位置计算安全目标，并配置六个工位流程。</summary>
        private static double[] ConfigureFlowTargets(RotaryLoadFlow loadFlow,
            RotaryAssemblyFlow assemblyFlow, RotaryInspectionFlow inspectionFlow,
            RotaryUnloadFlow unloadFlow, RotaryFeederFlow feederFlow, RotaryIndexFlow indexFlow,
            IReadOnlyList<ZMotionAxisConfiguration> axisMappings, IReadOnlyList<AxisStatus> initial)
        {
            var clear = new double[AxisCount];
            for (int index = 0; index < AxisCount; index++)
            {
                clear[index] = initial[index].Position;
            }

            double loadXWork = SafeOffset(clear[1], axisMappings[1], 10);
            double loadXApproach = SafeOffset(clear[1], axisMappings[1], 5);
            double loadZSafe = SafeOffset(clear[2], axisMappings[2], 5);
            double loadZWork = SafeOffset(clear[2], axisMappings[2], -10);
            double sharedLoadWork = SafeOffset(clear[10], axisMappings[10], 10);
            loadFlow.SetTargets(clear[1], clear[2], loadZSafe, loadXApproach, loadXWork, loadZWork,
                clear[10], sharedLoadWork);

            double pressPositionWork = SafeOffset(clear[3], axisMappings[3], 10);
            double pressHeadSafe = SafeOffset(clear[4], axisMappings[4], 5);
            double pressHeadWork = SafeOffset(clear[4], axisMappings[4], -10);
            assemblyFlow.SetTargets(clear[3], pressPositionWork, pressHeadSafe, pressHeadWork);

            double visionXWork = SafeOffset(clear[5], axisMappings[5], 10);
            double visionYWork = SafeOffset(clear[6], axisMappings[6], -10);
            double visionZSafe = SafeOffset(clear[7], axisMappings[7], 5);
            double visionZWork = SafeOffset(clear[7], axisMappings[7], -10);
            inspectionFlow.SetTargets(clear[5], clear[6], clear[7], visionZSafe,
                visionXWork, visionYWork, visionZWork);

            double unloadXGood = SafeOffset(clear[8], axisMappings[8], 10);
            double unloadXReject = SafeOffset(clear[8], axisMappings[8], -10);
            double unloadXGoodApproach = SafeOffset(clear[8], axisMappings[8], 5);
            double unloadXRejectApproach = SafeOffset(clear[8], axisMappings[8], -5);
            double unloadZSafe = SafeOffset(clear[9], axisMappings[9], 5);
            double unloadZWork = SafeOffset(clear[9], axisMappings[9], -10);
            double sharedUnloadGood = SafeOffset(clear[10], axisMappings[10], 10);
            double sharedUnloadReject = SafeOffset(clear[10], axisMappings[10], -10);
            unloadFlow.SetTargets(clear[8], clear[9], unloadZSafe, unloadZWork,
                unloadXGoodApproach, unloadXRejectApproach, unloadXGood, unloadXReject,
                clear[10], sharedUnloadGood, sharedUnloadReject);

            double feederWork = SafeOffset(clear[11], axisMappings[11], 10);
            feederFlow.SetTargets(clear[11], feederWork);

            double firstIndex = SafeOffset(clear[0], axisMappings[0], 10);
            double secondIndex = SafeOffset(clear[0], axisMappings[0], 20);
            indexFlow.SetTargets(firstIndex, secondIndex);

            var expectedAfterFinalCycle = (double[])clear.Clone();
            expectedAfterFinalCycle[4] = pressHeadSafe;
            expectedAfterFinalCycle[0] = secondIndex;
            return expectedAfterFinalCycle;
        }

        /// <summary>按轴参数模板映射物理轴 0 到 11，不改变厂商适配器代码。</summary>
        private static List<ZMotionAxisConfiguration> CreateAxisMappings(
            ZMotionAxisConfiguration template, string controllerId)
        {
            var mappings = new List<ZMotionAxisConfiguration>();
            for (int index = 0; index < AxisCount; index++)
            {
                mappings.Add(new ZMotionAxisConfiguration
                {
                    ControllerId = controllerId,
                    Name = AxisNames[index],
                    AxisNumber = index,
                    EngineeringUnit = template.EngineeringUnit,
                    UnitsPerEngineeringUnit = SimulatorPulsesPerEngineeringUnit,
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

            return mappings;
        }

        /// <summary>读取项目内仿真 JSON 配置，缺少文件时报告具体路径。</summary>
        private static JObject ReadJson(string folder, string fileName)
        {
            string path = Path.Combine(folder, fileName);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("缺少仿真配置。", path);
            }

            return JObject.Parse(File.ReadAllText(path));
        }

        /// <summary>运动前确认轴可读、停稳、无报警、无软硬限位且保留足够仿真行程。</summary>
        private static void EnsureReadyForTest(string name, AxisStatus status, ZMotionAxisConfiguration mapping)
        {
            if (status.Moving || Math.Abs(status.Velocity) > 0.01 || status.AlarmActive
                || status.PositiveLimitActive != false || status.NegativeLimitActive != false
                || status.PositiveSoftLimitActive != false || status.NegativeSoftLimitActive != false)
            {
                throw new InvalidOperationException("轴 '" + name + "' 未停稳或存在报警/限位，预检禁止运动。"
                    + status.AlarmMessage);
            }

            if (double.IsNaN(status.Position) || double.IsInfinity(status.Position)
                || status.Position < mapping.MinimumPosition + 25
                || status.Position > mapping.MaximumPosition - 25)
            {
                throw new InvalidOperationException("轴 '" + name + "' 的当前位置 " + status.Position
                    + " 离仿真软限位过近，预检禁止运动。");
            }
        }

        /// <summary>在软限位内计算偏移位置，空间不足时尝试反方向并明确失败原因。</summary>
        private static double SafeOffset(double start, ZMotionAxisConfiguration mapping, double offset)
        {
            double target = start + offset;
            if (target < mapping.MinimumPosition + 5 || target > mapping.MaximumPosition - 5)
            {
                target = start - offset;
            }

            if (target < mapping.MinimumPosition + 5 || target > mapping.MaximumPosition - 5)
            {
                throw new InvalidOperationException("轴 '" + mapping.Name + "' 无法找到安全的仿真位置目标。");
            }

            return target;
        }

        /// <summary>等待运动轴数量达到指定值，用实时轴状态选择中途暂停时机。</summary>
        private static async Task<int> WaitForLiveMotionProgressAsync(
            IReadOnlyList<IAxis> axes, MachineRuntime runtime, IReadOnlyList<AxisStatus> initial,
            int requiredMovingCount, int requiredChangedPositionCount, TimeSpan timeout)
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            int lastLoggedSecond = -1;
            while (elapsed.Elapsed < timeout)
            {
                if (runtime.State == MachineRunState.Alarmed)
                {
                    throw new InvalidOperationException("整机在首次运动检查前进入报警状态。", runtime.LastError);
                }

                int moving = 0;
                int changed = 0;
                for (int index = 0; index < axes.Count; index++)
                {
                    AxisStatus status = await axes[index].ReadStatusAsync(CancellationToken.None).ConfigureAwait(false);
                    if (status.Moving || Math.Abs(status.Velocity) > 0.01)
                    {
                        moving++;
                    }

                    if (Math.Abs(status.Position - initial[index].Position) > 0.05)
                    {
                        changed++;
                    }
                }

                int second = (int)elapsed.Elapsed.TotalSeconds;
                if (second != lastLoggedSecond)
                {
                    Console.WriteLine("[启动观察 " + second + " 秒] 整机状态=" + runtime.State
                        + "，状态位显示运动轴=" + moving + "，位置已变化轴=" + changed);
                    lastLoggedSecond = second;
                }

                if (moving >= requiredMovingCount && changed >= requiredChangedPositionCount)
                {
                    return moving;
                }

                await Task.Delay(PollInterval).ConfigureAwait(false);
            }

            throw new TimeoutException("等待真实位置变化超时。至少需要 " + requiredMovingCount
                + " 根运动状态轴和 " + requiredChangedPositionCount + " 根位置变化轴。");
        }

        /// <summary>等待上料和出料一方占用共享轴、另一方排队的实时状态。</summary>
        private static async Task WaitForSharedAxisContentionAsync(
            RotaryLoadFlow loadFlow, RotaryUnloadFlow unloadFlow, RotaryAssemblySignals signals)
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed < TimeSpan.FromSeconds(40))
            {
                bool loadWaits = loadFlow.CurrentStepName == "等待共享转运 X 轴";
                bool unloadWaits = unloadFlow.CurrentStepName == "等待共享转运 X 轴";
                bool loadOwns = string.Equals(signals.SharedAxisOwnerStation, "上料", StringComparison.Ordinal);
                bool unloadOwns = string.Equals(signals.SharedAxisOwnerStation, "出料", StringComparison.Ordinal);
                if (signals.SharedAxisOwners == 1 && ((loadOwns && unloadWaits) || (unloadOwns && loadWaits)))
                {
                    Console.WriteLine("[共享轴争用] " + (loadOwns ? "上料" : "出料")
                        + "工位正在使用转运 X 轴，另一工位等待同一逻辑轴释放。");
                    return;
                }

                await Task.Delay(50).ConfigureAwait(false);
            }

            throw new TimeoutException("未观察到上料与出料工位对共享转运 X 轴的同时请求。"
                + "当前占用工位=" + (signals.SharedAxisOwnerStation ?? "无")
                + "；上料步骤=" + loadFlow.CurrentStepName
                + "；出料步骤=" + unloadFlow.CurrentStepName
                + "；上料累计等待=" + loadFlow.ResourceWaitMilliseconds + "ms"
                + "；出料累计等待=" + unloadFlow.ResourceWaitMilliseconds + "ms。");
        }

        /// <summary>监控所有轴直到最后一轮完成，并持续记录实际并发运动峰值。</summary>
        private static async Task<int> WaitForAllCyclesAsync(IReadOnlyList<IAxis> axes,
            MachineRuntime runtime, RotaryAssemblySignals signals, RotaryLoadFlow loadFlow,
            RotaryAssemblyFlow assemblyFlow, RotaryInspectionFlow inspectionFlow,
            RotaryUnloadFlow unloadFlow, RotaryFeederFlow feederFlow, RotaryIndexFlow indexFlow)
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            int peak = 0;
            int lastLoggedSecond = -1;
            while (elapsed.Elapsed < FlowTimeout)
            {
                int moving = await CountMovingAxesAsync(axes).ConfigureAwait(false);
                peak = Math.Max(peak, moving);
                int second = (int)elapsed.Elapsed.TotalSeconds;
                if (second != lastLoggedSecond)
                {
                    Console.WriteLine("[运行 " + second + " 秒] 周期=" + signals.CurrentCycle
                        + "，当前运动轴=" + moving + "，并发峰值=" + peak
                        + "；上料=" + loadFlow.CurrentStepName
                        + "；压装=" + assemblyFlow.CurrentStepName
                        + "；视觉=" + inspectionFlow.CurrentStepName
                        + "；出料=" + unloadFlow.CurrentStepName
                        + "；送料=" + feederFlow.CurrentStepName
                        + "；转盘=" + indexFlow.CurrentStepName);
                    lastLoggedSecond = second;
                }

                if (runtime.State == MachineRunState.Alarmed)
                {
                    throw new InvalidOperationException("整机运行进入报警状态。", runtime.LastError);
                }

                if (signals.FinalCycleCompleted)
                {
                    return peak;
                }

                await Task.Delay(PollInterval).ConfigureAwait(false);
            }

            throw new TimeoutException("转盘三轮多工位流程在 150 秒内未全部完成。");
        }

        /// <summary>读取十二根轴并统计当前正在运动的轴数。</summary>
        private static async Task<int> CountMovingAxesAsync(IReadOnlyList<IAxis> axes)
        {
            int count = 0;
            foreach (IAxis axis in axes)
            {
                AxisStatus status = await axis.ReadStatusAsync(CancellationToken.None).ConfigureAwait(false);
                if (status.Moving || Math.Abs(status.Velocity) > 0.01)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>计算有多少根轴相对测试开始位置已经发生可见移动。</summary>
        private static async Task<int> CountAxesMovedFromStartAsync(
            IReadOnlyList<IAxis> axes, IReadOnlyList<AxisStatus> initial)
        {
            int count = 0;
            for (int index = 0; index < axes.Count; index++)
            {
                AxisStatus status = await axes[index].ReadStatusAsync(CancellationToken.None).ConfigureAwait(false);
                if (Math.Abs(status.Position - initial[index].Position) > 0.05)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>检查轴停稳且到达目标位置，验证连续读取三次均稳定。</summary>
        private static async Task EnsureStoppedAtAsync(IAxis axis, double target)
        {
            for (int sample = 0; sample < 3; sample++)
            {
                AxisStatus status = await axis.ReadStatusAsync(CancellationToken.None).ConfigureAwait(false);
                if (status.Moving || Math.Abs(status.Velocity) > 0.01
                    || Math.Abs(status.Position - target) > 0.05)
                {
                    throw new InvalidOperationException("轴 '" + axis.Name + "' 未稳定到位。当前位置="
                        + status.Position + "，目标=" + target + "。");
                }

                await Task.Delay(PollInterval).ConfigureAwait(false);
            }
        }

        /// <summary>确认所有轴停稳后并行恢复测试前位置，并逐轴复核恢复结果。</summary>
        private static async Task RestoreAxesAsync(IReadOnlyList<IAxis> axes, IReadOnlyList<AxisStatus> initial)
        {
            for (int index = 0; index < axes.Count; index++)
            {
                AxisStatus status = await axes[index].ReadStatusAsync(CancellationToken.None).ConfigureAwait(false);
                if (status.Moving || status.AlarmActive || status.PositiveLimitActive != false
                    || status.NegativeLimitActive != false || status.PositiveSoftLimitActive != false
                    || status.NegativeSoftLimitActive != false)
                {
                    throw new InvalidOperationException("恢复原位前，轴 '" + axes[index].Name + "' 存在运动、报警或限位。");
                }
            }

            var profile = new AxisMotionProfile(5, 10, 10);
            await Task.WhenAll(Enumerable.Range(0, AxisCount).Select(index =>
                axes[index].MoveAbsoluteAsync(initial[index].Position, profile, CancellationToken.None)))
                .ConfigureAwait(false);
            for (int index = 0; index < AxisCount; index++)
            {
                await EnsureStoppedAtAsync(axes[index], initial[index].Position).ConfigureAwait(false);
            }

            Console.WriteLine("[恢复] 十二根轴均已回到测试前位置并连续确认停稳。" );
        }

        /// <summary>流程报警后检查并受控停止仍在运动的仿真轴，避免清理时遗留运动。</summary>
        private static async Task StopMovingAxesForCleanupAsync(IReadOnlyList<IAxis> axes)
        {
            foreach (IAxis axis in axes)
            {
                AxisStatus status = await axis.ReadStatusAsync(CancellationToken.None).ConfigureAwait(false);
                if (status.Moving || Math.Abs(status.Velocity) > 0.01)
                {
                    await axis.StopAsync(AxisStopMode.Controlled, CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
        }
    }
}
