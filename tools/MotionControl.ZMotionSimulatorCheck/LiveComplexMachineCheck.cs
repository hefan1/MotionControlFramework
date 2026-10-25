using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Adapter.MotionCard.ZMotion;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Runtime.Alarms;
using MotionControl.Framework.Runtime.Machine;
using MotionControl.Framework.Runtime.Safety;
using Newtonsoft.Json.Linq;

namespace MotionControl.ZMotionSimulatorCheck
{
    /// <summary>用一张正运动仿真卡验证十二轴、六任务和一根共用轴的运行过程。</summary>
    internal static class LiveComplexMachineCheck
    {
        /// <summary>本次测试使用的物理轴数量。</summary>
        private const int AxisCount = 12;
        /// <summary>轴状态采样间隔。</summary>
        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
        /// <summary>六个任务全部完成的最长等待时间。</summary>
        private static readonly TimeSpan FlowTimeout = TimeSpan.FromSeconds(90);
        /// <summary>十二根轴在页面上的逻辑名称，顺序对应物理轴 0 到 11。</summary>
        private static readonly string[] AxisNames = {
            "A_X", "A_Y", "A_T1", "A_T2", "B_X", "B_Y", "B_T1", "B_T2",
            "Shared_Work_Z", "Transfer_X", "Transfer_Z", "Feeder_X"
        };

        /// <summary>运行仿真检查；连接不成功与流程失败分别返回不同结果。</summary>
        public static int Run()
        {
            try { return RunAsync().GetAwaiter().GetResult(); }
            catch (ZMotionApiException error) when (error.ErrorCode == 20008)
            {
                Console.Error.WriteLine("[连接未建立] SDK 返回 20008；本次检查器没有建立到 VPLC5xx-Simu 的独立会话，不能据此判断十二轴或流程能力失败。请保持 ZDevelop 仿真器运行。");
                return 2;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("[失败] 十二轴六任务仿真检查：" + error);
                return 1;
            }
        }

        /// <summary>先只读确认十二轴，再配置参数、执行并行流程，最后暂停并恢复原始位置。</summary>
        private static async Task<int> RunAsync()
        {
            ZMotionCard card = null;
            MachineRuntime runtime = null;
            List<IAxis> axes = null;
            AxisStatus[] initial = null;
            bool parametersApplied = false;
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
                    cardItem.Value<string>("connectionConfigurationFile"))
                    .ToObject<ZMotionCardConfiguration>();
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
                var signals = new ComplexMachineSignals();
                var aPosition = new ComplexPositionFlow("A侧定位", axes[0], axes[1], cards.AxisResources, signals, true);
                var bPosition = new ComplexPositionFlow("B侧定位", axes[4], axes[5], cards.AxisResources, signals, false);
                var aInspection = new ComplexInspectionFlow("A侧检测", axes[2], axes[3], axes[8], cards.AxisResources, signals, true);
                var bInspection = new ComplexInspectionFlow("B侧检测", axes[6], axes[7], axes[8], cards.AxisResources, signals, false);
                var transfer = new ComplexTransferFlow(axes[9], axes[10], cards.AxisResources, signals);
                var feeder = new ComplexFeederFlow(axes[11], cards.AxisResources, signals);
                runtime = new MachineRuntime(cards,
                    new MotionControl.Framework.Contracts.Flow.IStationFlow[] {
                        aPosition, bPosition, aInspection, bInspection, transfer, feeder },
                    new MachineAlarmService(),
                    new LogicalIoEmergencyStopStatus(card,
                        safetyJson.Value<string>("emergencyStopInputName")));

