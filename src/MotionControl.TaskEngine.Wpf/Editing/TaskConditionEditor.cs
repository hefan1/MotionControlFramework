using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Definitions;

namespace MotionControl.TaskEngine.Wpf.Editing
{
    /// <summary>具体的比较、全部、任一和取反条件表单，不要求编写表达式。</summary>
    internal sealed class TaskConditionEditor : StackPanel
    {
        /// <summary>现有对象目录。</summary>
        private readonly TaskResourceCatalog _catalog;
        /// <summary>当前语言供新增子条件使用。</summary>
        private readonly string _language;
        /// <summary>当前编辑深度，避免界面无限嵌套。</summary>
        private readonly int _depth;
        /// <summary>条件组合方式。</summary>
        private readonly ComboBox _kind;
        /// <summary>具体字段容器。</summary>
        private readonly StackPanel _body = new StackPanel();
        /// <summary>比较左值、操作符和右值。</summary>
        private TaskValueEditor _left, _right;
        /// <summary>比较操作符选择。</summary>
        private ComboBox _comparison;
        /// <summary>明确的子条件表单集合。</summary>
        private readonly List<TaskConditionEditor> _children = new List<TaskConditionEditor>();

        /// <summary>从现有条件创建表单，读取时才写回新模型。</summary>
        internal TaskConditionEditor(TaskResourceCatalog catalog, TaskCondition condition, string language, int depth = 0)
        {
            _catalog = catalog; _language = language; _depth = depth; TaskEditingUi.SetLanguage(this, language);
            condition = condition ?? TaskEditorNodes.Condition();
            _kind = TaskEditingUi.Choices(this, new object[] { TaskConditionKind.Compare, TaskConditionKind.All, TaskConditionKind.Any, TaskConditionKind.Not },
                new[] { "比较两个值", "全部成立", "任一成立", "取反" }, condition.Kind);
            Children.Add(_kind); Children.Add(_body); Build(condition);
            _kind.SelectionChanged += KindChanged;
        }
        /// <summary>组合类型变化时建立明确的新条件，不保留失效的隐藏字段。</summary>
        private void KindChanged(object sender, SelectionChangedEventArgs e)
        { if (_kind.SelectedValue != null) Build((TaskConditionKind)_kind.SelectedValue == TaskConditionKind.Compare ? TaskEditorNodes.Condition() : new TaskCondition { Kind = (TaskConditionKind)_kind.SelectedValue }); }
        /// <summary>按有限的四种类型直接创建控件。</summary>
        private void Build(TaskCondition condition)
        {
            _body.Children.Clear(); _children.Clear();
            if (condition.Kind == TaskConditionKind.Compare)
            {
                _left = new TaskValueEditor(_catalog, condition.Left, _language);
                _right = new TaskValueEditor(_catalog, condition.Right, _language);
                _comparison = TaskEditingUi.Choices(this, new object[] { TaskComparison.Equal, TaskComparison.NotEqual, TaskComparison.Less, TaskComparison.LessOrEqual, TaskComparison.Greater, TaskComparison.GreaterOrEqual },
                    new[] { "等于", "不等于", "小于", "小于等于", "大于", "大于等于" }, condition.Comparison);
                _body.Children.Add(TaskEditingUi.Label(this, "左值")); _body.Children.Add(_left);
                _body.Children.Add(TaskEditingUi.Label(this, "比较")); _body.Children.Add(_comparison);
                _body.Children.Add(TaskEditingUi.Label(this, "右值")); _body.Children.Add(_right);
            }
            else
            {
                foreach (var child in condition.Conditions) AddChild(child);
                if (_children.Count == 0) AddChild(null);
                if (condition.Kind != TaskConditionKind.Not) _body.Children.Add(TaskEditingUi.Button(this, "添加条件", AddCondition));
            }
        }
        /// <summary>明确新增一个子条件，限制编辑器深度。</summary>
        private void AddCondition(object sender, RoutedEventArgs e)
        {
            if (_depth >= 8) { MessageBox.Show(TaskEditingUi.T(this, "条件最多编辑九层")); return; }
            AddChild(null);
        }
        /// <summary>子条件带自己的移除入口，避免删除父条件。</summary>
        private void AddChild(TaskCondition value)
        {
            if (_depth >= 9) throw new InvalidOperationException("条件层数过深，请拆分工艺判断后编辑。");
            var child = new TaskConditionEditor(_catalog, value, _language, _depth + 1);
            var panel = new StackPanel { Margin = new Thickness(10, 6, 0, 6) };
            var remove = TaskEditingUi.Button(this, "移除此条件", RemoveCondition); remove.Tag = child;
            panel.Children.Add(child); panel.Children.Add(remove); child.Tag = panel;
            _children.Add(child); _body.Children.Add(panel);
        }
        /// <summary>移除明确选中的子条件，最终数量交由校验器检查。</summary>
        private void RemoveCondition(object sender, RoutedEventArgs e)
        {
            var child = (TaskConditionEditor)((Button)sender).Tag;
            _children.Remove(child); _body.Children.Remove((UIElement)child.Tag);
        }
        /// <summary>读取当前表单，组合条件只保存子条件，不混入比较字段。</summary>
        internal TaskCondition Read()
        {
            var condition = new TaskCondition { Kind = (TaskConditionKind)_kind.SelectedValue };
            if (condition.Kind == TaskConditionKind.Compare)
            { condition.Left = _left.Read(); condition.Right = _right.Read(); condition.Comparison = _comparison.SelectedValue as TaskComparison?; }
            else foreach (var child in _children) condition.Conditions.Add(child.Read());
            return condition;
        }
    }
}
