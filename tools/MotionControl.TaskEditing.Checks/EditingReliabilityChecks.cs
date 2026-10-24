using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.TaskEngine.Configuration;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Editing;
using MotionControl.TaskEngine.Wpf.Editing;
using Newtonsoft.Json.Linq;

namespace MotionControl.TaskEditing.Checks
{
    /// <summary>针对现场编辑丢失风险的文件与控件回归，不创建控制卡。</summary>
    internal static partial class Program
    {
        /// <summary>两个窗口及外部改动不能覆盖彼此草稿；冲突方仍可保留自己的完整副本。</summary>
        private static void DraftWindowConflicts()
        {
            using (var test = new Workspace())
            {
                var first = test.Store.Load(Workspace.Id);
                var second = test.Store.Load(Workspace.Id);
                string before = test.ActiveBytes();
                first.Tasks[0].Definition.Name = "第一个窗口";
                test.Store.SaveDraft(first);
                string draftPath = Path.Combine(test.Root, Workspace.Id, "Drafts/TaskWorkspace.json");
                string saved = File.ReadAllText(draftPath);
                Assert(JObject.Parse(saved)["draftSignature"] == null, "文件签名不能混入现有草稿格式。");

                second.Tasks[0].Definition.Name = "第二个窗口未保存";
                DateTime savedAt = second.SavedAt;
                Throws(() => test.Store.SaveDraft(second));
                string warning;
                Throws(() => test.Store.Apply(second, out warning));
                Assert(second.SavedAt == savedAt, "保存失败却改变了最近保存时间。");
                Assert(saved == File.ReadAllText(draftPath) && before == test.ActiveBytes(), "冲突拒绝后仍改动了共享草稿或运行文件。");

                string local = test.Store.SaveLocalCopy(second);
                var copy = JObject.Parse(File.ReadAllText(local));
                Assert((string)copy["tasks"][0]["definition"]["name"] == "第二个窗口未保存", "本窗口副本没有保留冲突内容。");
                Assert(saved == File.ReadAllText(draftPath) && before == test.ActiveBytes(), "保留副本意外覆盖了共享草稿或运行文件。");

                var latest = test.Store.Load(Workspace.Id);
                latest.Tasks[0].Definition.Name = "最新窗口继续修改";
                test.Store.SaveDraft(latest);
                test.Store.SaveDraft(latest);
                Throws(() => test.Store.SaveDraft(first));
                File.AppendAllText(draftPath, " ");
                Throws(() => test.Store.SaveDraft(latest));
                latest = test.Store.Load(Workspace.Id);
                test.Store.DiscardDraft(Workspace.Id);
                Throws(() => test.Store.SaveDraft(latest));
            }
        }