                await runtime.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
                if (!string.Equals(card.ActualControllerType, ZMotionCard.SimulationControllerType,
                    StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("控制器不是 VPLC5xx-Simu，测试禁止继续。");

                initial = new AxisStatus[AxisCount];
                for (int index = 0; index < AxisCount; index++)
                {
                    initial[index] = await axes[index].ReadStatusAsync(CancellationToken.None).ConfigureAwait(false);
                    EnsureReadyForTest(axes[index].Name, initial[index], axisMappings[index]);
                    Console.WriteLine("[只读预检] 物理轴 " + index + " / " + axes[index].Name
                        + "，位置=" + initial[index].Position.ToString("F3"));
                }
                Console.WriteLine("[预检通过] 十二根轴全部可读且停稳；本次测试不会写任何 DO。");

                double[] target = new double[AxisCount];
                for (int index = 0; index < AxisCount; index++)
                    target[index] = SafeTarget(initial[index].Position, axisMappings[index],
                        index % 2 == 0 ? 20 : -20);
                double sharedBTarget = SafeTarget(initial[8].Position, axisMappings[8], -20);
                if (Math.Abs(sharedBTarget - target[8]) < 5)
                    throw new InvalidOperationException("共用 Z 轴两次检测的目标过近，不能验证串行占用。");

                aPosition.SetTargets(target[0], target[1]);
                bPosition.SetTargets(target[4], target[5]);
                aInspection.SetTargets(target[2], target[3], target[8]);
                bInspection.SetTargets(target[6], target[7], sharedBTarget);
                transfer.SetTargets(target[9], target[10]);
                feeder.SetTarget(target[11]);

                await card.ApplyAxisParametersAsync(CancellationToken.None).ConfigureAwait(false);
                parametersApplied = true;
                await runtime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
                await runtime.StartAsync(CancellationToken.None).ConfigureAwait(false);
                int firstPeak = await WaitForInitialMotionAsync(axes).ConfigureAwait(false);
                int movingMidway = await WaitForMotionInProgressAsync(axes, initial, target).ConfigureAwait(false);
                await runtime.PauseAsync(CancellationToken.None).ConfigureAwait(false);
                int midway = await CountIntermediateStoppedAxesAsync(axes, initial, target).ConfigureAwait(false);
                if (midway < 4)
                    throw new InvalidOperationException("暂停时只有 " + midway + " 根轴停在起点和目标之间，未充分验证运动中暂停。");
                Console.WriteLine("[暂停通过] 首轮同时运动峰值=" + firstPeak + "；发出暂停前 "
                    + movingMidway + " 根轴处于运动中段，停止后 " + midway + " 根轴停在中途。");

                await runtime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
                await runtime.StartAsync(CancellationToken.None).ConfigureAwait(false);
                await WaitForSharedAxisContentionAsync(aInspection, bInspection).ConfigureAwait(false);
                int movingBeforeSharedPause = await CountMovingAxesAsync(axes).ConfigureAwait(false);
                if (movingBeforeSharedPause < 1)
                    throw new InvalidOperationException("检测任务争用共用 Z 轴时没有观察到实际运动，不能执行占用期间暂停检查。");
                await runtime.PauseAsync(CancellationToken.None).ConfigureAwait(false);
                int movingAfterSharedPause = await CountMovingAxesAsync(axes).ConfigureAwait(false);
                if (movingAfterSharedPause != 0 || signals.SharedAxisOwners != 0)
                    throw new InvalidOperationException("共用 Z 轴争用期间暂停后仍有轴运动或资源占用未释放。");
                Console.WriteLine("[共享轴暂停通过] 争用期间观察到 " + movingBeforeSharedPause
                    + " 根轴运动；暂停后全部停止且共用轴占用数为 0。");

                await runtime.ResetAsync(CancellationToken.None).ConfigureAwait(false);
                await runtime.StartAsync(CancellationToken.None).ConfigureAwait(false);
                int resumePeak = await WaitForFlowsAsync(axes, runtime,
                    aPosition, bPosition, aInspection, bInspection, transfer, feeder).ConfigureAwait(false);
                if (Math.Max(firstPeak, resumePeak) < 7)
                    throw new InvalidOperationException("未观察到七根独立轴同时运动，峰值=" + Math.Max(firstPeak, resumePeak));
                if (Math.Max(aInspection.ResourceWaitMilliseconds, bInspection.ResourceWaitMilliseconds) < 500)
                    throw new InvalidOperationException("两侧检测没有观察到共用 Z 轴排队等待。");

                target[8] = signals.LastSharedAxisTarget;
                await runtime.PauseAsync(CancellationToken.None).ConfigureAwait(false);
                for (int index = 0; index < AxisCount; index++)
                    EnsureStoppedAt(axes[index].Name,
                        await axes[index].ReadStatusAsync(CancellationToken.None).ConfigureAwait(false), target[index]);
                Console.WriteLine("[流程通过] 六任务完成；共用 Z 轴等待 A="
                    + aInspection.ResourceWaitMilliseconds + "ms，B="
                    + bInspection.ResourceWaitMilliseconds + "ms；十二轴目标到位。");

                await RestoreAxesAsync(axes, initial).ConfigureAwait(false);
                restored = true;
                Console.WriteLine("[通过] 十二轴、六任务、七轴并行、共用轴排队及占用期间暂停恢复、原位恢复均完成；未写入 DO。");
                return 0;
            }
            finally
            {
                if (runtime != null && (runtime.State == MachineRunState.Running || runtime.State == MachineRunState.Pausing))
                    await runtime.PauseAsync(CancellationToken.None).ConfigureAwait(false);
                if (!restored && parametersApplied && axes != null && initial != null
                    && initial.All(status => status != null) && runtime != null
                    && runtime.State == MachineRunState.Paused)
                {
                    try { await RestoreAxesAsync(axes, initial).ConfigureAwait(false); }
                    catch (Exception error) { Console.Error.WriteLine("[恢复未完成] " + error.Message); }
                }
                if (runtime != null && runtime.State != MachineRunState.Disconnected)
                    await runtime.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
                else if (card != null && card.IsConnected)
                    await card.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }

        /// <summary>读取项目附带的仿真配置文件，缺文件时直接报告文件名。</summary>
        private static JObject ReadJson(string folder, string fileName)
        {
            string path = Path.Combine(folder, fileName);
            if (!File.Exists(path)) throw new FileNotFoundException("缺少仿真配置。", path);
            return JObject.Parse(File.ReadAllText(path));
        }

        /// <summary>只在测试进程内把样例轴参数扩展成物理轴 0 到 11 的逻辑映射。</summary>
        private static List<ZMotionAxisConfiguration> CreateAxisMappings(
            ZMotionAxisConfiguration template, string controllerId)
        {
            var mappings = new List<ZMotionAxisConfiguration>();
            for (int index = 0; index < AxisCount; index++)
            {
                mappings.Add(new ZMotionAxisConfiguration {
                    ControllerId = controllerId, Name = AxisNames[index], AxisNumber = index,
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
            return mappings;
        }

        /// <summary>写参数和运动之前确认每根轴可读、停稳、无报警且位置在测试范围内。</summary>
        private static void EnsureReadyForTest(string name, AxisStatus status, ZMotionAxisConfiguration mapping)
        {
            if (status.Moving || Math.Abs(status.Velocity) > 0.01 || status.AlarmActive
                || status.PositiveLimitActive != false || status.NegativeLimitActive != false
                || status.PositiveSoftLimitActive != false || status.NegativeSoftLimitActive != false)
                throw new InvalidOperationException("轴 '" + name + "' 未停稳或存在报警/限位，预检禁止运动。"
                    + status.AlarmMessage);
            if (double.IsNaN(status.Position) || double.IsInfinity(status.Position)
                || status.Position < mapping.MinimumPosition + 25
                || status.Position > mapping.MaximumPosition - 25)
                throw new InvalidOperationException("轴 '" + name + "' 的当前位置 " + status.Position
                    + " 离软件行程边界过近，预检禁止运动。");
        }

        /// <summary>优先按指定方向移动二十个工程单位；接近软限位时自动反向。</summary>
        private static double SafeTarget(double start, ZMotionAxisConfiguration mapping, double distance)
        {
            double target = start + distance;
            if (target < mapping.MinimumPosition + 5 || target > mapping.MaximumPosition - 5)
                target = start - distance;
            if (target < mapping.MinimumPosition + 5 || target > mapping.MaximumPosition - 5)
                throw new InvalidOperationException("轴 '" + mapping.Name + "' 无法找到安全的仿真测试目标。");
            return target;
        }

        /// <summary>在首轮定位时观察 A/B、搬运和送料共七根轴的运动重叠。</summary>
        private static async Task<int> WaitForInitialMotionAsync(IReadOnlyList<IAxis> axes)
        {
            int[] firstAxes = { 0, 1, 4, 5, 9, 10, 11 };
            var elapsed = Stopwatch.StartNew();
            int peak = 0;
            while (elapsed.Elapsed < TimeSpan.FromSeconds(20))
            {
                int moving = 0;
                foreach (int index in firstAxes)
                {
                    AxisStatus status = await axes[index].ReadStatusAsync(CancellationToken.None).ConfigureAwait(false);
                    if (status.Moving || Math.Abs(status.Velocity) > 0.01) moving++;
                }
                peak = Math.Max(peak, moving);
                if (moving == firstAxes.Length) return peak;
                await Task.Delay(PollInterval).ConfigureAwait(false);
            }
            throw new TimeoutException("首轮未观察到七根独立轴同时运动，最多只看到 " + peak + " 根。");
        }

        /// <summary>确认暂停后至少四根首轮轴确实停在起点与目标之间。</summary>
        private static async Task<int> CountIntermediateStoppedAxesAsync(
            IReadOnlyList<IAxis> axes, IReadOnlyList<AxisStatus> initial, IReadOnlyList<double> target)
        {
            int[] firstAxes = { 0, 1, 4, 5, 9, 10, 11 };
            int middleCount = 0;
            foreach (int index in firstAxes)
            {
                AxisStatus status = await axes[index].ReadStatusAsync(CancellationToken.None).ConfigureAwait(false);
                if (status.Moving || Math.Abs(status.Velocity) > 0.01)
                    throw new InvalidOperationException("整机暂停后轴 '" + axes[index].Name + "' 仍在运动。");
                double direction = Math.Sign(target[index] - initial[index].Position);
                double traveled = (status.Position - initial[index].Position) * direction;
                double total = Math.Abs(target[index] - initial[index].Position);
                if (traveled > 0.05 && traveled < total - 0.05) middleCount++;
                Console.WriteLine("[暂停位置] " + axes[index].Name + " 起点="
                    + initial[index].Position.ToString("F3") + "，当前位置="
                    + status.Position.ToString("F3") + "，目标=" + target[index].ToString("F3"));
            }
            return middleCount;
        }

        /// <summary>按实时位置等待至少四根轴正在途中运动，避免用固定延时猜测暂停时机。</summary>
        private static async Task<int> WaitForMotionInProgressAsync(
            IReadOnlyList<IAxis> axes, IReadOnlyList<AxisStatus> initial, IReadOnlyList<double> target)
        {
            int[] firstAxes = { 0, 1, 4, 5, 9, 10, 11 };
            var elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed < TimeSpan.FromSeconds(10))
            {
                int movingMidway = 0;
                foreach (int index in firstAxes)
                {
                    AxisStatus status = await axes[index].ReadStatusAsync(CancellationToken.None).ConfigureAwait(false);
                    double direction = Math.Sign(target[index] - initial[index].Position);
                    double traveled = (status.Position - initial[index].Position) * direction;
                    double total = Math.Abs(target[index] - initial[index].Position);
                    if ((status.Moving || Math.Abs(status.Velocity) > 0.01)
                        && traveled > 0.05 && traveled < total - 0.05)
                        movingMidway++;
                }
                if (movingMidway >= 4) return movingMidway;
                await Task.Delay(50).ConfigureAwait(false);
            }
            throw new TimeoutException("未能按实时位置观察到至少四根轴同时位于运动中段。");
        }

        /// <summary>等到一个检测任务持有共用轴运动、另一个任务正排队等待。</summary>
        private static async Task WaitForSharedAxisContentionAsync(
            ComplexInspectionFlow aInspection, ComplexInspectionFlow bInspection)
        {
            var elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed < TimeSpan.FromSeconds(30))
            {
                bool aIsMoving = aInspection.CurrentStepName == "三轴同时动作，等待全部到位";
                bool bIsMoving = bInspection.CurrentStepName == "三轴同时动作，等待全部到位";
                bool aIsWaiting = aInspection.CurrentStepName == "申请两根工具轴和共用 Z 轴";
                bool bIsWaiting = bInspection.CurrentStepName == "申请两根工具轴和共用 Z 轴";
                if ((aIsMoving && bIsWaiting) || (bIsMoving && aIsWaiting))
                {
                    Console.WriteLine("[共用轴排队] 一侧检测正在驱动三轴，另一侧正在申请同一根 Z 轴。");
                    return;
                }
                await Task.Delay(50).ConfigureAwait(false);
            }
            throw new TimeoutException("未能同时观察到共用 Z 轴的持有任务和等待任务。");
        }

        /// <summary>读取十二根轴并统计当前实际运动数量。</summary>
        private static async Task<int> CountMovingAxesAsync(IReadOnlyList<IAxis> axes)
        {
            int count = 0;
            foreach (IAxis axis in axes)
            {
                AxisStatus status = await axis.ReadStatusAsync(CancellationToken.None).ConfigureAwait(false);
                if (status.Moving || Math.Abs(status.Velocity) > 0.01) count++;
            }
            return count;
        }

        /// <summary>观察恢复后的轴运动，并等待六个工位任务全部完成或报告运行故障。</summary>
        private static async Task<int> WaitForFlowsAsync(IReadOnlyList<IAxis> axes, MachineRuntime runtime,
            ComplexPositionFlow aPosition, ComplexPositionFlow bPosition,
            ComplexInspectionFlow aInspection, ComplexInspectionFlow bInspection,
            ComplexTransferFlow transfer, ComplexFeederFlow feeder)
        {
            var elapsed = Stopwatch.StartNew();
            int peak = 0;
            int lastSecond = -1;
            while (elapsed.Elapsed < FlowTimeout)
            {
                int moving = 0;
                foreach (IAxis axis in axes)
                {
                    AxisStatus status = await axis.ReadStatusAsync(CancellationToken.None).ConfigureAwait(false);
                    if (status.Moving || Math.Abs(status.Velocity) > 0.01) moving++;
                }
                peak = Math.Max(peak, moving);
                int second = (int)elapsed.Elapsed.TotalSeconds;
                if (second != lastSecond)
                {
                    Console.WriteLine("[运行 " + second + " 秒] 当前运动轴=" + moving
                        + "；同时运动峰值=" + peak + "；A检测=" + aInspection.CurrentStepName
                        + "；B检测=" + bInspection.CurrentStepName);
                    lastSecond = second;
                }
                if (runtime.State == MachineRunState.Alarmed)
                    throw new InvalidOperationException("整机运行报警。", runtime.LastError);
                if (aPosition.Completed && bPosition.Completed && aInspection.Completed
                    && bInspection.Completed && transfer.Completed && feeder.Completed)
                    return peak;
                await Task.Delay(PollInterval).ConfigureAwait(false);
            }
            throw new TimeoutException("十二轴六任务在 90 秒内未全部完成。");
        }

        /// <summary>检查轴已经停止并到达预定位置。</summary>
        private static void EnsureStoppedAt(string name, AxisStatus status, double target)
        {
            if (status.Moving || Math.Abs(status.Velocity) > 0.01
                || Math.Abs(status.Position - target) > 0.05)
                throw new InvalidOperationException("轴 '" + name + "' 未稳定到位。当前位置="
                    + status.Position + "，目标=" + target + "。");
        }

        /// <summary>所有流程暂停后，将十二根轴并行移回各自的测试前位置并复核。</summary>
        private static async Task RestoreAxesAsync(IReadOnlyList<IAxis> axes, IReadOnlyList<AxisStatus> initial)
        {
            foreach (IAxis axis in axes)
            {
                AxisStatus status = await axis.ReadStatusAsync(CancellationToken.None).ConfigureAwait(false);
                if (status.Moving || status.AlarmActive || status.PositiveLimitActive != false
                    || status.NegativeLimitActive != false || status.PositiveSoftLimitActive != false
                    || status.NegativeSoftLimitActive != false)
                    throw new InvalidOperationException("恢复原位前，轴 '" + axis.Name + "' 存在运动、报警或限位。");
            }
            var profile = new AxisMotionProfile(5, 10, 10);
            await Task.WhenAll(Enumerable.Range(0, AxisCount).Select(index =>
                axes[index].MoveAbsoluteAsync(initial[index].Position, profile, CancellationToken.None)))
                .ConfigureAwait(false);
            for (int index = 0; index < AxisCount; index++)
            {
                int stable = 0;
                while (stable < 3)
                {
                    AxisStatus status = await axes[index].ReadStatusAsync(CancellationToken.None).ConfigureAwait(false);
                    EnsureStoppedAt(axes[index].Name, status, initial[index].Position);
                    stable++;
                    await Task.Delay(PollInterval).ConfigureAwait(false);
                }
            }
            Console.WriteLine("[恢复] 十二根轴均已回到测试前位置并连续确认停稳。");
        }
    }
}
