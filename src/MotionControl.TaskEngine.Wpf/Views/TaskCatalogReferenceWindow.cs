using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Wpf.Monitoring;

namespace MotionControl.TaskEngine.Wpf.Views
{
    /// <summary>没有生产 Host 时查看只读目录；不伪造手动轴或 IO 控制功能。</summary>
    public sealed class TaskCatalogReferenceWindow : Window
    {
        /// <summary>显示当前会话使用的目录副本和示教值。</summary>
        public TaskCatalogReferenceWindow(TaskResourceCatalog catalog, string page, string name, Func<string, string> text)
        {
            Title = text("关联配置") + " · " + name;
            Width = 620; Height = 440; WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var fields = new List<TaskDetailField> { new TaskDetailField(text("名称"), name),
                new TaskDetailField(text("来源"), text("当前会话目录副本")) };
            if (catalog.Axes.TryGetValue(name, out var axis))
            {
                fields.Add(new TaskDetailField(text("所属控制卡"), axis.ControllerId));
                fields.Add(new TaskDetailField(text("单位"), axis.EngineeringUnit));
                fields.Add(new TaskDetailField(text("位置范围"), axis.MinimumPosition + " … " + axis.MaximumPosition));
                foreach (var point in catalog.TeachPoints.Where(item => item.AxisName == name))
                    fields.Add(new TaskDetailField(point.Name + " [" + point.PointId + "]", point.Position?.ToString() ?? text("未知")));
            }
            if (catalog.Variables.TryGetValue(name, out var type)) fields.Add(new TaskDetailField(text("类型"), TaskMonitorView.VariableTypeName(type.ToString())));
            if (catalog.Inputs.Contains(name)) fields.Add(new TaskDetailField(text("方向"), "DI"));
            if (catalog.Outputs.Contains(name)) fields.Add(new TaskDetailField(text("方向"), "DO"));
            var grid = new DataGrid { ItemsSource = fields, AutoGenerateColumns = false, IsReadOnly = true,
                CanUserAddRows = false, Margin = new Thickness(14), HeadersVisibility = DataGridHeadersVisibility.Column };
            grid.Columns.Add(new DataGridTextColumn { Header = text("项目"), Binding = new System.Windows.Data.Binding("Name"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            grid.Columns.Add(new DataGridTextColumn { Header = text("值"), Binding = new System.Windows.Data.Binding("Value"), Width = new DataGridLength(2, DataGridLengthUnitType.Star) });
            Content = grid;
        }
    }
}
