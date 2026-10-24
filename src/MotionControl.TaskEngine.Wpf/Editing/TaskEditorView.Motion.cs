using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Wpf.Monitoring;
using MotionControl.TaskEngine.Wpf.Views;

namespace MotionControl.TaskEngine.Wpf.Editing
{
    /// <summary>运动步骤表单使用逻辑轴和工程单位，厂商参数仍由现有轴配置管理。</summary>
    public partial class TaskEditorView
    {
        /// <summary>每根轴的具体表单，折叠只改变可见性，不丢参数。</summary>
        private readonly List<TaskMotionEditRow> _motionRows = new List<TaskMotionEditRow>();

        /// <summary>单轴、轴组和回零/停轴分别创建明确字段。</summary>
        private void BuildMotion()
        {
            if (_selected is MoveAxisNode move) AddMotionRow(new AxisPositionTarget { AxisName = move.AxisName, Mode = move.Mode, Target = move.Target, Profile = move.Profile }, false);
            else if (_selected is MoveAxisGroupNode group)
            {
                Details.Children.Add(TaskEditingUi.Label(this, "全部轴到位后继续，不保证直线路径或同时到达"));
                Details.Children.Add(TaskEditingUi.Button(this, "添加轴目标", AddTarget));
                foreach (var target in group.Targets) AddMotionRow(target, true);
            }
            else if (_selected is AxisCommandNode command)
            {
                AddField("Axis", "逻辑轴", TaskEditingUi.Names(_catalog.Axes.Keys, command.AxisName));
                AddField("Command", "轴命令", TaskEditingUi.Choices(this, new object[] { TaskAxisCommand.Home, TaskAxisCommand.Stop }, new[] { "回零", "停轴" }, command.Command));
                _fields["StopLabel"] = AddField("Stop", "停止方式(仅停轴使用)", TaskEditingUi.Choices(this, new object[] { AxisStopMode.Controlled, AxisStopMode.Abort }, new[] { "减速停止", "立即中止" }, command.StopMode));
                ((ComboBox)_fields["Command"]).SelectionChanged += CommandChanged; CommandChanged(null, null);
                Details.Children.Add(TaskEditingUi.Label(this, "回零使用现有轴配置，不在任务中填写厂商模式编号"));
                AddReference("轴映射配置", "@Axis"); AddReference("轴手动", "@Axis");
            }
        }

