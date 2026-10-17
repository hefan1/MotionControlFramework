using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Adapter.MotionCard.ZMotion;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Runtime.Alarms;
using MotionControl.Framework.Runtime.Machine;
using Newtonsoft.Json;

namespace MotionControl.ZMotionSimulatorCheck
{
    /// <summary>读取 ZDevelop 仿真配置并检查正运动适配器与单机运行时。</summary>
    internal static class Program
    {
        /// <summary>启动仿真器连接检查，并按命令行选项运行扩展验证。</summary>
        private static int Main(string[] args)
        {
            return MainAsync(args).GetAwaiter().GetResult();
        }

        /// <summary>解析命令行参数并执行仿真器检查。</summary>
        private static async Task<int> MainAsync(string[] args)
        {
            try
            {
                var options = ParseOptions(args);
                if (options.ShowHelp)
                {
                    PrintUsage();
                    return 0;
                }

                if (options.RuntimeRegressionOnly)
                {
                    var failures = await RuntimeRegressionChecks.RunAsync().ConfigureAwait(false);
                    if (failures.Count > 0)
                    {
                        throw new InvalidOperationException("Runtime 回归检查失败：" + string.Join("；", failures));
                    }

                    Console.WriteLine("Runtime 回归检查全部通过，不需要连接仿真器。");
                    return 0;
                }

                await RunChecksAsync(options).ConfigureAwait(false);
                Console.WriteLine("已请求的正运动仿真器检查全部通过。");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("仿真器检查失败：" + error);
                return 1;
            }
        }

        /// <summary>从命令行读取配置目录、轴运动检查和 IO 输出检查选项。</summary>
        private static CheckOptions ParseOptions(string[] args)
        {
            var options = new CheckOptions
            {
                ConfigurationDirectory = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "Samples",
                    "ZDevelopSimulation")
            };

            for (var index = 0; index < args.Length; index++)
            {
                var argument = args[index];
                if (string.Equals(argument, "--help", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(argument, "-h", StringComparison.OrdinalIgnoreCase))
                {
                    options.ShowHelp = true;
                }
                else if (string.Equals(argument, "--exercise-motion", StringComparison.OrdinalIgnoreCase))
                {
                    options.ExerciseMotion = true;
                }
                else if (string.Equals(argument, "--read-status", StringComparison.OrdinalIgnoreCase))
                {
                    options.ReadStatus = true;
                }
                else if (string.Equals(argument, "--exercise-io", StringComparison.OrdinalIgnoreCase))
                {
                    options.ExerciseIo = true;
                }
                else if (string.Equals(argument, "--exercise-runtime", StringComparison.OrdinalIgnoreCase))
                {
                    options.ExerciseRuntime = true;
                }
                else if (string.Equals(argument, "--runtime-regression", StringComparison.OrdinalIgnoreCase))
                {
                    options.RuntimeRegressionOnly = true;
                }
                else if (string.Equals(argument, "--exercise-home", StringComparison.OrdinalIgnoreCase))
                {
                    options.ExerciseHome = true;
                }
                else if (string.Equals(argument, "--home-mode", StringComparison.OrdinalIgnoreCase))
                {
                    if (index + 1 >= args.Length
                        || !int.TryParse(args[++index], out var homeMode)
                        || homeMode < 0)
                    {
                        throw new ArgumentException("--home-mode 后面必须提供大于或等于 0 的正运动 DATUM 模式号。");
                    }

                    options.HomeModeOverride = homeMode;
                    options.ExerciseHome = true;
                }
                else if (string.Equals(argument, "--config-dir", StringComparison.OrdinalIgnoreCase))
                {
                    if (index + 1 >= args.Length)
                    {
                        throw new ArgumentException("--config-dir 后面必须提供配置目录路径。");
                    }

                    options.ConfigurationDirectory = Path.GetFullPath(args[++index]);
                }
                else
                {
                    throw new ArgumentException("无法识别命令行参数：" + argument);
                }
            }

            return options;
        }

