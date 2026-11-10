using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Wpf.Editing;
using Newtonsoft.Json.Linq;

namespace MotionControl.TaskEditing.Checks
{
    /// <summary>检查任务来源换代后的页面失效与草稿保留；只操作隔离文件和真实 WPF 控件，不连接控制器。</summary>
    internal static partial class Program
    {
        /// <summary>准备文件未变但任务内容被替换时，不能继续显示上次检查通过或隐藏来源核查入口。</summary>
        private static void Roc18TaskFileSourceChange()
        {
            using (var test = new Workspace())
            {
                TaskConfigurationView page = CreateRoc18CheckedPage(test);
                string preparation = test.Store.ComputePreparationSignature(Workspace.Id);
                string path = Path.Combine(test.Configuration.GetMachineDirectory(Workspace.Id), "Tasks", "Load.json");
                JObject task = JObject.Parse(File.ReadAllText(path));
                task["name"] = "外部替换后的上料任务";
                File.WriteAllText(path, task.ToString());
                string currentFiles = test.ActiveBytes();

                ObserveRoc18TaskSources(page, () => ((TextBlock)page.FindName("SourceSummaryText")).Text.Contains("草稿基准待核查"));

                Assert(test.Store.ComputePreparationSignature(Workspace.Id) == preparation,
                    "任务文件变更检查意外改动了准备文件。");
                Assert(test.ActiveBytes() == currentFiles, "观察任务来源变更时改写了运行文件。");
                AssertRoc18SourcesNeedReview(page, "替换任务文件");
            }
        }

        /// <summary>只更换工位任务清单而不改准备文件时，页面同样必须撤销旧检查结论。</summary>
        private static void Roc18TaskMachineSourceChange()
        {
            using (var test = new Workspace())
            {
                TaskConfigurationView page = CreateRoc18CheckedPage(test);
                string preparation = test.Store.ComputePreparationSignature(Workspace.Id);
                string path = Path.Combine(test.Configuration.GetMachineDirectory(Workspace.Id), "Parameters", "TaskMachine.json");
                JObject machine = JObject.Parse(File.ReadAllText(path));
                machine["stations"][0]["name"] = "替换工艺后的上料工位";
                File.WriteAllText(path, machine.ToString());
                string currentFiles = test.ActiveBytes();

                ObserveRoc18TaskSources(page, () => ((TextBlock)page.FindName("SourceSummaryText")).Text.Contains("草稿基准待核查"));

                Assert(test.Store.ComputePreparationSignature(Workspace.Id) == preparation,
                    "任务清单变更检查意外改动了准备文件。");
                Assert(test.ActiveBytes() == currentFiles, "观察任务清单变更时改写了运行文件。");
                AssertRoc18SourcesNeedReview(page, "替换 TaskMachine 清单");
            }
        }

        /// <summary>任务目录新增文件也是来源变更，不能仅监视已加载文件的内容。</summary>
        private static void Roc18TaskFileAdded()
        {
            using (var test = new Workspace())
            {
                TaskConfigurationView page = CreateRoc18CheckedPage(test);
                string preparation = test.Store.ComputePreparationSignature(Workspace.Id);
                string directory = Path.Combine(test.Configuration.GetMachineDirectory(Workspace.Id), "Tasks");
                JObject task = JObject.Parse(File.ReadAllText(Path.Combine(directory, "Load.json")));
                task["taskId"] = "Roc18AdditionalTask";
                task["name"] = "新增但尚未载入的任务";
                File.WriteAllText(Path.Combine(directory, "Roc18AdditionalTask.json"), task.ToString());
                string currentFiles = test.ActiveBytes();

                ObserveRoc18TaskSources(page, () => ((TextBlock)page.FindName("SourceSummaryText")).Text.Contains("草稿基准待核查"));

                Assert(test.Store.ComputePreparationSignature(Workspace.Id) == preparation,
                    "新增任务检查意外改动了准备文件。");
                Assert(test.ActiveBytes() == currentFiles, "观察新增任务时改写了运行文件。");
                AssertRoc18SourcesNeedReview(page, "新增任务文件");
            }
        }

