using System;
using System.Reflection;
using System.Windows.Controls;
using MotionControl.TaskEngine.Editing;
using MotionControl.TaskEngine.Wpf.Editing;
namespace MotionControl.TaskEditing.Checks
{
    /// <summary>草稿、运行文件快照和未知会话来源的独立检查。</summary>
    internal static class V14TaskSourceSummaryChecks
    {
        /// <summary>验证不同修订不混源、dirty与保存时间并列、来源变化及中英显示。</summary>
        internal static void Run()
        {
            using (var test = new Workspace())
            {
                var draft = test.Store.Load(Workspace.Id);
                var files = test.Store.Load(Workspace.Id, false);
                draft.SavedAt = new DateTime(2026, 11, 6, 10, 0, 0);
                files.Tasks[0].Definition.Revision = 77;
                var helper = typeof(TaskEditorView).Assembly.GetType("MotionControl.TaskEngine.Wpf.Editing.TaskSourceSummary");
                var build = helper.GetMethod("Build", BindingFlags.Static | BindingFlags.NonPublic);
                string summary = (string)build.Invoke(null, new object[] { draft, files, true, true, false });
                if (!summary.Contains("有未保存修改") || !summary.Contains("2026-11-06 10:00:00") || !summary.Contains("R77")
                    || !summary.Contains("草稿基准待核查") || !summary.Contains("文件快照与当前文件的一致性未确认")
                    || summary.Contains("快照不是当前文件") || !summary.Contains("会话标识 / 加载时间未提供"))
                    throw new InvalidOperationException("来源、dirty、修订或未知会话语义错误");
                string english = (string)build.Invoke(null, new object[] { draft, files, true, true, true });
                if (!english.Contains("not the executing session") || !english.Contains("session ID / load time not provided")
                    || !english.Contains("draft base needs review") || !english.Contains("snapshot agreement with current files is unverified"))
                    throw new InvalidOperationException("英文来源边界缺失");
                string unknown = (string)build.Invoke(null, new object[] { null, null, false, false, false });
                string unknownEnglish = (string)build.Invoke(null, new object[] { null, null, false, false, true });
                if (!unknown.Contains("编辑状态未知") || unknown.Contains("无待保存编辑") || !unknownEnglish.Contains("editing state unknown")
                    || unknownEnglish.Contains("no pending workspace edits")) throw new InvalidOperationException("缺草稿被伪装成无编辑");
                var many = new TaskWorkspaceDocument();
                for (int index = 0; index < 35; index++) many.Tasks.Add(new TaskWorkspaceTask { Definition = new MotionControl.TaskEngine.Definitions.TaskDefinition { TaskId = "Source_" + index, Revision = index + 1 } });
                string bounded = (string)build.Invoke(null, new object[] { draft, many, false, false, false });
                string boundedEnglish = (string)build.Invoke(null, new object[] { draft, many, false, false, true });
                if (!bounded.Contains("列出 10 / 共 35") || !bounded.Contains("第11–35项未显示") || bounded.Contains("Source_10 R11")
                    || !boundedEnglish.Contains("Showing 10 / 35") || !boundedEnglish.Contains("summary is incomplete") || many.Tasks.Count != 35)
                    throw new InvalidOperationException("来源任务摘要无界、未说明遗漏范围或修改真实集合");
                many.Tasks[0].Definition.TaskId = new string('长', 500);
                string longText = (string)build.Invoke(null, new object[] { draft, many, false, false, false });
                if (!longText.Contains(many.Tasks[0].Definition.TaskId)) throw new InvalidOperationException("已显示来源的长标识被截断且不可查看");
                var catalog = test.Store.LoadCatalog(draft);
                var editor = new TaskEditorView(draft.Tasks[0].Definition, catalog, () => catalog, new MotionControl.TaskEngine.Definitions.TaskDefinition[0]);
                editor.SetSourceSummary(summary);
                ((TextBox)editor.FindName("TaskNameBox")).Text = new string('长', 300);
                var text = (TextBlock)editor.FindName("EditorSourceSummaryText");
                if (!text.Text.Contains("当前任务有未保存修改") || !(text.Parent is ScrollViewer scroll) || scroll.MaxHeight != 90)
                    throw new InvalidOperationException("编辑器dirty未优先显示或长摘要没有限制高度");
                if (files.Tasks[0].Definition.Revision != 77) throw new InvalidOperationException("只读摘要修改文件快照");
            }
        }
    }
}