        /// <summary>加载样例配置、连接正运动仿真器，并按选项执行扩展检查。</summary>
        private static async Task RunChecksAsync(CheckOptions options)
        {
            var configurationDirectory = Path.GetFullPath(options.ConfigurationDirectory);
            var motionCardsPath = GetConfigurationFilePath(configurationDirectory, "MotionCards.json");
            var axisParametersPath = GetConfigurationFilePath(configurationDirectory, "AxisParameters.json");
            var ioMappingPath = GetConfigurationFilePath(configurationDirectory, "IoMapping.json");

            var motionCards = LoadJson<MotionCardsDocument>(motionCardsPath);
            var axisParameters = LoadJson<AxisParametersDocument>(axisParametersPath);
            var ioMapping = LoadJson<IoMappingDocument>(ioMappingPath);
            ValidateConfigurationDocuments(motionCards, axisParameters, ioMapping);
            options.IoMapping = ioMapping;

            var cardDefinition = motionCards.MotionCards.Single();
            var cardConfigurationPath = GetConfigurationFilePath(
                configurationDirectory,
                cardDefinition.ConnectionConfigurationFile);
            var cardConfiguration = LoadJson<ZMotionCardConfiguration>(cardConfigurationPath);
            var axesForCard = axisParameters.Axes
                .Where(axis => string.Equals(
                    axis.ControllerId,
                    cardDefinition.ControllerId,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (options.HomeModeOverride.HasValue)
            {
                axesForCard[0].HomeMode = options.HomeModeOverride.Value;
            }

            var inputsForCard = ioMapping.DigitalInputs
                .Where(point => string.Equals(
                    point.ControllerId,
                    cardDefinition.ControllerId,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
            var outputsForCard = ioMapping.DigitalOutputs
                .Where(point => string.Equals(
                    point.ControllerId,
                    cardDefinition.ControllerId,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();

            var card = new ZMotionCard(
                cardDefinition.ControllerId,
                cardConfiguration,
                axesForCard,
                inputsForCard,
                outputsForCard);

            await RunAdapterCheckAsync(card, axesForCard, options, CancellationToken.None).ConfigureAwait(false);
            if (options.ExerciseRuntime)
            {
                await RunMachineRuntimeCheckAsync(card, CancellationToken.None).ConfigureAwait(false);
            }
        }

        /// <summary>校验样例版本、控制卡选择和轴/IO 配置关联。</summary>
        private static void ValidateConfigurationDocuments(
            MotionCardsDocument motionCards,
            AxisParametersDocument axisParameters,
            IoMappingDocument ioMapping)
        {
            if (motionCards.SchemaVersion != 1
                || axisParameters.SchemaVersion != 1
                || ioMapping.SchemaVersion != 1)
            {
                throw new InvalidOperationException("仿真验证工具当前只支持 schemaVersion 1。");
            }

            if (motionCards.MotionCards == null || motionCards.MotionCards.Count != 1)
            {
                throw new InvalidOperationException("仿真检查要求 MotionCards.json 中恰好配置一张卡。");
            }

            var card = motionCards.MotionCards[0];
            if (card == null
                || string.IsNullOrWhiteSpace(card.ControllerId)
                || !string.Equals(card.AdapterType, "ZMotion", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(card.ConnectionConfigurationFile))
            {
                throw new InvalidOperationException("仿真控制卡配置缺少控制卡标识、ZMotion 适配器或连接配置文件。");
            }

            if (axisParameters.Axes == null || axisParameters.Axes.Count == 0)
            {
                throw new InvalidOperationException("AxisParameters.json 至少需要配置一个仿真轴。");
            }

            if (ioMapping.DigitalInputs == null || ioMapping.DigitalOutputs == null)
            {
                throw new InvalidOperationException("IoMapping.json 中的数字输入和数字输出列表不能为空。");
            }

            if (!axisParameters.Axes.Any(axis => string.Equals(
                axis.ControllerId,
                card.ControllerId,
                StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("仿真控制卡没有关联的轴参数。");
            }
        }

        /// <summary>连接 ZDevelop 仿真器，检查身份、轴状态、DI/DO，并按选项执行运动和输出测试。</summary>
        private static async Task RunAdapterCheckAsync(
            ZMotionCard card,
            IReadOnlyCollection<ZMotionAxisConfiguration> axisConfigurations,
            CheckOptions options,
            CancellationToken cancellationToken)
        {
            await card.ConnectAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!string.Equals(
                    card.ActualControllerType,
                    ZMotionCard.SimulationControllerType,
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "连接成功但控制器身份不匹配：" + card.ActualControllerType + "。");
                }

                Console.WriteLine(
                    "[通过] 已连接 " + card.ActualControllerType
                    + "，版本 " + card.ActualControllerVersion
                    + "，控制器身份 " + card.ActualControllerId + "。");

                if (options.ReadStatus)
                {
                    foreach (var axis in card.Axes)
                    {
                        var status = await axis.ReadStatusAsync(cancellationToken).ConfigureAwait(false);
                        Console.WriteLine(
                            "[通过] 逻辑轴 " + axis.Name
                            + "：位置=" + status.Position
                            + "，伺服=" + status.ServoEnabled
                            + "，运动中=" + status.Moving
                            + "，正限位=" + status.PositiveLimitActive
                            + "，负限位=" + status.NegativeLimitActive
                            + "，已回零=" + status.Homed
                            + "，报警=" + status.AlarmActive + "。");
                    }

                    foreach (var point in options.IoMapping.DigitalInputs)
                    {
                        var value = await card.ReadInputAsync(point.Name, cancellationToken).ConfigureAwait(false);
                        Console.WriteLine("[通过] DI " + point.Name + "=" + value + "。");
                    }

                    foreach (var point in options.IoMapping.DigitalOutputs)
                    {
                        var value = await card.ReadOutputAsync(point.Name, cancellationToken).ConfigureAwait(false);
                        Console.WriteLine("[通过] DO " + point.Name + "=" + value + "。");
                    }
                }

                if (options.ExerciseMotion || options.ExerciseHome)
                {
                    var selectedAxis = card.Axes.First();
                    var axisConfiguration = axisConfigurations.First(axis => string.Equals(
                        axis.Name,
                        selectedAxis.Name,
                        StringComparison.OrdinalIgnoreCase));
                    await card.ApplyAxisParametersAsync(cancellationToken).ConfigureAwait(false);
                    Console.WriteLine("[通过] 仿真轴单位和软限位参数已应用。");
                    if (options.ExerciseMotion)
                    {
                        await ExerciseAxisAsync(
                            selectedAxis,
                            axisConfiguration,
                            cancellationToken).ConfigureAwait(false);
                    }

                    if (options.ExerciseHome)
                    {
                        await ExerciseHomeAsync(selectedAxis, cancellationToken).ConfigureAwait(false);
                    }
                }

                if (options.ExerciseIo)
                {
                    var output = options.IoMapping.DigitalOutputs.FirstOrDefault();
                    if (output == null)
                    {
                        throw new InvalidOperationException("没有配置可用于往返读写检查的仿真 DO 点位。");
                    }

                    await ExerciseOutputAsync(card, output.Name, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                if (card.IsConnected)
                {
                    await card.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
        }

        /// <summary>在仿真软限位内将首个逻辑轴往返移动一个工程单位，并恢复原伺服状态。</summary>
        private static async Task ExerciseAxisAsync(
            IAxis axis,
            ZMotionAxisConfiguration configuration,
            CancellationToken cancellationToken)
        {
            var originalStatus = await axis.ReadStatusAsync(cancellationToken).ConfigureAwait(false);
            var targetPosition = originalStatus.Position + 1.0;
            if (targetPosition > configuration.MaximumPosition)
            {
                targetPosition = originalStatus.Position - 1.0;
            }

            if (targetPosition < configuration.MinimumPosition
                || targetPosition > configuration.MaximumPosition)
            {
                throw new InvalidOperationException("仿真轴当前位置附近没有可供单步往返的软限位空间。");
            }

            var profile = new AxisMotionProfile(5.0, 10.0, 10.0);
            var servoWasEnabled = originalStatus.ServoEnabled;
            if (!servoWasEnabled)
            {
                await axis.EnableServoAsync(cancellationToken).ConfigureAwait(false);
            }

            try
            {
                await axis.MoveAbsoluteAsync(targetPosition, profile, cancellationToken).ConfigureAwait(false);
                await axis.MoveAbsoluteAsync(originalStatus.Position, profile, cancellationToken).ConfigureAwait(false);
                var returnedStatus = await axis.ReadStatusAsync(cancellationToken).ConfigureAwait(false);
                if (Math.Abs(returnedStatus.Position - originalStatus.Position) > 0.05)
                {
                    throw new InvalidOperationException(
                        "仿真轴往返定位后未回到起始位置，起点=" + originalStatus.Position
                        + "，当前位置=" + returnedStatus.Position + "。");
                }
            }
            finally
            {
                if (!servoWasEnabled)
                {
                    await axis.DisableServoAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }

            Console.WriteLine("[通过] 逻辑轴 " + axis.Name + " 完成一个工程单位往返定位。");
        }

        /// <summary>通过真实正运动 DATUM 指令执行样例轴回零，并恢复测试前的伺服状态。</summary>
        private static async Task ExerciseHomeAsync(IAxis axis, CancellationToken cancellationToken)
        {
            var originalStatus = await axis.ReadStatusAsync(cancellationToken).ConfigureAwait(false);
            if (!originalStatus.ServoEnabled)
            {
                await axis.EnableServoAsync(cancellationToken).ConfigureAwait(false);
            }

            try
            {
                await axis.HomeAsync(cancellationToken).ConfigureAwait(false);
                var homedStatus = await axis.ReadStatusAsync(cancellationToken).ConfigureAwait(false);
                if (!homedStatus.Homed)
                {
                    throw new InvalidOperationException("正运动控制器报告回零调用结束，但框架没有记录到回零完成状态。");
                }
            }
            finally
            {
                if (!originalStatus.ServoEnabled)
                {
                    await axis.DisableServoAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }

            Console.WriteLine("[通过] 逻辑轴 " + axis.Name + " 的正运动 DATUM 回零完成。");
        }

        /// <summary>翻转一个仿真 DO 并确认读回，最后恢复测试前的逻辑值。</summary>
        private static async Task ExerciseOutputAsync(
            ZMotionCard card,
            string outputName,
            CancellationToken cancellationToken)
        {
            var originalValue = await card.ReadOutputAsync(outputName, cancellationToken).ConfigureAwait(false);
            var testValue = !originalValue;
            await card.WriteOutputAsync(outputName, testValue, cancellationToken).ConfigureAwait(false);
            var actualValue = await card.ReadOutputAsync(outputName, cancellationToken).ConfigureAwait(false);
            if (actualValue != testValue)
            {
                await card.WriteOutputAsync(outputName, originalValue, CancellationToken.None).ConfigureAwait(false);
                throw new InvalidOperationException("仿真 DO 写入后读回值不一致：" + outputName + "。");
            }

            await card.WriteOutputAsync(outputName, originalValue, CancellationToken.None).ConfigureAwait(false);
            Console.WriteLine("[通过] 仿真 DO " + outputName + " 翻转读回正常，且已恢复原值。");
        }

        /// <summary>通过实际仿真卡实例验证 Runtime 连接、复位、启动、暂停、复位和断开状态流程。</summary>
        private static async Task RunMachineRuntimeCheckAsync(
            ZMotionCard card,
            CancellationToken cancellationToken)
        {
            var motionCards = new MotionCardGroup(new[] { card });
            var stationFlow = new WaitingStationFlow();
            var runtime = new MachineRuntime(
                motionCards,
                new IStationFlow[] { stationFlow },
                new MachineAlarmService(),
                TimeSpan.FromSeconds(10));

            await runtime.ConnectAsync(cancellationToken).ConfigureAwait(false);
            await runtime.ResetAsync(cancellationToken).ConfigureAwait(false);
            EnsureRuntimeState(runtime, MachineRunState.Ready, "复位后");

            await runtime.StartAsync(cancellationToken).ConfigureAwait(false);
            EnsureRuntimeState(runtime, MachineRunState.Running, "启动后");

            await runtime.PauseAsync(cancellationToken).ConfigureAwait(false);
            EnsureRuntimeState(runtime, MachineRunState.Paused, "暂停后");

            await runtime.ResetAsync(cancellationToken).ConfigureAwait(false);
            EnsureRuntimeState(runtime, MachineRunState.Ready, "再次复位后");

            await runtime.DisconnectAsync(cancellationToken).ConfigureAwait(false);
            EnsureRuntimeState(runtime, MachineRunState.Disconnected, "断开后");
            Console.WriteLine("[通过] 单机 Runtime 完成连接、复位、启动、暂停、复位和断开流程。");
        }

        /// <summary>检查 Runtime 是否进入了验证流程要求的状态。</summary>
        private static void EnsureRuntimeState(
            MachineRuntime runtime,
            MachineRunState expectedState,
            string operationName)
        {
            if (runtime.State != expectedState)
            {
                throw new InvalidOperationException(
                    operationName + "状态不正确，期望 " + expectedState + "，实际 " + runtime.State + "。");
            }
        }

        /// <summary>读取指定 JSON 文件并在解析失败时保留文件路径上下文。</summary>
        private static T LoadJson<T>(string filePath)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException("仿真配置文件不存在。", filePath);
            }

            try
            {
                var json = File.ReadAllText(filePath);
                var configuration = JsonConvert.DeserializeObject<T>(json);
                if (ReferenceEquals(configuration, null))
                {
                    throw new InvalidOperationException("JSON 文件没有包含可读取的配置对象。");
                }

                return configuration;
            }
            catch (Exception error)
            {
                throw new InvalidOperationException("读取仿真配置失败：" + filePath, error);
            }
        }

        /// <summary>组合并检查配置文件路径，禁止配置项越出当前仿真配置目录。</summary>
        private static string GetConfigurationFilePath(string configurationDirectory, string relativeFileName)
        {
            if (string.IsNullOrWhiteSpace(relativeFileName) || Path.IsPathRooted(relativeFileName))
            {
                throw new ArgumentException("仿真配置文件必须使用相对文件名。", nameof(relativeFileName));
            }

            var fullPath = Path.GetFullPath(Path.Combine(configurationDirectory, relativeFileName));
            var directoryPrefix = configurationDirectory.TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(directoryPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("仿真配置文件必须位于配置目录内。", nameof(relativeFileName));
            }

            return fullPath;
        }

        /// <summary>显示仿真器检查工具的命令行用法。</summary>
        private static void PrintUsage()
        {
            Console.WriteLine("MotionControl.ZMotionSimulatorCheck [选项]");
            Console.WriteLine("  --config-dir <目录>  指定 MotionCards、AxisParameters、IoMapping 和连接配置目录");
            Console.WriteLine("  默认只连接仿真器并读取控制器身份，不发送运动或 IO 写入命令");
            Console.WriteLine("  --read-status         读取逻辑轴、DI 和 DO 状态，不写入硬件值");
            Console.WriteLine("  --exercise-motion    在仿真软限位内执行一个工程单位的轴往返定位");
            Console.WriteLine("  --exercise-home      按样例轴参数执行真实 DATUM 回零（要求先确认仿真器输入映射）");
            Console.WriteLine("  --home-mode <模式号> 指定本次验证的 DATUM 模式并执行回零，例如 3、4 或 13");
            Console.WriteLine("  --exercise-io        翻转一个仿真 DO，读回后恢复原值");
            Console.WriteLine("  --exercise-runtime   验证 Runtime 连接、复位、启动和暂停生命周期");
            Console.WriteLine("  --runtime-regression 不连接仿真器，回归检查连接回滚、报警和步骤保留");
            Console.WriteLine("  --help               显示帮助");
        }

        /// <summary>保存命令行选项，供配置加载和硬件检查步骤读取。</summary>
        private sealed class CheckOptions
        {
            public string ConfigurationDirectory { get; set; }

            public bool ExerciseMotion { get; set; }

            public bool ExerciseHome { get; set; }

            public int? HomeModeOverride { get; set; }

            public bool ReadStatus { get; set; }

            public bool ExerciseIo { get; set; }

            public bool ExerciseRuntime { get; set; }

            public bool RuntimeRegressionOnly { get; set; }

            public bool ShowHelp { get; set; }

            public IoMappingDocument IoMapping { get; set; }
        }

        /// <summary>保存设备配置中的运动卡列表。</summary>
        private sealed class MotionCardsDocument
        {
            public int SchemaVersion { get; set; }

            public List<MotionCardDefinition> MotionCards { get; set; }
        }

        /// <summary>描述一张卡的稳定标识、适配器选择和厂商连接配置文件。</summary>
        private sealed class MotionCardDefinition
        {
            public string ControllerId { get; set; }

            public string AdapterType { get; set; }

            public string ConnectionConfigurationFile { get; set; }
        }

        /// <summary>保存设备配置中的逻辑轴与轴硬件参数。</summary>
        private sealed class AxisParametersDocument
        {
            public int SchemaVersion { get; set; }

            public List<ZMotionAxisConfiguration> Axes { get; set; }
        }

        /// <summary>保存设备配置中的逻辑 DI/DO 映射。</summary>
        private sealed class IoMappingDocument
        {
            public int SchemaVersion { get; set; }

            public List<ZMotionIoPointConfiguration> DigitalInputs { get; set; }

            public List<ZMotionIoPointConfiguration> DigitalOutputs { get; set; }
        }

        /// <summary>在不发出运动命令的情况下等待 Runtime 的暂停请求。</summary>
        private sealed class WaitingStationFlow : IStationFlow
        {
            public string StationName
            {
                get { return "仿真验证工位"; }
            }

            public string CurrentStepName
            {
                get { return "等待暂停请求"; }
            }

            /// <summary>保持当前验证步骤，直至 Runtime 取消流程令牌。</summary>
            public Task ExecuteCurrentStepAsync(CancellationToken cancellationToken)
            {
                return Task.Delay(Timeout.Infinite, cancellationToken);
            }

            /// <summary>验证流程没有额外的设备状态需要复位。</summary>
            public Task ResetAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }
        }
    }
}
