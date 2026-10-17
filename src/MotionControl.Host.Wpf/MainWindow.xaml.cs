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
            { "Settings", new[] { "控制卡设置", "轴参数", "IO映射", "通讯设置", "数据采集" } },
            { "Alarms", new[] { "当前报警", "历史报警", "报警统计", "操作日志" } },
            { "Users", new[] { "密码登录", "刷卡登录", "权限管理" } },
            { "Help", new[] { "软件信息", "使用说明" } }
        };

        private readonly DispatcherTimer _clockTimer;
        private string _currentSectionKey;
        private string _currentPageResourceKey;

        public string CurrentLanguage { get; private set; }

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

        private void PrimaryNavigationButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleButton navigationButton = sender as ToggleButton;
            if (navigationButton == null)
            {
                return;
            }

            NavigateToSection(navigationButton.Tag as string);
        }

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

        private void SidebarNavigationButton_Click(object sender, RoutedEventArgs e)
        {
            SelectSidebarPage(sender as ToggleButton);
        }

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
        }

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

        private string GetLocalizedText(string chineseResourceKey)
        {
            return (string)FindResource(chineseResourceKey);
        }

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

        private void ClockTimer_Tick(object sender, EventArgs e)
        {
            UpdateClock();
        }

        private void UpdateClock()
        {
            ClockText.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }

        private void MainWindow_Closed(object sender, EventArgs e)
        {
            _clockTimer.Stop();
        }
    }
}
