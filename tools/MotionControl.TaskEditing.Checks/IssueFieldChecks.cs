using System;
using System.Linq;
using System.Windows.Controls;
using MotionControl.TaskEngine.Validation;
using MotionControl.TaskEngine.Wpf.Editing;

namespace MotionControl.TaskEditing.Checks
{
    /// <summary>检查现场问题提示不暴露内部 JSON 路径。</summary>
    internal static partial class Program
    {
        /// <summary>中文和英文界面都显示可读字段名，内部路径仍用于定位。</summary>
        private static void ReadableIssueFields()
        {
            using (var test = new Workspace())
            {
                var workspace = test.Store.Load(Workspace.Id);
                var task = workspace.Tasks.First().Definition;
                var issue = new TaskValidationIssue(TaskIssueSeverity.Warning, task.TaskId, task.Root.Id,
                    "root.children[3].condition.left", "变量的当前有效值仍须在执行前检查。");
                var editor = new TaskEditorView(task, test.Store.LoadCatalog(workspace),
                    () => test.Store.LoadCatalog(workspace), workspace.Tasks.Skip(1).Select(item => item.Definition));
                editor.FocusIssue(issue);
                string status = ((TextBlock)editor.FindName("StatusText")).Text;
                Assert(status.StartsWith("左值：", StringComparison.Ordinal) && !status.Contains("root.children"),
                    "双击定位后的提示仍暴露内部 JSON 路径。");

                editor.SetLanguage("en-US");
                editor.FocusIssue(issue);
                status = ((TextBlock)editor.FindName("StatusText")).Text;
                Assert(status.StartsWith("Left value", StringComparison.Ordinal) && !status.Contains("root.children"),
                    "英文界面的提示没有使用易读字段名，实际提示为：" + status);
            }
        }
    }
}
