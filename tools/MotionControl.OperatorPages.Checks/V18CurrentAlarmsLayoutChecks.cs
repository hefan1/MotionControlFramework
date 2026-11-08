using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Runtime.Alarms;
using MotionControl.Framework.Runtime.Machine;
using MotionControl.Host.Wpf.Operation;

namespace MotionControl.OperatorPages.Checks
{
    /// <summary>验证报警工作区布局与断连时保留记录的边界。</summary>
    internal static class V18CurrentAlarmsLayoutChecks
    {
        /// <summary>宽窄布局保留报警选择；断连记录不冒充当前现场状态。</summary>
        internal static void Run()
        {
            DateTime now = DateTime.UtcNow;
            var definition = new MachineAlarmDefinition("UI_STOP", "测试停机条件", MachineAlarmKind.StopMachine);
            var snapshot = new OperationSnapshot { IsPreview = true, HasRuntime = true, State = MachineRunState.Alarmed };
            snapshot.Stations.Add(new StationOverviewData { Name = "测试工位", CurrentStep = "已提供工步" });
            snapshot.Alarms.Add(new MachineAlarmRecord(definition, "测试工位", "报警记录详情", true, now, now));
            var view = new CurrentAlarmsView();
            view.SetPreview(snapshot);
            var list = view.FindName("AlarmListPanel") as FrameworkElement;
            var details = view.FindName("DetailsPanel") as FrameworkElement;
            Require(list != null && details != null, "缺少报警列表或选中报警指导区域。");
            Layout(view, 1200, 800);
            Rect a = Bounds(list, view), b = Bounds(details, view);
            Require(a.Width > 400 && b.Width > 300 && a.Right <= b.Left && a.Height > 300 && b.Height > 300,
                "宽屏应同时并列显示报警列表和处理指导。");
            var actions = (FrameworkElement)view.FindName("AlarmActions");
            var detailsScroll = (ScrollViewer)view.FindName("AlarmDetailsScroll");
            Require(actions != null && detailsScroll != null && Bounds(actions, details).Bottom <= details.ActualHeight + 1
                && Bounds((Button)view.FindName("IoButton"), details).Bottom <= details.ActualHeight,
                "长处理指导不能把查看入口挤出详情区域。");
            var grid = (DataGrid)view.FindName("AlarmGrid");
            Layout(view, 1160, 800);
            a = Bounds(list, view); b = Bounds(details, view);
            Require(a.Right <= b.Left && grid.Columns.Sum(column => column.ActualWidth) <= a.Width + 1,
                "宽屏断点附近应完整显示报警状态列，不要求横向滚动才能阅读。");
            string selected = ((AlarmDisplayRow)grid.SelectedItem).Key;
            Layout(view, 700, 700);
            a = Bounds(list, view); b = Bounds(details, view);
            Require(a.Width > 500 && b.Width > 500 && a.Bottom <= b.Top && a.Height >= 100 && b.Height >= 160,
                "窄屏应上下显示报警列表和处理指导。");
            Require(detailsScroll.ScrollableHeight > 0 && ((FrameworkElement)view.FindName("AlarmScrollHint")).Visibility == Visibility.Visible,
                "窄屏处理指导超出视口时应提示可以滚动。");
            detailsScroll.ScrollToEnd();
            Layout(view, 700, 700);
            Rect after = Bounds((TextBlock)view.FindName("AfterText"), detailsScroll);
            Require(after.Top < detailsScroll.ViewportHeight && after.Bottom > 0
                && Bounds(actions, details).Bottom <= details.ActualHeight + 1,
                "窄屏滚动后处理指导或固定查看入口不可达。");
            Require(((AlarmDisplayRow)grid.SelectedItem).Key == selected, "切换窗口宽度改变了所选报警。");
            snapshot.State = MachineRunState.Disconnected;
            view.RefreshDisplay();
            var retained = (AlarmDisplayRow)grid.SelectedItem;
            Require(retained.Key == selected && retained.ConditionText.Contains("未知")
                && ((TextBlock)view.FindName("CauseText")).Text.Contains("未知")
                && !((TextBlock)view.FindName("WhereText")).Text.Contains("已提供工步")
                && !((Button)view.FindName("FlowButton")).IsEnabled
                && !((Button)view.FindName("IoButton")).IsEnabled
                && !((Button)view.FindName("AxisButton")).IsEnabled,
                "断连时留存报警被当作当前条件或仍允许旧记录定向。");
            view.SetLanguage("en-US");
            Require(((AlarmDisplayRow)grid.SelectedItem).ConditionText.IndexOf("unknown", StringComparison.OrdinalIgnoreCase) >= 0
                && ((TextBlock)view.FindName("CauseText")).Text.IndexOf("unknown", StringComparison.OrdinalIgnoreCase) >= 0,
                "英文断连状态未说明现场条件未知。");
            view.SetLanguage("zh-CN");
            snapshot.State = MachineRunState.Alarmed;
            view.RefreshDisplay();
            Require(((AlarmDisplayRow)grid.SelectedItem).Key == selected
                && ((AlarmDisplayRow)grid.SelectedItem).ConditionText.Contains("记录")
                && ((Button)view.FindName("FlowButton")).IsEnabled,
                "重连后报警应仍标为软件记录，并保留普通查看入口。");
            ((TextBox)view.FindName("SearchBox")).Text = "无匹配内容";
            Layout(view, 1200, 800);
            Require(grid.Items.Count == 0 && details.Visibility == Visibility.Collapsed
                && Bounds(list, view).Width > 1000, "空筛选不应显示旧详情或保留空白详情列。");
            ((TextBox)view.FindName("SearchBox")).Text = string.Empty;
            Require(((AlarmDisplayRow)grid.SelectedItem).Key == selected, "清除筛选后未恢复原报警选择。");

            var card = new SessionCard();
            var service = new MachineAlarmService();
            var runtime = new MachineRuntime(new MotionCardGroup(new IMotionCard[] { card }),
                new IStationFlow[] { new ReadOnlyFlow("测试工位") }, service, new GuardSafety());
            runtime.ConnectAsync(CancellationToken.None).GetAwaiter().GetResult();
            var live = new CurrentAlarmsView();
            live.SetRuntime(runtime);
            service.ReportCondition(definition, "测试工位", true, "设备报警记录");
            live.RefreshDisplay();
            var liveGrid = (DataGrid)live.FindName("AlarmGrid");
            var io = (Button)live.FindName("IoButton");
            OperatorNavigationEventArgs navigation = null;
            live.NavigationRequested += (sender, args) => navigation = args;
            io.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(navigation != null && navigation.Page == "IO监控"
                && navigation.StationName == null && navigation.IoPointName == null,
                "没有连接周期来源的报警不能自动定向工位或输入。");
            navigation = null;
            runtime.InvalidateConnectionSessionOnLoss();
            io.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(navigation == null, "两次刷新之间会话失效仍发出了旧导航。");
            live.RefreshDisplay();
            Require(((AlarmDisplayRow)liveGrid.SelectedItem).ConditionText.Contains("未知") && !io.IsEnabled,
                "连接周期失效时留存记录仍被展示为当前条件。");
            runtime.DisconnectAsync(CancellationToken.None).GetAwaiter().GetResult();
            runtime.ConnectAsync(CancellationToken.None).GetAwaiter().GetResult();
            live.RefreshDisplay();
            Require(((AlarmDisplayRow)liveGrid.SelectedItem).ConditionText.Contains("记录")
                && !((TextBlock)live.FindName("WhereText")).Text.Contains("等待测试输入"),
                "新连接周期将旧报警或旧工步冒充当前现场状态。");
            navigation = null;
            io.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(navigation != null && navigation.StationName == null && navigation.IoPointName == null,
                "重连后旧报警仍自动定向工位或输入。");
            service.ReportCondition(definition, "测试工位", false, null);
            service.ResetStopAlarms();
            runtime.InvalidateConnectionSessionOnLoss();
            live.RefreshDisplay();
            Require(((TextBlock)live.FindName("EmptyText")).Text.Contains("未知"),
                "连接周期失效且无报警记录时不能显示现场正常。");
        }

