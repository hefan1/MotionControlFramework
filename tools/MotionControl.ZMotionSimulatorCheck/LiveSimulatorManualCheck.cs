using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using MotionControl.Adapter.MotionCard.ZMotion;
using MotionControl.Framework.Contracts.Configuration;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Runtime.Alarms;
using MotionControl.Framework.Runtime.Machine;
using MotionControl.Framework.Runtime.Safety;
using MotionControl.Framework.Wpf.ManualDebug;
using MotionControl.Host.Wpf;
using Newtonsoft.Json;

namespace MotionControl.ZMotionSimulatorCheck
{
    /// <summary>把生产手动调试页连接到真实 ZDevelop 仿真适配器，供屏幕手动联调。</summary>
    internal static class LiveSimulatorManualCheck
    {
        /// <summary>读取仿真样例配置、连接卡并打开可交互的 Host 手动调试页。</summary>
        public static int Run(string[] arguments)
        {
            ZMotionCard card = null;
            MachineRuntime runtime = null;
            Application application = null;

            try
            {
                var configurationDirectory = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "Samples",
                    "ZDevelopSimulation");
                var cardsDocument = LoadJson<MotionCardsDocument>(configurationDirectory, "MotionCards.json");
                var axesDocument = LoadJson<AxisParametersDocument>(configurationDirectory, "AxisParameters.json");
                var ioDocument = LoadJson<IoMappingDocument>(configurationDirectory, "IoMapping.json");
                var cardDefinition = cardsDocument.MotionCards.Single();
                var cardConfiguration = LoadJson<ZMotionCardConfiguration>(
                    configurationDirectory,
                    cardDefinition.ConnectionConfigurationFile);
                var axisConfigurations = axesDocument.Axes
                    .Where(axis => string.Equals(
                        axis.ControllerId,
                        cardDefinition.ControllerId,
                        StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var homeModeOverride = ReadHomeModeOverride(arguments);
                if (homeModeOverride.HasValue)
                {
                    axisConfigurations[0].HomeMode = homeModeOverride.Value;
                }
                var inputs = ioDocument.DigitalInputs
                    .Where(point => string.Equals(
                        point.ControllerId,
                        cardDefinition.ControllerId,
                        StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var outputs = ioDocument.DigitalOutputs
                    .Where(point => string.Equals(
                        point.ControllerId,
                        cardDefinition.ControllerId,
                        StringComparison.OrdinalIgnoreCase))
                    .ToList();

                card = new ZMotionCard(
                    cardDefinition.ControllerId,
                    cardConfiguration,
                    axisConfigurations,
                    inputs,
                    outputs);
                var cardGroup = new MotionCardGroup(new IMotionCard[] { card });
                runtime = new MachineRuntime(
                    cardGroup,
                    new IStationFlow[] { new WaitingStationFlow() },
                    new MachineAlarmService(),
                    new LogicalIoEmergencyStopStatus(card, ioDocument.EmergencyStopInputName));

                runtime.ConnectAsync(CancellationToken.None).GetAwaiter().GetResult();
                if (!string.Equals(
                    card.ActualControllerType,
                    ZMotionCard.SimulationControllerType,
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "手动调试联调只允许连接 VPLC5xx-Simu，当前控制器身份为 "
                        + card.ActualControllerType + "。");
                }

                card.ApplyAxisParametersAsync(CancellationToken.None).GetAwaiter().GetResult();
                runtime.ResetAsync(CancellationToken.None).GetAwaiter().GetResult();

                application = new Application
                {
                    ShutdownMode = ShutdownMode.OnMainWindowClose
                };
                application.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri(
                        "/MotionControl.Host.Wpf;component/Localization/Strings.zh-CN.xaml",
                        UriKind.Relative)
                });

                var manualPage = new ManualMotionDebugView(
                    runtime,
                    cardGroup,
                    card,
                    axisConfigurations.Select(CreateAxisHardwareDefinition),
                    inputs.Select(point => point.Name),
                    outputs.Select(point => point.Name));
                var hostWindow = new MainWindow();
                hostWindow.SetManualMotionDebugPage(manualPage);
                hostWindow.Closed += (sender, eventArgs) =>
                    runtime.DisconnectAsync(CancellationToken.None).GetAwaiter().GetResult();
                application.MainWindow = hostWindow;

                Console.WriteLine(
                    "[通过] 已连接 " + card.ActualControllerType + "，身份 " + card.ActualControllerId
                    + "，DATUM 模式 " + axisConfigurations[0].HomeMode
                    + "。Host 手动调试页已加载，请在屏幕中进入“手动”并验证轴/IO操作；关闭窗口会断开仿真器连接。");
                return application.Run(hostWindow);
            }
            catch (Exception error)
            {
                if (runtime != null && runtime.State != MachineRunState.Disconnected)
                {
                    try
                    {
                        runtime.DisconnectAsync(CancellationToken.None).GetAwaiter().GetResult();
                    }
                    catch (Exception disconnectError)
                    {
                        Console.Error.WriteLine("[清理失败] 断开仿真控制器时发生异常：" + disconnectError.Message);
                    }
                }
                else if (card != null && card.IsConnected)
                {
                    card.DisconnectAsync(CancellationToken.None).GetAwaiter().GetResult();
                }

                if (application != null)
                {
                    application.Shutdown();
                }

                Console.Error.WriteLine("[失败] 实时手动调试页联调：" + error);
                return 1;
            }
        }

        /// <summary>读取可选 DATUM 模式号，供屏幕手动联调指定正运动回零方式。</summary>
        private static int? ReadHomeModeOverride(string[] arguments)
        {
            for (var index = 0; index < arguments.Length; index++)
            {
                if (!string.Equals(arguments[index], "--home-mode", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int homeMode;
                if (index + 1 >= arguments.Length
                    || !int.TryParse(arguments[index + 1], out homeMode)
                    || homeMode < 0)
                {
                    throw new ArgumentException("--home-mode 后面必须提供大于或等于 0 的 DATUM 模式号。");
                }

                return homeMode;
            }

            return null;
        }

        /// <summary>读取指定仿真配置文件，并在出错时附带文件路径。</summary>
        private static T LoadJson<T>(string directory, string fileName)
        {
            var filePath = Path.Combine(directory, fileName);
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException("实时手动调试所需的配置文件不存在。", filePath);
            }

            try
            {
                var result = JsonConvert.DeserializeObject<T>(File.ReadAllText(filePath));
                if (ReferenceEquals(result, null))
                {
                    throw new InvalidOperationException("配置内容为空。");
                }

                return result;
            }
            catch (Exception error) when (!(error is FileNotFoundException))
            {
                throw new InvalidOperationException("读取实时手动调试配置失败：" + filePath, error);
            }
        }

        /// <summary>把仿真轴配置转换为手动页面使用的轴号、限位和默认运动参数。</summary>
        private static AxisHardwareDefinition CreateAxisHardwareDefinition(
            ZMotionAxisConfiguration axisConfiguration)
        {
            return new AxisHardwareDefinition
            {
                ControllerId = axisConfiguration.ControllerId,
                Name = axisConfiguration.Name,
                AxisNumber = axisConfiguration.AxisNumber,
                MinimumPosition = axisConfiguration.MinimumPosition,
                MaximumPosition = axisConfiguration.MaximumPosition,
                ManualSpeed = axisConfiguration.ManualSpeed,
                ManualAcceleration = axisConfiguration.ManualAcceleration,
                ManualDeceleration = axisConfiguration.ManualDeceleration
            };
        }

        /// <summary>保存设备配置中的运动卡入口。</summary>
        private sealed class MotionCardsDocument
        {
            public int SchemaVersion { get; set; }

            public List<MotionCardDefinition> MotionCards { get; set; }
        }

        /// <summary>描述要连接的正运动仿真卡和连接配置文件。</summary>
        private sealed class MotionCardDefinition
        {
            public string ControllerId { get; set; }

            public string AdapterType { get; set; }

            public string ConnectionConfigurationFile { get; set; }
        }

        /// <summary>保存设备配置中的仿真轴参数。</summary>
        private sealed class AxisParametersDocument
        {
            public int SchemaVersion { get; set; }

            public List<ZMotionAxisConfiguration> Axes { get; set; }
        }

        /// <summary>保存设备配置中的逻辑输入输出映射。</summary>
        private sealed class IoMappingDocument
        {
            public int SchemaVersion { get; set; }

            public string EmergencyStopInputName { get; set; }

            public List<ZMotionIoPointConfiguration> DigitalInputs { get; set; }

            public List<ZMotionIoPointConfiguration> DigitalOutputs { get; set; }
        }

        /// <summary>保持工位待机，防止联调窗口自动启动设备流程。</summary>
        private sealed class WaitingStationFlow : IStationFlow
        {
            public string StationName
            {
                get { return "仿真手动联调工位"; }
            }

            public string CurrentStepName
            {
                get { return "等待手动调试"; }
            }

            /// <summary>等待 Runtime 暂停或关闭请求，不执行任何轴动作。</summary>
            public Task ExecuteCurrentStepAsync(CancellationToken cancellationToken)
            {
                return Task.Delay(Timeout.Infinite, cancellationToken);
            }

            /// <summary>确认待机工位没有需要重置的工艺状态。</summary>
            public Task ResetAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }
        }
    }
}
