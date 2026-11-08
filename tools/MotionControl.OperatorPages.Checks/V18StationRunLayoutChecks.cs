using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Runtime.Alarms;
using MotionControl.Framework.Runtime.Machine;
using MotionControl.Host.Wpf;
using MotionControl.TaskEngine.Machine;
using MotionControl.TaskEngine.Projection;
using MotionControl.TaskEngine.Wpf.Monitoring;
using MotionControl.TaskEngine.Wpf.Simulation;
using MotionControl.TaskEngine.Wpf.Views;

namespace MotionControl.OperatorPages.Checks
{
    /// <summary>核对两种工位运行入口的宽窄布局、选择和真实状态边界。</summary>
    internal static class V18StationRunLayoutChecks
    {
        /// <summary>任务运行宽屏三栏、窄屏选择栏，并保留同一个工位选择。</summary>
        internal static void TaskLayout()
        {
            var session = TaskMonitorExample.Create();
            try
            {
                var view = new TaskMonitorView(session);
                view.Refresh();
                var stations = (DataGrid)view.FindName("StationsGrid");
                var list = (FrameworkElement)view.FindName("TaskStationListCard");
                var steps = (FrameworkElement)view.FindName("TaskStepsCard");
                var detail = (FrameworkElement)view.FindName("TaskStepDetailsCard");
                Require(list != null && steps != null && detail != null, "任务运行缺少工位、步骤和详情分区。");
                Layout(view, 1200, 760);
                CheckWide(view, list, steps, detail, "任务运行");
                CheckTaskStepColumns(view);
                var stationText = Descendants(stations).OfType<TextBlock>().ToArray();
                Require(stationText.Any(item => item.Name == "StationActionText")
                    && stationText.Any(item => item.Name == "StationDetailText"),
                    "工位概览未保留各工位的真实动作及等待或异常摘要。");
                Require(double.IsNaN(stations.RowHeight), "工位摘要被固定行高截断。");
                Layout(view, 760, 760);
                CheckNarrow(view, list, steps, detail, "任务运行");
                var picker = (ComboBox)view.FindName("NarrowTaskStationPicker");
                Require(picker != null && picker.Visibility == Visibility.Visible && stations.Visibility == Visibility.Collapsed,
                    "任务运行窄屏没有使用工位选择栏。");
                Require(picker.ItemTemplate != null, "窄屏工位选项没有显示真实状态和等待摘要。");
                var stationTemplate = picker.ItemTemplate.LoadContent();
                var narrowTexts = Descendants(stationTemplate).OfType<TextBlock>().ToArray();
                Require(narrowTexts.Any(item => item.Name == "NarrowStationState")
                    && narrowTexts.Any(item => item.Name == "NarrowStationAction")
                    && narrowTexts.Any(item => item.Name == "NarrowStationDetail"), "窄屏工位选项丢失状态、动作或等待摘要。");
                Require(picker.Items.Count > 1, "测试会话未提供多个真实工位。");
                picker.SelectedIndex = 1;
                Require(ReferenceEquals(picker.SelectedItem, stations.SelectedItem)
                    && ((TextBlock)view.FindName("StepsTitle")).Text.Contains(((TaskStationRow)picker.SelectedItem).Name),
                    "窄屏切换工位后步骤仍属于旧工位。");
                Layout(view, 760, 350);
                var workspaceScroll = view.FindName("TaskWorkspaceScroll") as ScrollViewer;
                Require(workspaceScroll != null && workspaceScroll.ScrollableHeight > 0
                    && steps.ActualHeight >= 200 && steps.ActualHeight <= 400
                    && detail.ActualHeight >= 200 && detail.ActualHeight <= 400,
                    "短窗口中步骤或详情被压缩，无法滚动阅读。");
                Layout(view, 1200, 760);
                Require(ReferenceEquals(picker.SelectedItem, stations.SelectedItem) && stations.Visibility == Visibility.Visible,
                    "恢复宽屏时丢失当前工位选择。");
                // 测试中注入一条工程级投影诊断，不能混入当前步骤字段。
                var projectionConstructor = typeof(TaskProjectProjection).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
                var projection = (TaskProjectProjection)projectionConstructor.Invoke(new object[] { "已加载会话", "test", null, null,
                    session.Projection.References.ToList(), new List<string> { "其他任务的工程诊断" } });
                typeof(TaskMonitorSession).GetField("<Projection>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(session, projection);
                stations.SelectedIndex = 2;
                var tree = (TreeView)view.FindName("StepsTree");
                var branch = TaskStepTree.Flatten(((IEnumerable<TaskStepRow>)tree.ItemsSource).ToList()).Single(item => item.Node.Id == "judge");
                tree.RaiseEvent(new RoutedPropertyChangedEventArgs<object>(null, branch, TreeView.SelectedItemChangedEvent));
                var fields = ((ItemsControl)view.FindName("FlowDetails")).ItemsSource.Cast<TaskDetailField>().ToArray();
                Require(fields.Any(item => item.Name == "判断条件（定义）")
                    && ((TextBlock)view.FindName("RecordNote")).Text.Contains("尚无执行采样"), "定义条件与真实采样没有区分。");
                var globalDiagnostics = (ItemsControl)view.FindName("GlobalProjectionDiagnostics");
                var diagnosticsExpander = (Expander)view.FindName("GlobalProjectionDiagnosticsExpander");
                Require(!fields.Any(item => item.Value.Contains("其他任务的工程诊断"))
                    && globalDiagnostics.ItemsSource.Cast<TaskDetailField>().Any(item => item.Value.Contains("其他任务的工程诊断"))
                    && diagnosticsExpander.Visibility == Visibility.Visible && !diagnosticsExpander.IsExpanded,
                    "工程级诊断未与当前步骤区分并收纳。 ");
                var diagnosticItems = globalDiagnostics.ItemsSource;
                diagnosticsExpander.IsExpanded = true;
                view.Refresh();
                Require(ReferenceEquals(diagnosticItems, globalDiagnostics.ItemsSource) && diagnosticsExpander.IsExpanded,
                    "定时刷新重建了工程诊断列表或关闭了正在查看的详情。");
            }
            finally { session.Machine.CloseAsync().GetAwaiter().GetResult(); }
        }

        /// <summary>C# 工位使用相同阅读顺序，只显示真实提供的当前步骤和详情。</summary>
        internal static void CSharpLayout()
        {
            var completed = new CompletedFlow();
            var flows = new IStationFlow[] { new ReadOnlyFlow("工位甲"), new ReadOnlyFlow("工位乙"), new ReadOnlyInputFlow(), completed };
            var runtime = new MachineRuntime(new MotionCardGroup(new[] { new GuardCard { IsConnected = true } }),
                flows, new MachineAlarmService(), new GuardSafety());
            runtime.ConnectAsync(CancellationToken.None).GetAwaiter().GetResult();
            var view = new FlowStatusView(runtime);
            var list = (FrameworkElement)view.FindName("CSharpStationListCard");
            var steps = (FrameworkElement)view.FindName("CSharpStepsCard");
            var detail = (FrameworkElement)view.FindName("CSharpStationDetailsCard");
            Require(list != null && steps != null && detail != null, "C# 工位运行缺少工位、步骤和详情分区。");
            Layout(view, 1200, 760);
            CheckWide(view, list, steps, detail, "C# 工位运行");
            Require(Descendants(view).OfType<Button>().Count(button => Equals(button.Content, "查看任务运行")) == 1,
                "C# 工位运行出现返回自身的任务运行入口。");
            Layout(view, 760, 760);
            CheckNarrow(view, list, steps, detail, "C# 工位运行");
            var picker = (ComboBox)view.FindName("NarrowCSharpStationPicker");
            var stationList = (ListBox)view.FindName("StationList");
            Require(picker != null && picker.Visibility == Visibility.Visible && stationList.Visibility == Visibility.Collapsed,
                "C# 窄屏没有使用工位选择栏。");
            picker.SelectedIndex = 1;
            Require(ReferenceEquals(((ListBoxItem)stationList.SelectedItem).Tag, flows[1])
                && ((TextBlock)view.FindName("StationDetailText")).Text.Contains("工位乙")
                && ((TextBlock)view.FindName("CSharpCurrentStepText")).Text.Contains("20 等待测试输入"),
                "C# 窄屏切换后没有显示所选工位的真实当前步骤。");
            picker.SelectedIndex = 2;
            Require(((TextBlock)view.FindName("StationDetailText")).Text.Contains("工位提供的等待输入：夹紧到位")
                && !((TextBlock)view.FindName("StationDetailText")).Text.Contains("当前等待输入："),
                "尚未启动时把工位提供的输入名称表述为当前正在等待。");
            picker.SelectedIndex = 3;
            Require(((TextBlock)view.FindName("StationStepHeadingText")).Text == "当前步骤"
                && ((TextBlock)view.FindName("StationDetailText")).Text.Contains("已完成"),
                "有独立状态的工位仍使用整机状态描述步骤。");
            var runtimeState = typeof(MachineRuntime).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic);
            var before = runtime.State;
            try
            {
                runtimeState.SetValue(runtime, MachineRunState.Running);
                view.RefreshLocalizedText();
                Require(((TextBlock)view.FindName("StationStepHeadingText")).Text == "当前步骤",
                    "已完成工位被整机运行状态标为正在执行。");
                picker.SelectedIndex = 1;
                Require(((TextBlock)view.FindName("StationStepHeadingText")).Text == "当前步骤",
                    "独立状态未提供时将整机运行推断为工位正在执行。");
                picker.SelectedIndex = 3;
                completed.State = StationOperationState.Paused;
                runtimeState.SetValue(runtime, MachineRunState.Paused);
                view.RefreshLocalizedText();
                Require(((TextBlock)view.FindName("StationStepHeadingText")).Text == "暂停保留步骤",
                    "工位明确暂停时丢失步骤保留提示。");
                completed.State = StationOperationState.Stopped;
                runtimeState.SetValue(runtime, MachineRunState.Stopped);
                view.RefreshLocalizedText();
                Require(((TextBlock)view.FindName("StationStepHeadingText")).Text == "停止保留步骤",
                    "工位明确停止时丢失步骤保留提示。");
                picker.SelectedIndex = 1;
                Require(((TextBlock)view.FindName("StationStepHeadingText")).Text == "停止保留步骤",
                    "停止后工位未提供独立状态时无法刷新保留步骤。");
                picker.SelectedIndex = 3;
                completed.FailRead = true;
                view.RefreshLocalizedText();
                Require(((TextBlock)view.FindName("StationStepHeadingText")).Text == "当前步骤"
                    && ((TextBlock)view.FindName("StationDetailText")).Text.Contains("读取失败")
                    && !((Button)view.FindName("StationInputButton")).IsEnabled,
                    "工位状态读取失败后仍使用旧数据或允许输入定位。");
            }
            finally { runtimeState.SetValue(runtime, before); }
            view.MarkControllerConnectionLost();
            Require(((TextBlock)view.FindName("CSharpCurrentStepText")).Text.Contains("未知")
                && !((Button)view.FindName("StationInputButton")).IsEnabled,
                "C# 断连后仍把旧步骤或输入导航视为当前状态。");
        }

