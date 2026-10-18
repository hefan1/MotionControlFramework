using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Framework.Contracts.Configuration;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.IO;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Contracts.Safety;
using MotionControl.Framework.Runtime.Alarms;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.Framework.Runtime.Machine;
using MotionControl.Framework.Wpf.ManualDebug;
using MotionControl.Framework.Wpf.Configuration;
using MotionControl.Host.Wpf;

namespace MotionControl.ZMotionSimulatorCheck
{
    /// <summary>离屏检查手动调试页 XAML 加载、轴/IO 行构建和 Host 导航注入。</summary>
    internal static class ManualDebugSmokeCheck
    {
        /// <summary>建立 WPF 应用上下文并检查页面控件与 Host 导航入口。</summary>
        public static int Run()
        {
            try
            {
                var application = new Application
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown
                };
                var chineseResources = CreateLanguageResources("zh-CN");
                application.Resources.MergedDictionaries.Add(chineseResources);
                var englishResources = CreateLanguageResources("en-US");
                Ensure(
                    string.Equals(englishResources["伺服使能"] as string, "Servo On", StringComparison.Ordinal),
                    "英文手动调试资源没有正确加载。 ");

                var card = new SmokeMotionCard();
                var cards = new MotionCardGroup(new IMotionCard[] { card });
                var runtime = new MachineRuntime(
                    cards,
                    new IStationFlow[] { new SmokeStationFlow() },
                    new MachineAlarmService(),
                    new SmokeSafetyStatus());
                var page = new ManualMotionDebugView(
                    runtime,
                    cards,
                    card.LogicalIo,
                    new[]
                    {
                        new AxisHardwareDefinition
                        {
                            Name = "X",
                            ControllerId = card.ControllerId,
                            AxisNumber = 0,
                            MinimumPosition = -100,
                            MaximumPosition = 100,
                            ManualSpeed = 23,
                            ManualAcceleration = 34,
                            ManualDeceleration = 45
                        }
                    },
                    new[] { "EmergencyStop", "PartPresent" },
                    new[] { "Vacuum", "Clamp" });

                Ensure(page.FindName("AxisSelector") is ComboBox, "页面没有加载逻辑轴选择框。");
                Ensure(page.FindName("AxisStatusText") is TextBlock, "页面没有加载轴状态显示。");
                Ensure(((TextBox)page.FindName("SpeedTextBox")).Text == "23", "页面没有从轴配置加载手动速度。");
                Ensure(((TextBox)page.FindName("AccelerationTextBox")).Text == "34", "页面没有从轴配置加载手动加速度。");
                Ensure(((TextBox)page.FindName("DecelerationTextBox")).Text == "45", "页面没有从轴配置加载手动减速度。");
                Ensure(((StackPanel)page.FindName("DigitalInputsPanel")).Children.Count == 2, "DI 列表行数与配置数量不一致。");
                Ensure(((StackPanel)page.FindName("DigitalOutputsPanel")).Children.Count == 2, "DO 列表行数与配置数量不一致。");
                VerifyMachineStateLocalization(application, chineseResources, englishResources, page);
                VerifyDisconnectedControlsDoNotSendCommands(
                    application,
                    chineseResources,
                    englishResources,
                    page,
                    card);

                runtime.ConnectAsync(CancellationToken.None).GetAwaiter().GetResult();
                runtime.ResetAsync(CancellationToken.None).GetAwaiter().GetResult();
                VerifyConnectedControlsUseCommonContracts(page, card);

                var hostWindow = new MainWindow();
                hostWindow.SetManualMotionDebugPage(page);
                VerifyMotionCardConnectionStatus(application, hostWindow, englishResources, chineseResources);
                VerifyAxisMappingStartupConnection();
                var primaryNavigation = (StackPanel)hostWindow.FindName("PrimaryNavigation");
                var manualNavigation = primaryNavigation.Children
                    .OfType<ToggleButton>()
                    .Single(button => string.Equals(button.Tag as string, "Manual", StringComparison.Ordinal));
                manualNavigation.RaiseEvent(new RoutedEventArgs(ToggleButton.ClickEvent, manualNavigation));

                var manualContent = (ContentControl)hostWindow.FindName("ManualDebugContent");
                Ensure(ReferenceEquals(manualContent.Content, page), "Host 手动页面没有显示注入的通用调试页。");
                Ensure(manualContent.Visibility == Visibility.Visible, "Host 手动调试内容当前不可见。");

                application.Shutdown();
                Console.WriteLine("[通过] 手动调试页与轴映射页离屏检查完成：启动连接策略、轴参数读回、Host底栏卡状态、中英资源及手动调试控件；使用内存测试卡。");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("[失败] 手动调试页离屏检查：" + error);
                return 1;
            }
        }

