using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using MotionControl.Framework.Runtime.Machine;

namespace MotionControl.Host.Wpf
{
    /// <summary>提供设备操作外壳、功能导航、语言切换及状态栏显示。</summary>
    public partial class MainWindow : Window
    {
        // 这里保存的是语言资源键。键使用中文界面原文，方便开发时按画面文字搜索。
        private readonly Dictionary<string, string> _sectionTitles = new Dictionary<string, string>
        {
            { "Home", "首页" },
            { "Statistics", "统计" },
            { "Monitor", "监控" },
            { "Manual", "手动" },
            { "Settings", "设置" },
            { "Alarms", "报警" },
            { "Users", "用户管理" },
            { "Help", "帮助" }
        };

        /// <summary>按导航分区保存左侧子页面的中文资源键。</summary>
        private readonly Dictionary<string, string[]> _pagesBySection = new Dictionary<string, string[]>
        {
            { "Home", new[] { "设备总览", "轴状态", "运行日志" } },
            { "Statistics", new[] { "产量统计", "节拍统计", "运行统计" } },
            { "Monitor", new[] { "设备状态", "轴监控", "IO监控", "流程状态", "任务运行", "设备变量" } },
            { "Manual", new[] { "轴手动", "IO手动", "气缸手动", "通讯调试" } },
            { "Settings", new[] { "轴映射配置", "IO映射", "安全联锁", "通讯设置", "数据采集", "任务配置" } },
            { "Alarms", new[] { "当前报警", "历史报警", "报警统计", "操作日志" } },
            { "Users", new[] { "密码登录", "刷卡登录", "权限管理" } },
            { "Help", new[] { "软件信息", "使用说明" } }
        };

        /// <summary>每秒更新主窗口顶部时间显示的计时器。</summary>
        private readonly DispatcherTimer _clockTimer;

        /// <summary>当前选中的顶部导航分区资源键。</summary>
        private string _currentSectionKey;

        /// <summary>当前选中的左侧页面中文资源键。</summary>
        private string _currentPageResourceKey;

        /// <summary>由组合根注入的通用轴手动调试页面。</summary>
        private UserControl _manualMotionDebugPage;

        /// <summary>由组合根注入的通用 IO 手动调试页面。</summary>
        private UserControl _manualIoDebugPage;

        /// <summary>由组合根注入显示全部物理 DI/DO 状态的 IO 监控页面。</summary>
        private UserControl _ioMonitorPage;

        /// <summary>由 Host 注入按已映射逻辑轴读取状态的只读监控页面。</summary>
        private UserControl _axisMonitorPage;

        /// <summary>由 Host 注入的自动工位步骤与报警状态页面。</summary>
        private UserControl _flowStatusPage;

        /// <summary>由 Host 注入当前单机的变量定义、运行值和监控页面。</summary>
        private UserControl _deviceVariablePage;

        /// <summary>由 Host 组合根注入的通用轴映射配置页面。</summary>
        private UserControl _axisMappingConfigurationPage;

        /// <summary>由 Host 组合根注入的通用 DI/DO 映射配置页面。</summary>
        private UserControl _ioMappingConfigurationPage;

        /// <summary>由 Host 组合根注入的软件安全联锁配置页面。</summary>
        private UserControl _safetyInterlockConfigurationPage;

        /// <summary>当前已连接设备的运行时，用于显示整机状态并执行整机复位。</summary>
        private MachineRuntime _machineRuntime;

        /// <summary>标记整机复位正在执行，防止操作员重复点击复位按钮。</summary>
        private bool _isResetting;

        /// <summary>防止启动、暂停和停止命令被重复点击。</summary>
        private bool _isMachineCommandRunning;

        /// <summary>当前界面语言代码；支持 zh-CN 和 en-US。</summary>
        public string CurrentLanguage { get; private set; }

        /// <summary>当前界面的语言资源替换完成后通知代码生成的页面刷新显示文字。</summary>
        public event EventHandler LanguageChanged;

        /// <summary>
        /// 初始化主窗口、默认导航、多语言界面、整机状态和顶部时钟。
        /// </summary>
        public MainWindow()
        {
            InitializeComponent();

            CurrentLanguage = "zh-CN";
            CreateOperationPages();
            NavigateToSection("Home");
            UpdateMachineStateDisplay();

            _clockTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _clockTimer.Tick += ClockTimer_Tick;
            UpdateClock();
            _clockTimer.Start();

            Closed += MainWindow_Closed;
        }

