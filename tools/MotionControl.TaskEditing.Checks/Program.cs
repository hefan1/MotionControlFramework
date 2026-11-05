using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.TaskEngine.Configuration;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Editing;
using MotionControl.TaskEngine.Validation;
using MotionControl.TaskEngine.Wpf.Editing;
using MotionControl.TaskEngine.Wpf.Simulation;
using Newtonsoft.Json.Linq;

namespace MotionControl.TaskEditing.Checks
{
    /// <summary>有实际文件与 WPF 控件的内部回归，不连接控制器，不代替桌面屏幕验收。</summary>
    internal static partial class Program
    {
        /// <summary>保留明确失败信息和退出状态。</summary>
        [STAThread]
        private static int Main()
        {
            new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            int failed = 0;
            Check("草稿完整保存、无效引用保留与运行文件隔离", DraftIsolation, ref failed);
            Check("应用校验、版本递增与外部修改冲突", ApplyAndConflict, ref failed);
            Check("中途写入失败恢复全部原字节", FailedApplyRollback, ref failed);
            Check("意外退出标记阻止加载并恢复", InterruptedApplyRecovery, ref failed);
            Check("路径、未知字段和缺失版本拒绝", InvalidDraft, ref failed);
            Check("分类表单、复制ID和保存交互", EditingControls, ref failed);
            Check("中英文及两种尺寸离屏布局", Layouts, ref failed);
            Check("多窗口草稿冲突与本地副本", DraftWindowConflicts, ref failed);
            Check("来源变化时保留草稿、备份和多条报警关联", DraftSourceRebase, ref failed);
            Check("外部任务备份导出、恢复和设备校验", ExternalBackupRoundTrip, ref failed);
            Check("大小写无关引用完整保留", CaseInsensitiveReferences, ref failed);
            Check("空组合条件往返与取反条件重建", ConditionRoundTrip, ref failed);
            Check("工位任务选择与草稿副本交互", ConfigurationContext, ref failed);
            Check("无效参数不部分修改任务模型", InvalidFieldsKeepDefinition, ref failed);
            Check("任务检查字段显示为易读名称", ReadableIssueFields, ref failed);
            Check("任务库筛选及引用保护", V14TaskLibrarySearchChecks.Run, ref failed);
            Check("任务草稿、文件与会话来源", V14TaskSourceSummaryChecks.Run, ref failed);
            Console.WriteLine("任务编辑内部检查：17 组，失败 " + failed + "。未连接控制器；不代表实际屏幕验证。");
            return failed == 0 ? 0 : 1;
        }
        /// <summary>逐项执行并保留异常堆栈。</summary>
        private static void Check(string name, Action action, ref int failed)
        { try { action(); Console.WriteLine("[通过] " + name); } catch (Exception error) { failed++; Console.Error.WriteLine("[失败] " + name + "\n" + error); } }
        /// <summary>失败断言必须使检查返回失败。</summary>
        private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        /// <summary>只有确实抛出异常才算拒绝无效操作。</summary>
        private static void Throws(Action action) { bool thrown = false; try { action(); } catch { thrown = true; } Assert(thrown, "应拒绝的操作没有报错。"); }
        /// <summary>草稿可保存未完成引用，运行文件必须完全不变。</summary>
        private static void DraftIsolation()
        {
            using (var test = new Workspace())
            {
                var draft = test.Store.Load(Workspace.Id); var before = test.ActiveBytes();
                draft.Tasks[0].Definition.Name = "修改草稿";
                ((SequenceNode)draft.Tasks[0].Definition.Root).Children.Add(new WaitInputNode { Id = "missing", Name = "无效输入", InputName = "Missing", Expected = true });
                test.Store.SaveDraft(draft); var loaded = test.Store.Load(Workspace.Id);
                Assert(loaded.Tasks[0].Definition.Name == "修改草稿" && test.Store.Check(loaded).Any(issue => issue.Severity == TaskIssueSeverity.Error), "未保留可修正的草稿错误。");
                Assert(before == test.ActiveBytes(), "保存草稿修改了运行文件。");
                Throws(() => { string warning; test.Store.Apply(loaded, out warning); });
                Assert(before == test.ActiveBytes(), "校验失败仍修改了运行文件。");
                test.Store.DiscardDraft(Workspace.Id); Assert(test.Store.Load(Workspace.Id).Tasks[0].Definition.Name != "修改草稿", "放弃草稿未重载运行定义。");
            }
        }
        /// <summary>只在内容变化时升级版本，外部配置变化后禁止旧草稿覆盖。</summary>
        private static void ApplyAndConflict()
        {
            using (var test = new Workspace())
            {
                var draft = test.Store.Load(Workspace.Id); var item = draft.Tasks[0]; int revision = item.Definition.Revision;
                item.Definition.Name += " v2"; string warning; var applied = test.Store.Apply(draft, out warning);
                Assert(applied.Tasks[0].Definition.Revision == revision + 1 && string.IsNullOrEmpty(warning), "修改未正确升级版本。");
                var unchanged = test.Store.Apply(applied, out warning); Assert(unchanged.Tasks[0].Definition.Revision == revision + 1, "无变化仍重复升级。");
                File.AppendAllText(Path.Combine(test.Root, Workspace.Id, "HardwareConfig/IoMapping.json"), " ");
                Throws(() => test.Store.Apply(unchanged, out warning));
            }
        }
        /// <summary>制造第二个任务目标路径冲突，验证第一个已写文件也被回滚。</summary>
        private static void FailedApplyRollback()
        {
            using (var test = new Workspace())
            {
                var draft = test.Store.Load(Workspace.Id); string before = test.ActiveBytes(); draft.Tasks[0].Definition.Name += " changed";
                draft.Tasks.Add(new TaskWorkspaceTask { FileName = "Blocked.json", Definition = TaskDefinitionJson.Deserialize(TaskDefinitionJson.Serialize(draft.Tasks[0].Definition)) });
                draft.Tasks.Last().Definition.TaskId = "Blocked";
                Directory.CreateDirectory(Path.Combine(test.Root, Workspace.Id, "Tasks/Blocked.json"));
                string warning; Throws(() => test.Store.Apply(draft, out warning));
                Assert(before == test.ActiveBytes(), "应用失败后没有恢复全部原字节。");
                Assert(!test.Store.HasPendingApply(Workspace.Id), "正常回滚后残留恢复标记。");
            }
        }
        /// <summary>模拟写入后进程退出，恢复前不得打开任务会话。</summary>
        private static void InterruptedApplyRecovery()
        {
            using (var test = new Workspace())
            {
                string root = Path.Combine(test.Root, Workspace.Id), id = Guid.NewGuid().ToString("N");
                var initial = test.Store.Load(Workspace.Id); var catalog = test.Store.LoadCatalog(initial);
                string relative = "Tasks/Load.json", source = Path.Combine(root, relative), before = File.ReadAllText(source);
                string backup = Path.Combine(root, "Backups/TaskApply-" + id, relative); Directory.CreateDirectory(Path.GetDirectoryName(backup)); File.Copy(source, backup);
                File.WriteAllText(source, "incomplete");
                test.Configuration.SaveJson(Workspace.Id, "Drafts/TaskApplyPending.json", new { machineId = Workspace.Id, backupId = id, files = new[] { new { relativePath = relative, existed = true } } });
                Throws(() => test.Store.Load(Workspace.Id)); Throws(() => TaskMonitorSession.LoadMachine(test.Configuration, Workspace.Id));
                Throws(() => new TaskMachineConfigurationStore(test.Configuration).Load(Workspace.Id, initial.Tasks.Select(item => item.Definition), catalog));
                Throws(() => new TaskDefinitionStore(test.Configuration).Load(Workspace.Id, "Camera.json", catalog));
                test.Store.RecoverPendingApply(Workspace.Id); Assert(File.ReadAllText(source) == before && !test.Store.HasPendingApply(Workspace.Id), "意外退出恢复失败。");
            }
        }
        /// <summary>草稿与恢复标记均不能越出指定任务目录。</summary>
        private static void InvalidDraft()
        {
            using (var test = new Workspace())
            {
                var draft = test.Store.Load(Workspace.Id); draft.Tasks[0].FileName = "../escape.json"; Throws(() => test.Store.SaveDraft(draft));
                draft = test.Store.Load(Workspace.Id, false); test.Store.SaveDraft(draft);
                string path = Path.Combine(test.Root, Workspace.Id, "Drafts/TaskWorkspace.json"); var document = JObject.Parse(File.ReadAllText(path)); document.Remove("schemaVersion"); File.WriteAllText(path, document.ToString()); Throws(() => test.Store.Load(Workspace.Id));
                document["schemaVersion"] = 1; document["unknown"] = true; File.WriteAllText(path, document.ToString()); Throws(() => test.Store.Load(Workspace.Id));
                test.Configuration.SaveJson(Workspace.Id, "Drafts/TaskApplyPending.json", new { machineId = Workspace.Id, backupId = Guid.NewGuid().ToString("N"), files = new[] { new { relativePath = "../outside.json", existed = false } } });
                Throws(() => test.Store.RecoverPendingApply(Workspace.Id)); Assert(test.Store.HasPendingApply(Workspace.Id), "无效恢复标记被清除。");
                string before = test.ActiveBytes();
                test.Configuration.SaveJson(Workspace.Id, "Drafts/TaskApplyPending.json", new { machineId = Workspace.Id, backupId = Guid.NewGuid().ToString("N"), files = new[] { new { relativePath = "Tasks/Load.json" } } });
                Throws(() => test.Store.RecoverPendingApply(Workspace.Id)); Assert(before == test.ActiveBytes(), "缺少存在性字段时不应默认删除原任务。");
            }
        }
        /// <summary>建立真实编辑控件并测试保存回调和复制后的新节点 ID。</summary>
        private static void EditingControls()
        {
            using (var test = new Workspace())
            {
                var draft = test.Store.Load(Workspace.Id); var entry = draft.Tasks.Single(item => item.Definition.TaskId == "Transfer");
                var editor = new TaskEditorView(entry.Definition, test.Store.LoadCatalog(draft), () => test.Store.LoadCatalog(draft), draft.Tasks.Where(item => item != entry).Select(item => item.Definition));
                editor.DraftSaved += (sender, e) => { entry.Definition = editor.Definition; test.Store.SaveDraft(draft); };
                foreach (string id in AllNodes(editor.Definition.Root).Select(node => node.Id).ToArray()) { editor.FocusNode(id); Assert(((TaskNode)((TreeViewItem)((TreeView)editor.FindName("StepsTree")).SelectedItem).Tag).Id == id, "字段读取错误阻止了节点选择：" + id); }
                editor.FocusNode("place"); var ids = AllNodes(editor.Definition.Root).Select(node => node.Id).ToArray();
                FindButton(editor, "复制").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var after = AllNodes(editor.Definition.Root).Select(node => node.Id).ToArray();
                Assert(after.Length > ids.Length && after.Distinct().Count() == after.Length && ids.All(after.Contains), "复制重用了 ID 或改动了原 ID。");
                ((TextBox)editor.FindName("TaskNameBox")).Text = "编辑控件保存";
                Assert(editor.SaveDraft(), "编辑器保存失败。"); Assert(test.Store.Load(Workspace.Id).Tasks.Single(item => item.Definition.TaskId == "Transfer").Definition.Name == "编辑控件保存", "保存未进入完整草稿包。");
                foreach (var task in draft.Tasks)
                {
                    var view = new TaskEditorView(task.Definition, test.Store.LoadCatalog(draft), () => test.Store.LoadCatalog(draft), draft.Tasks.Where(item => item != task).Select(item => item.Definition));
                    foreach (var node in AllNodes(view.Definition.Root).ToArray()) view.FocusNode(node.Id);
                }
                var extra = TaskDefinitionJson.Deserialize(TaskDefinitionJson.Serialize(entry.Definition));
                ((SequenceNode)extra.Root).Children.Add(new AxisCommandNode { Id = "home", Name = "回零", AxisName = "搬运X", Command = TaskAxisCommand.Home });
                ((SequenceNode)extra.Root).Children.Add(new AxisCommandNode { Id = "stop", Name = "停轴", AxisName = "搬运X", Command = TaskAxisCommand.Stop, StopMode = MotionControl.Framework.Contracts.Motion.AxisStopMode.Controlled });
                ((SequenceNode)extra.Root).Children.Add(new ReadInputNode { Id = "sample", Name = "采样输入", InputName = "产品到位", VariableName = "Vision.OK" });
                ((SequenceNode)extra.Root).Children.Add(new WaitExclusiveInputsNode { Id = "pair", Name = "等待互斥输入", FirstInputName = "产品到位", SecondInputName = "产品到位", FirstVariableName = "Vision.OK", SecondVariableName = "Vision.OK", TimeoutAlarmCodes = new List<string>() });
                ((SequenceNode)extra.Root).Children.Add(new WaitOutputNode { Id = "wait-output", Name = "等待输出", OutputName = "夹具夹紧", Expected = false });
                ((SequenceNode)extra.Root).Children.Add(new WaitConditionNode { Id = "condition", Name = "等待变量", Condition = new TaskCondition { Kind = TaskConditionKind.All, Conditions = new List<TaskCondition> { new TaskCondition { Kind = TaskConditionKind.Compare, Comparison = TaskComparison.Equal, Left = new TaskValueBinding { Source = TaskValueSource.Variable, VariableName = "Vision.OK" }, Right = new TaskValueBinding { Source = TaskValueSource.Constant, DataType = MotionControl.Framework.Contracts.Variables.DeviceVariableType.Boolean, Constant = "true" } } } } });
                var allTypes = new TaskEditorView(extra, test.Store.LoadCatalog(draft), () => test.Store.LoadCatalog(draft), draft.Tasks.Where(item => item.Definition.TaskId != extra.TaskId).Select(item => item.Definition));
                foreach (var node in AllNodes(extra.Root).ToArray()) { allTypes.FocusNode(node.Id); Assert(((TaskNode)((TreeViewItem)((TreeView)allTypes.FindName("StepsTree")).SelectedItem).Tag).Id == node.Id, "分类表单未正确读取：" + node.Kind); }
                allTypes.FocusNode("condition"); FindButton(allTypes, "添加条件").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert(allTypes.IsDirty, "增加子条件未记录未保存状态。");
            }
        }
        /// <summary>渲染只是离屏布局补充；输出保存在忽略的检查 bin 目录。</summary>
        private static void Layouts()
        {
            using (var test = new Workspace())
            {
                var page = new TaskConfigurationView(test.Configuration, Workspace.Id, () => "测试会话仍占用");
                Render(page, "configuration-zh", 1200, 780);
                Assert(!((TextBlock)page.FindName("StatusText")).Text.Contains("有未保存"), "仅渲染页面就误标未保存。");
                Assert(((DataGrid)page.FindName("StationsGrid")).Columns.All(column => !string.IsNullOrEmpty(column.Header as string)), "表头没有正确加载局部资源。");
                Assert(FindButton(page, "保存并检查") != null, "任务配置页缺少合并后的保存检查入口。");
                Assert(FindButton(page, "备份与恢复") != null, "任务配置页缺少备份与恢复入口。");
                Assert(FindButton(page, "更新任务文件") != null, "任务配置页没有显示更新任务文件入口。");
                Assert(FindButton(page, "保留草稿副本") == null, "单独的草稿副本按钮仍占据日常工具栏。");
                Assert(((Button)page.FindName("RecoverButton")).Visibility == Visibility.Collapsed, "无异常恢复记录时仍显示恢复按钮。");
                page.SetLanguage("en-US"); Render(page, "configuration-en-small", 824, 550);
                Assert(FindButton(page, "Save and check") != null, "英语界面没有更新保存检查按钮文字。");
                var backupDialog = new TaskBackupActionsWindow("zh-CN");
                Render((FrameworkElement)backupDialog.Content, "backup-actions-zh", 470, 390);
                Assert(FindButton(backupDialog, "导出任务配置备份...") != null && FindButton(backupDialog, "从备份恢复为草稿...") != null,
                    "备份管理窗口缺少明确的导出和恢复入口。");
                var backupDialogEn = new TaskBackupActionsWindow("en-US");
                Render((FrameworkElement)backupDialogEn.Content, "backup-actions-en", 470, 390);
                Assert(FindButton(backupDialogEn, "Export task configuration backup...") != null,
                    "备份管理窗口未按英语语言切换按钮文字。");
                var readyPage = new TaskConfigurationView(test.Configuration, Workspace.Id, () => null);
                Render(readyPage, "configuration-ready-zh", 1200, 780);
                Assert(((Button)readyPage.FindName("ApplyButton")).IsEnabled, "会话空闲且草稿可用时，更新任务文件按钮仍被禁用。");
                var draft = test.Store.Load(Workspace.Id); var task = draft.Tasks.Single(item => item.Definition.TaskId == "Inspect");
                var editor = new TaskEditorView(task.Definition, test.Store.LoadCatalog(draft), () => test.Store.LoadCatalog(draft), draft.Tasks.Where(item => item != task).Select(item => item.Definition));
                editor.FocusNode("judge"); Render(editor, "editor-condition-zh", 1200, 780);
                editor.SetLanguage("en-US"); Render(editor, "editor-condition-en-small", 824, 550);
                var resourceTask = TaskDefinitionJson.Deserialize(TaskDefinitionJson.Serialize(draft.Tasks.Single(item => item.Definition.TaskId == "Transfer").Definition));
                var scope = AllNodes(resourceTask.Root).OfType<ResourceScopeNode>().First();
                scope.EntryInterlock = new TaskInterlock { Axes = new List<TaskAxisInterlock> { new TaskAxisInterlock { AxisName = "搬运X", MinimumPosition = -1, MaximumPosition = 1 } },
                    Inputs = new List<TaskInputInterlock> { new TaskInputInterlock { InputName = "产品到位", Expected = true } } };
                var resourceEditor = new TaskEditorView(resourceTask, test.Store.LoadCatalog(draft), () => test.Store.LoadCatalog(draft), draft.Tasks.Where(item => item.Definition.TaskId != resourceTask.TaskId).Select(item => item.Definition));
                resourceEditor.FocusNode(scope.Id); Render(resourceEditor, "editor-resource-zh", 1200, 780);
                Assert(!resourceEditor.IsDirty, "显示联锁绑定值就误标了未保存。");
                Assert(((Button)page.FindName("ApplyButton")).IsEnabled == false, "受阻应用按钮仍可用。");

                string axisParametersPath = Path.Combine(test.Configuration.GetMachineDirectory(Workspace.Id), "HardwareConfig", "AxisParameters.json");
                File.Delete(axisParametersPath);
                var missingAxisPage = new TaskConfigurationView(test.Configuration, Workspace.Id, () => null);
                var missingAxisStatus = (TextBlock)missingAxisPage.FindName("StatusText");
                var errorDiagnostics = (Expander)missingAxisPage.FindName("ErrorDiagnosticsExpander");
                var errorDiagnosticsText = (TextBox)missingAxisPage.FindName("ErrorDiagnosticsText");
                Assert(missingAxisStatus.Text.StartsWith("缺少轴参数配置。", StringComparison.Ordinal)
                    && !missingAxisStatus.Text.Contains("FileNotFoundException"), "缺少轴参数时应先显示简洁的操作指引。");
                Assert(errorDiagnostics.Visibility == Visibility.Visible && !errorDiagnostics.IsExpanded,
                    "完整异常诊断应可展开查看，但默认不能占满任务配置页。");
                Assert(errorDiagnosticsText.Text.Contains("FileNotFoundException")
                    && errorDiagnosticsText.Text.Contains("AxisParameters.json"), "展开详情前仍须保留完整诊断内容。");
            }
        }
        /// <summary>按明确像素尺寸构造并导出控件，不观察实际桌面。</summary>
        private static void Render(FrameworkElement view, string name, int width, int height)
        {
            view.Measure(new Size(width, height)); view.Arrange(new Rect(0, 0, width, height)); view.UpdateLayout();
            // DataGrid 列宽与绑定有延后布局；排空此轮队列后再取离屏图，避免误判为真实桌面画面。
            var frame = new System.Windows.Threading.DispatcherFrame();
            view.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            view.Measure(new Size(width, height)); view.Arrange(new Rect(0, 0, width, height)); view.UpdateLayout();
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            string folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Previews"); Directory.CreateDirectory(folder);
            using (var file = File.Create(Path.Combine(folder, name + ".png"))) encoder.Save(file);
        }
        /// <summary>递归寻找当前控件树中的明确按钮，仅用于内部事件回归。</summary>
        private static Button FindButton(DependencyObject root, string content)
        {
            if (root is Button button && (string)button.Content == content) return button;
            foreach (object child in LogicalTreeHelper.GetChildren(root)) if (child is DependencyObject node) { var found = FindButton(node, content); if (found != null) return found; }
            return null;
        }
        /// <summary>检查用的具体节点遍历。</summary>
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
    }
    /// <summary>每项检查使用独立的短路径临时配置目录，不连接现场设备。</summary>
    internal sealed class Workspace : IDisposable
    {
        /// <summary>预览辅助方法声明的隔离设备标识。</summary>
        internal const string Id = "TaskEditorPreview";
        /// <summary>本项检查的绝对临时根路径。</summary>
        internal string Root { get; private set; }
        /// <summary>现有配置服务。</summary>
        internal MachineConfigurationStore Configuration { get; private set; }
        /// <summary>被检查的实际存储实现。</summary>
        internal TaskWorkspaceStore Store { get; private set; }
        /// <summary>在短路径临时目录创建真实示例配置，不连接控制器。</summary>
        internal Workspace()
        {
            Root = Path.Combine(Path.GetTempPath(), "MCTFChecks", Guid.NewGuid().ToString("N"));
            Configuration = TaskEditingPreviewWindow.CreatePreviewStore(Root); Store = new TaskWorkspaceStore(Configuration);
        }
        /// <summary>按固定文件顺序取得原始内容，用于识别部分写入和恢复。</summary>
        internal string ActiveBytes()
        {
            string root = Path.Combine(Root, Id); var files = Directory.GetFiles(Path.Combine(root, "Tasks"), "*.json").Concat(new[] { Path.Combine(root, "Parameters/TaskMachine.json") });
            return string.Join("|", files.OrderBy(value => value).Select(path => Path.GetFileName(path) + ":" + Convert.ToBase64String(File.ReadAllBytes(path))));
        }
        /// <summary>递归清理前验证绝对路径严格位于本检查专用临时目录。</summary>
        public void Dispose()
        {
            string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MCTFChecks")) + Path.DirectorySeparatorChar;
            string target = Path.GetFullPath(Root);
            if (!target.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || target.Length <= parent.Length) throw new InvalidOperationException("检查清理路径不在专用目录。");
            if (Directory.Exists(target)) Directory.Delete(target, true);
        }
    }
}