        /// <summary>加载 Host 的指定语言资源字典供页面和导航解析 DynamicResource。</summary>
        private static ResourceDictionary CreateLanguageResources(string languageCode)
        {
            return new ResourceDictionary
            {
                Source = new Uri(
                    "/MotionControl.Host.Wpf;component/Localization/Strings." + languageCode + ".xaml",
                    UriKind.Relative)
            };
        }

        /// <summary>在 WPF 页面结构不符合预期时抛出明确错误。</summary>
        private static void Ensure(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        /// <summary>确认 Host 顶部显示设备状态，底栏独立显示中英文控制卡连接状态。</summary>
        private static void VerifyMotionCardConnectionStatus(
            Application application,
            MainWindow hostWindow,
            ResourceDictionary englishResources,
            ResourceDictionary chineseResources)
        {
            var headerMessage = (TextBlock)hostWindow.FindName("HeaderMessage");
            var footerStatus = (TextBlock)hostWindow.FindName("MotionCardConnectionStatus");

            hostWindow.SetMotionCardConnectionStatus(true);
            Ensure(headerMessage.Text.Contains("待机"), "Host 顶部没有保持设备待机状态。");
            Ensure(footerStatus.Text.Contains("控制卡 已连接"), "Host 底栏没有显示控制卡已连接。");

            application.Resources.MergedDictionaries[0] = englishResources;
            Ensure(headerMessage.Text.Contains("Machine state: standby"), "Host 顶部设备状态没有切换为英文。");
            Ensure(footerStatus.Text.Contains("Motion card: connected"), "Host 底栏连接状态没有切换为英文。");

            hostWindow.SetMotionCardConnectionStatus(false);
            Ensure(headerMessage.Text.Contains("Machine state: standby"), "Host 顶部设备状态受卡连接变化影响。");
            Ensure(footerStatus.Text.Contains("Motion card: not connected"), "Host 底栏没有显示未连接状态。");

            application.Resources.MergedDictionaries[0] = chineseResources;
            hostWindow.SetMotionCardConnectionStatus(false);
            Ensure(headerMessage.Text.Contains("设备状态：待机"), "Host 顶部未恢复中文设备状态。");
            Ensure(footerStatus.Text.Contains("控制卡 未连接"), "Host 底栏未恢复中文未连接状态。");
        }

        /// <summary>确认首次运行等待用户连接，已有控制卡配置时启动流程会自动重连。</summary>
        private static void VerifyAxisMappingStartupConnection()
        {
            string configurationRoot = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "AxisMappingSmokeData_" + Guid.NewGuid().ToString("N"));

            try
            {
                var configurationStore = new MachineConfigurationStore(configurationRoot);
                var firstRunConnection = new SmokeAxisMappingCardConnection();
                var firstRunPage = new AxisMappingConfigurationView(
                    configurationStore,
                    "FirstRun",
                    firstRunConnection);
                firstRunPage.InitializeSavedCardConnectionAsync().GetAwaiter().GetResult();

                Ensure(firstRunConnection.ConnectCount == 0, "首次运行没有已保存的卡配置，却自动连接了控制卡。");
                Ensure(!((Grid)firstRunPage.FindName("AxisDetailsGrid")).IsEnabled, "未连接时轴映射编辑区没有锁定。");

                var savedCardSettings = new MotionCardSettingsDocument
                {
                    MotionCards = new List<MotionCardConnectionDefinition>
                    {
                        new MotionCardConnectionDefinition
                        {
                            ControllerId = "MotionCard01",
                            AdapterType = "ZMotion",
                            ConnectionConfigurationFile = "HardwareConfig/ZDevelopSimulation.json"
                        }
                    }
                };
                configurationStore.SaveJson(
                    "SavedRun",
                    "HardwareConfig/MotionCards.json",
                    savedCardSettings);

                var savedRunConnection = new SmokeAxisMappingCardConnection();
                var savedRunPage = new AxisMappingConfigurationView(
                    configurationStore,
                    "SavedRun",
                    savedRunConnection);
                savedRunPage.InitializeSavedCardConnectionAsync().GetAwaiter().GetResult();

                Ensure(savedRunConnection.ConnectCount == 1, "启动时没有按已保存的控制卡配置自动连接。");
                Ensure(((Grid)savedRunPage.FindName("AxisDetailsGrid")).IsEnabled, "自动连接成功后轴映射编辑区没有启用。");
                Ensure(
                    ((TextBlock)savedRunPage.FindName("CardReadbackText")).Text.Contains("UNITS=1"),
                    "自动连接成功后没有读取所选物理轴的当前参数。");
            }
            finally
            {
                if (Directory.Exists(configurationRoot))
                {
                    Directory.Delete(configurationRoot, true);
                }
            }
        }

