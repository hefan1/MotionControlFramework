using System;
using System.Diagnostics;
using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MotionControl.TaskEngine.Wpf.Editing;
using MotionControl.TaskEngine.Machine;

namespace MotionControl.OperatorPages.Checks
{
    /// <summary>检查任务配置页布局、真实来源边界及编辑输入保护。</summary>
    internal static class V18TaskConfigurationLayoutChecks
    {
        /// <summary>宽屏并列、窄屏上下，状态可追溯且修改仍遵循原表单门禁。</summary>
        internal static void LayoutAndState()
        {
            using (var files = new PreviewFiles())
            {
                var page = new TaskConfigurationView(files.Configuration, "TaskEditorPreview", () => "测试更新拦截",
                    () => "等待复位", () => new[] { "真实工位甲" }, () => null);
                var list = page.FindName("StationListCard") as FrameworkElement;
                var detail = page.FindName("StationSettingsCard") as FrameworkElement;
                Require(list != null && detail != null, "工位安排缺少列表或选中工位设置区域。");
                Layout(page, 1200, 780);
                Rect a = Bounds(list, page), b = Bounds(detail, page);
                Require(a.Width > 450 && b.Width > 250 && a.Right <= b.Left && a.Height > 200 && b.Height > 200,
                    "任务配置宽屏没有同时呈现工位列表和编辑区域。");
                var draft = page.FindName("DraftSummaryText") as TextBlock;
                var file = page.FindName("FileSummaryText") as TextBlock;
                var session = page.FindName("SessionSummaryText") as TextBlock;
                var sourceDetails = page.FindName("SourceDetailsExpander") as Expander;
                Require(draft != null && file != null && session != null && sourceDetails != null && !sourceDetails.IsExpanded,
                    "没有分开展示草稿、文件快照与当前会话来源。");
                Require(draft.Text.Contains("无待保存") && file.Text.Contains("文件快照")
                    && session.Text.Contains("未提供") && !session.Text.Contains("R1"),
                    "来源摘要把文件版本或未知会话误认为当前运行状态。");
                Require(((TextBlock)page.FindName("SourceSummaryText")).Text.Contains("不是正在执行的会话")
                    && !((Button)page.FindName("ApplyButton")).IsEnabled
                    && ((TextBlock)page.FindName("ApplyReasonText")).Text.Contains("测试更新拦截"),
                    "收纳详情丢失文件与会话区别或更新拦截原因。");
                var sourceChanged = typeof(TaskConfigurationView).GetField("_sourceChanged", BindingFlags.Instance | BindingFlags.NonPublic);
                var refreshCards = typeof(TaskConfigurationView).GetMethod("RefreshSummaryCards", BindingFlags.Instance | BindingFlags.NonPublic);
                sourceChanged.SetValue(page, true);
                refreshCards.Invoke(page, null);
                Require(draft.Text.Contains("基准待核查") && file.Text.Contains("一致性未确认") && !file.Text.Contains("不是当前文件"),
                    "草稿基准变化被错误断言为已载入文件快照过期。");
                sourceChanged.SetValue(page, false);
                refreshCards.Invoke(page, null);
                var machineSettings = page.FindName("MachineSettingsExpander") as Expander;
                Require(machineSettings != null && !machineSettings.IsExpanded, "设备公共设置没有明确入口。");
                machineSettings.IsExpanded = true;
                sourceDetails.IsExpanded = true;
                Require(!page.HasUnsavedChanges, "展开只读来源或公共设置误记为草稿修改。");
                machineSettings.IsExpanded = false;
                sourceDetails.IsExpanded = false;
                var stations = (DataGrid)page.FindName("StationsGrid");
                object selected = stations.SelectedItem;
                Layout(page, 700, 550);
                a = Bounds(list, page); b = Bounds(detail, page);
                var scroll = page.FindName("StationWorkspaceScroll") as ScrollViewer;
                Require(a.Width > 400 && b.Width > 400 && a.Bottom <= b.Top && a.Height >= 200 && b.Height >= 300
                    && scroll != null && scroll.ScrollableHeight > 0,
                    "任务配置窄屏没有按列表、表单顺序提供可滚动的阅读区域。");
                var station = (StationTaskConfiguration)typeof(TaskConfigurationView).GetField("_station", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(page);
                var identity = page.FindName("NarrowStationIdentity") as FrameworkElement;
                var identityText = page.FindName("NarrowStationIdentityText") as TextBlock;
                Require(identity != null && identity.Visibility == Visibility.Visible && identityText != null
                    && identityText.Text.Contains(station.Name) && identityText.Text.Contains(station.StationId),
                    "窄屏向下编辑时无法确认所选工位的真实身份。");
                sourceDetails.IsExpanded = true;
                ((Expander)page.FindName("RuntimeDetailsExpander")).IsExpanded = true;
                var error = (Expander)page.FindName("ErrorDiagnosticsExpander");
                error.Visibility = Visibility.Visible; error.IsExpanded = true;
                ((TextBox)page.FindName("ErrorDiagnosticsText")).Text = new string('错', 1000);
                Layout(page, 700, 550);
                var statusScroll = page.FindName("ConfigurationStatusScroll") as ScrollViewer;
                var toolbar = (FrameworkElement)page.FindName("ConfigToolbar");
                Require(statusScroll != null && statusScroll.ScrollableHeight > 0 && Bounds(toolbar, page).Bottom <= 550
                    && scroll.ActualHeight > 80, "展开长来源或诊断后工具栏和表单不可达。");
                var invalidInterval = (TextBox)typeof(TaskConfigurationView).GetField("_interval", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(page);
                string originalInterval = invalidInterval.Text;
                invalidInterval.Text = "无效周期";
                stations.SelectedIndex = 1;
                error.IsExpanded = true;
                Layout(page, 700, 550);
                statusScroll.ScrollToEnd();
                Layout(page, 700, 550);
                ((Button)page.FindName("SaveAndCheckButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Layout(page, 700, 550);
                Rect errorBounds = Bounds((TextBlock)page.FindName("StatusText"), statusScroll);
                Require(errorBounds.Top >= -1 && errorBounds.Top < statusScroll.ViewportHeight
                    && ((TextBlock)page.FindName("StatusText")).Text.Contains("有效整数"),
                    "状态区滚动后产生的新错误未进入可见区域。");
                var statusText = (TextBlock)page.FindName("StatusText");
                var innerStatusScroll = (ScrollViewer)LogicalTreeHelper.GetParent(statusText);
                var showError = typeof(TaskConfigurationView).GetMethod("ShowError", BindingFlags.Instance | BindingFlags.NonPublic);
                var longError = new IOException(new string('错', 1200));
                showError.Invoke(page, new object[] { longError });
                Layout(page, 700, 550);
                innerStatusScroll.ScrollToEnd();
                Layout(page, 700, 550);
                Require(innerStatusScroll.VerticalOffset > 0, "长错误未形成内部滚动，无法验证重复错误。");
                showError.Invoke(page, new object[] { longError });
                Layout(page, 700, 550);
                Require(innerStatusScroll.VerticalOffset < 1, "重复的长错误未返回首行，可能遮住错误开头。");
                invalidInterval.Text = originalInterval;
                sourceDetails.IsExpanded = false;
                ((Expander)page.FindName("RuntimeDetailsExpander")).IsExpanded = false;
                error.IsExpanded = false; error.Visibility = Visibility.Collapsed;
                Layout(page, 1200, 780);
                Require(ReferenceEquals(selected, stations.SelectedItem), "切换宽窄布局改变了当前工位。");
                var interval = (TextBox)typeof(TaskConfigurationView).GetField("_interval", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(page);
                string original = interval.Text;
                interval.Text = "不是数字";
                stations.SelectedIndex = 1;
                Require(ReferenceEquals(selected, stations.SelectedItem) && interval.Text == "不是数字",
                    "无效周期输入导致切换工位并丢失内容。");
                interval.Text = original;
                stations.SelectedIndex = 1;
                Require(!ReferenceEquals(selected, stations.SelectedItem), "修正输入后仍无法切换工位。");
                Require(page.HasUnsavedChanges, "工位表单编辑没有标记为未保存。");
            }
            using (var files = new PreviewFiles())
            {
                var page = new TaskConfigurationView(files.Configuration, "TaskEditorPreview", () => null,
                    () => "等待复位", () => new[] { new string('工', 1000) }, () => new string('拦', 300));
                ((Expander)page.FindName("SourceDetailsExpander")).IsExpanded = true;
                ((Expander)page.FindName("RuntimeDetailsExpander")).IsExpanded = true;
                Layout(page, 700, 550);
                var scroll = (ScrollViewer)page.FindName("ConfigurationStatusScroll");
                var status = (TextBlock)page.FindName("StatusText");
                scroll.ScrollToEnd();
                Layout(page, 700, 550);
                Require(scroll.VerticalOffset > 0 && Bounds(status, scroll).Top < 0,
                    "长状态未遮住保存结果区域，无法验证结果定位。");
                var interval = (TextBox)typeof(TaskConfigurationView).GetField("_interval", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(page);
                interval.Text = interval.Text == "101" ? "100" : "101";
                Layout(page, 700, 550);
                Require(scroll.VerticalOffset > 0 && Bounds(status, scroll).Top < 0,
                    "输入表单时不应强制改变人员的状态区滚动位置。");
                ((Button)page.FindName("SaveAndCheckButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Layout(page, 700, 550);
                Rect result = Bounds(status, scroll);
                Require(status.Text.Contains("草稿已保存") && result.Top >= -1 && result.Top < scroll.ViewportHeight,
                    "状态区滚动后保存结果未进入可见区域。");
            }
            using (var files = new PreviewFiles())
            {
                // 隔离目录中模拟其他入口改动文件：已保存草稿的基准应失效，刚读取的快照不能被误判为旧文件。
                var draft = files.Store.Load("TaskEditorPreview");
                files.Store.SaveDraft(draft);
                string path = Path.Combine(files.Configuration.GetMachineDirectory("TaskEditorPreview"), "Tasks", draft.Tasks[0].FileName);
                File.AppendAllText(path, Environment.NewLine);
                var page = new TaskConfigurationView(files.Configuration, "TaskEditorPreview", () => null);
                Require(((TextBlock)page.FindName("DraftSummaryText")).Text.Contains("基准待核查")
                    && ((TextBlock)page.FindName("FileSummaryText")).Text.Contains("一致性未确认")
                    && !((TextBlock)page.FindName("FileSummaryText")).Text.Contains("不是当前文件")
                    && !((Button)page.FindName("ApplyButton")).IsEnabled
                    && ((TextBlock)page.FindName("ApplyReasonText")).Text.Contains("无法确认"),
                    "来源不一致或无法读取时，更新门禁应保留无法确认的可能性。");
            }
            using (var files = new PreviewFiles())
            {
                string path = Path.Combine(files.Configuration.GetMachineDirectory("TaskEditorPreview"), "Parameters", "StationPreparation.json");
                File.WriteAllText(path, "{\"schemaVersion\":1,\"stations\":[]}");
                var page = new TaskConfigurationView(files.Configuration, "TaskEditorPreview", () => null);
                var refresh = typeof(TaskConfigurationView).GetMethod("RefreshPermission", BindingFlags.Instance | BindingFlags.NonPublic);
                using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    refresh.Invoke(page, new object[] { null, EventArgs.Empty });
                    Require(((TextBlock)page.FindName("ApplyReasonText")).Text.Contains("无法读取")
                        && !((Button)page.FindName("ApplyButton")).IsEnabled
                        && !((Button)page.FindName("RebaseDraftButton")).IsEnabled
                        && !((Button)page.FindName("ReloadRuntimeButton")).IsEnabled,
                        "准备配置无法读取时应提示读取故障并阻止来源同步或重载。");
                    typeof(TaskConfigurationView).GetMethod("ReloadRuntimeFiles", BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(page, null);
                    Require(((TextBlock)page.FindName("StatusText")).Text.Contains("无法读取"),
                        "备份维护入口未阻止准备配置读取失败时重载。");
                    string diagnostic = ((TextBox)page.FindName("ErrorDiagnosticsText")).Text;
                    Require(diagnostic.Contains("System.IO.IOException") && diagnostic.Contains("StationPreparation.json"),
                        "重复阻断来源处理时丢失了准备文件读取失败的完整诊断。");
                    page.SetLanguage("en-US");
                    Require(((TextBlock)page.FindName("ApplyReasonText")).Text.Contains("cannot be read")
                        && !((Button)page.FindName("RebaseDraftButton")).IsEnabled
                        && !((Button)page.FindName("ReloadRuntimeButton")).IsEnabled
                        && ((TextBox)page.FindName("ErrorDiagnosticsText")).Text.Contains("System.IO.IOException"),
                        "英文读取故障说明或来源处理门禁不一致。");
                    page.SetLanguage("zh-CN");
                }
                ObserveLoadedSources(page, () => ((TextBlock)page.FindName("StatusText")).Text.Contains("读取已恢复"));
                Require(!((TextBlock)page.FindName("DraftSummaryText")).Text.Contains("基准待核查")
                    && ((TextBlock)page.FindName("StatusText")).Text.Contains("读取已恢复")
                    && !((TextBlock)page.FindName("StatusText")).Text.Contains("已变化"),
                    "准备文件恢复读取且内容未变后，应报告读取恢复而非文件变化。");
            }
            using (var files = new PreviewFiles())
            {
                string root = files.Configuration.GetMachineDirectory("TaskEditorPreview");
                string preparation = Path.Combine(root, "Parameters", "StationPreparation.json");
                string axes = Path.Combine(root, "HardwareConfig", "AxisParameters.json");
                File.WriteAllText(preparation, "{\"schemaVersion\":1,\"stations\":[]}");
                var page = new TaskConfigurationView(files.Configuration, "TaskEditorPreview", () => null);
                File.AppendAllText(preparation, Environment.NewLine);
                using (var locked = new FileStream(axes, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    ObserveLoadedSources(page, () => ((TextBox)page.FindName("ErrorDiagnosticsText")).Text.Contains("AxisParameters.json"));
                    Require(((TextBlock)page.FindName("ApplyReasonText")).Text.Contains("来源文件无法读取")
                        && ((TextBox)page.FindName("ErrorDiagnosticsText")).Text.Contains("AxisParameters.json")
                        && !((Button)page.FindName("RebaseDraftButton")).IsEnabled,
                        "读取完整来源失败时不应误指准备文件，诊断应保留实际文件路径。");
                    ((Button)page.FindName("SaveAndCheckButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    typeof(TaskConfigurationView).GetMethod("RefreshPermission", BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(page, new object[] { null, EventArgs.Empty });
                    Require(!((Button)page.FindName("ApplyButton")).IsEnabled
                        && !((Button)page.FindName("RebaseDraftButton")).IsEnabled
                        && !((Button)page.FindName("ReloadRuntimeButton")).IsEnabled
                        && ((TextBlock)page.FindName("ApplyReasonText")).Text.Contains("来源文件无法读取"),
                        "检查再次读取来源失败后，不能清除故障并重新开放来源处理。");
                }
            }
            using (var files = new PreviewFiles())
            {
                string axes = Path.Combine(files.Configuration.GetMachineDirectory("TaskEditorPreview"), "HardwareConfig", "AxisParameters.json");
                var page = new TaskConfigurationView(files.Configuration, "TaskEditorPreview", () => null);
                using (var locked = new FileStream(axes, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    var recheck = typeof(TaskConfigurationView).GetMethod("RefreshSourceAfterActionFailure", BindingFlags.Instance | BindingFlags.NonPublic);
                    recheck.Invoke(page, null);
                    typeof(TaskConfigurationView).GetMethod("RefreshPermission", BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(page, new object[] { null, EventArgs.Empty });
                    Require(!((Button)page.FindName("ApplyButton")).IsEnabled
                        && ((TextBlock)page.FindName("ApplyReasonText")).Text.Contains("来源文件无法读取"),
                        "检查或同步中途无法读取来源后，必须重新计算按钮门禁。");
                    bool allowed = (bool)typeof(TaskConfigurationView).GetMethod("CanResolveTaskSources", BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(page, null);
                    Require(!allowed && !((Button)page.FindName("ReloadRuntimeButton")).IsEnabled
                        && ((TextBox)page.FindName("ErrorDiagnosticsText")).Text.Contains("AxisParameters.json"),
                        "来源处理前必须检查完整来源，不能因准备文件未变化而漏掉其他来源读取故障。");
                }
            }
        }

        /// <summary>搜索只改变任务库列表，不应创建草稿修改或清除真实选择。</summary>
        internal static void LibrarySearchIsReadOnly()
        {
            using (var files = new PreviewFiles())
            {
                var page = new TaskConfigurationView(files.Configuration, "TaskEditorPreview", () => null);
                var search = (TextBox)page.FindName("LibrarySearchBox");
                var tabs = (TabControl)page.FindName("MainTabs");
                var grid = (DataGrid)page.FindName("TasksGrid");
                tabs.SelectedIndex = 1;
                Require(!page.HasUnsavedChanges, "初始任务配置意外标记为未保存。");
                grid.SelectedIndex = 0;
                string selectedId = ((TaskLibraryRow)grid.SelectedItem).Definition.TaskId;
                search.Text = "不匹配的任务";
                Require(!page.HasUnsavedChanges && grid.Items.Count == 0,
                    "任务库筛选被误记为草稿修改，或筛选未生效。");
                foreach (string name in new[] { "CopyTaskButton", "EditTaskButton", "ReferencesButton" })
                {
                    var button = page.FindName(name) as Button;
                    Require(button != null && !button.IsEnabled && button.ToolTip != null, "任务未选中时仍开放无目标操作。");
                }
                search.Text = string.Empty;
                Require(!page.HasUnsavedChanges && grid.Items.Count > 0 && ((TaskLibraryRow)grid.SelectedItem).Definition.TaskId == selectedId
                    && ((Button)page.FindName("EditTaskButton")).IsEnabled,
                    "清除任务筛选改变了草稿状态。");
            }
        }

        /// <summary>完整来源已改为可见页面的后台观察；真实触发 Loaded 并有界泵送，保留原恢复与门禁断言。</summary>
        private static void ObserveLoadedSources(TaskConfigurationView page, Func<bool> completed)
        {
            var window = new Window
            {
                Content = page, Width = 1100, Height = 760,
                ShowInTaskbar = false, ShowActivated = false, Title = "来源恢复离线检查"
            };
            var frame = new DispatcherFrame();
            var elapsed = Stopwatch.StartNew();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
            EventHandler poll = (sender, args) =>
            {
                if (elapsed.Elapsed >= TimeSpan.FromSeconds(5) || (page.IsLoaded && completed()))
                    frame.Continue = false;
            };
            timer.Tick += poll;
            try
            {
                window.Show();
                timer.Start();
                Dispatcher.PushFrame(frame);
            }
            finally
            {
                timer.Stop();
                timer.Tick -= poll;
                window.Close();
                window.Content = null;
            }
        }

        private static void Layout(FrameworkElement view, double width, double height)
        { view.Measure(new Size(width, height)); view.Arrange(new Rect(0, 0, width, height)); view.UpdateLayout(); }

        private static Rect Bounds(FrameworkElement element, FrameworkElement page)
        { return new Rect(element.TransformToAncestor(page).Transform(new Point(0, 0)), element.RenderSize); }

        private static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
    }
}