        /// <summary>目录允许的不同大小写引用，查看或保存表单时不变空，也不改写原拼写。</summary>
        private static void CaseInsensitiveReferences()
        {
            using (var test = new Workspace())
            {
                var workspace = test.Store.Load(Workspace.Id);
                var catalog = test.Store.LoadCatalog(workspace);
                var point = catalog.TeachPoints.First();
                var task = new TaskDefinition
                {
                    TaskId = "ReferenceCase", Name = "引用大小写回归",
                    Root = new SequenceNode
                    {
                        Id = "root", Name = "顺序", Children = new List<TaskNode>
                        {
                            new SetVariableNode { Id = "variable", Name = "写入变量", VariableName = "vision.ok",
                                Value = new TaskValueBinding { Source = TaskValueSource.Variable, VariableName = "VISION.OK" } },
                            new MoveAxisNode { Id = "motion", Name = "示教点目标", AxisName = point.AxisName.ToLowerInvariant(),
                                Target = new TaskValueBinding { Source = TaskValueSource.TeachPoint, AxisName = point.AxisName.ToLowerInvariant(), PointId = point.PointId.ToUpperInvariant() },
                                Profile = new TaskMotionProfile { Speed = 1, Acceleration = 2, Deceleration = 2 } },
                            new CallTaskNode { Id = "call", Name = "子任务", TaskId = "load" }
                        }
                    }
                };
                string before = TaskDefinitionJson.Serialize(task);
                var editor = new TaskEditorView(task, catalog, workspace.Tasks.Select(item => item.Definition));
                // 此项只验证表单往返，保存回调明确不写任何文件。
                editor.DraftSaved += (sender, e) => { task = editor.Definition; };
                foreach (var node in AllNodes(task.Root)) editor.FocusNode(node.Id);
                Assert(editor.SaveDraft(), "不同大小写引用的表单无法读取。");
                Assert(before == TaskDefinitionJson.Serialize(editor.Definition), "只查看和保存就丢失引用或改动了原拼写。");

                workspace.Machine.Stations[1].TaskId = "transfer";
                workspace.Machine.InitializationTaskId = "LOAD";
                test.Store.SaveDraft(workspace);
                var page = new TaskConfigurationView(test.Configuration, Workspace.Id, () => null);
                FindButton(page, "保存草稿").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var loaded = test.Store.Load(Workspace.Id);
                Assert(loaded.Machine.Stations[1].TaskId == "transfer" && loaded.Machine.InitializationTaskId == "LOAD", "工位或初始化任务引用被误清空。");
            }
        }

        /// <summary>空条件打开后仍为空；取反删除子条件后可重新加回，且不会添加第二个子条件。</summary>
        private static void ConditionRoundTrip()
        {
            using (var test = new Workspace())
            {
                var workspace = test.Store.Load(Workspace.Id);
                var task = new TaskDefinition { TaskId = "Conditions", Name = "条件回归", Root = new SequenceNode { Id = "root", Name = "顺序" } };
                foreach (var kind in new[] { TaskConditionKind.All, TaskConditionKind.Any, TaskConditionKind.Not })
                    ((SequenceNode)task.Root).Children.Add(new WaitConditionNode { Id = kind.ToString(), Name = kind.ToString(), Condition = new TaskCondition { Kind = kind } });
                string before = TaskDefinitionJson.Serialize(task);
                var editor = new TaskEditorView(task, test.Store.LoadCatalog(workspace), new TaskDefinition[0]);
                // 此项只验证表单往返，保存回调明确不写任何文件。
                editor.DraftSaved += (sender, e) => { task = editor.Definition; };
                foreach (var node in AllNodes(task.Root)) editor.FocusNode(node.Id);
                Assert(editor.SaveDraft() && before == TaskDefinitionJson.Serialize(editor.Definition), "空组合条件被自动补入默认条件。");

                editor.FocusNode("Not");
                Button add = FindButton(editor, "添加条件");
                Assert(add.IsEnabled, "空取反条件没有可用添加入口。");
                add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert(!add.IsEnabled, "取反条件允许加入多个子条件。");
                FindButton(editor, "移除此条件").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert(add.IsEnabled, "取反条件删除后无法再添加。");
                add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert(editor.SaveDraft(), "重建取反子条件后无法保存草稿。");
                var condition = ((SequenceNode)editor.Definition.Root).Children.OfType<WaitConditionNode>().Single(item => item.Id == "Not").Condition;
                Assert(condition.Conditions.Count == 1, "重建取反条件的子条件数量不正确。");
            }
        }