        /// <summary>
        /// 处理顶部主导航点击，并切换到对应功能分区。
        /// </summary>
        private void PrimaryNavigationButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleButton navigationButton = sender as ToggleButton;
            if (navigationButton == null)
            {
                return;
            }

            NavigateToSection(navigationButton.Tag as string);
        }

        /// <summary>
        /// 根据功能分区生成左侧导航，并显示该分区的默认页面。
        /// </summary>
        private void NavigateToSection(string sectionKey)
        {
            string sectionTitleResourceKey;
            string[] pageResourceKeys;
            if (string.IsNullOrEmpty(sectionKey)
                || !_sectionTitles.TryGetValue(sectionKey, out sectionTitleResourceKey)
                || !_pagesBySection.TryGetValue(sectionKey, out pageResourceKeys))
            {
                return;
            }

            _currentSectionKey = sectionKey;
            SidebarTitle.Text = GetLocalizedText(sectionTitleResourceKey);

            foreach (object item in PrimaryNavigation.Children)
            {
                ToggleButton navigationButton = item as ToggleButton;
                if (navigationButton != null)
                {
                    navigationButton.IsChecked = string.Equals(
                        navigationButton.Tag as string,
                        sectionKey,
                        StringComparison.Ordinal);
                }
            }

            SidebarNavigation.Children.Clear();
            ToggleButton firstPageButton = null;
            ToggleButton currentPageButton = null;

            foreach (string pageResourceKey in pageResourceKeys)
            {
                ToggleButton pageButton = new ToggleButton
                {
                    Content = GetLocalizedText(pageResourceKey),
                    Tag = pageResourceKey,
                    Style = (Style)FindResource("SidebarNavigationButton")
                };
                pageButton.Click += SidebarNavigationButton_Click;
                SidebarNavigation.Children.Add(pageButton);

                if (firstPageButton == null)
                {
                    firstPageButton = pageButton;
                }

                if (string.Equals(pageResourceKey, _currentPageResourceKey, StringComparison.Ordinal))
                {
                    currentPageButton = pageButton;
                }
            }

            SelectSidebarPage(currentPageButton ?? firstPageButton);
        }

        /// <summary>
        /// 处理左侧页面导航点击，并更新当前显示页面。
        /// </summary>
        private void SidebarNavigationButton_Click(object sender, RoutedEventArgs e)
        {
            SelectSidebarPage(sender as ToggleButton);
        }

        /// <summary>
        /// 选中左侧导航页面，并更新页面标题和按钮状态。
        /// </summary>
        private void SelectSidebarPage(ToggleButton selectedPageButton)
        {
            if (selectedPageButton == null)
            {
                return;
            }

            foreach (object item in SidebarNavigation.Children)
            {
                ToggleButton pageButton = item as ToggleButton;
                if (pageButton != null)
                {
                    pageButton.IsChecked = ReferenceEquals(pageButton, selectedPageButton);
                }
            }

            _currentPageResourceKey = selectedPageButton.Tag as string;
            PageTitle.Text = GetLocalizedText(_currentPageResourceKey);
            UpdatePageContent();
        }

        /// <summary>由 Host 组合根注入设备配置后的通用手动调试页。</summary>
        public void SetManualMotionDebugPage(UserControl manualMotionDebugPage)
        {
            _manualMotionDebugPage = manualMotionDebugPage;
            UpdatePageContent();
        }

        /// <summary>由 Host 组合根注入设备映射对应的 IO 手动调试页面。</summary>
        public void SetManualIoDebugPage(UserControl manualIoDebugPage)
        {
            _manualIoDebugPage = manualIoDebugPage;
            UpdatePageContent();
        }

        /// <summary>由 Host 组合根注入当前控制卡对应的物理 IO 监控页面。</summary>
        public void SetIoMonitorPage(UserControl ioMonitorPage)
        {
            _ioMonitorPage = ioMonitorPage;
            UpdatePageContent();
        }

        /// <summary>由 Host 组合根注入当前设备的只读轴状态监控页面。</summary>
        public void SetAxisMonitorPage(UserControl axisMonitorPage)
        {
            _axisMonitorPage = axisMonitorPage;
            UpdatePageContent();
        }

        /// <summary>由 Host 注入与手动页面共用同一 Runtime 的工位状态页面。</summary>
        public void SetFlowStatusPage(UserControl flowStatusPage)
        {
            _flowStatusPage = flowStatusPage;
            UpdatePageContent();
        }

