using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace MotionControl.Host.Wpf
{
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

        private readonly Dictionary<string, string[]> _pagesBySection = new Dictionary<string, string[]>
        {
            { "Home", new[] { "设备总览", "轴状态", "IO状态", "运行日志" } },
            { "Statistics", new[] { "产量统计", "节拍统计", "运行统计" } },
            { "Monitor", new[] { "设备状态", "轴监控", "IO监控", "流程状态" } },
            { "Manual", new[] { "轴手动", "IO手动", "气缸手动", "通讯调试" } },
            { "Settings", new[] { "轴映射配置", "IO映射", "通讯设置", "数据采集" } },
            { "Alarms", new[] { "当前报警", "历史报警", "报警统计", "操作日志" } },
            { "Users", new[] { "密码登录", "刷卡登录", "权限管理" } },
            { "Help", new[] { "软件信息", "使用说明" } }
        };

        private readonly DispatcherTimer _clockTimer;
        private string _currentSectionKey;
        private string _currentPageResourceKey;
        private UserControl _manualMotionDebugPage;

        /// <summary>由 Host 组合根注入的通用轴映射配置页面。</summary>
        private UserControl _axisMappingConfigurationPage;

        /// <summary>由 Host 组合根注入的通用 DI/DO 映射配置页面。</summary>
        private UserControl _ioMappingConfigurationPage;

        public string CurrentLanguage { get; private set; }

        /// <summary>
        /// 初始化主窗口、默认导航、多语言界面和顶部时钟。
        /// </summary>
        public MainWindow()
        {
            InitializeComponent();

            CurrentLanguage = "zh-CN";
            NavigateToSection("Home");

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

        /// <summary>由 Host 组合根按运行时连接结果更新底栏的控制卡状态。</summary>
        public void SetMotionCardConnectionStatus(bool isConnected)
        {
            string footerResourceKey = isConnected ? "控制卡已连接" : "控制卡未连接";

            MotionCardConnectionStatus.SetResourceReference(TextBlock.TextProperty, footerResourceKey);
        }

        /// <summary>根据当前导航在映射页、手动调试页或占位内容之间切换。</summary>
        private void UpdatePageContent()
        {
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

            var isManualDebugPage = _manualMotionDebugPage != null
                && string.Equals(_currentSectionKey, "Manual", StringComparison.Ordinal)
                && (string.Equals(_currentPageResourceKey, "轴手动", StringComparison.Ordinal)
                    || string.Equals(_currentPageResourceKey, "IO手动", StringComparison.Ordinal));

            if (isManualDebugPage)
            {
                ManualDebugContent.Content = _manualMotionDebugPage;
                ManualDebugContent.Visibility = Visibility.Visible;
                PagePlaceholder.Visibility = Visibility.Collapsed;
                PageDescription.SetResourceReference(TextBlock.TextProperty, "手动调试说明");
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

            // 左侧导航由代码生成，切换语言后重新生成当前导航项；保留当前选中页面。
            NavigateToSection(_currentSectionKey);
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

        /// <summary>
        /// 显示设备操作提示；具体设备控制逻辑由后续模块接入。
        /// </summary>
        private void MachineCommandButton_Click(object sender, RoutedEventArgs e)
        {
            Button commandButton = sender as Button;
            if (commandButton == null)
            {
                return;
            }

            string noticeResourceKey = commandButton.Tag as string;
            HeaderMessage.SetResourceReference(TextBlock.TextProperty, noticeResourceKey);
            CurrentMachineState.SetResourceReference(TextBlock.TextProperty, "控制逻辑未接入");
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
        /// 关闭主窗口时停止时钟定时器。
        /// </summary>
        private void MainWindow_Closed(object sender, EventArgs e)
        {
            _clockTimer.Stop();
        }
    }
}
