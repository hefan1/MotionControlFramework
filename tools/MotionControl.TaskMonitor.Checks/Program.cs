using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Execution;
using MotionControl.TaskEngine.Machine;
using MotionControl.TaskEngine.Simulation;
using MotionControl.TaskEngine.Wpf.Monitoring;
using MotionControl.TaskEngine.Wpf.Simulation;
using MotionControl.TaskEngine.Wpf.Views;

namespace MotionControl.TaskMonitor.Checks
{
    /// <summary>验证监控关联与 WPF 构造；不把这些内部检查当作屏幕验收。</summary>
    internal static class Program
    {
        /// <summary>运行独立内存用例，失败时返回非零状态。</summary>
        [STAThread]
        private static int Main()
        {
            int failures = 0;
            Check("示例任务与未知输入、整机暂停、恢复和关闭", () => ExampleLifecycle().GetAwaiter().GetResult(), ref failures);
            Check("重复子任务调用与未选择分支", () => TreeAssociations().GetAwaiter().GetResult(), ref failures);
            Check("同工位多条并行活动行及暂停覆盖", () => ParallelRows().GetAwaiter().GetResult(), ref failures);
            Check("WPF 页面构造、中英文资源与布局计算", ConstructViews, ref failures);
            Console.WriteLine("任务监控内部检查完成：4 组，失败 " + failures + "。不代表实际屏幕验证。");
            return failures == 0 ? 0 : 1;
        }

        /// <summary>逐项报告，保留完整原始异常。</summary>
        private static void Check(string name, Action action, ref int failures)
        {
            try { action(); Console.WriteLine("[通过] " + name); }
            catch (Exception error) { failures++; Console.Error.WriteLine("[失败] " + name + "\n" + error); }
        }

        /// <summary>示例由实际引擎运行，暂停时保留步骤，输入恢复后继续生产。</summary>
        private static async Task ExampleLifecycle()
        {
            var session = TaskMonitorExample.Create();
            try
            {
                Assert(session.Machine.GetSnapshot().State == TaskMachineState.NeedsReset, "构造自动启动了任务。");
                session.Machine.Reset(); session.Machine.Start();
                await Until(() => session.Machine.GetSnapshot().Stations.Any(station => station.Run?.Nodes.Any(node => node.NodeId == "clamped" && node.Diagnostics != null) == true));
                session.Device.SetInput("夹紧到位", null);
                await Until(() => session.Machine.GetSnapshot().Stations.Single(station => station.StationId == "inspect").Run.Nodes.Single(node => node.NodeId == "clamped").Diagnostics.Values[0].Satisfied == null);
                session.Machine.Pause();
                var stationSnapshot = session.Machine.GetSnapshot().Stations.Single(station => station.StationId == "inspect");
                var tree = TaskStepTree.Build("Inspect", session.Library);
                TaskStepTree.Update(tree, stationSnapshot.Run, TaskMachineState.Paused, text => text);
                Assert(TaskStepTree.Flatten(tree).Single(row => row.Node.Id == "clamped").StateText == "Paused", "暂停未覆盖活动节点。");
                TaskStepTree.Update(tree, stationSnapshot.Run, TaskMachineState.Ready, text => text);
                Assert(TaskStepTree.Flatten(tree).Single(row => row.Node.Id == "clamped").StateText == "Ready", "复位后仍把活动步骤显示成暂停。");
                TaskStepTree.Update(tree, stationSnapshot.Run, TaskMachineState.Faulted, text => text);
                Assert(TaskStepTree.Flatten(tree).Single(row => row.Node.Id == "clamped").StateText == "Faulted", "整机故障时没有区分故障暂停状态。");
                session.Device.SetInput("上料机构安全位", true);
                session.Device.SetInput("夹紧到位", true);
                session.Machine.Reset(); session.Machine.Start();
                await Until(() => session.Machine.GetSnapshot().Stations.Single(station => station.StationId == "inspect").CompletedCycles > 0, 25000);
                Assert(session.Machine.GetSnapshot().State == TaskMachineState.Running, "示例恢复后出现故障。");
            }
            finally { await session.Machine.CloseAsync(); }
            Assert(session.Machine.GetSnapshot().State == TaskMachineState.Closed, "会话没有收尾。");
        }