        /// <summary>由 Host 组合根注入按当前 MachineId 创建的设备变量页面。</summary>
        public void SetDeviceVariablePage(UserControl deviceVariablePage)
        {
            _deviceVariablePage = deviceVariablePage;
            UpdatePageContent();
        }

        /// <summary>由 Host 组合根注入按设备配置存储创建的轴映射页面。</summary>
        public void SetAxisMappingConfigurationPage(UserControl axisMappingConfigurationPage)
        {
            _axisMappingConfigurationPage = axisMappingConfigurationPage;
            UpdatePageContent();
        }

        /// <summary>由 Host 组合根注入当前设备的 DI/DO 映射配置页面。</summary>
        public void SetIoMappingConfigurationPage(UserControl ioMappingConfigurationPage)
        {
            _ioMappingConfigurationPage = ioMappingConfigurationPage;
            UpdatePageContent();
        }

        /// <summary>由 Host 组合根注入当前设备的软件安全联锁配置页面。</summary>
        public void SetSafetyInterlockConfigurationPage(UserControl safetyInterlockConfigurationPage)
        {
            _safetyInterlockConfigurationPage = safetyInterlockConfigurationPage;
            UpdatePageContent();
        }

        /// <summary>接收当前设备运行时，并根据其状态更新整机状态文字和复位按钮。</summary>
        public void SetMachineRuntime(MachineRuntime machineRuntime)
        {
            if (ReferenceEquals(_machineRuntime, machineRuntime))
            {
                UpdateMachineStateDisplay();
                return;
            }

            if (_machineRuntime != null)
            {
                _machineRuntime.StatusChanged -= MachineRuntime_StatusChanged;
            }

            _machineRuntime = machineRuntime;
            _equipmentOverviewPage.SetRuntime(machineRuntime);
            _currentAlarmsPage.SetRuntime(machineRuntime);
            if (_machineRuntime != null)
            {
                _machineRuntime.StatusChanged += MachineRuntime_StatusChanged;
            }

            UpdateMachineStateDisplay();
        }

        /// <summary>由 Host 组合根按运行时连接结果更新底栏的控制卡状态。</summary>
        public void SetMotionCardConnectionStatus(bool isConnected)
        {
            string footerResourceKey = isConnected ? "控制卡已连接" : "控制卡未连接";

            MotionCardConnectionStatus.SetResourceReference(TextBlock.TextProperty, footerResourceKey);
        }

