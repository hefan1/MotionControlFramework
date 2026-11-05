using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Wpf.Editing;
namespace MotionControl.OperatorPages.Checks
{
    /// <summary>新增步骤插入位置的只读说明检查，不弹出模态框或执行新增。</summary>
    internal static class V14InsertionContextChecks
    {
        /// <summary>覆盖顺序、普通步骤、资源、条件根、并行分支及缺失对象和长文本。</summary>
        internal static void Run()
        {
            var leaf = new DelayNode { Id = "leaf", Name = "同名", Milliseconds = 20 };
            var scope = new ResourceScopeNode { Id = "scope", Name = "资源", Body = new SequenceNode { Id = "body", Name = "内部顺序" } };
            var branch = new ConditionBranchNode { Id = "condition", Name = "条件" };
            var parallel = new ParallelGroupNode { Id = "parallel", Name = "并行" };
            var root = new SequenceNode { Id = "root", Name = "同名", Children = new List<TaskNode> { leaf, scope, branch, parallel } };
            var task = new TaskDefinition { TaskId = "task", Name = new string('长', 400), Root = root };
            var type = typeof(TaskEditorView).Assembly.GetType("MotionControl.TaskEngine.Wpf.Editing.TaskEditorSelectionSummary");
            var method = type.GetMethod("InsertionContext", BindingFlags.Static | BindingFlags.NonPublic);
            var objects = new TaskNode[] { root, leaf, scope, branch, parallel, new DelayNode { Id = "leaf", Name = "同名" } };
            string[] expected = { "所选顺序结构末尾", "所选步骤之后", "资源范围内部顺序结构末尾", "此目标不能接受插入", "新增顺序分支", "对象不在当前草稿" };
            for (int i = 0; i < objects.Length; i++)
            {
                string text = (string)method.Invoke(null, new object[] { task, objects[i], false });
                if (!text.Contains(expected[i])) throw new InvalidOperationException("插入说明不符合已有规则：" + expected[i]);
            }
            string english = (string)method.Invoke(null, new object[] { task, leaf, true });
            if (!english.Contains("root] > 同名 [leaf]") || !english.Contains("after the selected step") || !english.Contains("no device action"))
                throw new InvalidOperationException("英文或对象父级路径缺失");
            var dialogType = typeof(TaskEditorView).Assembly.GetType("MotionControl.TaskEngine.Wpf.Editing.TaskStepTypeDialog");
            var dialog = (Window)Activator.CreateInstance(dialogType, BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { "en-US", english }, null);
            try
            {
                var panel = (DockPanel)dialog.Content;
                var scroll = panel.Children.OfType<ScrollViewer>().Single(item => item.Name == "InsertionContextScroll");
                var text = (TextBlock)scroll.Content;
                if (scroll.MaxHeight != 140 || scroll.VerticalScrollBarVisibility != ScrollBarVisibility.Auto || text.Text != english || !Equals(text.ToolTip, english))
                    throw new InvalidOperationException("长插入路径不能完整查看或挤占类型列表");
                var list = panel.Children.OfType<ListBox>().Single();
                if (list.Items.Count != 21 || ((ListBoxItem)list.Items[20]).IsEnabled) throw new InvalidOperationException("只读提示改变了类型能力限制");
                if (root.Children.Count != 4 || ((DelayNode)root.Children[0]).Milliseconds != 20) throw new InvalidOperationException("显示上下文修改了草稿");
            }
            finally { dialog.Close(); }
        }
    }
}