        /// <summary>提供无硬件的轴映射页连接替身，记录自动连接及参数读取调用。</summary>
        private sealed class SmokeAxisMappingCardConnection : IAxisMappingCardConnection
        {
            /// <summary>当前内存测试卡的连接状态。</summary>
            public bool IsConnected { get; private set; }

            /// <summary>测试中收到的连接请求数量。</summary>
            public int ConnectCount { get; private set; }

            /// <summary>返回固定的测试控制器型号。</summary>
            public string ActualControllerType { get { return "内存测试控制器"; } }

            /// <summary>返回固定的测试控制器版本。</summary>
            public string ActualControllerVersion { get { return "SmokeTest"; } }

            /// <summary>记录一次连接并将内存测试卡切换为已连接。</summary>
            public Task ConnectAsync(MotionCardSettingsDocument settings, CancellationToken cancellationToken)
            {
                ConnectCount++;
                IsConnected = true;
                return Task.CompletedTask;
            }

            /// <summary>将内存测试卡切换为已断开。</summary>
            public Task DisconnectAsync(CancellationToken cancellationToken)
            {
                IsConnected = false;
                return Task.CompletedTask;
            }

            /// <summary>返回固定 Axis0 参数，供自动连接后验证页面读回显示。</summary>
            public Task<AxisMappingCardParameters> ReadAxisParametersAsync(
                int axisNumber,
                CancellationToken cancellationToken)
            {
                return Task.FromResult(new AxisMappingCardParameters
                {
                    AxisNumber = axisNumber,
                    UnitsPerEngineeringUnit = 1,
                    MinimumPosition = -100,
                    MaximumPosition = 100,
                    Speed = 5,
                    CreepSpeed = 1,
                    Acceleration = 10,
                    Deceleration = 10
                });
            }

            /// <summary>接受轴参数写入调用但不操作任何硬件。</summary>
            public Task WriteAxisParametersAsync(
                AxisMappingCardParameters parameters,
                CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }
        }

