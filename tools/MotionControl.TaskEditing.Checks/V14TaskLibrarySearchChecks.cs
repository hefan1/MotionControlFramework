using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using MotionControl.TaskEngine.Wpf.Editing;
namespace MotionControl.TaskEditing.Checks
{
    /// <summary>任务库只读筛选检查，复用隔离工作区，不执行设备动作。</summary>
    internal static class V14TaskLibrarySearchChecks
    {
        /// <summary>验证名称/ID匹配、稳定选择、清除、空列表及工作区和引用门禁不变。</summary>
        internal static void Run()
        {
            using (var test = new Workspace())
            {
                var page = new TaskConfigurationView(test.Configuration, Workspace.Id, () => null);
                var grid = (DataGrid)page.FindName("TasksGrid");
                var search = (TextBox)page.FindName("LibrarySearchBox");
                var count = (TextBlock)page.FindName("LibraryMatchText");
                var empty = (TextBlock)page.FindName("LibraryEmptyText");
                var clear = (Button)page.FindName("ClearLibrarySearchButton");
                int total = grid.Items.Count;
                if (total < 2) throw new InvalidOperationException("隔离数据不足以验证筛选");
                var target = (TaskLibraryRow)grid.Items[1];
                grid.SelectedItem = target;
                string before = test.ActiveBytes();
                search.Text = target.Definition.TaskId.ToLowerInvariant();
                if (!grid.Items.Cast<TaskLibraryRow>().Any(row => row.Definition.TaskId == target.Definition.TaskId)
                    || ((TaskLibraryRow)grid.SelectedItem).Definition.TaskId != target.Definition.TaskId) throw new InvalidOperationException("ID搜索或稳定选择失败");
                search.Text = "definitely-no-task";
                if (grid.Items.Count != 0 || !empty.Text.Contains("没有匹配") || grid.SelectedItem != null) throw new InvalidOperationException("无匹配与空任务库混淆或偷偷改选目标");
                clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (grid.Items.Count != total || ((TaskLibraryRow)grid.SelectedItem).Definition.TaskId != target.Definition.TaskId || clear.IsEnabled)
                    throw new InvalidOperationException("清除筛选未恢复列表或原TaskId选择");
                search.Text = target.Definition.Name;
                if (!grid.Items.Cast<TaskLibraryRow>().Any(row => row.Definition.TaskId == target.Definition.TaskId)) throw new InvalidOperationException("名称搜索失败");
                if (test.ActiveBytes() != before) throw new InvalidOperationException("搜索改动运行文件");
                page.SetLanguage("en-US");
                if (!count.Text.StartsWith("Matches")) throw new InvalidOperationException("筛选数量未切英文");
                var workspaceField = typeof(TaskConfigurationView).GetField("_workspace", BindingFlags.Instance | BindingFlags.NonPublic);
                var workspace = (MotionControl.TaskEngine.Editing.TaskWorkspaceDocument)workspaceField.GetValue(page);
                if (workspace.Tasks.Count != total) throw new InvalidOperationException("筛选删改工作区定义集合");
                string referenced = workspace.Machine.Stations.First(station => !string.IsNullOrEmpty(station.TaskId)).TaskId;
                var references = typeof(TaskConfigurationView).GetMethod("References", BindingFlags.Instance | BindingFlags.NonPublic);
                var refs = (System.Collections.Generic.List<string>)references.Invoke(page, new object[] { referenced });
                if (refs.Count == 0) throw new InvalidOperationException("筛选遮蔽了真实工位引用");
                search.Text = referenced;
                grid.SelectedItem = grid.Items.Cast<TaskLibraryRow>().First(row => row.Definition.TaskId == referenced);
                typeof(TaskConfigurationView).GetMethod("RemoveTask_Click", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(page, new object[] { page, new RoutedEventArgs() });
                if (workspace.Tasks.Count != total) throw new InvalidOperationException("筛选绕过被引用任务删除门禁");
                // 保持旧任务筛选，从工位实际入口打开不匹配的另一任务；返回后清除必须定位刚编辑项。
                clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var old = (TaskLibraryRow)grid.Items[0];
                var next = (TaskLibraryRow)grid.Items[1];
                grid.SelectedItem = old;
                search.Text = old.Definition.TaskId;
                if (!page.OpenTaskById(next.Definition.TaskId)) throw new InvalidOperationException("工位按ID打开任务失败");
                typeof(TaskConfigurationView).GetMethod("EditorBack", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(page, new object[] { page, EventArgs.Empty });
                clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (((TaskLibraryRow)grid.SelectedItem)?.Definition.TaskId != next.Definition.TaskId)
                    throw new InvalidOperationException("筛选下编辑返回后清除仍定位旧任务");
                // 新建与复制走同一EditorBack路径，逐项检查不匹配筛选时不会丢失新身份。
                foreach (string action in new[] { "NewTask_Click", "CopyTask_Click" })
                {
                    search.Text = next.Definition.TaskId;
                    grid.SelectedItem = grid.Items.Cast<TaskLibraryRow>().First(row => row.Definition.TaskId == next.Definition.TaskId);
                    typeof(TaskConfigurationView).GetMethod(action, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(page, new object[] { page, new RoutedEventArgs() });
                    var editing = (MotionControl.TaskEngine.Editing.TaskWorkspaceTask)typeof(TaskConfigurationView).GetField("_editingEntry", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(page);
                    typeof(TaskConfigurationView).GetMethod("EditorBack", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(page, new object[] { page, EventArgs.Empty });
                    clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    if (((TaskLibraryRow)grid.SelectedItem)?.Definition.TaskId != editing.Definition.TaskId)
                        throw new InvalidOperationException("新建或复制返回丢失实际任务身份");
                }
                // 仅改变隔离测试对象模拟空库，不调用保存或应用。
                workspace.Tasks.Clear();
                // 直接修改隔离集合不发送通知，实际改变查询以走真实列表刷新入口。
                search.Text = "empty-library-probe";
                search.Clear();
                if (grid.Items.Count != 0 || empty.Text != "Task library is empty") throw new InvalidOperationException("空库与无匹配混淆");
            }
        }
    }
}
