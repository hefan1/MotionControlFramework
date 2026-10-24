using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Definitions;

namespace MotionControl.TaskEngine.Wpf.Editing
{
    /// <summary>资源进入或退出的明确条件表单，不把条件当作持续防碰撞监控。</summary>
    internal sealed class TaskInterlockEditor : StackPanel
    {
        /// <summary>是否启用本组条件。</summary>
        private readonly CheckBox _enabled;
        /// <summary>是否启用设备变量条件。</summary>
        private readonly CheckBox _useCondition;
        /// <summary>变量条件的具体编辑控件。</summary>
        private readonly TaskConditionEditor _condition;
        /// <summary>静止轴位置范围表。</summary>
        private readonly DataGrid _axes;
        /// <summary>逻辑 DI 条件表。</summary>
        private readonly DataGrid _inputs;
        /// <summary>编辑器自己的范围副本。</summary>
        private readonly List<TaskAxisInterlock> _axisRows;
        /// <summary>编辑器自己的输入副本。</summary>
        private readonly List<TaskInputInterlock> _inputRows;
        /// <summary>仅在启用时显示详细条件，默认不堆满资源页。</summary>
        private readonly StackPanel _detailPanel = new StackPanel();

        /// <summary>复制具体字段，避免未确认编辑直接修改任务对象。</summary>
        internal TaskInterlockEditor(TaskResourceCatalog catalog, TaskInterlock value, string language)
        {
            TaskEditingUi.SetLanguage(this, language);
            _enabled = new CheckBox { Content = TaskEditingUi.T(this, "启用本组联锁"), IsChecked = value != null, Margin = new Thickness(0, 6, 0, 6) };
            Children.Add(_enabled);
            _axisRows = (value?.Axes ?? new List<TaskAxisInterlock>()).Select(item => new TaskAxisInterlock { AxisName = item.AxisName,
                MinimumPosition = item.MinimumPosition, MaximumPosition = item.MaximumPosition, RequireHomed = item.RequireHomed }).ToList();
            _inputRows = (value?.Inputs ?? new List<TaskInputInterlock>()).Select(item => new TaskInputInterlock { InputName = item.InputName, Expected = item.Expected }).ToList();
            _axes = NewGrid(); _axes.ItemsSource = _axisRows;
            _axes.Columns.Add(new DataGridComboBoxColumn { Header = TaskEditingUi.T(this, "轴"), ItemsSource = new[] { "" }.Concat(catalog.Axes.Keys).Concat(_axisRows.Select(item => item.AxisName)).Distinct().ToArray(), SelectedItemBinding = new Binding("AxisName") });
            _axes.Columns.Add(NumberColumn("下限", "MinimumPosition")); _axes.Columns.Add(NumberColumn("上限", "MaximumPosition"));
            _axes.Columns.Add(new DataGridCheckBoxColumn { Header = TaskEditingUi.T(this, "要求回零"), Binding = new Binding("RequireHomed") });
            Children.Add(TaskEditingUi.Label(this, "静止轴位置范围")); Children.Add(_axes);
            var axisButtons = new WrapPanel(); axisButtons.Children.Add(TaskEditingUi.Button(this, "添加轴条件", AddAxis)); axisButtons.Children.Add(TaskEditingUi.Button(this, "移除轴条件", RemoveAxis)); Children.Add(axisButtons);
            _inputs = NewGrid(); _inputs.ItemsSource = _inputRows;
            _inputs.Columns.Add(new DataGridComboBoxColumn { Header = TaskEditingUi.T(this, "逻辑输入"), ItemsSource = new[] { "" }.Concat(catalog.Inputs).Concat(_inputRows.Select(item => item.InputName)).Distinct().ToArray(), SelectedItemBinding = new Binding("InputName") });
            _inputs.Columns.Add(new DataGridCheckBoxColumn { Header = TaskEditingUi.T(this, "期望有效"), IsThreeState = true, Binding = new Binding("Expected") });
            Children.Add(TaskEditingUi.Label(this, "输入条件")); Children.Add(_inputs);
            var inputButtons = new WrapPanel(); inputButtons.Children.Add(TaskEditingUi.Button(this, "添加输入条件", AddInput)); inputButtons.Children.Add(TaskEditingUi.Button(this, "移除输入条件", RemoveInput)); Children.Add(inputButtons);
            _useCondition = new CheckBox { Content = TaskEditingUi.T(this, "使用变量条件"), IsChecked = value?.Condition != null, Margin = new Thickness(0, 8, 0, 5) };
            _condition = new TaskConditionEditor(catalog, value?.Condition ?? TaskEditorNodes.Condition(), language);
            Children.Add(_useCondition); Children.Add(_condition);
            _condition.Visibility = _useCondition.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            _useCondition.Checked += ConditionVisibility; _useCondition.Unchecked += ConditionVisibility;
            foreach (UIElement control in Children.Cast<UIElement>().Skip(1).ToList()) { Children.Remove(control); _detailPanel.Children.Add(control); }
            Children.Add(_detailPanel); InterlockVisibility(null, null); _enabled.Checked += InterlockVisibility; _enabled.Unchecked += InterlockVisibility;
        }
        /// <summary>条件数值使用 WPF 校验，错误时提交失败而不是悄悄沿用旧值。</summary>
        private DataGridTextColumn NumberColumn(string header, string property)
        { return new DataGridTextColumn { Header = TaskEditingUi.T(this, header), Binding = new Binding(property) { Mode = BindingMode.TwoWay, ValidatesOnExceptions = true, ConverterCulture = System.Globalization.CultureInfo.InvariantCulture } }; }
        /// <summary>创建局部可编辑表，禁止自动新增空行。</summary>
        private static DataGrid NewGrid()
        { return new DataGrid { AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, MinHeight = 55, MaxHeight = 155, ColumnWidth = new DataGridLength(1, DataGridLengthUnitType.Star) }; }
        /// <summary>新增尚未选择逻辑轴的行，必须通过后续检查。</summary>
        private void AddAxis(object sender, RoutedEventArgs e) { _axisRows.Add(new TaskAxisInterlock { AxisName = "" }); _axes.Items.Refresh(); }
        /// <summary>只移除表中明确选中行。</summary>
        private void RemoveAxis(object sender, RoutedEventArgs e) { if (_axes.SelectedItem is TaskAxisInterlock item) { _axisRows.Remove(item); _axes.Items.Refresh(); } }
        /// <summary>输入新增行保留未选择状态，不能默认放行。</summary>
        private void AddInput(object sender, RoutedEventArgs e) { _inputRows.Add(new TaskInputInterlock { InputName = "", Expected = null }); _inputs.Items.Refresh(); }
        /// <summary>只移除选中输入条件。</summary>
        private void RemoveInput(object sender, RoutedEventArgs e) { if (_inputs.SelectedItem is TaskInputInterlock item) { _inputRows.Remove(item); _inputs.Items.Refresh(); } }
        /// <summary>变量条件仅在明确勾选时参与保存。</summary>
        private void ConditionVisibility(object sender, RoutedEventArgs e) { _condition.Visibility = _useCondition.IsChecked == true ? Visibility.Visible : Visibility.Collapsed; }
        /// <summary>未启用的联锁隐藏字段，但保留本次编辑输入。</summary>
        private void InterlockVisibility(object sender, RoutedEventArgs e) { _detailPanel.Visibility = _enabled.IsChecked == true ? Visibility.Visible : Visibility.Collapsed; }
        /// <summary>提交单元格后建立条件模型，实际资源关联交由已有校验器检查。</summary>
        internal TaskInterlock Read()
        {
            if (_enabled.IsChecked != true) return null;
            if (!_axes.CommitEdit(DataGridEditingUnit.Cell, true) || !_axes.CommitEdit(DataGridEditingUnit.Row, true)
                || !_inputs.CommitEdit(DataGridEditingUnit.Cell, true) || !_inputs.CommitEdit(DataGridEditingUnit.Row, true))
                throw new InvalidOperationException("联锁表中存在无效数值，请先修正。");
            return new TaskInterlock { Axes = _axisRows.ToList(), Inputs = _inputRows.ToList(), Condition = _useCondition.IsChecked == true ? _condition.Read() : null };
        }
    }
}
