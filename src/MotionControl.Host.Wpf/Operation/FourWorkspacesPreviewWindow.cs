using System;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Framework.Runtime.Machine;
using MotionControl.TaskEngine.Wpf.Editing;
using MotionControl.TaskEngine.Wpf.Simulation;

namespace MotionControl.Host.Wpf.Operation
{
    /// <summary>四个 PDF 工作区的独立预览，不创建 SDK、现场 Runtime 或生产恢复服务。</summary>
    public sealed class FourWorkspacesPreviewWindow : Window
    {
        /// <summary>保持页面实例，切换时不丢失编辑内容和报警选择。</summary>
        private readonly ContentControl _content = new ContentControl();
        /// <summary>总览示例页。</summary>
        private readonly EquipmentOverviewView _overview;
        /// <summary>报警示例页。</summary>
        private readonly CurrentAlarmsView _alarms;
        /// <summary>使用独立配置目录的任务配置与编辑器。</summary>
        private readonly TaskConfigurationView _configuration;
        /// <summary>预览导航消息，不代表生产反馈。</summary>
        private readonly TextBlock _message = new TextBlock { TextWrapping = TextWrapping.Wrap };
        /// <summary>创建四页预览并持续标记离线示例。</summary>
        public FourWorkspacesPreviewWindow()
        {
            Title = "PDF 四个工作区 — 离线预览（不连接控制卡）";
            Width = 1440; Height = 930; MinWidth = 1040; MinHeight = 720; WindowStartupLocation = WindowStartupLocation.CenterScreen;
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MotionControlFramework", "FourWorkspacesPreview");
            var store = TaskEditingPreviewWindow.CreatePreviewStore(root);
            _configuration = new TaskConfigurationView(store, "TaskEditorPreview", () => null);
            _overview = new EquipmentOverviewView(); _alarms = new CurrentAlarmsView();
            var snapshot = CreateExampleSnapshot(); _overview.SetPreview(snapshot); _alarms.SetPreview(snapshot);
            _overview.NavigationRequested += PreviewNavigation; _alarms.NavigationRequested += PreviewNavigation;
            _configuration.ReferenceRequested += PreviewReference;
            var panel = new DockPanel(); var toolbar = new WrapPanel { Margin = new Thickness(10) }; DockPanel.SetDock(toolbar, Dock.Top);
            AddPageButton(toolbar, "设备总览 / Overview", Overview_Click); AddPageButton(toolbar, "任务配置 / Configuration", Configuration_Click);
            AddPageButton(toolbar, "任务编辑 / Editor", Editor_Click); AddPageButton(toolbar, "当前报警 / Alarms", Alarms_Click);
            var language = new ComboBox { ItemsSource = new[] { "zh-CN", "en-US" }, SelectedIndex = 0, Width = 100, Margin = new Thickness(10, 0, 0, 0) };
            language.SelectionChanged += LanguageChanged; toolbar.Children.Add(language);
            var footer = new StackPanel { Margin = new Thickness(12) }; DockPanel.SetDock(footer, Dock.Bottom);
            footer.Children.Add(new TextBlock { Text = "离线页面示例 / Offline UI example · " + root, TextWrapping = TextWrapping.Wrap }); footer.Children.Add(_message);
            panel.Children.Add(toolbar); panel.Children.Add(footer); panel.Children.Add(_content); Content = panel; _content.Content = _overview;
            Closing += PreviewClosing;
        }
        /// <summary>示例只构造不可变报警记录和页面数据，不调用报警服务或设备指令。</summary>
        public static OperationSnapshot CreateExampleSnapshot()
        {
            var result = new OperationSnapshot { IsPreview = true, HasRuntime = true, State = MachineRunState.Alarmed, SampledAt = DateTime.Now, CompletedCount = 128, CycleSeconds = 12.4 };
            result.Stations.Add(new StationOverviewData { Name = "上料工位", CurrentStep = "20 等待产品到位", PreviewReason = "示例：产品到位 DI 未满足" });
            result.Stations.Add(new StationOverviewData { Name = "搬运工位", CurrentStep = "40 XY 一起移动到放料位", PreviewReason = "示例：停机报警后保留当前动作" });
            result.Stations.Add(new StationOverviewData { Name = "检测工位", CurrentStep = "30 等待拍照区域", PreviewReason = "示例：等待搬运工位让出区域；真实资源证据来自任务会话" });
            DateTime now = DateTime.UtcNow;
            result.Alarms.Add(new MachineAlarmRecord(new MachineAlarmDefinition("PREVIEW_LIMIT", "正限位信号有效（离线示例）", MachineAlarmKind.StopMachine), "搬运工位", "示例：检查机构位置和限位信号；需要退离时进入原轴手动页面。", true, now.AddMinutes(-2), now));
            result.Alarms.Add(new MachineAlarmRecord(new MachineAlarmDefinition("PREVIEW_MATERIAL", "料仓余量不足（离线示例）", MachineAlarmKind.NonStop), "上料工位", "示例：检查料仓并补料，真实条件由设备流程报告。", true, now.AddMinutes(-3), now));
            return result;
        }
        /// <summary>预览导航按钮只切换页面。</summary>
        private static void AddPageButton(Panel panel, string text, RoutedEventHandler action)
        { var button = new Button { Content = text, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(3) }; button.Click += action; panel.Children.Add(button); }
        /// <summary>进入总览。</summary>
        private void Overview_Click(object sender, RoutedEventArgs e) { _content.Content = _overview; }
        /// <summary>进入配置；如仍在编辑，使用编辑器自己的返回入口确认保存。</summary>
        private void Configuration_Click(object sender, RoutedEventArgs e) { if (_configuration.ReturnToConfiguration()) _content.Content = _configuration; }
        /// <summary>从任务配置的真实入口进入示例任务，不另造一个编辑模型。</summary>
        private void Editor_Click(object sender, RoutedEventArgs e) { _content.Content = _configuration; _configuration.OpenTaskById("Transfer"); }
        /// <summary>进入当前报警示例。</summary>
        private void Alarms_Click(object sender, RoutedEventArgs e) { _content.Content = _alarms; }
        /// <summary>示例不打开任何现场手动页，明确说明预览边界。</summary>
        private void PreviewNavigation(object sender, OperatorNavigationEventArgs e)
        { if (e.Page == "当前报警") _content.Content = _alarms; else _message.Text = "离线预览：" + e.Page + " / " + e.StationName + "。正式 Host 将打开已有页面，此处不连接设备。"; }
        /// <summary>任务关联仅在预览底栏显示，不发送任何命令。</summary>
        private void PreviewReference(object sender, MotionControl.TaskEngine.Wpf.Views.TaskReferenceEventArgs e)
        { _message.Text = "离线预览关联：" + e.Page + " / " + e.Name; }
        /// <summary>同时切换四个工作区，保留工程名称和现有数据。</summary>
        private void LanguageChanged(object sender, SelectionChangedEventArgs e)
        { string language = ((ComboBox)sender).SelectedItem as string; _overview.SetLanguage(language); _alarms.SetLanguage(language); _configuration.SetLanguage(language); }
        /// <summary>关闭仍尊重当前草稿的保存、放弃、取消选择。</summary>
        private void PreviewClosing(object sender, CancelEventArgs e) { e.Cancel = !_configuration.ConfirmClose(); }
    }
}