        /// <summary>任务文件被移除后原检查范围已经失效，仍保留草稿供人员核查。</summary>
        private static void Roc18TaskFileRemoved()
        {
            using (var test = new Workspace())
            {
                TaskConfigurationView page = CreateRoc18CheckedPage(test);
                string preparation = test.Store.ComputePreparationSignature(Workspace.Id);
                string path = Path.Combine(test.Configuration.GetMachineDirectory(Workspace.Id), "Tasks", "Load.json");
                File.Delete(path);
                string currentFiles = test.ActiveBytes();

                ObserveRoc18TaskSources(page, () => ((TextBlock)page.FindName("SourceSummaryText")).Text.Contains("草稿基准待核查"));

                Assert(test.Store.ComputePreparationSignature(Workspace.Id) == preparation,
                    "删除任务检查意外改动了准备文件。");
                Assert(test.ActiveBytes() == currentFiles, "观察任务删除时重建或改写了运行文件。");
                AssertRoc18SourcesNeedReview(page, "删除任务文件");
            }
        }

        /// <summary>任务文件暂时不可读时显示未知并清旧结果；恢复读取后也不能自动恢复旧检查通过。</summary>
        private static void Roc18TaskSourceReadFailure()
        {
            using (var test = new Workspace())
            {
                TaskConfigurationView page = CreateRoc18CheckedPage(test);
                string path = Path.Combine(test.Configuration.GetMachineDirectory(Workspace.Id), "Tasks", "Load.json");
                string originalFiles = test.ActiveBytes();
                using (var blocked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    ObserveRoc18TaskSources(page, () => ((Expander)page.FindName("ErrorDiagnosticsExpander")).Visibility == Visibility.Visible);

                    AssertRoc18SourcesNeedReview(page, "任务来源读取失败");
                    Assert(!((Button)page.FindName("RebaseDraftButton")).IsEnabled
                        && !((Button)page.FindName("ReloadRuntimeButton")).IsEnabled,
                        "无法读取任务来源时仍允许同步基准或丢弃草稿。");
                    Assert(((Expander)page.FindName("ErrorDiagnosticsExpander")).Visibility == Visibility.Visible,
                        "任务来源读取失败没有保留可查看的错误详情。");
                }

                ObserveRoc18TaskSources(page, () => ((Expander)page.FindName("ErrorDiagnosticsExpander")).Visibility == Visibility.Collapsed);

                Assert(test.ActiveBytes() == originalFiles, "任务来源读取失败或恢复时改写了文件。");
                Assert(((DataGrid)page.FindName("TasksGrid")).Items.Cast<TaskLibraryRow>()
                    .All(row => !row.DraftState.StartsWith("任务检查", StringComparison.Ordinal)),
                    "恢复读取后自动恢复了失效前的旧检查结论。");
                Assert(((Expander)page.FindName("ErrorDiagnosticsExpander")).Visibility == Visibility.Collapsed,
                    "任务来源恢复读取后仍显示旧读取错误。");
            }
        }

        /// <summary>来源变化只使检查失效，不能关闭编辑器、清除未保存输入或自动保存草稿。</summary>
        private static void Roc18TaskSourceChangeKeepsUnsavedEditor()
        {
            using (var test = new Workspace())
            {
                CreatePreparationWorkspace(test);
                var page = new TaskConfigurationView(test.Configuration, Workspace.Id, () => null);
                Assert(page.OpenTaskById("Load"), "检查准备失败：无法打开实际任务编辑器。");
                var content = (ContentControl)page.FindName("ContentHost");
                var editor = (TaskEditorView)content.Content;
                var name = (TextBox)editor.FindName("TaskNameBox");
                name.Text = "本窗口尚未保存的任务名称";
                Assert(page.HasUnsavedChanges, "检查准备失败：任务名称输入没有标记为未保存。");
                string root = test.Configuration.GetMachineDirectory(Workspace.Id);
                string draftPath = Path.Combine(root, "Drafts", "TaskWorkspace.json");
                Assert(!File.Exists(draftPath), "检查准备失败：本项检查应从尚未保存的工作区开始。");
                string path = Path.Combine(root, "Parameters", "TaskMachine.json");
                JObject machine = JObject.Parse(File.ReadAllText(path));
                machine["name"] = "外部替换的新工艺名称";
                File.WriteAllText(path, machine.ToString());
                string currentFiles = test.ActiveBytes();

                ObserveRoc18TaskSources(page, () => ((TextBlock)editor.FindName("EditorSourceSummaryText")).Text.Contains("草稿基准待核查"));

                Assert(ReferenceEquals(content.Content, editor)
                    && name.Text == "本窗口尚未保存的任务名称" && page.HasUnsavedChanges,
                    "任务来源刷新丢失了原编辑器或未保存输入。");
                Assert(!File.Exists(draftPath) && test.ActiveBytes() == currentFiles,
                    "任务来源刷新自动保存草稿或改写了运行文件。");
                Assert(((TextBlock)editor.FindName("EditorSourceSummaryText")).Text.Contains("草稿基准待核查"),
                    "编辑器保留了输入，但没有显示任务来源已变的核查提示。");
            }
        }

