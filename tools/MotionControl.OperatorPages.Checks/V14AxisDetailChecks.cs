using System;
using System.Reflection;
using System.Windows.Controls;
using MotionControl.Framework.Wpf.Monitoring;
namespace MotionControl.OperatorPages.Checks
{
    /// <summary>验证选中轴详情仅使用已有映射和缓存，不发起采集。</summary>
    internal static class V14AxisDetailChecks
    {
        /// <summary>验证长名称、无缓存未知、原始错误及语言刷新。</summary>
        internal static void Run()
        {
            var view = new AxisMonitorView(false, null, null);
            var grid = (DataGrid)view.FindName("AxisStatusGrid");
            var text = (TextBox)view.FindName("AxisDetailText");
            var type = typeof(AxisMonitorView).Assembly.GetType("MotionControl.Framework.Wpf.Monitoring.AxisMonitorRow");
            object row = Activator.CreateInstance(type, true);
            string name = new string('A', 200);
            type.GetProperty("LogicalAxisName").SetValue(row, name);
            type.GetProperty("PhysicalAxisText").SetValue(row, "Card-X / Axis-13");
            type.GetProperty("ReadErrorMessage").SetValue(row, "原始错误 SDK-97");
            type.GetProperty("HasReadError").SetValue(row, true);
            grid.ItemsSource = new[] { row };
            view.Resources["轴详情格式"] = "{0}|{1}|{2}|{3}|{4}|{5}|{6}|{7}";
            view.Resources["轴详情未知"] = "UNKNOWN";
            grid.SelectedItem = row;
            Require(text.Text.Contains(name) && text.Text.Contains("Card-X / Axis-13") && text.Text.Contains("原始错误 SDK-97") && text.Text.Contains("UNKNOWN"), "完整映射、原始错误或未知提示缺失");
            view.Resources["轴详情未知"] = "未知";
            typeof(AxisMonitorView).GetMethod("RefreshAxisDetail", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(view, null);
            Require(text.Text.Contains("未知") && ReferenceEquals(grid.SelectedItem, row), "语言重绘改变选择或沿用旧文字");
            grid.SelectedItem = null;
            Require(!text.Text.Contains(name), "取消选择仍显示旧轴详情");
        }

        /// <summary>失败时给出详情边界诊断。</summary>
        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