        /// <summary>两次同名子任务必须匹配不同父调用；条件不选中的子树标记未选择。</summary>
        private static async Task TreeAssociations()
        {
            var child = Definition("Child", Delay("childDelay", 5));
            var branch = new ConditionBranchNode { Id = "branch", Name = "条件判断", Condition = new TaskCondition {
                Kind = TaskConditionKind.Compare, Comparison = TaskComparison.Equal, Left = Boolean("false"), Right = Boolean("true") },
                WhenTrue = Sequence("yes", Delay("yesDelay", 5)), WhenFalse = Sequence("no", Delay("noDelay", 5)) };
            var parent = Definition("Parent", new CallTaskNode { Id = "first", Name = "第一次", TaskId = "Child" },
                new CallTaskNode { Id = "second", Name = "第二次", TaskId = "Child" }, branch);
            var library = new Dictionary<string, TaskDefinition> { { "Parent", parent }, { "Child", child } };
            var run = new TaskExecutionEngine().Start(parent, Device(), new[] { child });
            try
            {
                var snapshot = await run.Completion;
                Assert(snapshot.Error == null, snapshot.Error?.ToString());
                var tree = TaskStepTree.Build("Parent", library);
                TaskStepTree.Update(tree, snapshot, TaskMachineState.Running, text => text);
                var children = TaskStepTree.Flatten(tree).Where(row => row.Node.Id == "childDelay").ToArray();
                Assert(children.Length == 2 && children.All(row => row.StateText == "已完成")
                    && children[0].Snapshot.ExecutionId != children[1].Snapshot.ExecutionId, "重复调用串行错配。");
                Assert(TaskStepTree.Flatten(tree).Single(row => row.Node.Id == "yesDelay").NotSelected, "未选择分支被显示为未执行或故障。");
                TaskStepTree.Update(tree, null, TaskMachineState.Running, text => text);
                Assert(TaskStepTree.Flatten(tree).All(row => row.Snapshot == null && !row.NotSelected), "新轮仍残留旧调用状态。");
            }
            finally { run.Cancel(); await run.Completion; }
        }

        /// <summary>并行组两个活动末端都显示，暂停不会把步骤显示成仍在运动。</summary>
        private static async Task ParallelRows()
        {
            var task = Definition("Parallel", new ParallelGroupNode { Id = "both", Name = "并行", Branches = new List<TaskNode> {
                Sequence("a", Delay("waitA", 600)), Sequence("b", Delay("waitB", 600)) } });
            var run = new TaskExecutionEngine().Start(task, Device());
            try
            {
                await Until(() => run.GetSnapshot().Nodes.Count(node => node.NodeId.StartsWith("wait") && node.State == TaskNodeState.Waiting) == 2);
                run.Pause();
                var tree = TaskStepTree.Build(task.TaskId, new Dictionary<string, TaskDefinition> { { task.TaskId, task } });
                TaskStepTree.Update(tree, run.GetSnapshot(), TaskMachineState.Paused, text => text);
                Assert(TaskStepTree.Flatten(tree).Count(row => row.Node.Id.StartsWith("wait") && row.StateText == "Paused") == 2, "并行进度被折成一个当前步。");
            }
            finally { run.Cancel(); await run.Completion; }
        }

