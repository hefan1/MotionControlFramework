using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
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
    /// <summary>检查手动调试页面、硬件配置页面和 Host 导航注入。</summary>
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
                Ensure(
                    string.Equals(chineseResources["轴当前位置"] as string, "轴当前位置", StringComparison.Ordinal),
                    "中文语言资源没有提供通用轴当前位置标签。");
                Ensure(
                    string.Equals(englishResources["轴当前位置"] as string, "Current axis position", StringComparison.Ordinal),
                    "英文语言资源没有提供通用轴当前位置标签。");

                var card = new SmokeMotionCard();
                var cards = new MotionCardGroup(new IMotionCard[] { card });
                var runtime = new MachineRuntime(
                    cards,
                    new IStationFlow[] { new SmokeStationFlow() },
                    new MachineAlarmService(),
                    new SmokeSafetyStatus());
                string positionConfigurationRoot = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "ManualAxisPositionsSmokeData_" + Guid.NewGuid().ToString("N"));
                var positionConfigurationStore = new MachineConfigurationStore(positionConfigurationRoot);
                var page = new ManualMotionDebugView(
                    runtime,
                    cards,
                    new[]
                    {
                        new AxisHardwareDefinition
                        {
                            Name = "X",
                            ControllerId = card.ControllerId,
                            AxisNumber = 0,
                            EngineeringUnit = "mm",
                            MinimumPosition = -100,
                            MaximumPosition = 100,
                            SoftLimitEnabled = true,
                            ManualSpeed = 23,
                            ManualAcceleration = 34,
                            ManualDeceleration = 45
                        }
                    },
                    positionConfigurationStore,
                    "ManualDebugSmoke");
                var ioPage = new ManualIoDebugView(
                    runtime,
                    card.LogicalIo,
                    new[] { "EmergencyStop", "PartPresent" },
                    new[] { "Vacuum", "Clamp" });

                var axisCards = (WrapPanel)page.FindName("AxisCardsPanel");
                Ensure(axisCards.Children.Count == 1, "已映射一个轴时没有生成对应的单轴调试卡片。");
                var axisControl = (ManualAxisControl)axisCards.Children[0];
                Ensure(axisControl.FindName("FeedbackPositionText") is TextBlock, "轴卡片没有加载轴当前位置显示。");
                Ensure(((TextBox)axisControl.FindName("SpeedTextBox")).Text == "23", "轴卡片没有从轴配置加载手动速度。");
                Ensure(((TextBox)axisControl.FindName("AccelerationTextBox")).Text == "34", "轴卡片没有从轴配置加载手动加速度。");
                Ensure(((TextBox)axisControl.FindName("DecelerationTextBox")).Text == "45", "轴卡片没有从轴配置加载手动减速度。");
                Ensure(((ItemsControl)axisControl.FindName("PositionsList")).Items.Count == 0, "新轴自动出现了默认示教位置。");
                Ensure(((TextBlock)axisControl.FindName("EmptyPositionsText")).Visibility == Visibility.Visible, "空位置列表没有显示添加提示。");
                VerifyManualPositionPrecision(runtime, card);
                Ensure(page.FindName("DigitalInputsPanel") == null, "轴手动页仍混入 DI 操作面板。");
                Ensure(page.FindName("DigitalOutputsPanel") == null, "轴手动页仍混入 DO 操作面板。");
                Ensure(((StackPanel)ioPage.FindName("DigitalInputsPanel")).Children.Count == 2, "IO 手动页 DI 列表行数与配置数量不一致。");
                Ensure(((StackPanel)ioPage.FindName("DigitalOutputsPanel")).Children.Count == 2, "IO 手动页 DO 列表行数与配置数量不一致。");
                VerifyMachineStateLocalization(application, chineseResources, englishResources, page);
                VerifyDisconnectedControlsDoNotSendCommands(
                    application,
                    chineseResources,
                    englishResources,
                    page,
                    ioPage,
                    card);

                runtime.ConnectAsync(CancellationToken.None).GetAwaiter().GetResult();
                runtime.ResetAsync(CancellationToken.None).GetAwaiter().GetResult();
                VerifyConnectedControlsUseCommonContracts(page, ioPage, card);

                var hostWindow = new MainWindow();
                hostWindow.SetManualMotionDebugPage(page);
                hostWindow.SetManualIoDebugPage(ioPage);
                VerifyMotionCardConnectionStatus(application, hostWindow, englishResources, chineseResources);
                VerifyAxisMappingStartupConnection();
                VerifyIoCapabilityDiagnosticDisplay(application, englishResources, chineseResources);
                var primaryNavigation = (StackPanel)hostWindow.FindName("PrimaryNavigation");
                var manualNavigation = primaryNavigation.Children
                    .OfType<ToggleButton>()
                    .Single(button => string.Equals(button.Tag as string, "Manual", StringComparison.Ordinal));
                manualNavigation.RaiseEvent(new RoutedEventArgs(ToggleButton.ClickEvent, manualNavigation));

                var manualContent = (ContentControl)hostWindow.FindName("ManualDebugContent");
                Ensure(ReferenceEquals(manualContent.Content, page), "Host 手动页面没有显示注入的通用调试页。");
                Ensure(manualContent.Visibility == Visibility.Visible, "Host 手动调试内容当前不可见。");
                var sidebar = (StackPanel)hostWindow.FindName("SidebarNavigation");
                var ioManualNavigation = sidebar.Children
                    .OfType<ToggleButton>()
                    .Single(button => string.Equals(button.Tag as string, "IO手动", StringComparison.Ordinal));
                ioManualNavigation.RaiseEvent(new RoutedEventArgs(ToggleButton.ClickEvent, ioManualNavigation));
                Ensure(ReferenceEquals(manualContent.Content, ioPage), "Host 的 IO 手动导航没有显示独立 IO 页面。");

                application.Shutdown();
                Console.WriteLine("[通过] 手动调试页与硬件配置页离屏检查完成：位置坐标精度与编辑保存、轴参数读回、I/O 能力诊断、Host 底栏卡状态和中英资源；使用内存测试卡。");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("[失败] 手动调试页离屏检查：" + error);
                return 1;
            }
        }

        /// <summary>确认位置输入框保留完整坐标精度，未修改时失焦不会触发配置保存。</summary>
        private static void VerifyManualPositionPrecision(MachineRuntime runtime, SmokeMotionCard card)
        {
            const double originalPosition = 12.3456789;
            var position = new ManualAxisPosition
            {
                Id = "Position1",
                Name = "精度检查位置",
                Position = originalPosition
            };
            var positions = new ObservableCollection<ManualAxisPosition> { position };
            var axisConfiguration = new AxisHardwareDefinition
            {
                Name = "X",
                ControllerId = card.ControllerId,
                AxisNumber = 0,
                EngineeringUnit = "mm",
                MinimumPosition = -100,
                MaximumPosition = 100,
                SoftLimitEnabled = true,
                ManualSpeed = 5
            };
            int saveCount = 0;
            var axisControl = new ManualAxisControl(
                card.Axes.Single(),
                axisConfiguration,
                runtime,
                positions,
                () => null,
                () =>
                {
                    saveCount++;
                });
            var positionsList = (ItemsControl)axisControl.FindName("PositionsList");

            axisControl.Measure(new Size(600, 700));
            axisControl.Arrange(new Rect(0, 0, 600, 700));
            axisControl.UpdateLayout();

            FrameworkElement itemContainer = positionsList.ItemContainerGenerator.ContainerFromIndex(0) as FrameworkElement;
            Ensure(itemContainer != null, "精度检查没有生成示教位置行。");
            var positionTextBox = positionsList.ItemTemplate.FindName("PositionValueTextBox", itemContainer) as TextBox;
            Ensure(positionTextBox != null, "示教位置行没有生成目标位置输入框。");

            double displayedPosition;
            Ensure(
                double.TryParse(positionTextBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out displayedPosition)
                    && displayedPosition == originalPosition,
                "目标位置输入框显示时舍入了已保存的高精度坐标。");

            positionTextBox.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            Ensure(position.Position == originalPosition, "未修改目标位置并移开输入框后，坐标精度发生变化。");
            Ensure(saveCount == 0, "未修改目标位置并移开输入框后，不应重复保存配置。");

            positionTextBox.Text = "12.5";
            positionTextBox.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            Ensure(position.Position == 12.5, "手动编辑目标位置后没有保存新坐标。");
            Ensure(saveCount == 1, "手动编辑目标位置后没有且仅有一次保存配置。");
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

        /// <summary>读取轴映射页校验结果，避免测试无效配置时打开阻塞式提示框。</summary>
        private static List<string> GetAxisMappingValidationErrors(AxisMappingConfigurationView page)
        {
            MethodInfo validationMethod = typeof(AxisMappingConfigurationView).GetMethod(
                "ValidateConfiguredAxes",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Ensure(validationMethod != null, "轴映射页没有找到配置校验入口。");
            return (List<string>)validationMethod.Invoke(page, null);
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
                Ensure(((Grid)firstRunPage.FindName("AxisDetailsGrid")).IsEnabled, "未连接控制卡时不能编辑轴映射配置。");
                Ensure(
                    ((ComboBox)firstRunPage.FindName("CardVendorComboBox")).Items.Count == 2,
                    "轴映射页没有按连接提供者显示不同厂商的选项。");
                Ensure(
                    ((ComboBox)firstRunPage.FindName("CardTypeComboBox")).Items.Count == 1,
                    "轴映射页没有按连接提供者显示卡类型选项。");
                Ensure(
                    ((ComboBox)firstRunPage.FindName("AxisCountComboBox")).Items.Count == 4,
                    "轴映射页没有按连接提供者显示可配置的轴通道数量。");

                ComboBox cardVendorEditor = (ComboBox)firstRunPage.FindName("CardVendorComboBox");
                ComboBoxItem testVendorItem = cardVendorEditor.Items
                    .OfType<ComboBoxItem>()
                    .Single(item => string.Equals(item.Tag as string, "测试厂商", StringComparison.Ordinal));
                cardVendorEditor.SelectedItem = testVendorItem;

                Ensure(
                    string.Equals(
                        ((ComboBoxItem)((ComboBox)firstRunPage.FindName("CardTypeComboBox")).SelectedItem).Tag as string,
                        "脉冲型",
                        StringComparison.Ordinal),
                    "切换到测试厂商后，轴映射页没有使用该厂商提供的卡类型。");
                Ensure(
                    ((ComboBox)firstRunPage.FindName("AxisCountComboBox")).Items
                        .OfType<ComboBoxItem>()
                        .Any(item => string.Equals(item.Tag as string, "8", StringComparison.Ordinal)),
                    "切换到测试厂商后，轴映射页没有显示该卡型支持的轴数量。");

                var savedCardSettings = new MotionCardSettingsDocument
                {
                    MotionCardVendor = "正运动",
                    MotionCardType = "总线型",
                    AxisCount = 64,
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
                configurationStore.SaveJson(
                    "SavedRun",
                    "HardwareConfig/AxisParameters.json",
                    new AxisMappingConfigurationDocument
                    {
                        Axes = new List<AxisMappingEntry>
                        {
                            new AxisMappingEntry
                            {
                                ControllerId = "MotionCard01",
                                AxisNumber = 0,
                                Name = "X",
                                AxisType = "直线轴",
                                EngineeringUnit = "mm",
                                UnitsPerEngineeringUnit = 1,
                                MinimumPosition = -100,
                                MaximumPosition = 100,
                                SoftLimitEnabled = true,
                                ManualSpeed = 5,
                                HomeMode = 3,
                                HomeModeVendor = "正运动",
                                HomeSearchSpeed = 5,
                                HomeCreepSpeed = 1,
                                HomeAcceleration = 10,
                                HomeDeceleration = 9
                            }
                        }
                    });

                var savedRunConnection = new SmokeAxisMappingCardConnection();
                var savedRunPage = new AxisMappingConfigurationView(
                    configurationStore,
                    "SavedRun",
                    savedRunConnection);
                savedRunPage.InitializeSavedCardConnectionAsync().GetAwaiter().GetResult();

                Ensure(savedRunConnection.ConnectCount == 1, "启动时没有按已保存的控制卡配置自动连接。");
                Ensure(((Grid)savedRunPage.FindName("AxisDetailsGrid")).IsEnabled, "自动连接成功后轴映射编辑区没有启用。");
                Ensure(
                    ((TextBlock)savedRunPage.FindName("CardReadbackText")).Text.Contains("单位换算=1"),
                    "自动连接成功后没有读取所选物理轴的当前参数。");
                ComboBox homeModeEditor = (ComboBox)savedRunPage.FindName("HomeModeComboBox");
                Ensure(homeModeEditor.Items.Count == 1, "回零模式选项没有来自当前连接提供者。");
                Ensure(
                    string.Equals(((ComboBoxItem)homeModeEditor.SelectedItem).Tag as string, "3", StringComparison.Ordinal),
                    "旧版厂商回零模式没有按已保存的适配器标识迁移。");

                var softLimitEditor = (ComboBox)savedRunPage.FindName("SoftLimitEnabledComboBox");
                Ensure(softLimitEditor != null, "轴参数编辑区没有软限位启用下拉框。");
                Ensure(
                    string.Equals(((ComboBoxItem)softLimitEditor.SelectedItem).Tag as string, "True", StringComparison.OrdinalIgnoreCase),
                    "已保存的软限位启用状态没有显示在下拉框中。");

                var readCardButton = (Button)savedRunPage.FindName("ReadAxisParametersButton");
                readCardButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, readCardButton));
                Ensure(
                    savedRunPage.SelectedAxis.HomeDeceleration == 9,
                    "卡上 DECEL=0 覆盖了原有有效的回零减速度配置。");
                Ensure(
                    ((TextBlock)savedRunPage.FindName("CardReadbackText")).Text.Contains("减速度=0"),
                    "卡上无效减速度读数没有保留在只读参数读回区显示。");
                Ensure(
                    ((TextBlock)savedRunPage.FindName("ConfigurationStatusText")).Text.Contains("回零减速度"),
                    "读卡后没有说明无效的回零减速度未回填到编辑区。");
                var checkMappingButton = (Button)savedRunPage.FindName("CheckMappingButton");
                checkMappingButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, checkMappingButton));
                Ensure(
                    ((TextBlock)savedRunPage.FindName("ConfigurationStatusText")).Text.Contains("检查通过"),
                    "保留有效回零减速度后，完整轴配置没有通过检查。");

                ComboBoxItem disabledSoftLimitItem = softLimitEditor.Items
                    .OfType<ComboBoxItem>()
                    .Single(item => string.Equals(item.Tag as string, "False", StringComparison.OrdinalIgnoreCase));
                softLimitEditor.SelectedItem = disabledSoftLimitItem;
                savedRunPage.SelectedAxis.MinimumPosition = null;
                savedRunPage.SelectedAxis.MaximumPosition = null;
                checkMappingButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, checkMappingButton));
                Ensure(
                    ((TextBlock)savedRunPage.FindName("ConfigurationStatusText")).Text.Contains("检查通过"),
                    "软限位停用且范围留空时仍未通过轴映射检查。");

                ComboBoxItem unsetSoftLimitItem = softLimitEditor.Items
                    .OfType<ComboBoxItem>()
                    .Single(item => string.IsNullOrWhiteSpace(item.Tag as string));
                softLimitEditor.SelectedItem = unsetSoftLimitItem;
                List<string> validationErrors = GetAxisMappingValidationErrors(savedRunPage);
                Ensure(
                    validationErrors.Any(error => error.Contains("尚未选择是否启用软限位")),
                    "尚未选择软限位启停状态时没有报告对应的配置错误。当前错误："
                    + string.Join("；", validationErrors));

                Button disconnectCardButton = (Button)savedRunPage.FindName("DisconnectCardButton");
                disconnectCardButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, disconnectCardButton));
                ComboBox savedRunVendorEditor = (ComboBox)savedRunPage.FindName("CardVendorComboBox");
                ComboBoxItem alternateVendorItem = savedRunVendorEditor.Items
                    .OfType<ComboBoxItem>()
                    .Single(item => string.Equals(item.Tag as string, "测试厂商", StringComparison.Ordinal));
                savedRunVendorEditor.SelectedItem = alternateVendorItem;

                MotionCardConnectionDefinition changedConnection = savedRunConnection.LastConnectedSettings
                    .MotionCards
                    .Single();
                Ensure(
                    string.Equals(changedConnection.AdapterType, "TestPulseAdapter", StringComparison.Ordinal),
                    "切换厂商后没有把新的适配器标识写入连接配置。");
                Ensure(
                    string.IsNullOrWhiteSpace(changedConnection.ConnectionConfigurationFile),
                    "切换厂商后仍沿用了旧适配器的连接配置文件路径。");
            }
            finally
            {
                if (Directory.Exists(configurationRoot))
                {
                    Directory.Delete(configurationRoot, true);
                }
            }
        }

        /// <summary>确认通用 IO 页面展示适配器诊断文字，不要求公共契约提供厂商错误码字段。</summary>
        private static void VerifyIoCapabilityDiagnosticDisplay(
            Application application,
            ResourceDictionary englishResources,
            ResourceDictionary chineseResources)
        {
            string configurationRoot = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "IoCapabilitySmokeData_" + Guid.NewGuid().ToString("N"));
            const string machineId = "IoCapabilitySmoke";

            try
            {
                var configurationStore = new MachineConfigurationStore(configurationRoot);
                configurationStore.SaveJson(machineId, "HardwareConfig/MotionCards.json", new MotionCardSettingsDocument
                {
                    AxisCount = 8,
                    MotionCards = new List<MotionCardConnectionDefinition>
                    {
                        new MotionCardConnectionDefinition
                        {
                            ControllerId = "SmokeCard",
                            AdapterType = "TestPulseAdapter",
                            ConnectionConfigurationFile = "HardwareConfig/TestPulse.json"
                        }
                    }
                });

                var connection = new SmokeAxisMappingCardConnection();
                connection.ConnectAsync(new MotionCardSettingsDocument(), CancellationToken.None)
                    .GetAwaiter().GetResult();
                var page = new IoMappingConfigurationView(configurationStore, machineId, connection);
                application.Resources.MergedDictionaries[0] = chineseResources;
                page.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));

                var statusText = (TextBlock)page.FindName("IoCapabilityStatusText");
                var configurationStatusText = (TextBlock)page.FindName("ConfigurationStatusText");
                var digitalInputsGrid = (DataGrid)page.FindName("DigitalInputsGrid");
                Ensure(connection.IoCapabilityReadCount == 1, "打开 IO 映射页没有执行一次能力读取。");
                Ensure(configurationStatusText.Text.Contains("已读取"), "中文 IO 映射状态没有显示读取完成文字。");
                Ensure(digitalInputsGrid.Columns[0].Header.ToString() == "逻辑名称", "中文 DI 表格列标题不正确。");
                Ensure(statusText.Text.Contains("TestPulse_ReadInput"), "中文 IO 能力摘要没有显示适配器提供的探测说明。");
                Ensure(statusText.Text.Contains("20008"), "适配器诊断中的错误码没有保留到 IO 页面文字中。");
                Ensure(statusText.Text.Contains("3 路"), "中文 IO 能力摘要没有使用本地化通道数量单位。");

                application.Resources.MergedDictionaries[0] = englishResources;
                page.RefreshLocalizedText();
                Ensure(statusText.Text.Contains("details:"), "英文 IO 能力摘要没有使用英文诊断标签。");
                Ensure(statusText.Text.Contains("TestPulse_ReadInput"), "英文 IO 能力摘要丢失了适配器提供的探测说明。");
                Ensure(statusText.Text.Contains("3 channels"), "英文 IO 能力摘要仍混入中文通道数量单位。");
                Ensure(configurationStatusText.Text.Contains("Loaded I/O"), "英文 IO 映射状态没有切换语言。");
                Ensure(digitalInputsGrid.Columns[0].Header.ToString() == "Logical Name", "英文 DI 表格列标题没有切换语言。");
                Ensure(connection.IoCapabilityReadCount == 1, "切换语言时不应重复读取控制卡 I/O 能力。");

                application.Resources.MergedDictionaries[0] = chineseResources;
                page.RefreshLocalizedText();
                Ensure(statusText.Text.Contains("3 路"), "切回中文后 IO 能力摘要没有恢复中文通道数量单位。");
                Ensure(configurationStatusText.Text.Contains("已读取"), "切回中文后 IO 映射状态没有恢复中文。");
                Ensure(digitalInputsGrid.Columns[0].Header.ToString() == "逻辑名称", "切回中文后 DI 表格列标题没有恢复中文。");
            }
            finally
            {
                application.Resources.MergedDictionaries[0] = chineseResources;
                if (Directory.Exists(configurationRoot))
                {
                    Directory.Delete(configurationRoot, true);
                }
            }
        }

        /// <summary>提供无硬件的轴映射页连接替身，记录自动连接及参数读取调用。</summary>
        private sealed class SmokeAxisMappingCardConnection : IAxisMappingCardConnection, IMotionCardIoCapabilityProvider
        {
            /// <summary>记录测试页面读取 I/O 能力的次数，确认语言刷新不会重复调用适配器。</summary>
            public int IoCapabilityReadCount { get; private set; }

            /// <summary>返回内存测试卡提供的卡型及轴数量选项。</summary>
            public IReadOnlyList<MotionCardSelectionOption> SupportedMotionCards
            {
                get
                {
                    return new List<MotionCardSelectionOption>
                    {
                        new MotionCardSelectionOption
                        {
                            VendorCode = "正运动",
                            VendorDisplayResourceKey = "厂商正运动",
                            CardTypeCode = "总线型",
                            CardTypeDisplayResourceKey = "卡类型总线",
                            AdapterType = "ZMotion",
                            SupportedAxisCounts = new[] { 8, 16, 32, 64 },
                            DefaultAxisCount = 64
                        },
                        new MotionCardSelectionOption
                        {
                            VendorCode = "测试厂商",
                            VendorDisplayResourceKey = "测试厂商",
                            CardTypeCode = "脉冲型",
                            CardTypeDisplayResourceKey = "脉冲型",
                            AdapterType = "TestPulseAdapter",
                            SupportedAxisCounts = new[] { 4, 8 },
                            DefaultAxisCount = 8
                        }
                    }.AsReadOnly();
                }
            }

            /// <summary>当前内存测试卡的连接状态。</summary>
            public bool IsConnected { get; private set; }

            /// <summary>测试中收到的连接请求数量。</summary>
            public int ConnectCount { get; private set; }

            /// <summary>保存最近一次连接调用收到的配置，供测试检查页面选择结果。</summary>
            public MotionCardSettingsDocument LastConnectedSettings { get; private set; }

            /// <summary>返回固定的测试控制器型号。</summary>
            public string ActualControllerType { get { return "内存测试控制器"; } }

            /// <summary>返回固定的测试控制器版本。</summary>
            public string ActualControllerVersion { get { return "SmokeTest"; } }

            /// <summary>记录一次连接并将内存测试卡切换为已连接。</summary>
            public Task ConnectAsync(MotionCardSettingsDocument settings, CancellationToken cancellationToken)
            {
                ConnectCount++;
                LastConnectedSettings = settings;
                IsConnected = true;
                return Task.CompletedTask;
            }

            /// <summary>将内存测试卡切换为已断开。</summary>
            public Task DisconnectAsync(CancellationToken cancellationToken)
            {
                IsConnected = false;
                return Task.CompletedTask;
            }

            /// <summary>重新连接测试卡，模拟 Host 按已保存配置恢复控制器。</summary>
            public Task ReconnectAsync(MotionCardSettingsDocument settings, CancellationToken cancellationToken)
            {
                return ConnectAsync(settings, cancellationToken);
            }

            /// <summary>返回测试卡唯一支持的回零模式，用于验证页面读取适配器选项。</summary>
            public IReadOnlyList<AxisHomeModeOption> GetSupportedHomeModes(
                MotionCardSelectionOption motionCard,
                int axisNumber)
            {
                if (motionCard == null || axisNumber < 0 || axisNumber >= 64
                    || !string.Equals(motionCard.AdapterType, "ZMotion", StringComparison.Ordinal))
                {
                    return new List<AxisHomeModeOption>().AsReadOnly();
                }

                return new List<AxisHomeModeOption>
                {
                    new AxisHomeModeOption { ModeNumber = 3 }
                }.AsReadOnly();
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
                    SoftLimitEnabled = true,
                    PositiveSoftLimitEffective = true,
                    NegativeSoftLimitEffective = true,
                    Speed = 5,
                    CreepSpeed = 1,
                    Acceleration = 10,
                    Deceleration = 0
                });
            }

            /// <summary>返回包含适配器诊断文字的测试 I/O 能力结果，不连接真实控制器。</summary>
            public Task<MotionCardIoCapabilities> ReadIoCapabilitiesAsync(CancellationToken cancellationToken)
            {
                IoCapabilityReadCount++;
                return Task.FromResult(new MotionCardIoCapabilities
                {
                    ControllerId = "SmokeCard",
                    DigitalInputChannelRange = new MotionCardIoChannelRange
                    {
                        MappableChannelCount = 3,
                        FirstUnavailableChannel = 3,
                        FirstUnavailableMessage = "TestPulse_ReadInput channel 3 returned code 20008."
                    },
                    DigitalOutputChannelRange = new MotionCardIoChannelRange
                    {
                        MappableChannelCount = 2,
                        FirstUnavailableChannel = 2,
                        FirstUnavailableMessage = "TestPulse_ReadOutput channel 2 returned code 20008."
                    }
                });
            }

            /// <summary>刷新时重新返回测试 I/O 能力，用于验证页面刷新流程。</summary>
            public Task<MotionCardIoCapabilities> RefreshIoCapabilitiesAsync(CancellationToken cancellationToken)
            {
                return ReadIoCapabilitiesAsync(cancellationToken);
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
            ManualIoDebugView ioPage,
            SmokeMotionCard card)
        {
            var axisControl = GetFirstAxisControl(page);
            var servoButton = (Button)axisControl.FindName("ServoToggleButton");
            servoButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, servoButton));
            var axisOperationMessage = (TextBlock)axisControl.FindName("OperationMessageText");
            Ensure(axisOperationMessage.Text.Contains("运动控制卡未连接"), "中文界面没有显示未连接保护信息。");

            var outputRows = (StackPanel)ioPage.FindName("DigitalOutputsPanel");
            var firstOutputRow = (StackPanel)outputRows.Children[0];
            var outputOnButton = (Button)firstOutputRow.Children[2];
            outputOnButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, outputOnButton));
            var operationMessage = (TextBlock)ioPage.FindName("OperationMessageText");
            Ensure(operationMessage.Text.Contains("运动控制卡未连接"), "中文界面没有拦截断开状态下的 DO 命令。");

            application.Resources.MergedDictionaries[0] = englishResources;
            servoButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, servoButton));
            Ensure(axisOperationMessage.Text.Contains("Motion card is disconnected"), "英文界面没有显示未连接保护信息。");

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
            ManualIoDebugView ioPage,
            SmokeMotionCard card)
        {
            var axis = (SmokeAxis)card.Axes.Single();
            var initialCommandCount = axis.CommandCount;
            ManualAxisControl axisControl = GetFirstAxisControl(page);
            ClickButton(axisControl, "ServoToggleButton");
            ClickButton(axisControl, "HomeButton");
            ClickButton(axisControl, "ResetAxisAlarmButton");
            ClickButton(axisControl, "StopButton");

            var targetPosition = (TextBox)axisControl.FindName("TargetPositionTextBox");
            targetPosition.Text = "50";
            ClickButton(axisControl, "MoveAbsoluteButton");
            Ensure(axis.LastMotionProfile != null, "手动定位没有使用运动参数配置。");
            Ensure(axis.LastMotionProfile.Speed == 23, "手动定位没有使用轴配置的默认速度。");
            Ensure(axis.LastMotionProfile.Acceleration == 34, "手动定位没有使用轴配置的默认加速度。");
            Ensure(axis.LastMotionProfile.Deceleration == 45, "手动定位没有使用轴配置的默认减速度。");
            var jogMode = (ToggleButton)axisControl.FindName("JogModeToggleButton");
            jogMode.IsChecked = false;
            var stepJogButton = (Button)axisControl.FindName("PositiveJogButton");
            stepJogButton.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent
            });
            Ensure(axis.LastMotionProfile != null, "寸动没有使用当前运动参数配置。");
            Ensure(axis.CommandCount == initialCommandCount + 6, "手动按钮没有通过 IAxis 执行预期的定位、回零和伺服操作。");

            var operationMessage = (TextBlock)axisControl.FindName("OperationMessageText");
            targetPosition.Text = "101";
            ClickButton(axisControl, "MoveAbsoluteButton");
            Ensure(axis.CommandCount == initialCommandCount + 6, "绝对定位超出软限位时仍然调用了轴。");
            Ensure(operationMessage.Text.Contains("软限位"), "软限位拒绝原因没有显示在页面上。");

            jogMode.IsChecked = true;
            VerifyJogButtonsStartAndStop(axisControl, axis);

            var outputRows = (StackPanel)ioPage.FindName("DigitalOutputsPanel");
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
        private static void ClickButton(FrameworkElement control, string controlName)
        {
            var button = control.FindName(controlName) as Button;
            Ensure(button != null, "轴卡片缺少按钮控件：" + controlName + "。");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
        }

        /// <summary>返回手动页中的第一张轴卡片，供单轴内存卡检查使用。</summary>
        private static ManualAxisControl GetFirstAxisControl(ManualMotionDebugView page)
        {
            var axisCards = page.FindName("AxisCardsPanel") as WrapPanel;
            Ensure(axisCards != null && axisCards.Children.Count > 0, "手动页没有生成轴调试卡片。");
            return (ManualAxisControl)axisCards.Children[0];
        }

        /// <summary>模拟按下和松开两个方向点动按钮，确认轴卡片调用点动与受控停止。</summary>
        private static void VerifyJogButtonsStartAndStop(ManualAxisControl axisControl, SmokeAxis axis)
        {
            VerifyJogButtonStartAndStop(axisControl, axis, "NegativeJogButton", MotionDirection.Negative);
            VerifyJogButtonStartAndStop(axisControl, axis, "PositiveJogButton", MotionDirection.Positive);
        }

        /// <summary>检查指定轴卡片的点动按钮按下启动对应方向，松开后发送受控停止。</summary>
        private static void VerifyJogButtonStartAndStop(
            ManualAxisControl axisControl,
            SmokeAxis axis,
            string controlName,
            MotionDirection expectedDirection)
        {
            var jogButton = (Button)axisControl.FindName(controlName);
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
                return Task.FromResult(new AxisStatus(0, 0, false, false, false, false, false, false, false, false, null, false));
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
