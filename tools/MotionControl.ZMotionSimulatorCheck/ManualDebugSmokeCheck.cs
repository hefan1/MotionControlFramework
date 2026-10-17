using System;
using System.Collections.Generic;
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
using MotionControl.Framework.Runtime.Machine;
using MotionControl.Framework.Wpf.ManualDebug;
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
                            MaximumPosition = 100
                        }
                    },
                    new[] { "EmergencyStop", "PartPresent" },
                    new[] { "Vacuum", "Clamp" });

                Ensure(page.FindName("AxisSelector") is ComboBox, "页面没有加载逻辑轴选择框。");
                Ensure(page.FindName("AxisStatusText") is TextBlock, "页面没有加载轴状态显示。");
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
                var primaryNavigation = (StackPanel)hostWindow.FindName("PrimaryNavigation");
                var manualNavigation = primaryNavigation.Children
                    .OfType<ToggleButton>()
                    .Single(button => string.Equals(button.Tag as string, "Manual", StringComparison.Ordinal));
                manualNavigation.RaiseEvent(new RoutedEventArgs(ToggleButton.ClickEvent, manualNavigation));

                var manualContent = (ContentControl)hostWindow.FindName("ManualDebugContent");
                Ensure(ReferenceEquals(manualContent.Content, page), "Host 手动页面没有显示注入的通用调试页。");
                Ensure(manualContent.Visibility == Visibility.Visible, "Host 手动调试内容当前不可见。");

                application.Shutdown();
                Console.WriteLine("[通过] 手动调试页中英资源、断开保护、双向点动、轴/IO命令、软限位、DO读回和Host导航离屏检查完成；使用内存测试卡。");
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
            public Task MoveAbsoluteAsync(double position, AxisMotionProfile profile, CancellationToken cancellationToken) { CommandCount++; return Task.CompletedTask; }

            /// <summary>本次离屏检查不发出相对移动命令。</summary>
            public Task MoveRelativeAsync(double distance, AxisMotionProfile profile, CancellationToken cancellationToken) { CommandCount++; return Task.CompletedTask; }

            /// <summary>本次离屏检查不发出点动命令。</summary>
            public Task StartJogAsync(MotionDirection direction, double speed, CancellationToken cancellationToken)
            {
                CommandCount++;
                LastJogDirection = direction;
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
