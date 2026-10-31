using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Contracts.Safety;
using MotionControl.Framework.Runtime.Alarms;
using MotionControl.Framework.Runtime.Machine;
using MotionControl.Host.Wpf;
using MotionControl.Host.Wpf.Operation;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Editing;
using MotionControl.TaskEngine.Wpf.Editing;
using MotionControl.TaskEngine.Wpf.Simulation;

namespace MotionControl.OperatorPages.Checks
{
    /// <summary>四页真实控件和内存服务检查；不连接厂商 SDK，不代替桌面验收。</summary>
    internal static class Program
    {
        /// <summary>逐项运行并保留错误堆栈和非零失败退出码。</summary>
        [STAThread]
        private static int Main()
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/MotionControl.Host.Wpf;component/Localization/Strings.zh-CN.xaml", UriKind.Relative) });
            int failures = 0;
            Check("未知状态、真实Runtime来源和无硬件调用", RuntimeSource, ref failures);
            Check("暂停覆盖、任意数量工位和预览来源", OverviewStates, ref failures);
            Check("报警恢复待复位、搜索和稳定选择", AlarmConditions, ref failures);
            Check("报警状态与报警库恢复指导同时可见", AlarmGuidanceChecks.Run, ref failures);
            Check("Host导航和返回原页面", HostNavigation, ref failures);
            Check("工位版本、草稿与多轴参数独立保存", EditorIntegration, ref failures);
            Check("四页中英文和两种尺寸离屏布局", RenderWorkspaces, ref failures);
            Console.WriteLine("四个工作区内部检查：7 组，失败 " + failures + "。未连接控制器，不代表屏幕验收。");
            app.Shutdown();
            return failures == 0 ? 0 : 1;
        }
        /// <summary>统一输出每组结果，异常保留原始堆栈。</summary>
        private static void Check(string name, Action action, ref int failures)
        { try { action(); Console.WriteLine("[通过] " + name); } catch (Exception error) { failures++; Console.Error.WriteLine("[失败] " + name + "\n" + error); } }
        /// <summary>失败必须中止当前组，不能只打印提示。</summary>
        private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        /// <summary>只读页面可接真实 Runtime 对象，但绝不能调用卡的连接或动作入口。</summary>
        private static void RuntimeSource()
        {
            var overview = new EquipmentOverviewView(); var alarms = new CurrentAlarmsView();
            Assert(((TextBlock)overview.FindName("MachineStateText")).Text.Contains("未读取"), "没有来源却显示已就绪。");
            Assert(((TextBlock)overview.FindName("CompletedText")).Text == "未接入循环统计", "没有来源却显示产量零或含糊的连接状态。");
            Assert(((TextBlock)alarms.FindName("EmptyText")).Text == "未读取报警状态", "无来源被当作零报警。");
            var card = new GuardCard(); var service = new MachineAlarmService();
            var runtime = CreateRuntime(card, service);
            var notice = new MachineAlarmDefinition("NOTICE", "测试提醒", MachineAlarmKind.NonStop);
            service.ReportCondition(notice, "测试工位", true, "测试条件");
            overview.SetRuntime(runtime); alarms.SetRuntime(runtime);
            Assert(((DataGrid)alarms.FindName("AlarmGrid")).Items.Count == 1, "没有读取当前Runtime报警。");
            Assert(((TextBlock)overview.FindName("SourceText")).Text == "状态来源：设备运行数据", "设备总览显示了内部 Runtime 名称。");
            Assert(((TextBlock)alarms.FindName("SourceText")).Text.StartsWith("状态来源：设备运行数据"), "当前报警显示了内部 Runtime 名称。");
            service.ReportCondition(notice, "测试工位", false, null); alarms.RefreshDisplay();
            Assert(((DataGrid)alarms.FindName("AlarmGrid")).Items.Count == 0, "非停机提醒恢复后没有消失。");
            overview.SetRuntime(null); alarms.SetRuntime(null);
            Assert(((ItemsControl)overview.FindName("StationCards")).Items.Count == 0 && card.Calls == 0, "断开来源残留工位或页面调用了卡。");
        }
        /// <summary>暂停卡片不能继续显示运行，超过三工位仍完整呈现。</summary>
        private static void OverviewStates()
        {
            var snapshot = FourWorkspacesPreviewWindow.CreateExampleSnapshot(); snapshot.State = MachineRunState.Paused;
            for (int i = 3; i < 12; i++) snapshot.Stations.Add(new StationOverviewData { Name = "附加工位" + i, CurrentStep = "保留当前动作" });
            var view = new EquipmentOverviewView(); view.SetPreview(snapshot);
            Assert(((ItemsControl)view.FindName("StationCards")).Items.Count == 12, "卡片数量被固定为三工位。");
            Assert(((TextBlock)view.FindName("MachineStateText")).Text == "设备已暂停", "整机暂停标签错误。");
            Assert(!FindControls<TextBlock>(view).Any(text => text.Text == "整机状态 · 设备运行中"), "暂停后卡片仍标为运行。");
            Assert(FindControls<TextBlock>(view).Any(text => text.Text.Contains("40 XY")), "暂停丢失原动作文字。");
            view.SetLanguage("en-US"); Assert(((TextBlock)view.FindName("SourceText")).Text == "Offline UI example", "英文来源标签未切换。");
            bool rejected = false; try { view.SetPreview(new OperationSnapshot()); } catch (ArgumentException) { rejected = true; }
            Assert(rejected, "未标记的快照可伪装成预览。");
        }
        /// <summary>界面只观察报警服务，刷新不清除锁存，状态变化仍选择同一次报警。</summary>
        private static void AlarmConditions()
        {
            var service = new MachineAlarmService();
            var stop = new MachineAlarmDefinition("STOP", "测试停机报警", MachineAlarmKind.StopMachine);
            service.ReportCondition(stop, "测试工位", true, "先检查测试输入");
            var snapshot = new OperationSnapshot { IsPreview = true, HasRuntime = true, State = MachineRunState.Alarmed, SampledAt = DateTime.Now, Alarms = service.Alarms.ToList() };
            snapshot.Stations.Add(new StationOverviewData { Name = "测试工位", CurrentStep = "20 等待输入" });
            var view = new CurrentAlarmsView(); view.SetPreview(snapshot);
            var grid = (DataGrid)view.FindName("AlarmGrid"); var first = (AlarmDisplayRow)grid.SelectedItem;
            service.ReportCondition(stop, "测试工位", false, null); snapshot.Alarms = service.Alarms.ToList(); view.RefreshDisplay();
            var recovered = (AlarmDisplayRow)grid.SelectedItem;
            Assert(first.Key == recovered.Key && recovered.ConditionText.Contains("等待复位"), "条件恢复后丢失选择或不显示待复位。");
            FindButton(view, "刷新").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(service.HasLatchedStopAlarm, "刷新居然清除了停机报警。");
            ((TextBox)view.FindName("SearchBox")).Text = "stop";
            Assert(grid.Items.Count == 1, "代码搜索未按大小写无关匹配。");
            ((TextBox)view.FindName("SearchBox")).Text = "没有的内容";
            Assert(grid.Items.Count == 0 && !((Button)view.FindName("AxisButton")).IsEnabled, "空搜索结果仍有可操作报警。");
            ((TextBox)view.FindName("SearchBox")).Text = ""; view.SetLanguage("en-US");
            Assert(((AlarmDisplayRow)grid.SelectedItem).Key == first.Key && ((AlarmDisplayRow)grid.SelectedItem).ConditionText.Contains("reset"), "语言切换丢失选择或待复位语义。");
            OperatorNavigationEventArgs requested = null; view.NavigationRequested += (sender, e) => requested = e;
            ((Button)view.FindName("IoButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(requested != null && requested.Page == "IO监控" && service.HasLatchedStopAlarm, "只读导航改变了报警状态。");
        }
        /// <summary>验证 Host 导航不重复 IO 监控入口，并保留原内容区导航。</summary>
        private static void HostNavigation()
        {
            var card = new GuardCard(); var runtime = CreateRuntime(card, new MachineAlarmService());
            var window = new MainWindow();
            try
            {
                var primaryNavigation = (StackPanel)window.FindName("PrimaryNavigation");
                var sidebar = (StackPanel)window.FindName("SidebarNavigation");
                Assert(!sidebar.Children.OfType<ToggleButton>().Any(button => Equals(button.Tag, "IO状态")),
                    "首页重复列出了独立的 IO 状态页。");
                var monitorButton = primaryNavigation.Children.OfType<ToggleButton>()
                    .Single(button => Equals(button.Tag, "Monitor"));
                monitorButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert(sidebar.Children.OfType<ToggleButton>().Count(button => Equals(button.Tag, "IO监控")) == 1,
                    "监控区没有唯一的 IO 监控入口。");
                var homeButton = primaryNavigation.Children.OfType<ToggleButton>()
                    .Single(button => Equals(button.Tag, "Home"));
                homeButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

                window.SetFlowStatusPage(new FlowStatusView(runtime)); window.SetMachineRuntime(runtime);
                var content = (ContentControl)window.FindName("ManualDebugContent");
                var original = content.Content as EquipmentOverviewView;
                Assert(original != null, "首页仍是占位。");
                FindButton(original, "查看流程").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert(content.Content is FlowStatusView, "卡片没有定位实际流程页。");
                ((Button)window.FindName("OperationReturnButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert(ReferenceEquals(content.Content, original), "返回重建了原总览。");
                FindButton(original, "查看当前报警").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert(content.Content is CurrentAlarmsView && card.Calls == 0, "报警导航失败或触发了控制卡操作。");
            }
            finally { window.Close(); }
        }
        /// <summary>版本与草稿来自实际文件，多轴表只显示选中轴参数，切换后逐轴保存。</summary>
        private static void EditorIntegration()
        {
            using (var test = new PreviewFiles())
            {
                var page = new TaskConfigurationView(test.Configuration, "TaskEditorPreview", () => null);
                var library = (DataGrid)page.FindName("TasksGrid");
                Assert(library.Items.Cast<TaskLibraryRow>().All(row => row.AppliedVersion == "R1" && row.DraftState == "无修改"), "文件版本或无修改状态错误。");
                Render(page, "station-row-navigation", 1200, 780);
                var stations = (DataGrid)page.FindName("StationsGrid");
                var cell = stations.Columns[5].GetCellContent(stations.Items[1]);
                var edit = (Button)VisualTreeHelper.GetChild(cell, 0);
                edit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var editor = (TaskEditorView)((ContentControl)page.FindName("ContentHost")).Content;
                Assert(editor.Definition.TaskId == "Transfer", "工位行内按钮没有打开其指定任务。");
                ((TextBox)editor.FindName("TaskNameBox")).Text = "未保存的搬运任务";
                Assert(page.OpenTaskById("Transfer") && ReferenceEquals(((ContentControl)page.FindName("ContentHost")).Content, editor), "再次定位同一任务重新创建了编辑器。");
                Assert(((TextBox)editor.FindName("TaskNameBox")).Text == "未保存的搬运任务", "再次定位同一任务丢失输入。");
                var group = AllNodes(editor.Definition.Root).OfType<MoveAxisGroupNode>().First(); editor.FocusNode(group.Id);
                var grid = FindControls<DataGrid>(editor).First(item => item.Columns.Count == 4 && (string)item.Columns[0].Header == "逻辑轴");
                Assert(grid.Items.Count == group.Targets.Count && grid.Items.Count >= 2, "多轴目标表没有完整读取轴。");
                grid.SelectedIndex = 1;
                var second = FindControls<Expander>(editor).Single(item => item.Visibility == Visibility.Visible);
                SpeedBox(second).Text = "7";
                grid.SelectedIndex = 0;
                var first = FindControls<Expander>(editor).Single(item => item.Visibility == Visibility.Visible);
                SpeedBox(first).Text = "5";
                Assert(editor.SaveDraft(), "多轴参数保存失败。");
                var saved = test.Store.Load("TaskEditorPreview");
                var savedGroup = AllNodes(saved.Tasks.Single(task => task.Definition.TaskId == "Transfer").Definition.Root).OfType<MoveAxisGroupNode>().First();
                Assert(savedGroup.Targets[0].Profile.Speed == 5 && savedGroup.Targets[1].Profile.Speed == 7, "切换轴后参数串行或丢失。");
                Assert(((TextBlock)editor.FindName("DirtyIndicator")).Visibility == Visibility.Collapsed, "保存后仍显示未保存标记。");
                Assert(page.ReturnToConfiguration(), "已保存任务无法返回配置页。");
                var libraryRow = library.Items.Cast<TaskLibraryRow>().Single(item => item.Definition.TaskId == "Transfer");
                Assert(libraryRow.AppliedVersion == "R1" && libraryRow.DraftState == "草稿待检查", "保存草稿冒充了已应用版本或已通过检查。");
                FindButton(page, "保存并检查").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert(library.Items.Cast<TaskLibraryRow>().Single(item => item.Definition.TaskId == "Transfer").DraftState == "任务检查通过", "检查结果没有更新任务行。");
                var machineDetails = (StackPanel)page.FindName("MachineDetails");
                machineDetails.Children.OfType<TextBox>().First().Text += " 修改";
                Assert(library.Items.Cast<TaskLibraryRow>().All(item => item.DraftState != "任务检查通过"), "修改后仍保留旧检查通过状态。");
            }
        }
        /// <summary>只按工程字段标签找到当前轴的速度框，不反射读取私有状态。</summary>
        private static TextBox SpeedBox(Expander expander)
        {
            foreach (var panel in FindControls<StackPanel>(expander))
                for (int i = 0; i + 1 < panel.Children.Count; i++)
                    if (panel.Children[i] is TextBlock label && label.Text == "速度") return (TextBox)panel.Children[i + 1];
            throw new InvalidOperationException("当前轴速度框未找到。");
        }
        /// <summary>两种语言和尺寸导出布局补充图，图片明确不是桌面截图。</summary>
        private static void RenderWorkspaces()
        {
            using (var test = new PreviewFiles())
            {
                foreach (string language in new[] { "zh-CN", "en-US" })
                {
                    var overview = new EquipmentOverviewView(); overview.SetPreview(FourWorkspacesPreviewWindow.CreateExampleSnapshot()); overview.SetLanguage(language);
                    var alarms = new CurrentAlarmsView(); alarms.SetPreview(FourWorkspacesPreviewWindow.CreateExampleSnapshot()); alarms.SetLanguage(language);
                    var configuration = new TaskConfigurationView(test.Configuration, "TaskEditorPreview", () => null); configuration.SetLanguage(language);
                    foreach (int width in new[] { 1200, 824 })
                    {
                        int height = width == 1200 ? 780 : 550;
                        Render(overview, "overview-" + language + "-" + width, width, height);
                        Render(alarms, "alarms-" + language + "-" + width, width, height);
                        Render(configuration, "configuration-" + language + "-" + width, width, height);
                    }
                    Assert(configuration.OpenTaskById("Transfer"), "编辑预览入口失败。");
                    var editor = (TaskEditorView)((ContentControl)configuration.FindName("ContentHost")).Content;
                    editor.FocusNode(AllNodes(editor.Definition.Root).OfType<MoveAxisGroupNode>().First().Id);
                    Render(configuration, "editor-" + language + "-1200", 1200, 780);
                    Render(configuration, "editor-" + language + "-824", 824, 550);
                    Assert(!editor.IsDirty, "只显示多轴目标就误标未保存。");
                }
            }
        }
        /// <summary>离屏排空布局后渲染，不能作为实际鼠标和焦点验收。</summary>
        private static void Render(FrameworkElement view, string name, int width, int height)
        {
            view.Measure(new Size(width, height)); view.Arrange(new Rect(0, 0, width, height)); view.UpdateLayout();
            var frame = new System.Windows.Threading.DispatcherFrame();
            view.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            view.Measure(new Size(width, height)); view.Arrange(new Rect(0, 0, width, height)); view.UpdateLayout();
            var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); image.Render(view);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Previews"); Directory.CreateDirectory(directory);
            using (var file = File.Create(Path.Combine(directory, name + ".png"))) encoder.Save(file);
        }
        /// <summary>按实际控件内容查找按钮。</summary>
        private static Button FindButton(DependencyObject root, string text)
        { return FindControls<Button>(root).First(button => button.Content as string == text); }
        /// <summary>递归检查具体控件，不访问私有字段或执行动态脚本。</summary>
        private static IEnumerable<T> FindControls<T>(DependencyObject root) where T : DependencyObject
        {
            if (root is T control) yield return control;
            foreach (object child in LogicalTreeHelper.GetChildren(root))
                if (child is DependencyObject node) foreach (var item in FindControls<T>(node)) yield return item;
        }
        /// <summary>沿已定义的容器关系读取节点。</summary>
        private static IEnumerable<TaskNode> AllNodes(TaskNode node)
        {
            yield return node;
            IEnumerable<TaskNode> children = new TaskNode[0];
            if (node is SequenceNode sequence) children = sequence.Children;
            else if (node is ParallelGroupNode parallel) children = parallel.Branches;
            else if (node is ResourceScopeNode scope) children = new[] { scope.Body };
            else if (node is ConditionBranchNode branch) children = new[] { branch.WhenTrue, branch.WhenFalse };
            foreach (var child in children.Where(item => item != null)) foreach (var item in AllNodes(child)) yield return item;
        }
        /// <summary>Runtime 仅供观察，卡和安全入口都带调用保护。</summary>
        private static MachineRuntime CreateRuntime(GuardCard card, MachineAlarmService service)
        { return new MachineRuntime(new MotionCardGroup(new IMotionCard[] { card }), new IStationFlow[] { new ReadOnlyFlow() }, service, new GuardSafety()); }
    }

    /// <summary>测试观察页不能访问的卡入口；任何调用都会失败并计数。</summary>
    internal sealed class GuardCard : IMotionCard
    {
        /// <summary>记录所有卡生命周期调用。</summary>
        public int Calls { get; private set; }
        /// <summary>检查专用卡标识。</summary>
        public string ControllerId { get { return "ReadOnlyCheck"; } }
        /// <summary>不建立连接。</summary>
        public bool IsConnected { get { return false; } }
        /// <summary>无需任何实轴。</summary>
        public IReadOnlyCollection<IAxis> Axes { get { return new IAxis[0]; } }
        /// <summary>页面若意外连接立即失败。</summary>
        public Task ConnectAsync(CancellationToken token) { Calls++; throw new InvalidOperationException("页面禁止连接检查卡。"); }
        /// <summary>页面若意外断开立即失败。</summary>
        public Task DisconnectAsync(CancellationToken token) { Calls++; throw new InvalidOperationException("页面禁止断开检查卡。"); }
    }
    /// <summary>只读检查不应启动安全输入轮询。</summary>
    internal sealed class GuardSafety : IMachineSafetyStatus
    {
        /// <summary>意外读取也会令检查失败。</summary>
        public Task<bool> ReadEmergencyStopActiveAsync(CancellationToken token) { throw new InvalidOperationException("只读页面不应读取安全硬件。"); }
    }
    /// <summary>有明确名称和当前动作，但绝不执行的测试工位。</summary>
    internal sealed class ReadOnlyFlow : IStationFlow
    {
        /// <summary>用于报警和导航的稳定名称。</summary>
        public string StationName { get { return "测试工位"; } }
        /// <summary>当前动作原文。</summary>
        public string CurrentStepName { get { return "20 等待测试输入"; } }
        /// <summary>页面禁止运行流程。</summary>
        public Task ExecuteCurrentStepAsync(CancellationToken token) { throw new InvalidOperationException("页面不应执行流程。"); }
        /// <summary>页面禁止复位流程。</summary>
        public Task ResetAsync(CancellationToken token) { throw new InvalidOperationException("页面不应复位流程。"); }
    }
    /// <summary>本轮检查使用独立临时配置目录。</summary>
    internal sealed class PreviewFiles : IDisposable
    {
        /// <summary>当前检查绝对目录。</summary>
        private readonly string _root;
        /// <summary>实际 JSON 存储。</summary>
        internal MotionControl.Framework.Runtime.Configuration.MachineConfigurationStore Configuration { get; private set; }
        /// <summary>实际草稿服务。</summary>
        internal TaskWorkspaceStore Store { get; private set; }
        /// <summary>复制现有内存示例定义，不连接任何控制器。</summary>
        internal PreviewFiles()
        { _root = Path.Combine(Path.GetTempPath(), "MotionControlOperatorChecks", Guid.NewGuid().ToString("N")); Configuration = TaskEditingPreviewWindow.CreatePreviewStore(_root); Store = new TaskWorkspaceStore(Configuration); }
        /// <summary>递归清理前确认路径处于本测试专用父目录内。</summary>
        public void Dispose()
        {
            string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MotionControlOperatorChecks")) + Path.DirectorySeparatorChar;
            string target = Path.GetFullPath(_root);
            if (!target.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || target.Length <= parent.Length) throw new InvalidOperationException("测试清理路径超出专用目录。");
            if (Directory.Exists(target)) Directory.Delete(target, true);
        }
    }
}
