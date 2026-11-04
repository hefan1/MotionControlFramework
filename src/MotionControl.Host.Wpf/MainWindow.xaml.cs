using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Wpf.ManualDebug;
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
            { "MotionControl", "运动控制" },
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
            { "Home", new[] { "运行日志" } },
            { "MotionControl", new[] { "设备总览", "设备准备", "工位运行", "轴监控", "IO监控", "当前报警", "历史报警", "轴手动", "IO手动", "轴映射配置", "IO映射", "安全联锁", "任务配置", "设备变量", "报警库", "设备工程" } },
            { "Statistics", new[] { "产量统计", "节拍统计", "运行统计" } },
            { "Monitor", new string[0] },
            { "Manual", new[] { "气缸手动", "通讯调试" } },
            { "Settings", new[] { "通讯设置", "数据采集" } },
            { "Alarms", new[] { "报警统计", "操作日志" } },
            { "Users", new[] { "密码登录", "刷卡登录", "权限管理" } },
            { "Help", new[] { "软件信息", "使用说明" } }
        };

        /// <summary>每秒更新主窗口顶部时间显示的计时器。</summary>
        private readonly DispatcherTimer _clockTimer;

        /// <summary>当前选中的顶部导航分区资源键。</summary>
        private string _currentSectionKey;

        /// <summary>当前选中的左侧页面中文资源键。</summary>
        private string _currentPageResourceKey;

        /// <summary>从流程状态进入 IO 监控时自动切到 DI，方便核对等待信号。</summary>
        private bool _selectInputTabWhenOpeningIoMonitor;

        /// <summary>由组合根注入的通用轴手动调试页面。</summary>
        private UserControl _manualMotionDebugPage;
        /// <summary>离线时按已保存映射重建原轴手动页面的只读视图。</summary>
        private Func<UserControl> _disconnectedManualMotionPageFactory;
        /// <summary>当前离线轴手动视图；连接后由真实运行时页面替换。</summary>
        private UserControl _disconnectedManualMotionPage;

        /// <summary>由组合根注入的通用 IO 手动调试页面。</summary>
        private UserControl _manualIoDebugPage;

        /// <summary>由组合根注入显示全部物理 DI/DO 状态的 IO 监控页面。</summary>
        private UserControl _ioMonitorPage;

        /// <summary>连接会话标志，不代表控制器当前读写一定正常。</summary>
        private bool _motionCardSessionConnected;

        /// <summary>当前连接后端是否为软件控制器，用于明确标识连接来源。</summary>
        private bool _softwareControllerSelected;

        /// <summary>IO 监控最近一次可见 DI 采样是否失败；空值表示尚未采样。</summary>
        private bool? _latestVisibleDiReadFailed;

        /// <summary>IO 监控最近一次可见 DO 采样是否失败；空值表示尚未采样。</summary>
        private bool? _latestVisibleDoReadFailed;

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

        /// <summary>由 Host 组合根注入的设备报警目录配置页面。</summary>
        private UserControl _alarmCatalogConfigurationPage;

        /// <summary>当前已连接设备的运行时，用于显示整机状态并执行整机复位。</summary>
        private MachineRuntime _machineRuntime;

        /// <summary>显式指定时记录隔离仿真配置目录；正常设备运行时为空。</summary>
        private string _simulationConfigurationRoot;

        /// <summary>标记整机复位正在执行，防止操作员重复点击复位按钮。</summary>
        private bool _isResetting;

        /// <summary>防止启动、暂停和停止命令被重复点击。</summary>
        private bool _isMachineCommandRunning;

        /// <summary>自动启动的当前拦截原因；为空时仍需结合状态检查是否允许启动。</summary>
        private string _automaticStartBlockReason;
        /// <summary>恢复记录可能保留运行保护；查询不能改变保护状态。</summary>
        private Func<string> _recoveryStartBlockReason;

        /// <summary>注入 Host 恢复检查，让两种自动入口显示同一个启动拦截原因。</summary>
        internal void SetRecoveryStartBlockReason(Func<string> getReason)
        {
            _recoveryStartBlockReason = getReason;
        }

        /// <summary>是否已完成最近一次自动启动就绪检查。</summary>
        private bool _automaticStartReadinessKnown;

        /// <summary>防止界面计时器并行读取多份自动启动就绪状态。</summary>
        private bool _automaticStartReadinessCheckInProgress;

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
            NavigateToSection("MotionControl");
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

        /// <summary>持续标识当前使用隔离仿真配置，并提供配置目录供核对。</summary>
        internal void SetSimulationConfiguration(string configurationRoot)
        {
            _simulationConfigurationRoot = configurationRoot;
            SimulationConfigurationBadge.ToolTip = configurationRoot;
            SimulationConfigurationBadge.Visibility = Visibility.Visible;
            UpdateWindowTitle();
        }

        /// <summary>更新窗口标题，语言切换后仍保留仿真配置标识。</summary>
        private void UpdateWindowTitle()
        {
            Title = GetLocalizedText("运动控制上位机");
            if (_simulationConfigurationRoot != null)
                Title += " — " + GetLocalizedText("隔离配置标识");
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

            ClearOperationReturnTarget();
            NavigateToSection(navigationButton.Tag as string);
        }

        /// <summary>窗口较窄时缩小导航图标间距，为设备状态及原有命令保留空间。</summary>
        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (PrimaryNavigation == null) return;
            double buttonWidth = Math.Max(38, Math.Min(60, (e.NewSize.Width - 560) / 9 - 8));
            foreach (object item in PrimaryNavigation.Children)
                if (item is ToggleButton button) button.Width = buttonWidth;
        }

        /// <summary>集中分区复用原页面，调用方还须检查具体页面名称。</summary>
        private bool IsCurrentPageInSection(string sectionKey)
        {
            return string.Equals(_currentSectionKey, sectionKey, StringComparison.Ordinal)
                || (string.Equals(_currentSectionKey, "MotionControl", StringComparison.Ordinal)
                    && Array.IndexOf(_pagesBySection["MotionControl"], _currentPageResourceKey) >= 0);
        }

        /// <summary>已迁移的页面始终进入运动控制分区，其他页面沿用原分类。</summary>
        private void NavigateToRelatedPage(string page, string defaultSection)
        {
            if (page == "流程状态" || page == "任务接入状态") page = "工位运行";
            bool isMotionControlPage = Array.IndexOf(_pagesBySection["MotionControl"], page) >= 0;
            _currentPageResourceKey = page;
            NavigateToSection(isMotionControlPage ? "MotionControl" : defaultSection);
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
                if (sectionKey == "MotionControl")
                {
                    string groupKey = pageResourceKey == "设备总览" ? "运动控制导航.运行监控"
                        : pageResourceKey == "轴手动" ? "运动控制导航.手动调试"
                        : pageResourceKey == "轴映射配置" ? "运动控制导航.工程配置" : null;
                    if (groupKey != null)
                        SidebarNavigation.Children.Add(new TextBlock
                        {
                            Text = GetLocalizedText(groupKey), Margin = new Thickness(14, 9, 4, 3),
                            Foreground = Brushes.DimGray, FontSize = 12, FontWeight = FontWeights.SemiBold
                        });
                }
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

            if (firstPageButton == null)
            {
                _currentPageResourceKey = null;
                _selectInputTabWhenOpeningIoMonitor = false;
                ManualDebugContent.Content = null;
                ManualDebugContent.Visibility = Visibility.Collapsed;
                PagePlaceholder.Visibility = Visibility.Collapsed;
                EmptySectionMessage.Visibility = Visibility.Visible;
                PageTitle.Text = GetLocalizedText(sectionTitleResourceKey);
                PageDescription.SetResourceReference(TextBlock.TextProperty, "分类暂无页面说明");
                return;
            }
            SelectSidebarPage(currentPageButton ?? firstPageButton);
        }

        /// <summary>
        /// 处理左侧页面导航点击，并更新当前显示页面。
        /// </summary>
        private void SidebarNavigationButton_Click(object sender, RoutedEventArgs e)
        {
            ClearOperationReturnTarget();
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

            EmptySectionMessage.Visibility = Visibility.Collapsed;

            foreach (object item in SidebarNavigation.Children)
            {
                ToggleButton pageButton = item as ToggleButton;
                if (pageButton != null)
                {
                    pageButton.IsChecked = ReferenceEquals(pageButton, selectedPageButton);
                }
            }

            string nextPageResourceKey = selectedPageButton.Tag as string;
            if (nextPageResourceKey == "轴手动" && _manualMotionDebugPage == null
                && _disconnectedManualMotionPageFactory != null)
                _disconnectedManualMotionPage = _disconnectedManualMotionPageFactory();
            _selectInputTabWhenOpeningIoMonitor = string.Equals(
                _currentPageResourceKey,
                "流程状态",
                StringComparison.Ordinal)
                && string.Equals(nextPageResourceKey, "IO监控", StringComparison.Ordinal);
            _currentPageResourceKey = nextPageResourceKey;
            PageTitle.Text = GetLocalizedText(_currentPageResourceKey);
            UpdatePageContent();
        }

        /// <summary>由 Host 组合根注入设备配置后的通用手动调试页。</summary>
        public void SetManualMotionDebugPage(UserControl manualMotionDebugPage)
        {
            _manualMotionDebugPage = manualMotionDebugPage;
            _disconnectedManualMotionPage = null;
            if (manualMotionDebugPage == null && _currentPageResourceKey == "轴手动"
                && _disconnectedManualMotionPageFactory != null)
                _disconnectedManualMotionPage = _disconnectedManualMotionPageFactory();
            UpdatePageContent();
        }

        /// <summary>注入离线只读轴页面工厂，进入页面时重读保存的映射。</summary>
        internal void SetDisconnectedManualMotionPageFactory(Func<UserControl> factory)
        {
            _disconnectedManualMotionPageFactory = factory;
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
            if (_ioMonitorPage is IoMonitorView oldMonitor)
                oldMonitor.VisibleReadStatusChanged -= IoMonitorVisibleReadStatusChanged;
            _ioMonitorPage = ioMonitorPage;
            _latestVisibleDiReadFailed = null;
            _latestVisibleDoReadFailed = null;
            if (_ioMonitorPage is IoMonitorView newMonitor)
                newMonitor.VisibleReadStatusChanged += IoMonitorVisibleReadStatusChanged;
            UpdateMotionCardFooter();
            UpdatePageContent();
        }

        /// <summary>分别保留 DI 和 DO 最近一次可见范围的读回结果，不推断整卡断线。</summary>
        private void IoMonitorVisibleReadStatusChanged(bool isInput, bool failed)
        {
            if (!_motionCardSessionConnected) return;
            if (isInput) _latestVisibleDiReadFailed = failed;
            else _latestVisibleDoReadFailed = failed;
            UpdateMotionCardFooter();
        }

        /// <summary>由 Host 组合根注入当前设备的只读轴状态监控页面。</summary>
        public void SetAxisMonitorPage(UserControl axisMonitorPage)
        {
            _axisMonitorPage = axisMonitorPage;
            UpdatePageContent();
        }

        /// <summary>由 Host 注入工位状态页，并接通只读的 IO 与报警导航。</summary>
        public void SetFlowStatusPage(UserControl flowStatusPage)
        {
            if (_flowStatusPage is FlowStatusView oldFlowStatusPage)
                oldFlowStatusPage.NavigationRequested -= OperationNavigationRequested;

            _flowStatusPage = flowStatusPage;

            if (_flowStatusPage is FlowStatusView flowStatusView)
            {
                flowStatusView.NavigationRequested += OperationNavigationRequested;
                UpdateAutomaticStartReadinessPages();
            }

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

        /// <summary>由 Host 组合根注入当前设备可维护的报警库页面。</summary>
        public void SetAlarmCatalogConfigurationPage(UserControl alarmCatalogConfigurationPage)
        {
            _alarmCatalogConfigurationPage = alarmCatalogConfigurationPage;
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
            _automaticStartBlockReason = null;
            _automaticStartReadinessKnown = machineRuntime == null
                || machineRuntime.State != MachineRunState.Ready;
            _equipmentOverviewPage.SetRuntime(machineRuntime);
            _currentAlarmsPage.SetRuntime(machineRuntime);
            _equipmentPreparationPage.SetRuntime(machineRuntime);
            if (_machineRuntime != null)
            {
                _machineRuntime.StatusChanged += MachineRuntime_StatusChanged;
            }

            UpdateMachineStateDisplay();
            if (_currentPageResourceKey == "工位运行") UpdatePageContent();
        }

        /// <summary>由 Host 组合根按运行时连接结果更新底栏的控制卡状态。</summary>
        public void SetMotionCardConnectionStatus(bool isConnected, string statusMessage = null, bool softwareController = false)
        {
            _motionCardSessionConnected = isConnected;
            _softwareControllerSelected = softwareController;
            if (!isConnected) { _latestVisibleDiReadFailed = null; _latestVisibleDoReadFailed = null; }
            UpdateMotionCardFooter();
            _equipmentOverviewPage.SetConnectionStatus(isConnected, statusMessage);
            UpdateMachineStateDisplay();
        }

        /// <summary>显示会话和最近可见 IO 读取异常；无采样不能当作通信正常。</summary>
        private void UpdateMotionCardFooter()
        {
            if (MotionCardConnectionStatus == null) return;
            bool diFailed = _motionCardSessionConnected && _latestVisibleDiReadFailed == true;
            bool doFailed = _motionCardSessionConnected && _latestVisibleDoReadFailed == true;
            if (_softwareControllerSelected && !_motionCardSessionConnected)
                MotionCardConnectionStatus.SetResourceReference(TextBlock.TextProperty, "软件控制器未连接");
            else if (diFailed || doFailed)
                MotionCardConnectionStatus.Text = string.Format(GetLocalizedText(_softwareControllerSelected
                        ? "软件控制器IO读取异常格式" : "控制卡会话IO读取异常格式"),
                    diFailed && doFailed ? "DI/DO" : diFailed ? "DI" : "DO");
            else if (_softwareControllerSelected)
                MotionCardConnectionStatus.SetResourceReference(TextBlock.TextProperty, "软件控制器已连接");
            else
                MotionCardConnectionStatus.SetResourceReference(TextBlock.TextProperty,
                    _motionCardSessionConnected ? "控制卡已连接" : "控制卡未连接");
            MotionCardConnectionStatus.Foreground = diFailed || doFailed || (_softwareControllerSelected && !_motionCardSessionConnected)
                ? Brushes.Firebrick : new SolidColorBrush(Color.FromRgb(94, 102, 107));
            MotionCardConnectionStatus.SetResourceReference(FrameworkElement.ToolTipProperty, "控制卡会话状态说明");
        }

        /// <summary>将导航目标放入原内容区，已接入页优先显示，其余页面保留占位。</summary>
        private void UpdatePageContent()
        {
            // 空分类没有可选择的页面，保留已显示的分类说明。
            if (string.IsNullOrEmpty(_currentPageResourceKey)) return;
            if (TryShowOperationPage()) return;
            if (TryShowEquipmentProjectPage()) return;
            if (_taskConfigurationPage != null && IsCurrentPageInSection("Settings") && _currentPageResourceKey == "任务配置")
            {
                ManualDebugContent.Content = _taskConfigurationPage; ManualDebugContent.Visibility = Visibility.Visible;
                PagePlaceholder.Visibility = Visibility.Collapsed;
                PageDescription.SetResourceReference(TextBlock.TextProperty, "任务配置页面说明"); return;
            }
            var isAxisMappingPage = _axisMappingConfigurationPage != null
                && IsCurrentPageInSection("Settings")
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
                && IsCurrentPageInSection("Settings")
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
                && IsCurrentPageInSection("Monitor")
                && string.Equals(_currentPageResourceKey, "轴监控", StringComparison.Ordinal);

            if (isAxisMonitorPage)
            {
                ManualDebugContent.Content = _axisMonitorPage;
                ManualDebugContent.Visibility = Visibility.Visible;
                PagePlaceholder.Visibility = Visibility.Collapsed;
                PageDescription.SetResourceReference(TextBlock.TextProperty, "轴状态页面说明");
                return;
            }

            var isIoMonitorPage = _ioMonitorPage != null
                && IsCurrentPageInSection("Monitor")
                && string.Equals(_currentPageResourceKey, "IO监控", StringComparison.Ordinal);

            if (isIoMonitorPage)
            {
                if (_selectInputTabWhenOpeningIoMonitor && _ioMonitorPage is IoMonitorView ioMonitorView)
                {
                    ioMonitorView.SelectDigitalInputs();
                }

                _selectInputTabWhenOpeningIoMonitor = false;
                ManualDebugContent.Content = _ioMonitorPage;
                ManualDebugContent.Visibility = Visibility.Visible;
                PagePlaceholder.Visibility = Visibility.Collapsed;
                PageDescription.SetResourceReference(TextBlock.TextProperty, "IO监控页面说明");
                return;
            }


            var isDeviceVariablePage = _deviceVariablePage != null
                && IsCurrentPageInSection("Monitor")
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
                && IsCurrentPageInSection("Settings")
                && string.Equals(_currentPageResourceKey, "安全联锁", StringComparison.Ordinal);

            if (isSafetyInterlockPage)
            {
                ManualDebugContent.Content = _safetyInterlockConfigurationPage;
                ManualDebugContent.Visibility = Visibility.Visible;
                PagePlaceholder.Visibility = Visibility.Collapsed;
                PageDescription.SetResourceReference(TextBlock.TextProperty, "安全联锁页面说明");
                return;
            }

            var isAlarmCatalogPage = _alarmCatalogConfigurationPage != null
                && IsCurrentPageInSection("Settings")
                && string.Equals(_currentPageResourceKey, "报警库", StringComparison.Ordinal);

            if (isAlarmCatalogPage)
            {
                ManualDebugContent.Content = _alarmCatalogConfigurationPage;
                ManualDebugContent.Visibility = Visibility.Visible;
                PagePlaceholder.Visibility = Visibility.Collapsed;
                PageDescription.SetResourceReference(TextBlock.TextProperty, "报警库页面说明");
                return;
            }

            var visibleManualMotionPage = _manualMotionDebugPage ?? _disconnectedManualMotionPage;
            var isAxisManualPage = visibleManualMotionPage != null
                && IsCurrentPageInSection("Manual")
                && string.Equals(_currentPageResourceKey, "轴手动", StringComparison.Ordinal);

            if (isAxisManualPage)
            {
                ManualDebugContent.Content = visibleManualMotionPage;
                ManualDebugContent.Visibility = Visibility.Visible;
                PagePlaceholder.Visibility = Visibility.Collapsed;
                PageDescription.SetResourceReference(TextBlock.TextProperty, "轴手动页面说明");
                return;
            }

            var isIoManualPage = _manualIoDebugPage != null
                && IsCurrentPageInSection("Manual")
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
            UpdateMotionCardFooter();
            _equipmentOverviewPage.SetLanguage(languageCode);
            _currentAlarmsPage.SetLanguage(languageCode);
            _equipmentPreparationPage.SetLanguage(languageCode);
            UpdateWindowTitle();
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

        /// <summary>从顶部启动当前选择的执行入口，复用各自的安全检查。</summary>
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
                if (runtime.ControlOwner == MachineControlOwner.TaskEngine)
                {
                    var launcher = _taskMonitorPage as Tasks.TaskMonitorLauncher;
                    if (launcher == null) throw new InvalidOperationException("任务运行入口尚未就绪。");
                    await launcher.RunFromHostAsync(true);
                }
                else await runtime.StartAsync(CancellationToken.None);
            }
            catch (Exception error)
            {
                if (ReferenceEquals(runtime, _machineRuntime) && runtime.State == MachineRunState.Ready)
                {
                    _automaticStartReadinessKnown = false;
                    await RefreshAutomaticStartReadinessAsync();
                }

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

        /// <summary>停止本次自动生产，保留现场并要求继续前明确核查。</summary>
        private async void MachineStopButton_Click(object sender, RoutedEventArgs e)
        {
            MachineRuntime runtime = _machineRuntime;
            if (runtime == null || _isMachineCommandRunning) return;
            _isMachineCommandRunning = true;
            UpdateMachineStateDisplay();
            try { await runtime.StopAutomaticProductionAsync(CancellationToken.None); }
            catch (Exception error) { ShowMachineCommandError("停止", error); }
            finally { _isMachineCommandRunning = false; UpdateMachineStateDisplay(); }
        }

        /// <summary>串行执行页面的暂停请求，并报告 Runtime 返回的具体错误。</summary>
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
                if (runtime.IsPreparing) await runtime.StopPreparationAsync(CancellationToken.None);
                else if (runtime.ControlOwner == MachineControlOwner.TaskEngine)
                {
                    var launcher = _taskMonitorPage as Tasks.TaskMonitorLauncher;
                    if (launcher == null) throw new InvalidOperationException("任务运行入口尚未就绪。");
                    await launcher.RunFromHostAsync(false);
                }
                else await runtime.PauseAsync(CancellationToken.None);
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
            if (_machineRuntime != null && _machineRuntime.State == MachineRunState.Ready)
            {
                _automaticStartReadinessKnown = false;
            }
            else
            {
                _automaticStartBlockReason = null;
                _automaticStartReadinessKnown = true;
            }

            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(UpdateMachineStateDisplay));
                return;
            }

            UpdateMachineStateDisplay();
        }

        /// <summary>把运行时状态转换成当前语言资源中的简短显示文字。</summary>
        internal static string GetMachineStateResourceKey(MachineRunState state)
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
                case MachineRunState.Stopped:
                    return "状态已停止";
                case MachineRunState.Alarmed:
                    return "状态报警";
                case MachineRunState.Preparing:
                    return "状态准备中";
                default:
                    return "状态未连接";
            }
        }

        /// <summary>同步顶部和底部整机状态，并按运行状态启用或禁用整机复位。</summary>
        private void UpdateMachineStateDisplay()
        {
            RefreshStationRunSource();
            MachineRunState actualState = _machineRuntime == null
                ? MachineRunState.Disconnected
                : _machineRuntime.State;
            MachineRunState state = _softwareControllerSelected && !_motionCardSessionConnected
                ? MachineRunState.Disconnected : actualState;
            string recoveryBlock = state == MachineRunState.Ready ? _recoveryStartBlockReason?.Invoke() : null;
            if (!string.IsNullOrWhiteSpace(recoveryBlock))
            {
                _automaticStartBlockReason = recoveryBlock;
                _automaticStartReadinessKnown = true;
            }
            string stateResourceKey = state == MachineRunState.Ready && !_automaticStartReadinessKnown
                ? "状态检查启动条件"
                : state == MachineRunState.Ready && !string.IsNullOrWhiteSpace(_automaticStartBlockReason)
                    ? "状态启动条件未满足"
                    : GetMachineStateResourceKey(state);
            string stateText = GetLocalizedText(stateResourceKey);

            CurrentMachineState.SetResourceReference(TextBlock.TextProperty, stateResourceKey);
            HeaderMessage.Text = GetLocalizedText("设备状态标题") + stateText;
            UpdateAutomaticStartReadinessPages();
            MachineResetButton.SetResourceReference(
                ContentControl.ContentProperty,
                _isResetting ? "整机复位中" : "整机复位");
            var taskLauncher = _taskMonitorPage as Tasks.TaskMonitorLauncher;
            bool taskOwner = _machineRuntime != null && _machineRuntime.ControlOwner == MachineControlOwner.TaskEngine;
            MachineResetButton.IsEnabled = !_isResetting
                && !_isMachineCommandRunning
                && _machineRuntime != null
                && (!taskOwner || taskLauncher?.IsCommandInProgress != true)
                && (state == MachineRunState.ResetRequired
                    || state == MachineRunState.Paused
                    || state == MachineRunState.Stopped
                    || state == MachineRunState.Alarmed
                    || (state == MachineRunState.Ready && taskOwner && taskLauncher?.NeedsResetFromHost == true));
            MachineStartButton.IsEnabled = !_isResetting && !_isMachineCommandRunning
                && _machineRuntime != null
                && state == MachineRunState.Ready
                && _automaticStartReadinessKnown
                && string.IsNullOrWhiteSpace(_automaticStartBlockReason)
                && (taskOwner ? taskLauncher?.CanStartFromHost == true
                    : _machineRuntime.ControlOwner == MachineControlOwner.StationFlows && _machineRuntime.StationFlows.Count > 0);
            MachinePauseButton.IsEnabled = !_isMachineCommandRunning
                && _machineRuntime != null
                && ((_machineRuntime.ControlOwner == MachineControlOwner.StationFlows && actualState == MachineRunState.Running)
                    || (taskOwner && taskLauncher?.CanPauseFromHost == true) || _machineRuntime.IsPreparing);
            MachineStopButton.IsEnabled = !_isMachineCommandRunning
                && _machineRuntime != null
                && ((_machineRuntime.ControlOwner == MachineControlOwner.StationFlows && (actualState == MachineRunState.Running || actualState == MachineRunState.Pausing || actualState == MachineRunState.Paused))
                    || (taskOwner && taskLauncher?.CanStopFromHost == true) || _machineRuntime.IsPreparing);
        }

        /// <summary>把同一份启动就绪结果同步到设备总览和流程状态页。</summary>
        private void UpdateAutomaticStartReadinessPages()
        {
            MachineRunState state = _machineRuntime == null
                ? MachineRunState.Disconnected
                : _machineRuntime.State;
            bool isReady = state == MachineRunState.Ready;
            string blockReason = isReady ? _automaticStartBlockReason : null;
            bool checking = isReady && !_automaticStartReadinessKnown;

            if (_equipmentOverviewPage != null)
            {
                _equipmentOverviewPage.SetAutomaticStartReadiness(blockReason, checking);
            }

            if (_flowStatusPage is FlowStatusView flowStatusView)
            {
                flowStatusView.SetAutomaticStartReadiness(blockReason, checking);
            }
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
                bool confirmedStoppedProduction = false;
                if (runtime.ProductionStopReviewRequired)
                {
                    string message = GetLocalizedText("停止后核查提示");
                    if (MessageBox.Show(message, GetLocalizedText("停止后核查标题"), MessageBoxButton.YesNo,
                        MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                    confirmedStoppedProduction = true;
                }
                var launcher = _taskMonitorPage as Tasks.TaskMonitorLauncher;
                if (runtime.IsTaskEngineControlClaimed)
                {
                    if (launcher == null) throw new InvalidOperationException("任务运行入口尚未就绪，不能核查并复位任务。");
                    await launcher.ResetFromHostAsync(confirmedStoppedProduction);
                }
                else await runtime.ResetAsync(CancellationToken.None, confirmedStoppedProduction);
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
        private async void ClockTimer_Tick(object sender, EventArgs e)
        {
            UpdateClock();
            await RefreshAutomaticStartReadinessAsync();
        }

        /// <summary>每秒刷新自动启动拦截原因，让总览与顶部启动按钮反映轴和安全就绪状态。</summary>
        private async Task RefreshAutomaticStartReadinessAsync()
        {
            MachineRuntime runtime = _machineRuntime;
            string recoveryBlock = runtime != null && runtime.State == MachineRunState.Ready
                ? _recoveryStartBlockReason?.Invoke() : null;
            if (!string.IsNullOrWhiteSpace(recoveryBlock))
            {
                _automaticStartBlockReason = recoveryBlock;
                _automaticStartReadinessKnown = true;
                UpdateMachineStateDisplay();
                return;
            }
            if (runtime != null && runtime.State == MachineRunState.Ready && runtime.ControlOwner == MachineControlOwner.TaskEngine)
            {
                var launcher = _taskMonitorPage as Tasks.TaskMonitorLauncher;
                _automaticStartBlockReason = launcher == null ? "任务运行入口尚未就绪。" : launcher.GetHostStartBlockReason();
                _automaticStartReadinessKnown = true;
                UpdateMachineStateDisplay();
                return;
            }
            if (runtime == null || runtime.State != MachineRunState.Ready || runtime.StationFlows.Count == 0)
            {
                _automaticStartBlockReason = null;
                _automaticStartReadinessKnown = true;
                UpdateMachineStateDisplay();
                return;
            }

            if (_automaticStartReadinessCheckInProgress)
            {
                return;
            }

            _automaticStartReadinessCheckInProgress = true;
            _automaticStartReadinessKnown = false;
            UpdateMachineStateDisplay();

            string blockReason;
            try
            {
                blockReason = await runtime
                    .GetAutomaticStartBlockReasonAsync(CancellationToken.None)
                    .ConfigureAwait(true);
            }
            catch (Exception readinessError)
            {
                blockReason = "无法确认自动启动条件。" + readinessError.Message;
            }
            finally
            {
                _automaticStartReadinessCheckInProgress = false;
            }

            if (!ReferenceEquals(runtime, _machineRuntime))
            {
                return;
            }

            if (runtime.State == MachineRunState.Ready)
            {
                _automaticStartBlockReason = blockReason;
                _automaticStartReadinessKnown = true;
            }
            else
            {
                _automaticStartBlockReason = null;
                _automaticStartReadinessKnown = true;
            }

            UpdateMachineStateDisplay();
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