        /// <summary>内存中的已完成工位，只用于验证页面不把整机状态冒充独立工位状态。</summary>
        private sealed class CompletedFlow : IStationFlow, IStationOperationStatusProvider
        {
            public string StationName => "已完成工位";
            public string CurrentStepName => "保留步骤";
            public StationOperationState State { get; set; } = StationOperationState.Completed;
            public bool FailRead { get; set; }
            public StationOperationStatus ReadOperationStatus()
            {
                if (FailRead) throw new InvalidOperationException("测试工位读取失败");
                return new StationOperationStatus("completed", StationName, "step", CurrentStepName, State);
            }
            public Task ExecuteCurrentStepAsync(CancellationToken token) { throw new InvalidOperationException("只读页面不能运行工位。"); }
            public Task ResetAsync(CancellationToken token) { throw new InvalidOperationException("只读页面不能复位工位。"); }
        }

        /// <summary>步骤名称受本列裁切，状态与动作之间留出间距，层级行不能向右溢出。</summary>
        private static void CheckTaskStepColumns(FrameworkElement view)
        {
            var tree = (TreeView)view.FindName("StepsTree");
            var names = Descendants(tree).OfType<TextBlock>().Where(item => item.Name == "StepNameText").ToList();
            Require(names.Count > 0, "未找到实际生成的步骤名称列。");
            foreach (TextBlock name in names)
            {
                var grid = name.Parent as Grid;
                var state = grid?.Children.OfType<TextBlock>().FirstOrDefault(item => item.Name == "StepStateText");
                Require(state != null && name.ClipToBounds && name.TextTrimming == TextTrimming.CharacterEllipsis,
                    "步骤动作没有限定宽度，或缺少独立状态列。");
                Rect nameBounds = Bounds(name, tree), stateBounds = Bounds(state, tree);
                Require(nameBounds.Right + 6 <= stateBounds.Left && stateBounds.Right <= tree.ActualWidth + 1
                    && stateBounds.Left >= tree.ActualWidth - 112,
                    "步骤动作和状态列发生挤占、错位或超出步骤区域：tree=" + tree.ActualWidth
                    + " name=" + nameBounds + " state=" + stateBounds + " grid=" + grid.ActualWidth
                    + " parents=" + DescribeAncestors(grid, tree));
            }
        }