        /// <summary>将导航目标放入原内容区，已接入页优先显示，其余页面保留占位。</summary>
        private void UpdatePageContent()
        {
            if (TryShowOperationPage()) return;
            if (_taskConfigurationPage != null && _currentSectionKey == "Settings" && _currentPageResourceKey == "任务配置")
            {
                ManualDebugContent.Content = _taskConfigurationPage; ManualDebugContent.Visibility = Visibility.Visible;
                PagePlaceholder.Visibility = Visibility.Collapsed;
                PageDescription.SetResourceReference(TextBlock.TextProperty, "任务配置页面说明"); return;
            }
            if (_taskMonitorPage != null && _currentSectionKey == "Monitor" && _currentPageResourceKey == "任务运行")
            {
                ManualDebugContent.Content = _taskMonitorPage;
                ManualDebugContent.Visibility = Visibility.Visible;
                PagePlaceholder.Visibility = Visibility.Collapsed;
                PageDescription.SetResourceReference(TextBlock.TextProperty, "任务运行页面说明");
                return;
            }
            var isAxisMappingPage = _axisMappingConfigurationPage != null
                && string.Equals(_currentSectionKey, "Settings", StringComparison.Ordinal)
                && string.Equals(_currentPageResourceKey, "轴映射配置", StringComparison.Ordinal);

            if (isAxisMappingPage)
            {
                ManualDebugContent.Content = _axisMappingConfigurationPage;
                ManualDebugContent.Visibility = Visibility.Visible;
                PagePlaceholder.Visibility = Visibility.Collapsed;
                PageDescription.SetResourceReference(TextBlock.TextProperty, "轴映射页面说明");
                return;
            }

            var isIoMappingPage = _ioMappingConfigurationPage != null
                && string.Equals(_currentSectionKey, "Settings", StringComparison.Ordinal)
                && string.Equals(_currentPageResourceKey, "IO映射", StringComparison.Ordinal);

            if (isIoMappingPage)
            {
                ManualDebugContent.Content = _ioMappingConfigurationPage;
                ManualDebugContent.Visibility = Visibility.Visible;
                PagePlaceholder.Visibility = Visibility.Collapsed;
                PageDescription.SetResourceReference(TextBlock.TextProperty, "IO映射页面说明");
                return;
            }

            var isAxisMonitorPage = _axisMonitorPage != null
                && ((string.Equals(_currentSectionKey, "Home", StringComparison.Ordinal)
                        && string.Equals(_currentPageResourceKey, "轴状态", StringComparison.Ordinal))
                    || (string.Equals(_currentSectionKey, "Monitor", StringComparison.Ordinal)
                        && string.Equals(_currentPageResourceKey, "轴监控", StringComparison.Ordinal)));

            if (isAxisMonitorPage)
            {
                ManualDebugContent.Content = _axisMonitorPage;
                ManualDebugContent.Visibility = Visibility.Visible;
                PagePlaceholder.Visibility = Visibility.Collapsed;
                PageDescription.SetResourceReference(TextBlock.TextProperty, "轴状态页面说明");
                return;
            }

            var isIoMonitorPage = _ioMonitorPage != null
                && string.Equals(_currentSectionKey, "Monitor", StringComparison.Ordinal)
                && string.Equals(_currentPageResourceKey, "IO监控", StringComparison.Ordinal);

            if (isIoMonitorPage)
            {
                ManualDebugContent.Content = _ioMonitorPage;
                ManualDebugContent.Visibility = Visibility.Visible;
                PagePlaceholder.Visibility = Visibility.Collapsed;
                PageDescription.SetResourceReference(TextBlock.TextProperty, "IO监控页面说明");
                return;
            }

            var isFlowStatusPage = _flowStatusPage != null
                && string.Equals(_currentSectionKey, "Monitor", StringComparison.Ordinal)
                && string.Equals(_currentPageResourceKey, "流程状态", StringComparison.Ordinal);

            if (isFlowStatusPage)
            {
                ManualDebugContent.Content = _flowStatusPage;
                ManualDebugContent.Visibility = Visibility.Visible;
                PagePlaceholder.Visibility = Visibility.Collapsed;
                PageDescription.SetResourceReference(TextBlock.TextProperty, "工位流程状态说明");
                return;
            }

            var isDeviceVariablePage = _deviceVariablePage != null
                && string.Equals(_currentSectionKey, "Monitor", StringComparison.Ordinal)
                && string.Equals(_currentPageResourceKey, "设备变量", StringComparison.Ordinal);

            if (isDeviceVariablePage)
            {
                ManualDebugContent.Content = _deviceVariablePage;
                ManualDebugContent.Visibility = Visibility.Visible;
                PagePlaceholder.Visibility = Visibility.Collapsed;
                PageDescription.SetResourceReference(TextBlock.TextProperty, "设备变量页面说明");
                return;
            }

            var isSafetyInterlockPage = _safetyInterlockConfigurationPage != null
                && string.Equals(_currentSectionKey, "Settings", StringComparison.Ordinal)
                && string.Equals(_currentPageResourceKey, "安全联锁", StringComparison.Ordinal);

            if (isSafetyInterlockPage)
            {
                ManualDebugContent.Content = _safetyInterlockConfigurationPage;
                ManualDebugContent.Visibility = Visibility.Visible;
                PagePlaceholder.Visibility = Visibility.Collapsed;
                PageDescription.SetResourceReference(TextBlock.TextProperty, "安全联锁页面说明");
                return;
            }

            var isAxisManualPage = _manualMotionDebugPage != null
                && string.Equals(_currentSectionKey, "Manual", StringComparison.Ordinal)
                && string.Equals(_currentPageResourceKey, "轴手动", StringComparison.Ordinal);

            if (isAxisManualPage)
            {
                ManualDebugContent.Content = _manualMotionDebugPage;
                ManualDebugContent.Visibility = Visibility.Visible;
                PagePlaceholder.Visibility = Visibility.Collapsed;
                PageDescription.SetResourceReference(TextBlock.TextProperty, "轴手动页面说明");
                return;
            }

            var isIoManualPage = _manualIoDebugPage != null
                && string.Equals(_currentSectionKey, "Manual", StringComparison.Ordinal)
                && string.Equals(_currentPageResourceKey, "IO手动", StringComparison.Ordinal);

            if (isIoManualPage)
            {
                ManualDebugContent.Content = _manualIoDebugPage;
                ManualDebugContent.Visibility = Visibility.Visible;
                PagePlaceholder.Visibility = Visibility.Collapsed;
                PageDescription.SetResourceReference(TextBlock.TextProperty, "IO手动页面说明");
                return;
            }

            ManualDebugContent.Content = null;
            ManualDebugContent.Visibility = Visibility.Collapsed;
            PagePlaceholder.Visibility = Visibility.Visible;
            PageDescription.SetResourceReference(TextBlock.TextProperty, "该页面为导航占位具体功能后续接入");
        }