        /// <summary>供连接代次回归使用的纯内存卡，不连接设备。</summary>
        private sealed class SessionCard : IMotionCard
        {
            /// <summary>内存卡标识。</summary>
            public string ControllerId => "AlarmSessionCheck";
            /// <summary>仅在测试中切换的连接标记。</summary>
            public bool IsConnected { get; private set; }
            /// <summary>不提供实际轴。</summary>
            public IReadOnlyCollection<IAxis> Axes => new IAxis[0];
            /// <summary>建立内存连接。</summary>
            public Task ConnectAsync(CancellationToken token) { IsConnected = true; return Task.CompletedTask; }
            /// <summary>断开内存连接。</summary>
            public Task DisconnectAsync(CancellationToken token) { IsConnected = false; return Task.CompletedTask; }
        }

        /// <summary>按窗口尺寸完成 WPF 布局。</summary>
        private static void Layout(FrameworkElement view, double width, double height)
        { view.Measure(new Size(width, height)); view.Arrange(new Rect(0, 0, width, height)); view.UpdateLayout(); }
        /// <summary>读取控件相对页面的可见区域。</summary>
        private static Rect Bounds(FrameworkElement element, FrameworkElement page)
        { return new Rect(element.TransformToAncestor(page).Transform(new Point(0, 0)), element.RenderSize); }
        /// <summary>失败时说明操作员可见问题。</summary>
        private static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
    }
}
