using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MotionControl.Framework.Contracts.Configuration;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.Framework.Runtime.Variables;
using MotionControl.Framework.Wpf.Monitoring;
using MotionControl.Framework.Wpf.Variables;
namespace MotionControl.OperatorPages.Checks
{
    /// <summary>筛选内容区内部离屏预览，不创建窗口、不连接设备，不等同Sandbox验收。</summary>
    internal static class V14FilterLayoutChecks
    {
        /// <summary>在现有STA应用中核对绑定列的中英文表头，输出8张PNG并检查主要控件边界。</summary>
        internal static void Run()
        {
            string output = Path.GetFullPath(Path.Combine(".artifacts", "ui-filter-layout"));
            Directory.CreateDirectory(output);
            foreach (string language in new[] { "zh-CN", "en-US" })
                foreach (int width in new[] { 824, 1200 })
                {
                    int height = width == 824 ? 600 : 800;
                    var mappings = Enumerable.Range(0, 16).Select(index => new AxisHardwareDefinition { Name = "Axis_" + index + "_长名称", ControllerId = "Card01", AxisNumber = index, EngineeringUnit = "mm" }).ToArray();
                    var axis = new AxisMonitorView(false, null, mappings);
                    SetLanguage(axis, language); axis.RefreshLocalizedText();
                    ((TextBox)axis.FindName("AxisSearchBox")).Text = "Axis";
                    ((CheckBox)axis.FindName("AttentionOnlyBox")).IsChecked = true;
                    ((DataGrid)axis.FindName("AxisStatusGrid")).SelectedIndex = 0;
                    Render(axis, output, "axis-filter", language, width, height,
                        new[] { "AxisSearchBox", "AttentionOnlyBox", "ClearAxisFilterButton", "AxisMatchText", "AxisStatusGrid", "AxisDetailText" });
                    var axisDetail = (TextBox)axis.FindName("AxisDetailText");
                    if (!axisDetail.Text.Contains(Environment.NewLine) || axisDetail.ActualHeight < 50)
                        throw new InvalidOperationException("选中轴详情未分行显示：" + language + "/" + width);
                    var definitions = Enumerable.Range(0, 20).Select(index => new DeviceVariableDefinition { Name = "Variable_" + index,
                        DataType = "Boolean", InitialValue = "false", Description = "长说明：用于内部布局检查，不关联设备。", AllowOperatorWrite = index == 0 }).ToArray();
                    var variables = new DeviceVariableView(new DeviceVariableStore(definitions), new MachineConfigurationStore(Path.Combine(output, "unused-config")), "Machine01");
                    SetLanguage(variables, language); variables.RefreshLocalizedText(); variables.FocusVariableByName("Variable_0");
                    var variableGrid = (DataGrid)variables.FindName("VariablesGrid");
                    var writeColumn = variableGrid.Columns.OfType<DataGridCheckBoxColumn>()
                        .Single(column => ((Binding)column.Binding).Path.Path == "AllowOperatorWrite");
                    if (!Equals(writeColumn.Header, variables.TryFindResource("变量人工可写")))
                        throw new InvalidOperationException("变量人工可写列未随页面语言刷新：" + language);
                    var unitColumn = variableGrid.Columns.OfType<DataGridTextColumn>()
                        .Single(column => ((Binding)column.Binding).Path.Path == "EngineeringUnitDisplay");
                    if (!Equals(unitColumn.Header, variables.TryFindResource("变量工程单位")))
                        throw new InvalidOperationException("变量工程单位列未随页面语言刷新：" + language);
                    ((TextBox)variables.FindName("VariableSearchBox")).Text = "Variable";
                    Render(variables, output, "variable-filter", language, width, height,
                        new[] { "VariableSearchBox", "ClearVariableSearchButton", "VariableSearchCountText", "VariablesGrid", "ManualValueTextBox", "WriteValueButton" });
                }
            File.WriteAllText(Path.Combine(output, "README.txt"), "Internal offscreen layout only. No window was activated; no device or mouse was controlled. Not Sandbox or hardware acceptance.\r\nEntry: V14FilterLayoutChecks.Run().");
        }
        /// <summary>仅为本控件加载语言资源，不改变应用全局语言或设备设置。</summary>
        private static void SetLanguage(FrameworkElement view, string language)
        {
            view.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/MotionControl.Host.Wpf;component/Localization/Strings." + language + ".xaml", UriKind.Relative) });
        }
        /// <summary>只做布局测量和位图渲染，检查筛选与表格在指定内容区边界内。</summary>
        private static void Render(FrameworkElement view, string output, string name, string language, int width, int height, string[] controls)
        {
            view.Width = width; view.Height = height;
            view.Measure(new Size(width, height)); view.Arrange(new Rect(0, 0, width, height)); view.UpdateLayout();
            // DataGrid星号列的延迟测量需要调度排空；不创建或激活任何Window。
            for (int round = 0; round < 3; round++)
            {
                view.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(() => { }));
                view.Measure(new Size(width, height)); view.Arrange(new Rect(0, 0, width, height)); view.UpdateLayout();
            }
            foreach (string controlName in controls)
            {
                var control = (FrameworkElement)view.FindName(controlName);
                Rect bounds = control.TransformToAncestor(view).TransformBounds(new Rect(control.RenderSize));
                if (bounds.Width <= 0 || bounds.Height <= 0 || bounds.Left < -1 || bounds.Top < -1 || bounds.Right > width + 1 || bounds.Bottom > height + 1)
                    throw new InvalidOperationException(name + "/" + language + "/" + width + ": 控件超出布局或不可见：" + controlName + " " + bounds);
                if (control is DataGrid table)
                    foreach (var column in table.Columns)
                        if (column.ActualWidth < Math.Max(45, column.MinWidth) - 1)
                            throw new InvalidOperationException("主列宽度不可读：" + name + "/" + column.Header + " width=" + column.ActualWidth);
                if (control is DataGrid && control.ActualHeight < 140) throw new InvalidOperationException("筛选区挤占了主要表格：" + name);
            }
            var tables = controls.Select(controlName => view.FindName(controlName)).OfType<DataGrid>();
            File.WriteAllText(Path.Combine(output, name + "-" + language + "-" + width + "x" + height + "-columns.txt"),
                string.Join("\r\n", tables.SelectMany(table => table.Columns.Select(column => column.Header + "=" + column.ActualWidth.ToString(System.Globalization.CultureInfo.InvariantCulture)))));
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.Combine(output, name + "-" + language + "-" + width + "x" + height + ".png"))) encoder.Save(stream);
        }
    }
}