        private static string DescribeAncestors(DependencyObject element, DependencyObject stop)
        {
            var result = new List<string>();
            for (; element != null && element != stop; element = VisualTreeHelper.GetParent(element))
                if (element is FrameworkElement item)
                    result.Add(item.GetType().Name + ":" + item.ActualWidth + ":" + item.HorizontalAlignment);
            return string.Join("/", result);
        }

        /// <summary>枚举 WPF 已生成的实际步骤元素。</summary>
        private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
        {
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, index);
                yield return child;
                foreach (DependencyObject nested in Descendants(child)) yield return nested;
            }
        }

        /// <summary>隐藏内部来源路径时，不能同时隐藏任务故障和恢复提示。</summary>
        internal static void TaskWarnings()
        {
            var session = TaskMonitorExample.Create();
            var state = typeof(TaskMachineController).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic);
            var original = session.Machine.GetSnapshot().State;
            try
            {
                var view = new TaskMonitorView(session);
                view.HideSourcePath();
                state.SetValue(session.Machine, TaskMachineState.Faulted);
                view.Refresh();
                var source = (TextBlock)view.FindName("SourceText");
                Require(source.Visibility == Visibility.Visible && source.Text.Contains("故障已锁存")
                    && !source.Text.Contains(session.Source), "隐藏来源路径时隐藏了真实故障提示或泄露来源路径。");
                var warningScroll = view.FindName("WarningScroll") as ScrollViewer;
                Require(warningScroll != null && warningScroll.VerticalScrollBarVisibility == ScrollBarVisibility.Auto
                    && source.TextTrimming == TextTrimming.None, "长故障及资源核查提示不能滚动阅读。");
                state.SetValue(session.Machine, original);
                view.Refresh();
                Require(source.Visibility == Visibility.Visible && source.Text.Contains("执行来源：任务列表")
                    && !source.Text.Contains("故障已锁存") && !source.Text.Contains(session.Source),
                    "故障解除后来源摘要缺失、旧故障未清除或暴露了内部路径。");
            }
            finally
            {
                state.SetValue(session.Machine, original);
                session.Machine.CloseAsync().GetAwaiter().GetResult();
            }
        }

        /// <summary>检查宽屏工位、步骤、详情由左到右排列。</summary>
        private static void CheckWide(FrameworkElement view, FrameworkElement list, FrameworkElement steps, FrameworkElement detail, string page)
        {
            Rect a = Bounds(list, view), b = Bounds(steps, view), c = Bounds(detail, view);
            Require(a.Width > 150 && b.Width > 300 && c.Width > 200 && a.Right <= b.Left && b.Right <= c.Left,
                page + "宽屏没有同时呈现三栏。");
        }

        /// <summary>检查窄屏工位选择、步骤和详情按阅读顺序向下排列。</summary>
        private static void CheckNarrow(FrameworkElement view, FrameworkElement list, FrameworkElement steps, FrameworkElement detail, string page)
        {
            Rect a = Bounds(list, view), b = Bounds(steps, view), c = Bounds(detail, view);
            Require(a.Width > 300 && b.Width > 300 && c.Width > 300 && a.Bottom <= b.Top && b.Bottom <= c.Top,
                page + "窄屏没有按选择、步骤、详情排列。");
        }

        /// <summary>执行真实 WPF 测量与排列，不调用设备动作。</summary>
        private static void Layout(FrameworkElement view, double width, double height)
        { view.Measure(new Size(width, height)); view.Arrange(new Rect(0, 0, width, height)); view.UpdateLayout(); }

        /// <summary>取得控件相对页面的实际位置和尺寸。</summary>
        private static Rect Bounds(FrameworkElement element, FrameworkElement page)
        { return new Rect(element.TransformToAncestor(page).Transform(new Point(0, 0)), element.RenderSize); }

        /// <summary>输出可定位的检查失败说明。</summary>
        private static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
    }
}
