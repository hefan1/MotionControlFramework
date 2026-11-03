using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Framework.Contracts.Configuration;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.Framework.Wpf.Configuration;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Execution;
using MotionControl.TaskEngine.Machine;
using MotionControl.TaskEngine.Device.InMemory;
using MotionControl.TaskEngine.Resources;
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
            Check("WPF 监控页面、关联详情与操作按钮生产流程", ConstructViews, ref failures);
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
            bool requireBlockedClose = false;
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
                await Until(() => session.Device.ReadResources().Any(item => item.State == TaskResourceState.Held));
                requireBlockedClose = true;
            }
            finally { await CloseExampleSessionAsync(session, requireBlockedClose); }
            Assert(session.Machine.GetSnapshot().State == TaskMachineState.Closed, "会话没有收尾。");
        }

        /// <summary>关闭示例时保留隔离，核实内存设备的停止及报警状态后才显式恢复。</summary>
        private static async Task CloseExampleSessionAsync(TaskMonitorSession session, bool requireBlockedClose)
        {
            InvalidOperationException closeFailure = null;
            try { await session.Machine.CloseAsync(); }
            catch (InvalidOperationException error) { closeFailure = error; }
            if (closeFailure == null)
            {
                Assert(!requireBlockedClose, "资源被占用时关闭未保留待确认状态。");
                return;
            }
            var blocked = session.Device.ReadResources().Where(item => item.State == TaskResourceState.Blocked).ToArray();
            if (blocked.Length == 0) throw closeFailure;
            Assert(session.Machine.GetSnapshot().State != TaskMachineState.Closed, "资源待确认时错误关闭了整机会话。");
            ConfirmMemoryResources(session, blocked);
            await session.Machine.CloseAsync();
        }

        /// <summary>模拟人员清理内存场景；无退出位置配置时只验证示例实际定义的条件，不推断现场安全。</summary>
        private static void ConfirmMemoryResources(TaskMonitorSession session, TaskResourceSnapshot[] blocked)
        {
            foreach (string output in session.Device.Catalog.Outputs)
            {
                session.Device.WriteOutput(output, false);
                Assert(session.Device.ReadOutput(output) == false, "内存输出未复位：" + output);
            }
            var axes = blocked.SelectMany(item => item.Resources)
                .Where(name => name.StartsWith("Axis:", StringComparison.OrdinalIgnoreCase))
                .Select(name => name.Substring(5)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            Assert(axes.Length > 0, "示例资源没有用于确认的轴。");
            foreach (string axisName in axes)
            {
                var axis = session.Device.ReadAxis(axisName);
                Assert(!axis.IsBusy && axis.Enabled && axis.Homed && string.IsNullOrEmpty(axis.Alarm),
                    "内存轴未停止、未就绪或有报警：" + axisName);
            }
            // 验证确认入口会拒绝仍有轴报警的区域，然后才模拟修复该报警。
            var first = blocked.First(item => item.Resources.Any(name => name.StartsWith("Axis:", StringComparison.OrdinalIgnoreCase)));
            string firstAxis = first.Resources.First(name => name.StartsWith("Axis:", StringComparison.OrdinalIgnoreCase)).Substring(5);
            session.Device.SetAxisAlarm(firstAxis, "测试报警");
            string reason;
            Assert(!session.Device.TryRecoverResources(first.RequestId, "已核实内存场景", out reason), "存在轴报警时错误解除资源隔离。");
            session.Device.SetAxisAlarm(firstAxis, null);
            foreach (var resource in blocked)
                Assert(session.Device.TryRecoverResources(resource.RequestId, "内存检查：已复位输出并核对轴停止", out reason),
                    "确认内存资源失败：" + reason);
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

        /// <summary>离屏验证语言资源、工位切换跟随、循环待执行提示和监控页面布局。</summary>
        private static void ConstructViews()
        {
            var application = new Application();
            var session = TaskMonitorExample.Create();
            TaskMonitorSession pendingSession = null;
            Window pendingWindow = null;
            try
            {
                var view = new TaskMonitorView(session);
                TaskReferenceEventArgs requestedReference = null;
                view.ReferenceRequested += (sender, args) => requestedReference = args;
                view.Refresh(); view.SetLanguage("en-US");
                Assert(view.T("步骤详情") == "Step details", "英文资源未加载。");
                Assert(((DataGrid)view.FindName("StationsGrid")).Columns[0].Header.ToString() == "Station",
                    "英文工位列表表头没有说明该列显示工位名称。");
                view.SetLanguage("zh-CN");
                Assert(((DataGrid)view.FindName("StationsGrid")).Columns[0].Header.ToString() == "工位名称",
                    "工位列表表头没有说明该列显示工位名称。");

                const string sharedSignalName = "ClampSignal";
                var ambiguousCatalog = new TaskResourceCatalog(
                    new[] { new TaskAxisInfo(sharedSignalName, "ControllerA", "mm", 0, 100) },
                    new[] { sharedSignalName }, new[] { sharedSignalName },
                    new Dictionary<string, DeviceVariableType> { { sharedSignalName, DeviceVariableType.Boolean } },
                    new TaskTeachPoint[0]);
                var diReference = new TaskCatalogReferenceWindow(ambiguousCatalog, "IO映射", sharedSignalName, "DI", view.T);
                var diFields = ((DataGrid)diReference.Content).ItemsSource.Cast<TaskDetailField>().ToArray();
                Assert(diReference.Title.Contains("DI")
                    && diFields.Count(field => field.Name == view.T("方向") && field.Value == "DI") == 1
                    && !diFields.Any(field => field.Name == view.T("方向") && field.Value == "DO")
                    && !diFields.Any(field => field.Name == view.T("单位") || field.Name == view.T("类型")),
                    "DI 关联把同名 DO、轴或变量显示成当前对象。");
                var doReference = new TaskCatalogReferenceWindow(ambiguousCatalog, "IO映射", sharedSignalName, "DO", view.T);
                var doFields = ((DataGrid)doReference.Content).ItemsSource.Cast<TaskDetailField>().ToArray();
                Assert(doReference.Title.Contains("DO")
                    && doFields.Count(field => field.Name == view.T("方向") && field.Value == "DO") == 1
                    && !doFields.Any(field => field.Name == view.T("方向") && field.Value == "DI"),
                    "DO 关联把同名 DI 显示成当前对象。");
                var axisReference = new TaskCatalogReferenceWindow(ambiguousCatalog, "轴映射配置", sharedSignalName, null, view.T);
                var axisFields = ((DataGrid)axisReference.Content).ItemsSource.Cast<TaskDetailField>().ToArray();
                Assert(axisFields.Any(field => field.Name == view.T("单位"))
                    && !axisFields.Any(field => field.Name == view.T("方向") || field.Name == view.T("类型")),
                    "轴映射关联混入同名 IO 或变量字段。");
                var variableReference = new TaskCatalogReferenceWindow(ambiguousCatalog, "设备变量", sharedSignalName, null, view.T);
                var variableFields = ((DataGrid)variableReference.Content).ItemsSource.Cast<TaskDetailField>().ToArray();
                Assert(variableFields.Any(field => field.Name == view.T("类型"))
                    && !variableFields.Any(field => field.Name == view.T("方向") || field.Name == view.T("单位")),
                    "变量关联混入同名 IO 或轴字段。");

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
                ExerciseMultipleStationFaultSelection(view, session, stationsGrid, causeButton);
                ExerciseFiveStationLayout(application);
                ExerciseTimeoutAlarmLocation(application);
                ExerciseOperatorWaitDescriptions(application);
                ExerciseOperatorControls(application);
                ExerciseFaultRecoveryFromOperatorInput(application);
                ExerciseIoMappingFocus(application);
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

                var inspectStation = stationsGrid.Items.OfType<TaskStationRow>().Single(row => row.Id == "inspect");
                stationsGrid.SelectedItem = inspectStation;
                view.UpdateLayout();
                view.Refresh();
                var clampStep = MotionControl.TaskEngine.Wpf.Monitoring.TaskStepTree
                    .Flatten(stepsTree.Items.OfType<MotionControl.TaskEngine.Wpf.Monitoring.TaskStepRow>())
                    .Single(row => row.Node.Id == "clamped");
                Assert(clampStep.Snapshot?.State == TaskNodeState.Waiting, "夹紧 DI 步骤没有保持为等待态供操作员诊断。");
                clampStep.IsSelected = true;
                view.UpdateLayout();
                var referencesPanel = view.FindName("ReferencesPanel") as WrapPanel;
                var clampInputReference = referencesPanel?.Children.OfType<Button>()
                    .SingleOrDefault(button => (button.Content as string)?.Contains("IO映射 · 夹紧到位") == true);
                Assert(clampInputReference != null && ((string)clampInputReference.Content).EndsWith("(DI)"),
                    "夹紧到位步骤没有显示明确的 DI 关联入口。");
                var stateBeforeReference = session.Machine.GetSnapshot().State;
                clampInputReference.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, clampInputReference));
                Assert(requestedReference?.Page == "IO映射" && requestedReference.Name == "夹紧到位"
                    && requestedReference.Direction == "DI"
                    && session.Machine.GetSnapshot().State == stateBeforeReference,
                    "点击夹紧关联没有定位到 DI，或意外改变了整机状态。");
                Console.WriteLine("[通过] 故障详情：夹紧到位入口指向 DI，点击不触发设备动作");

                var pendingTask = Definition("CyclePending", Delay("first", 250), Delay("next", 250));
                var pendingConfiguration = new TaskMachineConfiguration
                {
                    MachineId = "CyclePendingCheck",
                    Name = "循环待执行提示检查",
                    Stations = new List<StationTaskConfiguration> { new StationTaskConfiguration
                    {
                        StationId = "cycle", Name = "循环工位", TaskId = pendingTask.TaskId,
                        Enabled = true, Mode = StationTaskMode.Cyclic, CycleIntervalMs = 1
                    } }
                };
                pendingSession = new TaskMonitorSession(pendingConfiguration, new[] { pendingTask }, Device(), "内部检查配置", false);
                var pendingView = new TaskMonitorView(pendingSession);
                pendingWindow = new Window { Content = pendingView, Width = 1360, Height = 720, Opacity = 0, ShowInTaskbar = false };
                pendingWindow.Show();
                application.Dispatcher.Invoke(new Action(() => { }), DispatcherPriority.ApplicationIdle);
                pendingSession.Machine.Reset();
                pendingSession.Machine.Start();
                Until(() =>
                {
                    var cycle = pendingSession.Machine.GetSnapshot().Stations.Single();
                    return cycle.CompletedCycles > 0 && cycle.State == StationTaskState.Executing
                        && cycle.Run?.Nodes.Any(node => node.NodeId == "first" && node.State == TaskNodeState.Waiting) == true
                        && !cycle.Run.Nodes.Any(node => node.NodeId == "next");
                }).GetAwaiter().GetResult();
                pendingView.Refresh();
                var pendingStations = pendingView.FindName("StationsGrid") as System.Windows.Controls.DataGrid;
                var pendingFollow = pendingView.FindName("FollowBox") as System.Windows.Controls.CheckBox;
                var pendingTree = pendingView.FindName("StepsTree") as TreeView;
                var pendingDetail = pendingView.FindName("DetailSummary") as System.Windows.Controls.TextBlock;
                pendingStations.SelectedIndex = 0;
                pendingView.UpdateLayout();
                pendingFollow.IsChecked = false;
                var notEnteredThisCycle = pendingTree.Items[1] as MotionControl.TaskEngine.Wpf.Monitoring.TaskStepRow;
                Assert(notEnteredThisCycle != null && notEnteredThisCycle.Snapshot == null && !notEnteredThisCycle.NotSelected,
                    "循环下一步没有保持为当前周期的待执行步骤。");
                notEnteredThisCycle.IsSelected = true;
                pendingView.UpdateLayout();
                pendingView.Refresh();
                Assert(notEnteredThisCycle.Detail == "本周期尚未进入此步骤"
                    && pendingDetail.Text == "本周期尚未进入此步骤", "监控页没有说明选中步骤尚未进入当前周期。");
                window.Close();
                Assert(view.ActualWidth > 0 && view.T("步骤详情") == "步骤详情", "页面资源或布局构造失败。");
            }
            finally
            {
                pendingWindow?.Close();
                if (pendingSession != null) pendingSession.Machine.CloseAsync().GetAwaiter().GetResult();
                CloseExampleSessionAsync(session, false).GetAwaiter().GetResult();
                application.Shutdown();
            }
        }

        /// <summary>在透明的离屏窗口中走操作员按钮和自动工艺信号，验证真实三工位循环可暂停并续跑。</summary>
        private static void ExerciseOperatorControls(Application application)
        {
            var session = TaskMonitorExample.Create();
            TaskSimulationWindow window = null;
            bool requireBlockedClose = false;
            try
            {
                window = new TaskSimulationWindow(session)
                {
                    Opacity = 0,
                    ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -2000,
                    Top = -2000
                };
                window.Show();
                application.Dispatcher.Invoke(new Action(() => { }), DispatcherPriority.ApplicationIdle);

                var reset = (Button)window.FindName("ResetButton");
                var start = (Button)window.FindName("StartButton");
                var pause = (Button)window.FindName("PauseButton");
                var automaticSignals = (CheckBox)window.FindName("AutomaticProductionSignalsCheckBox");
                Assert(reset != null && start != null && pause != null && automaticSignals?.IsChecked == true,
                    "操作页的复位、启动、暂停按钮或自动工艺信号未就绪。");
                Assert(!ReadPresentedText(window).Contains("仿真"), "操作页可见文字或操作提示出现了禁止显示的词语。");

                ClickButton(reset);
                Assert(session.Machine.GetSnapshot().State == TaskMachineState.Ready && start.IsEnabled,
                    "复位按钮没有将整机带入就绪状态。");
                ClickButton(start);
                Assert(session.Machine.GetSnapshot().State == TaskMachineState.Running && pause.IsEnabled,
                    "启动按钮没有启动整机循环。");

                WaitWithDispatcher(() => ProductionCycleCounts(session).All(count => count >= 2), 45000,
                    "自动工艺信号未能带动上料、搬运、检测三个工位连续完成两个生产周期。");
                ClickButton(pause);
                Assert(session.Machine.GetSnapshot().State == TaskMachineState.Paused && reset.IsEnabled && !start.IsEnabled,
                    "暂停按钮没有锁存整机状态或刷新复位权限。");
                long[] pausedCounts = ProductionCycleCounts(session);
                PumpFor(TimeSpan.FromMilliseconds(700));
                Assert(ProductionCycleCounts(session).SequenceEqual(pausedCounts), "整机暂停后仍有工位完成新的生产周期。");

                ClickButton(reset);
                Assert(session.Machine.GetSnapshot().State == TaskMachineState.Ready && start.IsEnabled,
                    "暂停后整机复位没有恢复启动权限。");
                ClickButton(start);
                WaitWithDispatcher(() => ProductionCycleCounts(session).Zip(pausedCounts,
                    (current, prior) => current - prior >= 2).All(value => value),
                    45000, "暂停后复位并启动，三个工位没有各自连续完成两个追加生产周期。");
                Assert(session.Machine.GetSnapshot().State == TaskMachineState.Running,
                    "续跑期间整机没有保持运行状态。");
                Console.WriteLine("[通过] 操作界面：复位、启动、三工位连续循环、暂停、复位续跑");

                var inputButtons = ((WrapPanel)window.FindName("InputsPanel")).Children.OfType<Button>();
                var physicalStart = inputButtons.Single(button => (string)button.Tag == "启动按钮");
                var physicalPause = inputButtons.Single(button => (string)button.Tag == "停止按钮");
                var physicalReset = inputButtons.Single(button => (string)button.Tag == "复位按钮");
                long[] beforePhysicalButtons = ProductionCycleCounts(session);
                ClickButton(physicalPause);
                Assert(session.Machine.GetSnapshot().State == TaskMachineState.Paused
                    && session.Device.ReadInput("停止按钮") == false,
                    "停止 DI 没有暂停整机或信号未自动释放。");
                ClickButton(physicalReset);
                Assert(session.Machine.GetSnapshot().State == TaskMachineState.Ready
                    && session.Device.ReadInput("复位按钮") == false,
                    "复位 DI 没有将整机复位就绪或信号未自动释放。");
                ClickButton(physicalStart);
                Assert(session.Machine.GetSnapshot().State == TaskMachineState.Running
                    && session.Device.ReadInput("启动按钮") == false,
                    "启动 DI 没有开始运行或信号未自动释放。");
                WaitWithDispatcher(() => ProductionCycleCounts(session).Zip(beforePhysicalButtons, (current, prior) => current > prior).All(value => value),
                    30000, "实体 DI 按钮启动后，三个工位没有全部继续生产。");
                Console.WriteLine("[通过] 操作界面：停止、复位、启动 DI 按钮按瞬时信号执行并自动释放");
                WaitWithDispatcher(() => session.Device.ReadResources().Any(item => item.State == TaskResourceState.Held),
                    5000, "未观察到示例占用资源，无法验证关闭保护。");
                requireBlockedClose = true;
            }
            finally
            {
                if (window != null && window.IsVisible)
                {
                    window.Close();
                    if (requireBlockedClose)
                    {
                        var feedback = (TextBlock)window.FindName("CommandFeedback");
                        WaitWithDispatcher(() => window.IsVisible && window.IsEnabled
                            && session.Device.ReadResources().Any(item => item.State == TaskResourceState.Blocked)
                            && feedback.Text.Contains("待确认"), 10000, "资源待确认时窗口没有保留并显示原因。");
                        Assert(session.Machine.GetSnapshot().State != TaskMachineState.Closed,
                            "资源待确认时窗口错误关闭了整机会话。");
                        ConfirmMemoryResources(session, session.Device.ReadResources().Where(item => item.State == TaskResourceState.Blocked).ToArray());
                        window.Close();
                    }
                    WaitWithDispatcher(() => !window.IsVisible
                        && session.Machine.GetSnapshot().State == TaskMachineState.Closed,
                        30000, "操作员关闭窗口后，整机会话或窗口没有完成收尾。");
                }
                else CloseExampleSessionAsync(session, false).GetAwaiter().GetResult();
            }
        }

        /// <summary>读取三个启用工位已经完成的周期数。</summary>
        private static long[] ProductionCycleCounts(TaskMonitorSession session)
        {
            var stations = session.Machine.GetSnapshot().Stations;
            return new[] { "load", "transfer", "inspect" }
                .Select(id => stations.Single(station => station.StationId == id).CompletedCycles).ToArray();
        }

        /// <summary>用 WPF Dispatcher 推进窗口计时器和后台 UI 刷新。</summary>
        private static void WaitWithDispatcher(Func<bool> condition, int timeoutMs, string timeoutMessage)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (!condition())
            {
                if (DateTime.UtcNow >= deadline) throw new TimeoutException(timeoutMessage);
                var frame = new DispatcherFrame();
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
                timer.Tick += (sender, args) => { timer.Stop(); frame.Continue = false; };
                timer.Start();
                Dispatcher.PushFrame(frame);
            }
        }

        /// <summary>保持 WPF 消息泵运行一段时间，检查暂停期间周期数不会变化。</summary>
        private static void PumpFor(TimeSpan duration)
        {
            DateTime deadline = DateTime.UtcNow.Add(duration);
            WaitWithDispatcher(() => DateTime.UtcNow >= deadline, (int)duration.TotalMilliseconds + 1000,
                "WPF Dispatcher 在等待期间没有继续处理消息。");
        }

        /// <summary>通过真实 WPF Click 路由触发操作按钮。</summary>
        private static void ClickButton(Button button)
        {
            Assert(button.IsEnabled, "操作按钮当前不可用：" + (button.Content ?? button.Name));
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
        }

        /// <summary>收集运行窗口呈现的标题、文本、按钮内容、列表项和提示文字。</summary>
        private static string ReadPresentedText(DependencyObject root)
        {
            var values = new List<string>();
            foreach (DependencyObject element in EnumerateVisualElements(root))
            {
                if (element is Window window) values.Add(window.Title);
                if (element is TextBlock textBlock) values.Add(textBlock.Text);
                if (element is ContentControl contentControl && contentControl.Content is string content)
                    values.Add(content);
                if (element is HeaderedContentControl headeredControl && headeredControl.Header is string header)
                    values.Add(header);
                if (element is FrameworkElement frameworkElement && frameworkElement.ToolTip is string toolTip)
                    values.Add(toolTip);
                if (element is ItemsControl itemsControl)
                {
                    foreach (object item in itemsControl.Items)
                        if (item is string itemText) values.Add(itemText);
                }
            }

            return string.Join("\n", values.Where(value => !string.IsNullOrWhiteSpace(value)));
        }

        /// <summary>遍历已加载窗口的视觉子树，读取控件模板实际生成的可见文本元素。</summary>
        private static IEnumerable<DependencyObject> EnumerateVisualElements(DependencyObject root)
        {
            if (root == null) yield break;
            yield return root;
            if (!(root is Visual)) yield break;

            int childCount = VisualTreeHelper.GetChildrenCount(root);
            for (int index = 0; index < childCount; index++)
            {
                foreach (DependencyObject child in EnumerateVisualElements(VisualTreeHelper.GetChild(root, index)))
                    yield return child;
            }
        }

        /// <summary>用同名 DI/DO 配置验证关联跳转分别选中各自的物理行。</summary>
        private static void ExerciseIoMappingFocus(Application application)
        {
            string configurationRoot = Path.Combine(Path.GetTempPath(), "MotionControl.TaskMonitor.Checks",
                "IoMappingFocus-" + Guid.NewGuid().ToString("N"));
            Window host = null;
            try
            {
                var store = new MachineConfigurationStore(configurationRoot);
                application.Resources["IO映射读取完成"] = "映射已读取";
                application.Resources["IO映射请先连接控制卡"] = "未连接控制卡";
                application.Resources["控制卡Io数量未连接"] = "未连接控制卡，尚未读取 I/O 能力。";
                const string machineId = "IoMappingFocus";
                var document = new IoMappingConfigurationDocument();
                document.DigitalInputs.Add(new DigitalIoHardwareDefinition
                    { Name = "夹紧到位", ControllerId = "ControllerA", Channel = 3, ActiveHigh = true });
                document.DigitalOutputs.Add(new DigitalIoHardwareDefinition
                    { Name = "夹紧到位", ControllerId = "ControllerA", Channel = 5, ActiveHigh = true });
                store.SaveJson(machineId, "HardwareConfig/IoMapping.json", document);

                var view = new IoMappingConfigurationView(store, machineId, new NoOpCardConnection());
                host = new Window
                {
                    Content = view,
                    Width = 900,
                    Height = 650,
                    Opacity = 0,
                    ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -2000,
                    Top = -2000
                };
                host.Show();
                application.Dispatcher.Invoke(new Action(() => { }), DispatcherPriority.ApplicationIdle);
                view.UpdateLayout();
                var inputs = (DataGrid)view.FindName("DigitalInputsGrid");
                var outputs = (DataGrid)view.FindName("DigitalOutputsGrid");
                Assert(inputs != null && outputs != null, "IO 映射页没有加载 DI/DO 表格。");

                Assert(view.FocusPointByLogicalName("夹紧到位", false)
                    && ReferenceEquals(inputs.SelectedItem, view.DigitalInputs.Single())
                    && ((DigitalIoHardwareDefinition)inputs.SelectedItem).Channel == 3
                    && outputs.SelectedItem == null,
                    "DI 关联没有选中正确的 DI 物理通道，或串到了同名 DO。");
                Assert(view.FocusPointByLogicalName("夹紧到位", true)
                    && ReferenceEquals(outputs.SelectedItem, view.DigitalOutputs.Single())
                    && ((DigitalIoHardwareDefinition)outputs.SelectedItem).Channel == 5,
                    "DO 关联没有选中正确的 DO 物理通道。");
                Assert(!view.FocusPointByLogicalName("不存在的信号", false)
                    && ReferenceEquals(inputs.SelectedItem, view.DigitalInputs.Single()),
                    "找不到逻辑信号时错误地移动了 DI 行选择。");
                Console.WriteLine("[通过] IO 映射页面：DI/DO 同名时仍分别定位物理通道 3/5");
            }
            finally
            {
                host?.Close();
                if (Directory.Exists(configurationRoot)) Directory.Delete(configurationRoot, true);
            }
        }

        /// <summary>用真实 WPF 输入选择器修复超时 DI，再按复位和启动恢复故障步骤。</summary>
        private static void ExerciseFaultRecoveryFromOperatorInput(Application application)
        {
            var catalog = new TaskResourceCatalog(new TaskAxisInfo[0], new[] { "夹紧到位" }, new string[0],
                new Dictionary<string, DeviceVariableType>(), new TaskTeachPoint[0],
                alarmDefinitions: new[] { new TaskAlarmInfo("CLAMP_TIMEOUT", "夹紧等待超时", MachineAlarmKind.StopMachine) });
            var device = new InMemoryTaskDevice(catalog);
            device.SetInput("夹紧到位", false);
            var task = Definition("ClampRecovery", new WaitInputNode
            {
                Id = "clamped",
                Name = "等待夹紧到位",
                InputName = "夹紧到位",
                Expected = true,
                TimeoutMs = 400,
                TimeoutAlarmCodes = new List<string> { "CLAMP_TIMEOUT" }
            });
            var machineConfiguration = new TaskMachineConfiguration
            {
                MachineId = "ClampRecoveryUiCheck",
                Name = "夹紧输入故障恢复检查",
                Stations = new List<StationTaskConfiguration>
                {
                    new StationTaskConfiguration
                    {
                        StationId = "inspect",
                        Name = "夹紧工位",
                        TaskId = task.TaskId,
                        Enabled = true,
                        Mode = StationTaskMode.Once
                    }
                }
            };
            var session = new TaskMonitorSession(machineConfiguration, new[] { task }, device, "内存故障恢复检查", false);
            TaskSimulationWindow window = null;
            try
            {
                window = new TaskSimulationWindow(session)
                {
                    Opacity = 0,
                    ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -2000,
                    Top = -2000
                };
                window.Show();
                application.Dispatcher.Invoke(new Action(() => { }), DispatcherPriority.ApplicationIdle);
                Assert(((CheckBox)window.FindName("AutomaticProductionSignalsCheckBox")).Visibility == Visibility.Collapsed,
                    "本机配置会话仍显示内置示例的自动工艺信号控件。");

                var reset = (Button)window.FindName("ResetButton");
                var start = (Button)window.FindName("StartButton");
                ClickButton(reset);
                ClickButton(start);
                WaitWithDispatcher(() => session.Machine.GetSnapshot().State == TaskMachineState.Faulted,
                    5000, "夹紧 DI 超时没有使整机进入故障暂停。");

                var monitor = (TaskMonitorView)((ContentControl)window.FindName("MonitorContent")).Content;
                monitor.Refresh();
                var locateCause = (Button)monitor.FindName("LocateCurrentCauseButton");
                Assert(locateCause.IsEnabled, "DI 超时后故障原因定位按钮没有启用。");
                locateCause.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, locateCause));
                monitor.UpdateLayout();
                var detailTitle = (TextBlock)monitor.FindName("DetailTitle");
                var detailNotice = (TextBlock)monitor.FindName("DetailNotice");
                Assert(detailTitle.Text.Contains("等待夹紧到位")
                    && detailNotice.Text.Contains("整机复位") && detailNotice.Text.Contains("启动"),
                    "故障定位没有显示夹紧 DI 步骤或恢复顺序。");

                var selector = ((WrapPanel)window.FindName("InputsPanel")).Children.OfType<StackPanel>()
                    .SelectMany(row => row.Children.OfType<ComboBox>())
                    .Single(input => (string)input.Tag == "夹紧到位");
                Assert(selector.IsEnabled && selector.SelectedIndex == 1,
                    "故障时夹紧 DI 手动选择器未显示当前无效状态或不可操作。");
                selector.SelectedIndex = 0;
                var feedback = (TextBlock)window.FindName("CommandFeedback");
                Assert(device.ReadInput("夹紧到位") == true
                    && session.Machine.GetSnapshot().State == TaskMachineState.Faulted
                    && feedback.Text.Contains("故障暂停"),
                    "输入修复没有保持故障锁存或没有提示先复位再启动。");

                ClickButton(reset);
                Assert(session.Machine.GetSnapshot().State == TaskMachineState.Ready,
                    "输入修复后整机复位没有清除故障锁存。");
                ClickButton(start);
                WaitWithDispatcher(() => session.Machine.GetSnapshot().Stations.Single().Run?.Nodes
                        .Any(node => node.NodeId == "clamped" && node.State == TaskNodeState.Completed) == true,
                    5000, "夹紧 DI 修复后没有从故障步骤恢复并完成任务。");
                Assert(session.Machine.GetSnapshot().State != TaskMachineState.Faulted,
                    "夹紧 DI 修复后整机仍处于故障状态。");
                Console.WriteLine("[通过] 故障恢复：DI 超时定位、输入修复、故障锁存、复位启动后继续");
            }
            finally
            {
                if (window != null && window.IsVisible)
                {
                    window.Close();
                    WaitWithDispatcher(() => !window.IsVisible
                        && session.Machine.GetSnapshot().State == TaskMachineState.Closed,
                        30000, "故障恢复检查窗口关闭后，整机会话没有完成收尾。");
                }
                else session.Machine.CloseAsync().GetAwaiter().GetResult();
            }
        }

        /// <summary>验证初始化与四个工位在常见任务监控高度下默认可见，步骤区仍可操作。</summary>
        private static void ExerciseFiveStationLayout(Application application)
        {
            var task = Definition("LayoutCheck", Delay("delay", 10));
            var initializationTask = Definition("LayoutInitialization", Delay("initialize", 10));
            var configuration = new TaskMachineConfiguration
            {
                MachineId = "LayoutCheck", Name = "四工位布局检查", InitializationTaskId = initializationTask.TaskId,
                Stations = new[] { "上料", "压装", "检测", "下料" }.Select((name, index) =>
                    new StationTaskConfiguration { StationId = "station" + index, Name = name, TaskId = task.TaskId,
                        Enabled = true, Mode = StationTaskMode.Cyclic }).ToList()
            };
            var session = new TaskMonitorSession(configuration, new[] { initializationTask, task }, Device(), "布局检查", false);
            var view = new TaskMonitorView(session);
            var window = new Window { Content = view, Width = 1360, Height = 430, Opacity = 0, ShowInTaskbar = false };
            try
            {
                window.Show();
                application.Dispatcher.Invoke(new Action(() => { }), DispatcherPriority.ApplicationIdle);
                view.Refresh();
                view.UpdateLayout();
                var grid = (DataGrid)view.FindName("StationsGrid");
                var lastRow = grid.ItemContainerGenerator.ContainerFromIndex(4) as DataGridRow;
                Assert(grid.Items.Count == 5 && lastRow != null && lastRow.IsVisible,
                    "初始化和四个工位没有同时生成可见行。 ");
                var rowBottom = lastRow.TranslatePoint(new Point(0, lastRow.ActualHeight), grid).Y;
                Assert(rowBottom <= grid.ActualHeight - 1,
                    "任务监控默认高度下末工位被裁切，需要滚动才能看到。 ");
                var tree = (TreeView)view.FindName("StepsTree");
                var cause = (Button)view.FindName("LocateCurrentCauseButton");
                Assert(tree.ActualHeight >= 40 && cause.IsVisible,
                    "工位区调整后步骤列表或原因定位入口不可见。 ");
            }
            finally
            {
                window.Close();
                session.Machine.CloseAsync().GetAwaiter().GetResult();
            }
        }

        /// <summary>注入两个工位故障快照，验证现场选择与无匹配工位时的回退；不启动设备。</summary>
        private static void ExerciseMultipleStationFaultSelection(TaskMonitorView view, TaskMonitorSession session,
            DataGrid stationsGrid, Button locateCause)
        {
            // 通过故障列表注入稳定快照，避免依赖多个工位同时报错的线程时序。
            var field = typeof(TaskMachineController).GetField("_faults", BindingFlags.Instance | BindingFlags.NonPublic);
            var faults = (List<TaskMachineFault>)field.GetValue(session.Machine);
            var constructor = typeof(TaskMachineFault).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
                null, new[] { typeof(string), typeof(string), typeof(TaskExecutionException) }, null);
            faults.Add((TaskMachineFault)constructor.Invoke(new object[] { "EXTERNAL_STOP", "外部停机", null }));
            faults.Add((TaskMachineFault)constructor.Invoke(new object[] { "LOAD", "上料工位故障", null }));
            faults.Add((TaskMachineFault)constructor.Invoke(new object[] { "INSPECT", "检测工位故障", null }));
            try
            {
                view.Refresh();
                var inspect = stationsGrid.Items.OfType<TaskStationRow>().Single(row => row.Id == "inspect");
                stationsGrid.SelectedItem = inspect;
                locateCause.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, locateCause));
                Assert(((TaskStationRow)stationsGrid.SelectedItem).Id == "inspect"
                    && ((TextBlock)view.FindName("DetailNotice")).Text.Contains("检测工位故障"),
                    "多工位故障时没有优先定位当前选中工位。 ");

                stationsGrid.SelectedItem = stationsGrid.Items.OfType<TaskStationRow>().Single(row => row.Id == "transfer");
                locateCause.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, locateCause));
                Assert(((TaskStationRow)stationsGrid.SelectedItem).Id == "load"
                    && ((TextBlock)view.FindName("DetailNotice")).Text.Contains("上料工位故障"),
                    "全局报警先于工位故障时，没有回退到可定位的工位。 ");
                faults.RemoveRange(1, faults.Count - 1);
                view.Refresh();
                stationsGrid.SelectedItem = inspect;
                locateCause.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, locateCause));
                Assert(((TaskStationRow)stationsGrid.SelectedItem).Id == "inspect"
                    && ((TextBlock)view.FindName("DetailNotice")).Text.Contains("未关联具体工位"),
                    "全局报警没有明确提示无法定位到工位。 ");
                view.Refresh();
                Assert(((TextBlock)view.FindName("DetailNotice")).Text.Contains("未关联具体工位"),
                    "全局报警提示被定时刷新覆盖。 ");
                Assert(session.Machine.GetSnapshot().State == TaskMachineState.NeedsReset,
                    "查看故障意外改变了设备运行状态。 ");
            }
            finally
            {
                faults.Clear();
                view.Refresh();
                stationsGrid.SelectedIndex = 0;
            }
        }

        /// <summary>执行实际输入超时和报警事件，验证先产生报警代码时仍定位故障工位与步骤。</summary>
        private static void ExerciseTimeoutAlarmLocation(Application application)
        {
            var catalog = new TaskResourceCatalog(new TaskAxisInfo[0], new[] { "压装到位" }, new string[0],
                new Dictionary<string, DeviceVariableType>(), new TaskTeachPoint[0],
                alarmDefinitions: new[] { new TaskAlarmInfo("PRESS_TIMEOUT", "压装到位超时", MachineAlarmKind.StopMachine) });
            var device = new InMemoryTaskDevice(catalog);
            device.SetInput("压装到位", false);
            var initialization = Definition("FaultInitialization", Delay("initialize", 10));
            var load = Definition("FaultLoad", Delay("load", 10000));
            var press = Definition("FaultPress", new WaitInputNode { Id = "pressInput", Name = "等待压装到位",
                InputName = "压装到位", Expected = true, TimeoutMs = 400,
                TimeoutAlarmCodes = new List<string> { "PRESS_TIMEOUT" } });
            var configuration = new TaskMachineConfiguration
            {
                MachineId = "FaultLocation", Name = "多工位报警定位检查", InitializationTaskId = initialization.TaskId,
                Stations = new List<StationTaskConfiguration> {
                    new StationTaskConfiguration { StationId = "load", Name = "上料工位", TaskId = load.TaskId, Enabled = true, Mode = StationTaskMode.Once },
                    new StationTaskConfiguration { StationId = "press", Name = "压装工位", TaskId = press.TaskId, Enabled = true, Mode = StationTaskMode.Once } }
            };
            var session = new TaskMonitorSession(configuration, new[] { initialization, load, press }, device, "多工位故障检查", false);
            var view = new TaskMonitorView(session);
            var window = new Window { Content = view, Width = 1360, Height = 720, Opacity = 0, ShowInTaskbar = false };
            try
            {
                window.Show();
                application.Dispatcher.Invoke(new Action(() => { }), DispatcherPriority.ApplicationIdle);
                session.Machine.Reset();
                session.Machine.Start();
                WaitWithDispatcher(() => session.Machine.GetSnapshot().Faults.Any(fault => fault.Source == "press"),
                    5000, "压装超时没有写入工位故障。 ");
                var snapshot = session.Machine.GetSnapshot();
                Assert(snapshot.Faults.Count >= 2 && snapshot.Faults[0].Source == "PRESS_TIMEOUT",
                    "测试没有复现报警代码先于工位故障的快照顺序。 ");
                view.Refresh();
                var grid = (DataGrid)view.FindName("StationsGrid");
                grid.SelectedItem = grid.Items.OfType<TaskStationRow>().Single(row => row.Id == "__initialization");
                var locate = (Button)view.FindName("LocateCurrentCauseButton");
                locate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, locate));
                var selectedStep = ((TreeView)view.FindName("StepsTree")).SelectedItem as TaskStepRow;
                Assert(((TaskStationRow)grid.SelectedItem).Id == "press" && selectedStep?.Node.Id == "pressInput",
                    "从初始化工位定位压装超时时，没有跳到故障工位和步骤。 ");
            }
            finally
            {
                window.Close();
                session.Machine.CloseAsync().GetAwaiter().GetResult();
            }
        }

        /// <summary>由真实工位执行快照验证等待提示、未知采样和工程诊断可追溯。</summary>
        private static void ExerciseOperatorWaitDescriptions(Application application)
        {
            var catalog = new TaskResourceCatalog(new TaskAxisInfo[0], new string[0], new string[0],
                new Dictionary<string, DeviceVariableType> { { "Press.Request", DeviceVariableType.Boolean },
                    { "Press.Complete", DeviceVariableType.Boolean }, { "Load.Ready", DeviceVariableType.Boolean } },
                new TaskTeachPoint[0], alarmDefinitions: new[] { new TaskAlarmInfo("WAIT_TIMEOUT", "等待超时",
                    MachineAlarmKind.StopMachine, "检查条件。", "恢复后复位。") });
            var device = new InMemoryTaskDevice(catalog);
            device.SetVariable("Press.Request", "false"); device.SetVariable("Press.Complete", "false");
            device.SetVariable("Load.Ready", "false");
            var press = Definition("Press", Delay("work", 200));
            var load = Definition("Load", new WaitConditionNode { Id = "wait", Name = "等待压装工位完成", TimeoutMs = 30000,
                TimeoutAlarmCodes = new List<string> { "WAIT_TIMEOUT" }, Condition = new TaskCondition {
                    Kind = TaskConditionKind.Compare, Comparison = TaskComparison.Equal,
                    Left = new TaskValueBinding { Source = TaskValueSource.Variable, VariableName = "Load.Ready" }, Right = Boolean("true") } });
            var config = new TaskMachineConfiguration { MachineId = "OperatorWait", Name = "等待提示检查",
                Stations = new List<StationTaskConfiguration> {
                    new StationTaskConfiguration { StationId = "press", Name = "压装工位", TaskId = "Press", Mode = StationTaskMode.OnRequest,
                        RequestVariable = "Press.Request", CompletedVariable = "Press.Complete" },
                    new StationTaskConfiguration { StationId = "load", Name = "上料工位", TaskId = "Load", Mode = StationTaskMode.Once } } };
            var session = new TaskMonitorSession(config, new[] { press, load }, device, "等待提示检查", false);
            var view = new TaskMonitorView(session);
            try
            {
                session.Machine.Reset(); session.Machine.Start();
                WaitWithDispatcher(() => session.Machine.GetSnapshot().Stations.Any(station => station.StationId == "press" && station.State == StationTaskState.WaitingRequest)
                    && session.Machine.GetSnapshot().Stations.Any(station => station.StationId == "load" && station.Run?.Nodes.Any(node => node.NodeId == "wait" && node.Diagnostics != null) == true),
                    5000, "工位未进入等待状态。");
                view.Refresh();
                var grid = (DataGrid)view.FindName("StationsGrid");
                var pressRow = grid.Items.OfType<TaskStationRow>().Single(row => row.Id == "press");
                var loadRow = grid.Items.OfType<TaskStationRow>().Single(row => row.Id == "load");
                Assert(pressRow.Detail == "等待请求信号" && pressRow.EngineeringDetail.Contains("Press.Request=False"),
                    "工位请求提示没有简化，或原始采样丢失。");
                Assert(loadRow.Detail == "等待条件满足" && loadRow.EngineeringDetail.Contains("Load.Ready Equal"),
                    "条件提示没有简化，或原始比较诊断丢失。");
                grid.SelectedItem = loadRow; view.Refresh();
                Assert(((TextBlock)view.FindName("DetailSummary")).Text == "等待条件满足"
                    && ((DataGrid)view.FindName("ObservationTable")).Items.Count > 0,
                    "步骤详情未给出简洁提示，或维护采样表丢失。");
                device.SetVariable("Press.Request", null); device.SetVariable("Load.Ready", null);
                WaitWithDispatcher(() => session.Machine.GetSnapshot().Stations.Single(station => station.StationId == "press").Detail.Contains("=未知")
                    && session.Machine.GetSnapshot().Stations.Single(station => station.StationId == "load").Run.Nodes.Single(node => node.NodeId == "wait").Diagnostics.Decision == null,
                    5000, "未知信号没有反映到运行快照。");
                view.Refresh();
                Assert(pressRow.Detail.Contains("未知") && loadRow.Detail.Contains("未知"), "未知信号被误显示为普通等待或有效信号。");
                view.SetLanguage("en-US"); view.Refresh();
                Assert(pressRow.Detail.Contains("unknown") && loadRow.Detail.Contains("unknown"), "未知状态没有随语言切换。");
                view.SetLanguage("zh-CN"); device.SetVariable("Press.Request", "true");
                WaitWithDispatcher(() => session.Machine.GetSnapshot().Stations.Single(station => station.StationId == "press").Detail.StartsWith("已完成，等待请求方清除 "),
                    5000, "工位完成后没有进入等待请求复位阶段。");
                view.Refresh();
                Assert(pressRow.Detail == "本工位已完成，等待请求方复位信号"
                    && pressRow.EngineeringDetail.Contains("Press.Request"), "完成后的交接提示不准确或原始信号丢失。");
            }
            finally { session.Machine.CloseAsync().GetAwaiter().GetResult(); }
        }

        /// <summary>用于只读定位检查的未连接控制卡替身。</summary>
        private sealed class NoOpCardConnection : IAxisMappingCardConnection
        {
            public IReadOnlyList<MotionCardSelectionOption> SupportedMotionCards => new MotionCardSelectionOption[0];
            public bool IsConnected => false;
            public string ActualControllerType => null;
            public string ActualControllerVersion => null;
            public Task ConnectAsync(MotionCardSettingsDocument settings, CancellationToken cancellationToken) => Task.CompletedTask;
            public Task DisconnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            public Task ReconnectAsync(MotionCardSettingsDocument settings, CancellationToken cancellationToken) => Task.CompletedTask;
            public Task<AxisMappingCardParameters> ReadAxisParametersAsync(int axisNumber, CancellationToken cancellationToken)
                => Task.FromResult<AxisMappingCardParameters>(null);
            public IReadOnlyList<AxisHomeModeOption> GetSupportedHomeModes(MotionCardSelectionOption motionCard, int axisNumber)
                => new AxisHomeModeOption[0];
        }

        /// <summary>创建没有硬件对象的空目录设备。</summary>
        private static InMemoryTaskDevice Device()
        { return new InMemoryTaskDevice(new TaskResourceCatalog(new TaskAxisInfo[0], new string[0], new string[0], new Dictionary<string, DeviceVariableType>(), new TaskTeachPoint[0])); }
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