        /// <summary>
        /// 处理语言下拉框变化，并应用所选界面语言。
        /// </summary>
        private void LanguageSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded)
            {
                return;
            }

            ComboBoxItem selectedLanguage = LanguageSelector.SelectedItem as ComboBoxItem;
            string languageCode = selectedLanguage == null ? "zh-CN" : selectedLanguage.Tag as string;
            ApplyLanguage(languageCode);
        }

        /// <summary>
        /// 加载指定语言资源，并刷新当前导航显示文字。
        /// </summary>
        private void ApplyLanguage(string languageCode)
        {
            if (!string.Equals(languageCode, "en-US", StringComparison.OrdinalIgnoreCase))
            {
                languageCode = "zh-CN";
            }

            CurrentLanguage = languageCode;

            ResourceDictionary languageResources = new ResourceDictionary
            {
                Source = new Uri(
                    "/MotionControl.Host.Wpf;component/Localization/Strings." + languageCode + ".xaml",
                    UriKind.Relative)
            };

            ReplaceLanguageResources(languageResources);
            _equipmentOverviewPage.SetLanguage(languageCode);
            _currentAlarmsPage.SetLanguage(languageCode);
            Title = GetLocalizedText("运动控制上位机");
            UpdateMachineStateDisplay();

            // 左侧导航由代码生成，切换语言后重新生成当前导航项；保留当前选中页面。
            NavigateToSection(_currentSectionKey);

            EventHandler languageChanged = LanguageChanged;
            if (languageChanged != null)
            {
                languageChanged(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// 根据中文资源键读取当前语言对应的显示文字。
        /// </summary>
        private string GetLocalizedText(string chineseResourceKey)
        {
            return (string)FindResource(chineseResourceKey);
        }

        /// <summary>
        /// 将应用中已加载的语言资源替换为指定资源。
        /// </summary>
        private void ReplaceLanguageResources(ResourceDictionary languageResources)
        {
            var applicationDictionaries = Application.Current.Resources.MergedDictionaries;
            for (int index = 0; index < applicationDictionaries.Count; index++)
            {
                Uri dictionarySource = applicationDictionaries[index].Source;
                if (dictionarySource != null
                    && dictionarySource.OriginalString.IndexOf(
                        "Localization/Strings.",
                        StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    applicationDictionaries[index] = languageResources;
                    return;
                }
            }

            applicationDictionaries.Add(languageResources);
        }

        /// <summary>从顶部启动按钮调用当前 Runtime 的自动工位流程。</summary>
        private async void MachineStartButton_Click(object sender, RoutedEventArgs e)
        {
            MachineRuntime runtime = _machineRuntime;
            if (runtime == null || _isMachineCommandRunning)
            {
                return;
            }

            _isMachineCommandRunning = true;
            UpdateMachineStateDisplay();
            try
            {
                await runtime.StartAsync(CancellationToken.None);
            }
            catch (Exception error)
            {
                ShowMachineCommandError("启动", error);
            }
            finally
            {
                _isMachineCommandRunning = false;
                UpdateMachineStateDisplay();
            }
        }

        /// <summary>从顶部暂停按钮请求工位受控退出当前步骤。</summary>
        private async void MachinePauseButton_Click(object sender, RoutedEventArgs e)
        {
            await PauseMachineAsync("暂停");
        }

        /// <summary>演示工位的停止按钮也请求受控停止，步骤保留供复位后重试。</summary>
        private async void MachineStopButton_Click(object sender, RoutedEventArgs e)
        {
            await PauseMachineAsync("停止");
        }

        /// <summary>串行执行页面的暂停或停止请求，并报告 Runtime 返回的具体错误。</summary>
        private async System.Threading.Tasks.Task PauseMachineAsync(string commandName)
        {
            MachineRuntime runtime = _machineRuntime;
            if (runtime == null || _isMachineCommandRunning)
            {
                return;
            }

            _isMachineCommandRunning = true;
            UpdateMachineStateDisplay();
            try
            {
                await runtime.PauseAsync(CancellationToken.None);
            }
            catch (Exception error)
            {
                ShowMachineCommandError(commandName, error);
            }
            finally
            {
                _isMachineCommandRunning = false;
                UpdateMachineStateDisplay();
            }
        }

        /// <summary>将自动命令失败原因反馈给操作员，保留异常上下文以便排查。</summary>
        private void ShowMachineCommandError(string commandName, Exception error)
        {
            MessageBox.Show(
                GetLocalizedText(commandName) + "：" + error.Message,
                GetLocalizedText("自动流程命令失败标题"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        /// <summary>在界面线程上响应运行时状态变化并刷新整机状态显示。</summary>
        private void MachineRuntime_StatusChanged(object sender, EventArgs e)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(UpdateMachineStateDisplay));
                return;
            }

            UpdateMachineStateDisplay();
        }

        /// <summary>把运行时状态转换成当前语言资源中的简短显示文字。</summary>
        private string GetMachineStateResourceKey(MachineRunState state)
        {
            switch (state)
            {
                case MachineRunState.ResetRequired:
                    return "状态等待复位";
                case MachineRunState.Ready:
                    return "状态就绪";
                case MachineRunState.Running:
                    return "状态运行中";
                case MachineRunState.Pausing:
                    return "状态暂停中";
                case MachineRunState.Paused:
                    return "状态已暂停";
                case MachineRunState.Alarmed:
                    return "状态报警";
                default:
                    return "状态未连接";
            }
        }

        /// <summary>同步顶部和底部整机状态，并按运行状态启用或禁用整机复位。</summary>
        private void UpdateMachineStateDisplay()
        {
            MachineRunState state = _machineRuntime == null
                ? MachineRunState.Disconnected
                : _machineRuntime.State;
            string stateResourceKey = GetMachineStateResourceKey(state);
            string stateText = GetLocalizedText(stateResourceKey);

            CurrentMachineState.SetResourceReference(TextBlock.TextProperty, stateResourceKey);
            HeaderMessage.Text = GetLocalizedText("设备状态标题") + stateText;
            MachineResetButton.SetResourceReference(
                ContentControl.ContentProperty,
                _isResetting ? "整机复位中" : "整机复位");
            MachineResetButton.IsEnabled = !_isResetting
                && !_isMachineCommandRunning
                && _machineRuntime != null
                && (state == MachineRunState.ResetRequired
                    || state == MachineRunState.Paused
                    || state == MachineRunState.Alarmed);
            MachineStartButton.IsEnabled = !_isResetting && !_isMachineCommandRunning
                && _machineRuntime != null && _machineRuntime.StationFlows.Count > 0
                && state == MachineRunState.Ready;
            MachinePauseButton.IsEnabled = !_isMachineCommandRunning
                && _machineRuntime != null && state == MachineRunState.Running;
            MachineStopButton.IsEnabled = !_isMachineCommandRunning
                && _machineRuntime != null
                && (state == MachineRunState.Running || state == MachineRunState.Pausing);
        }

        /// <summary>调用 Runtime 的整机复位流程，并向操作员显示未能完成的原因。</summary>
        private async void MachineResetButton_Click(object sender, RoutedEventArgs e)
        {
            MachineRuntime runtime = _machineRuntime;
            if (runtime == null || _isResetting)
            {
                return;
            }

            _isResetting = true;
            UpdateMachineStateDisplay();
            try
            {
                await runtime.ResetAsync(CancellationToken.None);
            }
            catch (Exception error)
            {
                MessageBox.Show(
                    GetLocalizedText("整机复位失败内容") + error.Message,
                    GetLocalizedText("整机复位失败标题"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            finally
            {
                _isResetting = false;
                UpdateMachineStateDisplay();
            }
        }

        /// <summary>
        /// 响应时钟定时器，并刷新主窗口的当前时间。
        /// </summary>
        private void ClockTimer_Tick(object sender, EventArgs e)
        {
            UpdateClock();
        }

        /// <summary>
        /// 将当前本地时间显示在主窗口顶部。
        /// </summary>
        private void UpdateClock()
        {
            ClockText.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }

        /// <summary>
        /// 关闭主窗口时停止时钟定时器并解除运行时状态事件订阅。
        /// </summary>
        private void MainWindow_Closed(object sender, EventArgs e)
        {
            _clockTimer.Stop();
            if (_machineRuntime != null)
            {
                _machineRuntime.StatusChanged -= MachineRuntime_StatusChanged;
                _machineRuntime = null;
            }
        }
    }
}