        /// <summary>确认断开状态下点击轴和 DO 控件不会向设备发送命令。</summary>
        private static void VerifyDisconnectedControlsDoNotSendCommands(
            Application application,
            ResourceDictionary chineseResources,
            ResourceDictionary englishResources,
            ManualMotionDebugView page,
            SmokeMotionCard card)
        {
            var servoButton = (Button)page.FindName("ServoEnableButton");
            servoButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, servoButton));
            var operationMessage = (TextBlock)page.FindName("OperationMessageText");
            Ensure(operationMessage.Text.Contains("运动控制卡未连接"), "中文界面没有显示未连接保护信息。");

            var outputRows = (StackPanel)page.FindName("DigitalOutputsPanel");
            var firstOutputRow = (StackPanel)outputRows.Children[0];
            var outputOnButton = (Button)firstOutputRow.Children[2];
            outputOnButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, outputOnButton));
            Ensure(operationMessage.Text.Contains("运动控制卡未连接"), "中文界面没有拦截断开状态下的 DO 命令。");

            application.Resources.MergedDictionaries[0] = englishResources;
            servoButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, servoButton));
            Ensure(operationMessage.Text.Contains("Motion card is disconnected"), "英文界面没有显示未连接保护信息。");

            outputOnButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, outputOnButton));
            Ensure(operationMessage.Text.Contains("Motion card is disconnected"), "英文界面没有拦截断开状态下的 DO 命令。");
            application.Resources.MergedDictionaries[0] = chineseResources;

            var axis = (SmokeAxis)card.Axes.Single();
            Ensure(axis.CommandCount == 0, "断开时轴按钮仍然发送了运动命令。");
            Ensure(card.LogicalIo.WriteCount == 0, "断开时 DO 按钮仍然发送了输出命令。");
        }

        /// <summary>检查手动页中的整机状态随中英文资源切换显示。</summary>
        private static void VerifyMachineStateLocalization(
            Application application,
            ResourceDictionary chineseResources,
            ResourceDictionary englishResources,
            ManualMotionDebugView page)
        {
            var machineStateText = (TextBlock)page.FindName("MachineStateText");
            page.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Ensure(machineStateText.Text.Contains("未连接"), "中文界面没有显示本地化整机状态。");

            application.Resources.MergedDictionaries[0] = englishResources;
            page.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Ensure(machineStateText.Text.Contains("Disconnected"), "英文界面没有显示本地化整机状态。");

            application.Resources.MergedDictionaries[0] = chineseResources;
        }

        /// <summary>用内存测试卡检查已连接时的轴操作、软限位拦截和 DO 读回。</summary>
        private static void VerifyConnectedControlsUseCommonContracts(
            ManualMotionDebugView page,
            SmokeMotionCard card)
        {
            var axis = (SmokeAxis)card.Axes.Single();
            var initialCommandCount = axis.CommandCount;
            ClickButton(page, "ServoEnableButton");
            ClickButton(page, "ServoDisableButton");
            ClickButton(page, "HomeButton");
            ClickButton(page, "ResetAxisAlarmButton");
            ClickButton(page, "StopButton");

            var targetPosition = (TextBox)page.FindName("TargetPositionTextBox");
            targetPosition.Text = "50";
            ClickButton(page, "MoveAbsoluteButton");
            Ensure(axis.LastMotionProfile != null, "手动定位没有使用运动参数配置。");
            Ensure(axis.LastMotionProfile.Speed == 23, "手动定位没有使用轴配置的默认速度。");
            Ensure(axis.LastMotionProfile.Acceleration == 34, "手动定位没有使用轴配置的默认加速度。");
            Ensure(axis.LastMotionProfile.Deceleration == 45, "手动定位没有使用轴配置的默认减速度。");
            targetPosition.Text = "1";
            ClickButton(page, "MoveRelativeButton");
            Ensure(axis.CommandCount == initialCommandCount + 7, "已连接时轴按钮没有通过 IAxis 调用预期的操作。");

            var operationMessage = (TextBlock)page.FindName("OperationMessageText");
            targetPosition.Text = "101";
            ClickButton(page, "MoveAbsoluteButton");
            Ensure(axis.CommandCount == initialCommandCount + 7, "绝对定位超出软限位时仍然调用了轴。");
            Ensure(operationMessage.Text.Contains("软限位"), "软限位拒绝原因没有显示在页面上。");

            VerifyJogButtonsStartAndStop(page, axis);

            var outputRows = (StackPanel)page.FindName("DigitalOutputsPanel");
            var firstOutputRow = (StackPanel)outputRows.Children[0];
            var outputStatusText = (TextBlock)firstOutputRow.Children[1];
            var outputOnButton = (Button)firstOutputRow.Children[2];
            var outputOffButton = (Button)firstOutputRow.Children[3];
            outputOnButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, outputOnButton));
            Ensure(outputStatusText.Text == "输出已开", "DO 开启后页面没有显示读回状态。");
            outputOffButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, outputOffButton));
            Ensure(outputStatusText.Text == "输出已关", "DO 关闭后页面没有显示读回状态。");
            Ensure(card.LogicalIo.WriteCount == 2, "DO 开启和关闭按钮没有各写入一次逻辑 IO。");
            Ensure(!card.LogicalIo.ReadOutput("Vacuum"), "DO 关闭后读回状态仍然为开启。");
        }

        /// <summary>通过页面名称点击指定按钮，便于检查页面到契约的命令连接。</summary>
        private static void ClickButton(ManualMotionDebugView page, string controlName)
        {
            var button = page.FindName(controlName) as Button;
            Ensure(button != null, "页面缺少按钮控件：" + controlName + "。");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
        }

        /// <summary>模拟按下和松开两个方向点动按钮，确认页面调用对应点动与受控停止。</summary>
        private static void VerifyJogButtonsStartAndStop(ManualMotionDebugView page, SmokeAxis axis)
        {
            VerifyJogButtonStartAndStop(page, axis, "NegativeJogButton", MotionDirection.Negative);
            VerifyJogButtonStartAndStop(page, axis, "PositiveJogButton", MotionDirection.Positive);
        }

        /// <summary>检查指定方向点动按钮按下启动对应方向，松开后发送受控停止。</summary>
        private static void VerifyJogButtonStartAndStop(
            ManualMotionDebugView page,
            SmokeAxis axis,
            string controlName,
            MotionDirection expectedDirection)
        {
            var jogButton = (Button)page.FindName(controlName);
            var initialCommandCount = axis.CommandCount;
            var mouseDown = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent
            };
            jogButton.RaiseEvent(mouseDown);
            Ensure(axis.CommandCount == initialCommandCount + 1, "按下点动按钮后没有调用连续点动。");
            Ensure(axis.LastJogDirection == expectedDirection, "点动按钮发送的运动方向不正确。");
            Ensure(axis.LastJogSpeed == 23, "点动命令没有使用轴配置的默认速度。");

            var mouseUp = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent
            };
            jogButton.RaiseEvent(mouseUp);
            Ensure(axis.CommandCount == initialCommandCount + 2, "松开点动按钮后没有调用受控停止。");
        }

        /// <summary>提供页面构造所需的一张未连接测试卡和一个逻辑轴。</summary>
        private sealed class SmokeMotionCard : IMotionCard
        {
            private bool _isConnected;

            public string ControllerId { get { return "SmokeCard"; } }

            public bool IsConnected { get { return _isConnected; } }

            /// <summary>创建一张断开状态的测试卡和对应逻辑 IO。</summary>
            public SmokeMotionCard()
            {
                var axis = new SmokeAxis();
                Axes = new[] { axis };
                LogicalIo = new SmokeLogicalIo();
            }

            public IReadOnlyCollection<IAxis> Axes { get; private set; }

            public SmokeLogicalIo LogicalIo { get; private set; }

            /// <summary>标记内存测试卡为已连接状态。</summary>
            public Task ConnectAsync(CancellationToken cancellationToken) { _isConnected = true; return Task.CompletedTask; }

            /// <summary>标记内存测试卡为已断开状态。</summary>
            public Task DisconnectAsync(CancellationToken cancellationToken) { _isConnected = false; return Task.CompletedTask; }
        }

        /// <summary>提供页面构造所需的无动作测试轴。</summary>
        private sealed class SmokeAxis : IAxis
        {
            public int CommandCount { get; private set; }

            public MotionDirection? LastJogDirection { get; private set; }

            /// <summary>保存页面最近一次定位命令的参数，供离屏检查确认。</summary>
            public AxisMotionProfile LastMotionProfile { get; private set; }

            /// <summary>保存页面最近一次点动命令的速度，供离屏检查确认。</summary>
            public double LastJogSpeed { get; private set; }

            public string Name { get { return "X"; } }

            /// <summary>返回固定无报警状态，不读取真实设备。</summary>
            public Task<AxisStatus> ReadStatusAsync(CancellationToken cancellationToken)
            {
                return Task.FromResult(new AxisStatus(0, 0, false, false, false, false, false, false, false, false, null, null, 0, false));
            }

            /// <summary>本次离屏检查不发出轴使能命令。</summary>
            public Task EnableServoAsync(CancellationToken cancellationToken) { CommandCount++; return Task.CompletedTask; }

            /// <summary>本次离屏检查不发出轴关闭命令。</summary>
            public Task DisableServoAsync(CancellationToken cancellationToken) { CommandCount++; return Task.CompletedTask; }

            /// <summary>本次离屏检查不发出回零命令。</summary>
            public Task HomeAsync(CancellationToken cancellationToken) { CommandCount++; return Task.CompletedTask; }

            /// <summary>本次离屏检查不发出定位命令。</summary>
            public Task MoveAbsoluteAsync(double position, AxisMotionProfile profile, CancellationToken cancellationToken)
            {
                CommandCount++;
                LastMotionProfile = profile;
                return Task.CompletedTask;
            }

            /// <summary>本次离屏检查不发出相对移动命令。</summary>
            public Task MoveRelativeAsync(double distance, AxisMotionProfile profile, CancellationToken cancellationToken)
            {
                CommandCount++;
                LastMotionProfile = profile;
                return Task.CompletedTask;
            }

            /// <summary>本次离屏检查不发出点动命令。</summary>
            public Task StartJogAsync(MotionDirection direction, double speed, CancellationToken cancellationToken)
            {
                CommandCount++;
                LastJogDirection = direction;
                LastJogSpeed = speed;
                return Task.CompletedTask;
            }

            /// <summary>本次离屏检查不发出停止命令。</summary>
            public Task StopAsync(AxisStopMode stopMode, CancellationToken cancellationToken) { CommandCount++; return Task.CompletedTask; }

            /// <summary>本次离屏检查不发出报警复位命令。</summary>
            public Task ResetAlarmAsync(CancellationToken cancellationToken) { CommandCount++; return Task.CompletedTask; }
        }

        /// <summary>提供未触发急停的测试安全状态。</summary>
        private sealed class SmokeSafetyStatus : IMachineSafetyStatus
        {
            /// <summary>报告测试急停状态未触发。</summary>
            public Task<bool> ReadEmergencyStopActiveAsync(CancellationToken cancellationToken)
            {
                return Task.FromResult(false);
            }
        }

        /// <summary>提供页面初始化需要的逻辑 DI/DO 名称。</summary>
        private sealed class SmokeLogicalIo : ILogicalIo
        {
            private readonly Dictionary<string, bool> _outputs = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            public int WriteCount { get; private set; }

            /// <summary>返回测试 DI 的关闭状态。</summary>
            public Task<bool> ReadInputAsync(string inputName, CancellationToken cancellationToken) { return Task.FromResult(false); }

            /// <summary>返回测试 DO 的关闭状态。</summary>
            public Task<bool> ReadOutputAsync(string outputName, CancellationToken cancellationToken)
            {
                return Task.FromResult(ReadOutput(outputName));
            }

            /// <summary>接受测试 DO 写入，但不控制真实硬件。</summary>
            public Task WriteOutputAsync(string outputName, bool value, CancellationToken cancellationToken)
            {
                WriteCount++;
                _outputs[outputName] = value;
                return Task.CompletedTask;
            }

            /// <summary>读取内存测试卡上保存的 DO 值。</summary>
            public bool ReadOutput(string outputName)
            {
                bool value;
                return _outputs.TryGetValue(outputName, out value) && value;
            }
        }

        /// <summary>提供 Runtime 构造所需的最小测试工位。</summary>
        private sealed class SmokeStationFlow : IStationFlow
        {
            public string StationName { get { return "页面检查工位"; } }

            public string CurrentStepName { get { return "页面检查步骤"; } }

            /// <summary>离屏检查不会启动工位流程。</summary>
            public Task ExecuteCurrentStepAsync(CancellationToken cancellationToken) { return Task.CompletedTask; }

            /// <summary>离屏检查不会复位工位流程。</summary>
            public Task ResetAsync(CancellationToken cancellationToken) { return Task.CompletedTask; }
        }
    }
}
