using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Definitions;

namespace MotionControl.TaskEngine.Wpf.Editing
{
    /// <summary>固定值、设备变量和示教点的具体选择控件，只编辑引用，不写设备变量。</summary>
    internal sealed class TaskValueEditor : StackPanel
    {
        /// <summary>当前设备共享目录。</summary>
        private readonly TaskResourceCatalog _catalog;
        /// <summary>参数来源。</summary>
        private readonly ComboBox _source;
        /// <summary>常量类型。</summary>
        private readonly ComboBox _type;
        /// <summary>常量文本。</summary>
        private readonly TextBox _constant;
        /// <summary>设备变量选择。</summary>
        private readonly ComboBox _variable;
        /// <summary>示教点所属逻辑轴。</summary>
        private readonly ComboBox _axis;
        /// <summary>稳定示教点编号选择。</summary>
        private readonly ComboBox _point;
        /// <summary>运动目标的所属轴已由外层行选择，不重复显示第二个轴下拉。</summary>
        private bool _fixedAxis;
        /// <summary>目标表只读显示当前来源名称，不触发解析或写入。</summary>
        internal string SourceText { get { return (_source.SelectedItem as ComboBoxItem)?.Content as string ?? "—"; } }
        /// <summary>目标表显示当前输入；是否有效仍由检查器判定。</summary>
        internal string ValueText
        {
            get
            {
                var source = _source.SelectedValue as TaskValueSource?;
                if (source == TaskValueSource.Variable) return _variable.SelectedItem as string ?? "";
                if (source == TaskValueSource.TeachPoint) return (_point.SelectedItem as ComboBoxItem)?.Content as string ?? "";
                return source == TaskValueSource.Constant ? _constant.Text : "";
            }
        }

        /// <summary>按字段用途过滤目录，保留旧引用供校验。</summary>
        internal TaskValueEditor(TaskResourceCatalog catalog, TaskValueBinding binding, string language, bool numeric = false, bool teachPoint = true, string axisName = null)
        {
            _catalog = catalog; TaskEditingUi.SetLanguage(this, language);
            bool missing = binding == null;
            binding = binding ?? new TaskValueBinding { Source = TaskValueSource.Constant, Constant = "" };
            _source = TaskEditingUi.Choices(this, teachPoint ? new object[] { TaskValueSource.Constant, TaskValueSource.Variable, TaskValueSource.TeachPoint } : new object[] { TaskValueSource.Constant, TaskValueSource.Variable },
                teachPoint ? new[] { "固定值", "设备变量", "示教点" } : new[] { "固定值", "设备变量" }, binding.Source);
            var types = numeric ? new[] { DeviceVariableType.Number, DeviceVariableType.Integer, DeviceVariableType.UInt32, DeviceVariableType.Byte } : (DeviceVariableType[])Enum.GetValues(typeof(DeviceVariableType));
            _type = TaskEditingUi.Choices(this, types.Cast<object>().ToArray(), types.Select(TypeName).ToArray(), binding.DataType);
            _constant = TaskEditingUi.Text(binding.Constant);
            _variable = TaskEditingUi.Names(catalog.Variables.Where(item => !numeric || IsNumeric(item.Value)).Select(item => item.Key), binding.VariableName);
            _axis = TaskEditingUi.Names(catalog.Axes.Keys, binding.Source == TaskValueSource.TeachPoint ? binding.AxisName : axisName);
            _point = new ComboBox { MinHeight = 30, SelectedValuePath = "Tag" };
            Children.Add(_source); Children.Add(_type); Children.Add(_constant); Children.Add(_variable); Children.Add(_axis); Children.Add(_point);
            FillPoints(binding.PointId);
            _source.SelectionChanged += SourceChanged; _axis.SelectionChanged += AxisChanged;
            if (missing) _source.SelectedIndex = -1;
            ShowSource();
        }

        /// <summary>读取明确来源，实际值格式和引用存在性由任务校验器检查。</summary>
        internal TaskValueBinding Read()
        {
            if (_source.SelectedValue == null) throw new InvalidOperationException("请选择受支持的值来源。");
            var source = (TaskValueSource)_source.SelectedValue;
            if (source == TaskValueSource.Variable) return new TaskValueBinding { Source = source, VariableName = _variable.SelectedItem as string ?? string.Empty };
            if (source == TaskValueSource.TeachPoint) return new TaskValueBinding { Source = source, AxisName = _axis.SelectedItem as string ?? string.Empty, PointId = _point.SelectedValue as string ?? string.Empty };
            if (_type.SelectedValue == null) throw new InvalidOperationException("请选择常量类型。");
            return new TaskValueBinding { Source = source, DataType = (DeviceVariableType)_type.SelectedValue, Constant = _constant.Text };
        }