        /// <summary>离屏验证语言资源、工位切换跟随和监控页面布局。</summary>
        private static void ConstructViews()
        {
            var application = new Application();
            var session = TaskMonitorExample.Create();
            try
            {
                var view = new TaskMonitorView(session);
                view.Refresh(); view.SetLanguage("en-US");
                Assert(view.T("步骤详情") == "Step details", "英文资源未加载。");
                view.SetLanguage("zh-CN");
                view.Measure(new Size(1360, 720)); view.Arrange(new Rect(0, 0, 1360, 720)); view.UpdateLayout();
                var stationsGrid = view.FindName("StationsGrid") as System.Windows.Controls.DataGrid;
                var followBox = view.FindName("FollowBox") as System.Windows.Controls.CheckBox;
                var stepsTree = view.FindName("StepsTree") as TreeView;
                var causeButton = view.FindName("LocateCurrentCauseButton") as Button;
                var window = new Window { Content = view, Width = 1360, Height = 720, Opacity = 0, ShowInTaskbar = false };
                window.Show();
                application.Dispatcher.Invoke(new Action(() => { }), DispatcherPriority.ApplicationIdle);
                Assert(stationsGrid != null && stationsGrid.Items.Count > 1 && followBox != null && stepsTree != null && causeButton != null,
                    "工位列表、步骤树、跟随选项或原因定位按钮未加载。");
                Assert(!causeButton.IsEnabled, "没有运行步骤或停机故障时仍允许点击原因定位。");
                followBox.IsChecked = false;
                stationsGrid.SelectedIndex = 2;
                view.UpdateLayout();
                application.Dispatcher.Invoke(new Action(() => { }), DispatcherPriority.ApplicationIdle);
                Assert(followBox.IsChecked == true, "切换工位后没有恢复当前步骤跟随。");

                var firstStep = stepsTree.Items[0] as MotionControl.TaskEngine.Wpf.Monitoring.TaskStepRow;
                Assert(firstStep != null, "切换工位后没有生成步骤。");
                firstStep.IsSelected = false;
                view.UpdateLayout();
                firstStep.IsSelected = true;
                view.UpdateLayout();
                Assert(followBox.IsChecked == true, "界面绑定导致的步骤选择错误关闭了跟随。");

                var secondStep = stepsTree.Items.Count > 1
                    ? stepsTree.Items[1] as MotionControl.TaskEngine.Wpf.Monitoring.TaskStepRow : null;
                var firstContainer = stepsTree.ItemContainerGenerator.ContainerFromItem(firstStep) as TreeViewItem;
                Assert(secondStep != null && firstContainer != null, "没有生成可验证的步骤行。");
                var mouseInput = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.PreviewMouseDownEvent };
                firstContainer.RaiseEvent(mouseInput);
                secondStep.IsSelected = true;
                view.UpdateLayout();
                Assert(followBox.IsChecked == false, "现场人员选择其他步骤后没有暂停自动跟随。");
                session.Machine.Reset();
                session.Machine.Start();
                Until(() => session.Machine.GetSnapshot().Stations.Any(station => station.Run?.Nodes.Any(node => node.State == TaskNodeState.Waiting) == true))
                    .GetAwaiter().GetResult();
                view.Refresh();
                Assert(causeButton.IsEnabled, "存在等待步骤时原因定位按钮仍被禁用。");
                causeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, causeButton));
                view.UpdateLayout();
                var selectedStation = stationsGrid.SelectedItem as TaskStationRow;
                Assert(selectedStation?.Snapshot?.Run?.Nodes.Any(node => node.State == TaskNodeState.Waiting) == true,
                    "原因定位没有选中含等待步骤的工位。");
                Assert(stepsTree.SelectedItem is MotionControl.TaskEngine.Wpf.Monitoring.TaskStepRow selectedStep
                    && selectedStep.Snapshot?.State == TaskNodeState.Waiting, "原因定位没有选中具体等待步骤。");
                window.Close();
                Assert(view.ActualWidth > 0 && view.T("步骤详情") == "步骤详情", "页面资源或布局构造失败。");
            }
            finally { session.Machine.CloseAsync().GetAwaiter().GetResult(); application.Shutdown(); }
        }

        /// <summary>创建没有硬件对象的空目录设备。</summary>
        private static SimulatedTaskDevice Device()
        { return new SimulatedTaskDevice(new TaskResourceCatalog(new TaskAxisInfo[0], new string[0], new string[0], new Dictionary<string, DeviceVariableType>(), new TaskTeachPoint[0])); }
        /// <summary>创建合法任务根节点。</summary>
        private static TaskDefinition Definition(string id, params TaskNode[] nodes)
        { return new TaskDefinition { TaskId = id, Name = id, Root = Sequence(id + "Root", nodes) }; }
        /// <summary>创建普通顺序节点。</summary>
        private static SequenceNode Sequence(string id, params TaskNode[] nodes)
        { return new SequenceNode { Id = id, Name = id, Children = new List<TaskNode>(nodes) }; }
        /// <summary>创建有明确时限的延时节点。</summary>
        private static DelayNode Delay(string id, int milliseconds) { return new DelayNode { Id = id, Name = id, Milliseconds = milliseconds }; }
        /// <summary>创建布尔常量。</summary>
        private static TaskValueBinding Boolean(string value)
        { return new TaskValueBinding { Source = TaskValueSource.Constant, DataType = DeviceVariableType.Boolean, Constant = value }; }
        /// <summary>有界等待可观察状态，不无限挂起检查器。</summary>
        private static async Task Until(Func<bool> condition, int timeout = 5000)
        {
            var clock = Stopwatch.StartNew();
            while (!condition()) { if (clock.ElapsedMilliseconds > timeout) throw new TimeoutException("监控条件等待超时。"); await Task.Delay(10); }
        }
        /// <summary>失败时输出能定位原因的说明。</summary>
        private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