        /// <summary>保存和语言切换保留当前选择，编辑器副本包含当前输入且不清除未保存标记。</summary>
        private static void ConfigurationContext()
        {
            using (var test = new Workspace())
            {
                var page = new TaskConfigurationView(test.Configuration, Workspace.Id, () => null);
                var stations = (DataGrid)page.FindName("StationsGrid");
                var tasks = (DataGrid)page.FindName("TasksGrid");
                var tabs = (TabControl)page.FindName("MainTabs");
                stations.SelectedIndex = 1;
                tasks.SelectedIndex = 2;
                string taskId = ((TaskWorkspaceTask)tasks.SelectedItem).Definition.TaskId;
                FindButton(page, "保存草稿").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert(stations.SelectedIndex == 1 && ((TaskWorkspaceTask)tasks.SelectedItem).Definition.TaskId == taskId, "保存后跳回第一工位或丢失任务选择。");
                page.SetLanguage("en-US");
                Assert(stations.SelectedIndex == 1 && ((TaskWorkspaceTask)tasks.SelectedItem).Definition.TaskId == taskId, "语言切换丢失当前选择。");
                page.SetLanguage("zh-CN");
                tabs.SelectedIndex = 1;
                FindButton(page, "编辑任务").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var editor = (TaskEditorView)((ContentControl)page.FindName("ContentHost")).Content;
                ((TextBox)editor.FindName("TaskNameBox")).Text = "尚未保存的编辑器副本";
                string draftPath = Path.Combine(test.Root, Workspace.Id, "Drafts/TaskWorkspace.json");
                string before = File.ReadAllText(draftPath);
                FindButton(editor, "保留草稿副本").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert(editor.IsDirty && before == File.ReadAllText(draftPath), "保留副本清除了未保存状态或覆盖共享草稿。");
                string local = Directory.GetFiles(Path.Combine(test.Root, Workspace.Id, "Backups"), "TaskLocal-*.json").Single();
                var copy = JObject.Parse(File.ReadAllText(local));
                Assert(copy["tasks"].Any(item => (string)item["definition"]["name"] == "尚未保存的编辑器副本"), "副本不包含编辑器尚未保存的任务内容。");
                Assert(((TextBlock)editor.FindName("StatusText")).Text.Contains(local), "没有显示副本实际路径。");
                File.WriteAllText(draftPath, "损坏的外部草稿");
                page.LoadWorkspace();
                Assert(ReferenceEquals(((ContentControl)page.FindName("ContentHost")).Content, editor), "载入损坏草稿时丢失了原编辑器。");
                Assert(((TextBox)editor.FindName("TaskNameBox")).Text == "尚未保存的编辑器副本", "载入失败时丢失了本窗口输入。");
            }
        }

        /// <summary>变量名称和任务名称的输入只有全部解析成功后才提交，不能在报错时留下半改模型。</summary>
        private static void InvalidFieldsKeepDefinition()
        {
            using (var test = new Workspace())
            {
                var workspace = test.Store.Load(Workspace.Id);
                var catalog = test.Store.LoadCatalog(workspace);
                var task = new TaskDefinition
                {
                    TaskId = "InvalidFields", Name = "原任务名称",
                    Root = new SequenceNode
                    {
                        Id = "root", Name = "顺序", Children = new List<TaskNode>
                        {
                            new SetVariableNode { Id = "write", Name = "写入变量", VariableName = "Vision.OK",
                                Value = new TaskValueBinding { Source = TaskValueSource.Constant, DataType = DeviceVariableType.Boolean, Constant = "true" } }
                        }
                    }
                };
                var editor = new TaskEditorView(task, catalog, new TaskDefinition[0]);
                editor.FocusNode("write");
                string before = TaskDefinitionJson.Serialize(editor.Definition);
                var details = (StackPanel)editor.FindName("Details");
                details.Children.OfType<ComboBox>().First().SelectedItem = catalog.Variables.Keys.First(name => name != "Vision.OK");
                var binding = details.Children.OfType<StackPanel>().Single();
                binding.Children.OfType<ComboBox>().First().SelectedIndex = -1;
                ((TextBox)editor.FindName("TaskNameBox")).Text = "尚未提交的新名称";
                Assert(!editor.SaveDraft(), "缺少值来源时不应保存成功。");
                Assert(before == TaskDefinitionJson.Serialize(editor.Definition), "解析失败后原模型被部分修改。");
                Assert(((TextBox)editor.FindName("TaskNameBox")).Text == "尚未提交的新名称", "报错后丢失了人员输入。");
            }
        }
    }
}