        /// <summary>运动目标的点位轴由明确的逻辑轴选择决定。</summary>
        internal void FixAxis() { _fixedAxis = true; _axis.IsEnabled = false; ShowSource(); }
        /// <summary>用户切换逻辑轴后更新点位目录并清除旧点位。</summary>
        internal void SelectAxis(string name)
        {
            _axis.SelectedItem = _axis.Items.Cast<string>().FirstOrDefault(value => string.Equals(value, name ?? string.Empty, StringComparison.OrdinalIgnoreCase));
        }
        /// <summary>相对移动禁用新选示教点；已有错误引用保留给检查定位。</summary>
        internal void AllowTeachPoint(bool allowed)
        { foreach (ComboBoxItem item in _source.Items) if ((TaskValueSource)item.Tag == TaskValueSource.TeachPoint) item.IsEnabled = allowed; }
        /// <summary>用户明确切换目标变量后，使固定值类型与变量一致，文本仍需校验。</summary>
        internal void SelectConstantType(DeviceVariableType type) { _type.SelectedValue = type; }

        /// <summary>用户明确切换轴时清空原点位，不把另一根轴的同号点位静默套用。</summary>
        private void AxisChanged(object sender, SelectionChangedEventArgs e) { FillPoints(null); }
        /// <summary>切换来源只改变可见字段，其他输入暂时保留。</summary>
        private void SourceChanged(object sender, SelectionChangedEventArgs e) { ShowSource(); }
        /// <summary>示教点保存编号，显示名称与位置，空引用保持未选择。</summary>
        private void FillPoints(string selected)
        {
            _point.Items.Clear(); _point.Items.Add(new ComboBoxItem { Tag = string.Empty, Content = string.Empty });
            foreach (var point in _catalog.TeachPoints.Where(item => string.Equals(item.AxisName, _axis.SelectedItem as string, StringComparison.OrdinalIgnoreCase)))
                _point.Items.Add(new ComboBoxItem { Tag = string.Equals(point.PointId, selected, StringComparison.OrdinalIgnoreCase) ? selected : point.PointId,
                    Content = point.Name + " [" + point.PointId + "] = " + point.Position });
            ComboBoxItem existing = _point.Items.Cast<ComboBoxItem>().FirstOrDefault(item => string.Equals((string)item.Tag, selected ?? string.Empty, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(selected) && existing == null)
                _point.Items.Add(new ComboBoxItem { Tag = selected, Content = selected + " (?)" });
            _point.SelectedValue = existing == null ? selected ?? string.Empty : (string)existing.Tag;
        }
        /// <summary>只有当前来源的字段可见，避免现场误填无关参数。</summary>
        private void ShowSource()
        {
            var source = _source.SelectedValue as TaskValueSource?;
            _type.Visibility = _constant.Visibility = source == TaskValueSource.Constant ? Visibility.Visible : Visibility.Collapsed;
            _variable.Visibility = source == TaskValueSource.Variable ? Visibility.Visible : Visibility.Collapsed;
            _point.Visibility = source == TaskValueSource.TeachPoint ? Visibility.Visible : Visibility.Collapsed;
            _axis.Visibility = source == TaskValueSource.TeachPoint && !_fixedAxis ? Visibility.Visible : Visibility.Collapsed;
        }
        /// <summary>显示熟悉的 C# 类型名，存储仍使用兼容枚举。</summary>
        internal static string TypeName(DeviceVariableType type)
        {
            switch (type) { case DeviceVariableType.Boolean: return "bool"; case DeviceVariableType.Integer: return "int"; case DeviceVariableType.Number: return "double";
                case DeviceVariableType.UInt32: return "uint"; case DeviceVariableType.Byte: return "byte"; case DeviceVariableType.Character: return "char"; case DeviceVariableType.String: return "string"; default: return "DateTime"; }
        }
        /// <summary>仅把现有数值类型提供给运动目标。</summary>
        private static bool IsNumeric(DeviceVariableType type)
        { return type == DeviceVariableType.Number || type == DeviceVariableType.Integer || type == DeviceVariableType.UInt32 || type == DeviceVariableType.Byte; }
    }
}