        /// <summary>子编辑器自己的旧问题清单也须随来源失效，正在输入的无效数值不能被重新建表或自动解析覆盖。</summary>
        private static void Roc18EditorSourceCheckInvalidation()
        {
            using (var test = new Workspace())
            {
                var draft = CreatePreparationWorkspace(test);
                var task = draft.Tasks.Single(item => item.Definition.TaskId == "Load");
                ((SequenceNode)task.Definition.Root).Children.Add(new WaitInputNode
                {
                    Id = "roc18-missing-input",
                    Name = "用于识别旧检查的输入引用",
                    InputName = "Roc18MissingInput",
                    Expected = true,
                    TimeoutMs = 1000
                });
                test.Store.SaveDraft(draft);
                var page = new TaskConfigurationView(test.Configuration, Workspace.Id, () => null);
                Assert(page.OpenTaskById("Load"), "检查准备失败：无法打开带旧检查问题的任务。");
                var content = (ContentControl)page.FindName("ContentHost");
                var editor = (TaskEditorView)content.Content;
                editor.FocusNode("roc18-missing-input");
                FindButton(editor, "检查任务").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var issues = (DataGrid)editor.FindName("IssuesGrid");
                var status = (TextBlock)editor.FindName("StatusText");
                Assert(issues.Items.Count > 0 && status.Text.StartsWith("检查结果", StringComparison.Ordinal),
                    "检查准备失败：子编辑器没有形成可识别的旧检查结果。");
                string oldStatus = status.Text;
                var fields = (Dictionary<string, FrameworkElement>)typeof(TaskEditorView)
                    .GetField("_fields", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(editor);
                var timeout = (TextBox)fields["Timeout"];
                timeout.Text = "-";
                Assert(page.HasUnsavedChanges, "检查准备失败：未完成的数值输入没有标记为未保存。");
                string root = test.Configuration.GetMachineDirectory(Workspace.Id);
                string draftPath = Path.Combine(root, "Drafts", "TaskWorkspace.json");
                string savedDraft = File.ReadAllText(draftPath);
                string path = Path.Combine(root, "Parameters", "TaskMachine.json");
                JObject machine = JObject.Parse(File.ReadAllText(path));
                machine["name"] = "子编辑器检查后的新工艺";
                File.WriteAllText(path, machine.ToString());
                string currentFiles = test.ActiveBytes();

                ObserveRoc18TaskSources(page, () => issues.Items.Count == 0 && status.Text != oldStatus);

                Assert(issues.Items.Count == 0 && issues.Visibility == Visibility.Collapsed,
                    "ROC-18：来源变化后子编辑器仍显示旧问题清单。");
                Assert(status.Text != oldStatus && status.Text.Contains("重新检查"),
                    "ROC-18：子编辑器未撤销旧检查状态并提示重新检查。");
                Assert(ReferenceEquals(content.Content, editor) && timeout.Text == "-"
                    && ((Panel)editor.FindName("Details")).Children.Contains(timeout) && page.HasUnsavedChanges,
                    "撤销子编辑器旧检查时重建了字段或丢失未完成的数值输入。");
                Assert(File.ReadAllText(draftPath) == savedDraft && test.ActiveBytes() == currentFiles,
                    "撤销子编辑器检查时自动保存草稿或改写了运行文件。");
            }
        }

        /// <summary>文件没有变化时重复刷新保留检查状态，不把正常轮询当成新的工艺切换。</summary>
        private static void Roc18UnchangedTaskSourcesKeepChecks()
        {
            using (var test = new Workspace())
            {
                TaskConfigurationView page = CreateRoc18CheckedPage(test);
                var grid = (DataGrid)page.FindName("TasksGrid");
                string[] states = grid.Items.Cast<TaskLibraryRow>().Select(row => row.DraftState).ToArray();
                string source = ((TextBlock)page.FindName("SourceSummaryText")).Text;
                string files = test.ActiveBytes();

                ObserveRoc18TaskSources(page, () => true, TimeSpan.FromMilliseconds(2500));

                Assert(grid.Items.Cast<TaskLibraryRow>().Select(row => row.DraftState).SequenceEqual(states),
                    "任务来源未变化却清除了已有检查结果。");
                Assert(((TextBlock)page.FindName("SourceSummaryText")).Text == source
                    && ((Button)page.FindName("ApplyButton")).IsEnabled
                    && ((Button)page.FindName("RebaseDraftButton")).Visibility == Visibility.Collapsed,
                    "任务来源未变化却显示来源冲突或禁止正常应用。");
                Assert(test.ActiveBytes() == files && !page.HasUnsavedChanges,
                    "重复只读刷新改动了文件或草稿状态。");
            }
        }

        /// <summary>外部编辑器允许读写和替换的句柄不代表文件失读；后台观察不能反向独占写入来源。</summary>
        private static void Roc18ObservationAllowsWritableSourceHandle()
        {
            using (var test = new Workspace())
            {
                TaskConfigurationView page = CreateRoc18CheckedPage(test);
                var grid = (DataGrid)page.FindName("TasksGrid");
                string[] states = grid.Items.Cast<TaskLibraryRow>().Select(row => row.DraftState).ToArray();
                string path = Path.Combine(test.Configuration.GetMachineDirectory(Workspace.Id), "Tasks", "Load.json");
                string files = test.ActiveBytes();
                using (var editorHandle = new FileStream(path, FileMode.Open, FileAccess.ReadWrite,
                    FileShare.ReadWrite | FileShare.Delete))
                {
                    // 没有写入新字节；只保留一个正常允许其他读写与原子替换的编辑器句柄。
                    ObserveRoc18TaskSources(page, () => true, TimeSpan.FromMilliseconds(2500));
                    Assert(grid.Items.Cast<TaskLibraryRow>().Select(row => row.DraftState).SequenceEqual(states)
                        && ((Expander)page.FindName("ErrorDiagnosticsExpander")).Visibility == Visibility.Collapsed
                        && ((Button)page.FindName("ApplyButton")).IsEnabled,
                        "ROC-18：只读观察与允许共享的编辑器写句柄冲突，未变化文件被错误标记为无法读取。");
                }
                Assert(test.ActiveBytes() == files && !page.HasUnsavedChanges,
                    "只读来源观察改动了已保存文件或草稿。");
            }
        }

        /// <summary>建立有效准备文件与任务，并通过真实页面检查入口生成供后续失效核对的检查状态。</summary>
        private static TaskConfigurationView CreateRoc18CheckedPage(Workspace test)
        {
            CreatePreparationWorkspace(test);
            var page = new TaskConfigurationView(test.Configuration, Workspace.Id, () => null);
            typeof(TaskConfigurationView).GetMethod("Check_Click", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(page, new object[] { page, new RoutedEventArgs() });
            var rows = ((DataGrid)page.FindName("TasksGrid")).Items.Cast<TaskLibraryRow>().ToList();
            Assert(rows.Count > 0 && rows.All(row => row.DraftState.StartsWith("任务检查", StringComparison.Ordinal)),
                "检查准备失败：页面没有生成任务检查状态。");
            Assert(((Button)page.FindName("ApplyButton")).IsEnabled,
                "检查准备失败：初始有效来源不应阻止应用。");
            return page;
        }

        /// <summary>用真实窗口触发 Loaded 并泵送调度器，最多等五秒观察页面结果；不假定来源读取必须同步完成。</summary>
        private static void ObserveRoc18TaskSources(TaskConfigurationView page, Func<bool> completed,
            TimeSpan minimumObservation = default(TimeSpan))
        {
            var window = new Window
            {
                Content = page,
                Width = 1100,
                Height = 760,
                ShowInTaskbar = false,
                ShowActivated = false,
                Title = "任务来源离线检查"
            };
            var frame = new DispatcherFrame();
            var elapsed = Stopwatch.StartNew();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
            var refresh = typeof(TaskConfigurationView).GetMethod("RefreshPermission", BindingFlags.Instance | BindingFlags.NonPublic);
            // 只为本次隔离检查取证；不改变产品刷新、原计时器优先级或五秒观察条件。
            var dispatcher = Dispatcher.CurrentDispatcher;
            Thread uiThread = Thread.CurrentThread;
            var observationFinished = new ManualResetEvent(false);
            string phase = "窗口尚未显示";
            int pollEntered = 0, refreshReturned = 0, pollExited = 0;
            int operationsStarted = 0, operationsCompleted = 0, deadlineMissed = 0;
            DispatcherOperation lastStarted = null, lastCompleted = null;
            DispatcherHookEventHandler operationStarted = (sender, args) =>
            {
                Interlocked.Increment(ref operationsStarted);
                Volatile.Write(ref lastStarted, args.Operation);
            };
            DispatcherHookEventHandler operationCompleted = (sender, args) =>
            {
                Interlocked.Increment(ref operationsCompleted);
                Volatile.Write(ref lastCompleted, args.Operation);
            };
            dispatcher.Hooks.OperationStarted += operationStarted;
            dispatcher.Hooks.OperationCompleted += operationCompleted;
            // 独立后台线程先记下原始停点，再只投递一次 Send/Background 探针；探针唤醒不能算通过。
            var watchdog = new Thread(() =>
            {
                try
                {
                    if (observationFinished.WaitOne(8000)) return;
                    Interlocked.Exchange(ref deadlineMissed, 1);
                    for (int sample = 0; sample < 3; sample++)
                    {
                        Console.Error.WriteLine("[来源观察守护] 用时=" + elapsed.ElapsedMilliseconds
                            + "ms；阶段=" + Volatile.Read(ref phase) + "；UI线程=" + uiThread.ManagedThreadId
                            + "/" + uiThread.ThreadState + "；轮询进入/刷新返回/轮询退出="
                            + Volatile.Read(ref pollEntered) + "/" + Volatile.Read(ref refreshReturned) + "/" + Volatile.Read(ref pollExited)
                            + "；调度开始/结束=" + Volatile.Read(ref operationsStarted) + "/" + Volatile.Read(ref operationsCompleted)
                            + "；最后开始=" + DescribeRoc18DispatcherOperation(Volatile.Read(ref lastStarted))
                            + "；最后结束=" + DescribeRoc18DispatcherOperation(Volatile.Read(ref lastCompleted)));
                        if (sample == 0)
                        {
                            dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
                                Console.Error.WriteLine("[来源观察探针] Send 已执行，原观察期限已经失守。")));
                            dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                                Console.Error.WriteLine("[来源观察探针] Background 已执行，原观察期限已经失守。")));
                        }
                        if (observationFinished.WaitOne(1000)) return;
                    }
                }
                catch (Exception error)
                {
                    Interlocked.Exchange(ref deadlineMissed, 1);
                    Console.Error.WriteLine("[来源观察诊断失败] " + error);
                }
            }) { IsBackground = true, Name = "ROC18 来源观察诊断" };
            // 测试只等待可见结果；到期后由调用处的具体断言报告缺失行为。
            EventHandler poll = (sender, args) =>
            {
                Volatile.Write(ref phase, "轮询已进入");
                int entered = Interlocked.Increment(ref pollEntered);
                if (entered == 1) Console.WriteLine("[来源观察阶段] 首次轮询已进入。");
                if (page.IsLoaded)
                {
                    // 模拟同步入口没有同步上下文的实际条件；后台观察必须显式回到页面 Dispatcher。
                    SynchronizationContext context = SynchronizationContext.Current;
                    try
                    {
                        SynchronizationContext.SetSynchronizationContext(null);
                        Volatile.Write(ref phase, "正在同步刷新页面");
                        refresh.Invoke(page, new object[] { null, EventArgs.Empty });
                        Interlocked.Increment(ref refreshReturned);
                        Volatile.Write(ref phase, "同步刷新已经返回");
                    }
                    finally { SynchronizationContext.SetSynchronizationContext(context); }
                }
                if (elapsed.Elapsed >= TimeSpan.FromSeconds(5)
                    || (page.IsLoaded && elapsed.Elapsed >= minimumObservation && completed()))
                {
                    Volatile.Write(ref phase, "已请求退出消息泵");
                    Console.WriteLine("[来源观察阶段] 请求退出消息泵；轮询次数=" + entered + "。");
                    frame.Continue = false;
                }
                Interlocked.Increment(ref pollExited);
                if (entered == 1) Console.WriteLine("[来源观察阶段] 首次轮询已返回。");
            };
            timer.Tick += poll;
            try
            {
                watchdog.Start();
                Volatile.Write(ref phase, "正在显示窗口");
                Console.WriteLine("[来源观察阶段] 显示任务来源窗口。");
                window.Show();
                Volatile.Write(ref phase, "正在启动轮询计时器");
                Console.WriteLine("[来源观察阶段] 窗口已显示，启动轮询计时器。");
                timer.Start();
                Volatile.Write(ref phase, "计时器已启动，正在泵送");
                Console.WriteLine("[来源观察阶段] 计时器已启动，进入消息泵。");
                Dispatcher.PushFrame(frame);
                Volatile.Write(ref phase, "消息泵已经返回");
                Console.WriteLine("[来源观察阶段] 泵送已返回。");
                Assert(Volatile.Read(ref deadlineMissed) == 0,
                    "ROC-18：原观察期限已失守，诊断探针唤醒或后来返回不能算检查通过。");
            }
            finally
            {
                observationFinished.Set();
                if (watchdog.Join(1000)) observationFinished.Dispose();
                else Console.Error.WriteLine("[来源观察诊断失败] 诊断线程未在一秒内退出，保留其等待句柄供外部进程守护收尾。");
                dispatcher.Hooks.OperationStarted -= operationStarted;
                dispatcher.Hooks.OperationCompleted -= operationCompleted;
                timer.Stop();
                timer.Tick -= poll;
                Console.WriteLine("[来源观察阶段] 关闭任务来源窗口。");
                window.Close();
                window.Content = null;
                Console.WriteLine("[来源观察阶段] 窗口已关闭并解除内容绑定。");
            }
        }

        /// <summary>仅在观察超时后记录 WPF 最后调度操作，私有字段不可用时如实说明，不影响产品行为。</summary>
        private static string DescribeRoc18DispatcherOperation(DispatcherOperation operation)
        {
            if (operation == null) return "无";
            var callbackField = typeof(DispatcherOperation).GetField("_method", BindingFlags.Instance | BindingFlags.NonPublic);
            var callback = callbackField?.GetValue(operation) as Delegate;
            string method = callback == null ? "回调名称未提供"
                : callback.Method.DeclaringType?.FullName + "." + callback.Method.Name;
            return operation.Priority + "/" + operation.Status + "/" + method;
        }

        /// <summary>核对人员实际可见的旧检查撤销、来源提示和安全重载入口，不要求自动覆盖草稿。</summary>
        private static void AssertRoc18SourcesNeedReview(TaskConfigurationView page, string scenario)
        {
            Assert(((DataGrid)page.FindName("TasksGrid")).Items.Cast<TaskLibraryRow>()
                .All(row => !row.DraftState.StartsWith("任务检查", StringComparison.Ordinal)),
                "ROC-18：" + scenario + "后仍显示上次任务检查结果。");
            Assert(((TextBlock)page.FindName("SourceSummaryText")).Text.Contains("草稿基准待核查"),
                "ROC-18：" + scenario + "后未显示草稿来源待核查。");
            Assert(!((Button)page.FindName("ApplyButton")).IsEnabled,
                "ROC-18：" + scenario + "后仍开放基于旧来源的应用入口。");
            Assert(((Button)page.FindName("RebaseDraftButton")).Visibility == Visibility.Visible
                && ((Button)page.FindName("ReloadRuntimeButton")).Visibility == Visibility.Visible,
                "ROC-18：" + scenario + "后没有提供同步来源或重新载入入口。");
        }
    }
}
