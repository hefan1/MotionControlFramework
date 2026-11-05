using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Controls;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Wpf.Editing;
namespace MotionControl.OperatorPages.Checks
{
    /// <summary>草稿步骤路径与有界只读摘要检查，不保存或执行任务。</summary>
    internal static class V14EditorSelectionSummaryChecks
    {
        /// <summary>验证多层对象身份、缺失目标、参数更新、中英及长文字完整滚动。</summary>
        internal static void Run()
        {
            var selected = new WaitInputNode { Id = "wait", Name = "同名", InputName = "Input_A", Expected = false, TimeoutMs = 123 };
            var nested = new SequenceNode { Id = "nested", Name = "同名", Children = new List<TaskNode> { selected } };
            var root = new SequenceNode { Id = "root", Name = "同名", Children = new List<TaskNode> { nested } };
            var task = new TaskDefinition { TaskId = "task", Name = "测试", Root = root };
            var summaryType = typeof(TaskEditorView).Assembly.GetType("MotionControl.TaskEngine.Wpf.Editing.TaskEditorSelectionSummary");
            var build = summaryType.GetMethod("Build", BindingFlags.Static | BindingFlags.NonPublic);
            string text = (string)build.Invoke(null, new object[] { task, selected, false });
            if (!text.Contains("同名 [root] > 同名 [nested] > 同名 [wait]") || !text.Contains("Input_A") || !text.Contains("123"))
                throw new InvalidOperationException("多层真实对象路径或参数丢失");
            var impostor = new WaitInputNode { Id = "wait", Name = "同名" };
            if (!((string)build.Invoke(null, new object[] { task, impostor, false })).Contains("未在当前草稿中找到"))
                throw new InvalidOperationException("以同名同ID冒充实际选择对象");
            selected.InputName = "Input_B"; selected.TimeoutMs = 456;
            string english = (string)build.Invoke(null, new object[] { task, selected, true });
            if (!english.Contains("Path:") || !english.Contains("Input_B") || !english.Contains("456") || !english.Contains("not runtime state"))
                throw new InvalidOperationException("字段或语言刷新未反映真实草稿");
            if (!((string)build.Invoke(null, new object[] { task, nested, false })).Contains("直接结构子项数：1"))
                throw new InvalidOperationException("结构子项数不真实");
            task.Name = new string('长', 500);
            var catalog = new TaskResourceCatalog(new TaskAxisInfo[0], new[] { "Input_B" }, new string[0], new Dictionary<string, DeviceVariableType>(), new TaskTeachPoint[0]);
            var view = new TaskEditorView(task, catalog, () => catalog, new TaskDefinition[0]);
            var details = (StackPanel)view.FindName("Details");
            var scroll = details.Children.OfType<ScrollViewer>().Single(item => item.Name == "SelectionSummaryScroll");
            var summary = (TextBlock)scroll.Content;
            if (scroll.MaxHeight != 120 || scroll.VerticalScrollBarVisibility != ScrollBarVisibility.Auto || !summary.Text.Contains(task.Name)
                || !Equals(summary.ToolTip, summary.Text)) throw new InvalidOperationException("长摘要挤占表单或全文不可查看");
            view.SetLanguage("en-US");
            scroll = details.Children.OfType<ScrollViewer>().Single(item => item.Name == "SelectionSummaryScroll");
            if (!((TextBlock)scroll.Content).Text.Contains("Current draft")) throw new InvalidOperationException("页面语言切换未刷新摘要");
        }
    }
}
