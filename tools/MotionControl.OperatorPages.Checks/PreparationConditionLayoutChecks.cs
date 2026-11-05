using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.Preparation;
using MotionControl.Framework.Runtime.Alarms;
using MotionControl.Framework.Runtime.Machine;
using MotionControl.Host.Wpf.Operation;

namespace MotionControl.OperatorPages.Checks
{
    /// <summary>离屏测量真实 WPF 列表及详情的滚动范围，不创建窗口或设备动作。</summary>
    internal static class PreparationConditionLayoutChecks
    {
        internal static void Run()
        {
            foreach (int stationCount in new[] { 0, 40 })
            {
                var card = new GuardCard { IsConnected = true };
                var runtime = new MachineRuntime(new MotionCardGroup(new[] { card }), Array.Empty<IStationFlow>(),
                    new MachineAlarmService(), new GuardSafety());
                runtime.ConfigurePreparations(Enumerable.Range(1, stationCount).Select(i => new Station("Station" + i)));
                runtime.SetRecoveryBlockReason(string.Concat(Enumerable.Repeat("核对恢复记录 / Inspect retained record. ", 75)) + "END");
                runtime.ConnectAsync(CancellationToken.None).GetAwaiter().GetResult();
                foreach (var language in new[] { "zh-CN", "en-US" })
                    foreach (var width in new[] { 824, 1200 })
                        Check(runtime, language, width, width == 824 ? 600 : 800, stationCount);
                Require(card.Calls == 0, "滚动或选择触发控制卡调用。");
            }
        }

        private static void Check(MachineRuntime runtime, string language, int width, int height, int stationCount)
        {
            string destination = null;
            var page = new EquipmentPreparationView(() => throw new InvalidOperationException("浏览禁止装载配置。"),
                name => destination = name);
            page.SetLanguage(language);
            page.SetRuntime(runtime);
            ((Button)page.FindName("StartConditionsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var list = (ListBox)page.FindName("StartConditionsList");
            void Layout()
            {
                page.Measure(new Size(width, height)); page.Arrange(new Rect(0, 0, width, height)); page.UpdateLayout();
            }
            Layout();
            var scroll = Descendant<ScrollViewer>(list);
            var detail = (ScrollViewer)page.FindName("ConditionDetailScroll");
            var button = (Button)page.FindName("ConditionTargetButton");
            object Row(string key) => list.Items.Cast<object>().Single(item => Equals(item.GetType().GetProperty("Key").GetValue(item), key));
            list.SelectedItem = Row("runtime.recovery");
            Layout();
            Require(scroll != null && scroll.ScrollableWidth == 0 && detail.ScrollableHeight > 0,
                "列表横向溢出或详情无法显示完整的长原因。");
            detail.ScrollToEnd(); Layout();
            Require(detail.VerticalOffset >= detail.ScrollableHeight - 1
                && ((TextBlock)page.FindName("ConditionDetailText")).Text.Contains("END"), "恢复原因末尾不可达。");
            list.SelectedItem = Row("runtime.stop-alarms");
            Layout(); scroll.ScrollToBottom(); Layout();
            var last = (FrameworkElement)list.ItemContainerGenerator.ContainerFromIndex(list.Items.Count - 1);
            Require(last != null && last.TranslatePoint(new Point(0, last.ActualHeight), list).Y <= list.ActualHeight + 1
                && (stationCount == 0 || scroll.ScrollableHeight > 0), "启动条件末行无法滚动查看。");
            Point end = button.TranslatePoint(new Point(button.ActualWidth, button.ActualHeight), page);
            Require(button.IsEnabled && button.Visibility == Visibility.Visible && button.ActualWidth > 0
                && end.X <= width + 1 && end.Y <= height + 1, "关联报警入口被裁切。");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(destination == "当前报警", "可见按钮未沿原入口导航。");
        }

        private static T Descendant<T>(DependencyObject root) where T : DependencyObject
        {
            if (root is T found) return found;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var result = Descendant<T>(VisualTreeHelper.GetChild(root, i));
                if (result != null) return result;
            }
            return null;
        }

        private static void Require(bool condition, string error)
        { if (!condition) throw new InvalidOperationException(error); }

        private sealed class Station : IStationPreparation
        {
            internal Station(string id) { StationId = id; }
            public string StationId { get; }
            public string StationName => StationId;
            public IReadOnlyCollection<string> Dependencies => Array.Empty<string>();
            public Task ValidateStartAsync(CancellationToken token) => throw new InvalidOperationException("禁止动作检查。");
            public Task ExecuteAsync(IProgress<StationPreparationProgress> progress, CancellationToken token) => throw new InvalidOperationException("禁止回零。");
            public Task ValidateReadyAsync(CancellationToken token) => throw new InvalidOperationException("禁止验收读取。");
        }
    }
}