        /// <summary>回零不显示无关的停止方式，停轴才要求选择。</summary>
        private void CommandChanged(object sender, SelectionChangedEventArgs e)
        {
            bool stopping = (TaskAxisCommand)((ComboBox)_fields["Command"]).SelectedValue == TaskAxisCommand.Stop;
            _fields["Stop"].Visibility = _fields["StopLabel"].Visibility = stopping ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>每轴独立目标与速度；轴组按行折叠，常用参数不堆成大表。</summary>
        private void AddMotionRow(AxisPositionTarget target, bool group)
        {
            var row = new TaskMotionEditRow();
            row.Axis = TaskEditingUi.Names(_catalog.Axes.Keys, target.AxisName);
            row.Mode = TaskEditingUi.Choices(this, new object[] { TaskMoveMode.Absolute, TaskMoveMode.Relative }, new[] { "绝对位置", "相对距离" }, target.Mode);
            row.Value = new TaskValueEditor(_catalog, target.Target, _language, true, true, target.AxisName); row.Value.FixAxis(); row.Value.AllowTeachPoint(target.Mode == TaskMoveMode.Absolute);
            row.Speed = TaskEditingUi.Text(target.Profile?.Speed.ToString(CultureInfo.InvariantCulture));
            row.Acceleration = TaskEditingUi.Text(target.Profile?.Acceleration.ToString(CultureInfo.InvariantCulture));
            row.Deceleration = TaskEditingUi.Text(target.Profile?.Deceleration.ToString(CultureInfo.InvariantCulture));
            var panel = new StackPanel { Margin = new Thickness(7) };
            panel.Children.Add(TaskEditingUi.Label(this, "逻辑轴")); panel.Children.Add(row.Axis);
            panel.Children.Add(TaskEditingUi.Label(this, "定位方式")); panel.Children.Add(row.Mode);
            panel.Children.Add(TaskEditingUi.Label(this, "目标来源和值")); panel.Children.Add(row.Value);
            row.Unit = TaskEditingUi.Label(this, "工程单位"); panel.Children.Add(row.Unit); UpdateMotionUnit(row);
            panel.Children.Add(TaskEditingUi.Label(this, "速度")); panel.Children.Add(row.Speed);
            panel.Children.Add(TaskEditingUi.Label(this, "加速度")); panel.Children.Add(row.Acceleration);
            panel.Children.Add(TaskEditingUi.Label(this, "减速度")); panel.Children.Add(row.Deceleration);
            var map = TaskEditingUi.Button(this, "查看轴映射", OpenAxisMapping); map.Tag = row; panel.Children.Add(map);
            var manual = TaskEditingUi.Button(this, "查看轴手动", OpenAxisManual); manual.Tag = row; panel.Children.Add(manual);
            if (group) { var remove = TaskEditingUi.Button(this, "移除此轴目标", RemoveTarget); remove.Tag = row; panel.Children.Add(remove); }
            row.Container = new Expander { Header = string.IsNullOrEmpty(target.AxisName) ? T("未选择逻辑轴") : target.AxisName, Content = panel,
                IsExpanded = !group || _motionRows.Count == 0, Margin = new Thickness(0, 7, 0, 0) };
            row.Axis.Tag = row; row.Mode.Tag = row; row.Axis.SelectionChanged += MotionAxisChanged; row.Mode.SelectionChanged += MotionModeChanged;
            _motionRows.Add(row); Details.Children.Add(row.Container);
        }
        /// <summary>用户选轴时同步更新单位和该轴点位，不取手动速度代替工艺速度。</summary>
        private void MotionAxisChanged(object sender, SelectionChangedEventArgs e)
        {
            var row = (TaskMotionEditRow)((ComboBox)sender).Tag; row.Value.SelectAxis(row.Axis.SelectedItem as string);
            row.Container.Header = row.Axis.SelectedItem as string; UpdateMotionUnit(row);
        }
        /// <summary>相对模式不允许新选绝对示教点。</summary>
        private void MotionModeChanged(object sender, SelectionChangedEventArgs e)
        { var row = (TaskMotionEditRow)((ComboBox)sender).Tag; row.Value.AllowTeachPoint((TaskMoveMode)row.Mode.SelectedValue == TaskMoveMode.Absolute); }
        /// <summary>显示当前轴的位移、速度和加减速度单位。</summary>
        private void UpdateMotionUnit(TaskMotionEditRow row)
        {
            MotionControl.TaskEngine.Catalog.TaskAxisInfo axis;
            string unit = _catalog.Axes.TryGetValue(row.Axis.SelectedItem as string ?? "", out axis) ? axis.EngineeringUnit : "?";
            if (string.IsNullOrWhiteSpace(unit)) unit = "?";
            row.Unit.Text = T("工程单位") + "：" + unit + " / " + unit + "/s / " + unit + "/s²";
        }
        /// <summary>新轴目标必须由人员明确选轴。</summary>
        private void AddTarget(object sender, RoutedEventArgs e) { AddMotionRow(TaskEditorNodes.Target(), true); IsDirty = true; }
        /// <summary>删除选中轴目标不删除步骤，剩余轴数由检查器验证。</summary>
        private void RemoveTarget(object sender, RoutedEventArgs e)
        { var row = (TaskMotionEditRow)((Button)sender).Tag; _motionRows.Remove(row); Details.Children.Remove(row.Container); IsDirty = true; }
        /// <summary>使用当前选择轴导航，避免修改下拉后跳到旧轴。</summary>
        private void OpenAxisMapping(object sender, RoutedEventArgs e) { OpenMotionReference(sender, "轴映射配置"); }
        /// <summary>手动页只定位，不发动作。</summary>
        private void OpenAxisManual(object sender, RoutedEventArgs e) { OpenMotionReference(sender, "轴手动"); }
        /// <summary>将当前行的逻辑轴发给 Host 导航。</summary>
        private void OpenMotionReference(object sender, string page)
        {
            var row = (TaskMotionEditRow)((Button)sender).Tag; string name = row.Axis.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(name)) { StatusText.Text = T("未选择或引用失效"); return; }
            ReferenceRequested?.Invoke(this, new TaskReferenceEventArgs(page, name));
        }
        /// <summary>先解析全部轴行，再一次性替换该步骤参数。</summary>
        private void ReadMotion()
        {
            if (_selected is AxisCommandNode command)
            {
                command.AxisName = ((ComboBox)_fields["Axis"]).SelectedItem as string ?? "";
                command.Command = (TaskAxisCommand)((ComboBox)_fields["Command"]).SelectedValue;
                command.StopMode = command.Command == TaskAxisCommand.Stop ? (AxisStopMode?)((ComboBox)_fields["Stop"]).SelectedValue : null; return;
            }
            var targets = _motionRows.Select(row => new AxisPositionTarget { AxisName = row.Axis.SelectedItem as string ?? "", Mode = (TaskMoveMode)row.Mode.SelectedValue,
                Target = row.Value.Read(), Profile = new TaskMotionProfile { Speed = TaskEditingUi.Number(row.Speed, "速度"), Acceleration = TaskEditingUi.Number(row.Acceleration, "加速度"), Deceleration = TaskEditingUi.Number(row.Deceleration, "减速度") } }).ToList();
            if (_selected is MoveAxisGroupNode group) group.Targets = targets;
            else if (_selected is MoveAxisNode move) { var first = targets[0]; move.AxisName = first.AxisName; move.Mode = first.Mode; move.Target = first.Target; move.Profile = first.Profile; }
        }
    }
    /// <summary>一根轴的具体控件集合。</summary>
    internal sealed class TaskMotionEditRow
    {
        /// <summary>逻辑轴选择。</summary>
        internal ComboBox Axis;
        /// <summary>绝对位置或相对距离。</summary>
        internal ComboBox Mode;
        /// <summary>目标引用字段。</summary>
        internal TaskValueEditor Value;
        /// <summary>独立速度。</summary>
        internal TextBox Speed;
        /// <summary>独立加速度。</summary>
        internal TextBox Acceleration;
        /// <summary>独立减速度。</summary>
        internal TextBox Deceleration;
        /// <summary>工程单位提示。</summary>
        internal TextBlock Unit;
        /// <summary>轴组中的折叠行。</summary>
        internal Expander Container;
    }
}
