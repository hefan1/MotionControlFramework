using System;
using System.Reflection;
using System.Windows;
using System.IO;
using System.Linq;
using System.Windows.Controls;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.Framework.Runtime.Variables;
using MotionControl.Framework.Wpf.Variables;
namespace MotionControl.OperatorPages.Checks
{
    /// <summary>验证变量筛选仅改变行显示，不影响当前变量和值输入。</summary>
    internal static class V14VariableSearchChecks
    {
        /// <summary>覆盖大小写、说明、零匹配、刷新与语言，且不保存或写入运行值。</summary>
        internal static void Run()
        {
            string root = Path.Combine(Path.GetTempPath(), "VariableSearch-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new DeviceVariableStore(new[] {
                    new DeviceVariableDefinition { Name = "Flag", DataType = "Boolean", InitialValue = "false", Description = "夹紧反馈", AllowOperatorWrite = true },
                    new DeviceVariableDefinition { Name = "Other", DataType = "Integer" } });
                var view = new DeviceVariableView(store, new MachineConfigurationStore(root), "Machine01");
                // 离屏 DataContext 的继承和绑定会排队，先完成绑定再模拟真实网格选行。
                var frame = new System.Windows.Threading.DispatcherFrame();
                view.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
                System.Windows.Threading.Dispatcher.PushFrame(frame);
                var actualGrid = (DataGrid)view.FindName("VariablesGrid");
                Require(actualGrid.Items.Count == 2, "夹具未完成变量网格绑定");
                actualGrid.SelectedItem = view.Variables.First(row => row.Name == "Flag");
                Require(view.SelectedVariable != null && view.SelectedVariable.Name == "Flag", "夹具未通过真实网格建立初始选择");
                var selected = view.SelectedVariable;
                var input = (TextBox)view.FindName("ManualValueTextBox");
                input.Text = "尚未提交";
                var search = (TextBox)view.FindName("VariableSearchBox");
                var grid = (DataGrid)view.FindName("VariablesGrid");
                search.Text = "FLAG";
                Require(selected.IsSearchMatch && view.Variables.Count(row => row.IsSearchMatch) == 1, "名称搜索未忽略大小写");
                search.Text = "夹紧";
                Require(selected.IsSearchMatch, "说明搜索失败");
                search.Text = "无匹配";
                Require(view.Variables.All(row => !row.IsSearchMatch), "零匹配未隐藏所有行");
                Require(grid.Items.Count == 0 && grid.SelectedItem == null, "筛选视图异常：Items=" + grid.Items.Count + "; Selected=" + (grid.SelectedItem as DeviceVariableRow)?.Name + "; View=" + grid.ItemsSource.GetType().FullName);
                Require(!((Button)view.FindName("EditVariableButton")).IsEnabled && !((Button)view.FindName("DeleteVariableButton")).IsEnabled
                    && !((Button)view.FindName("WriteValueButton")).IsEnabled, "隐藏目标仍开放操作");
                foreach (string handler in new[] { "WriteValueButton_Click", "EditVariableButton_Click", "DeleteVariableButton_Click" })
                    typeof(DeviceVariableView).GetMethod(handler, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(view, new object[] { view, new RoutedEventArgs() });
                Require(store.GetDefinitions().Count == 2 && !store.GetBoolean("Flag") && input.Text == "尚未提交", "隐藏目标事件执行了写入/编辑/删除");
                store.SetBoolean("Flag", true);
                Require(ReferenceEquals(selected, view.SelectedVariable) && input.Text == "尚未提交" && !selected.IsSearchMatch, "刷新丢失选择、输入或筛选");
                view.Resources["变量筛选数量"] = "Matches {0} / Total {1}";
                view.RefreshLocalizedText();
                Require(((TextBlock)view.FindName("VariableSearchCountText")).Text == "Matches 0 / Total 2", "语言未刷新计数");
                search.Text = string.Empty;
                Require(view.Variables.All(row => row.IsSearchMatch) && selected.AllowOperatorWrite && !view.Variables.Single(row => row.Name == "Other").AllowOperatorWrite, "清除或真实人工权限丢失");
                Require(input.Text == "尚未提交" && ReferenceEquals(selected, view.SelectedVariable), "清除改变当前编辑目标");
                Require(grid.Items.Count == 2 && ReferenceEquals(grid.SelectedItem, selected), "清除未恢复真实网格选择");
                grid.SelectedItem = view.Variables.Single(row => row.Name == "Other");
                input.Text = "未提交整数";
                grid.SelectedItem = selected;
                Require(input.Text == "尚未提交", "换选覆盖了原变量未提交输入");
                grid.SelectedItem = view.Variables.Single(row => row.Name == "Other");
                Require(input.Text == "未提交整数" && view.SelectedVariable.Name == "Other", "新选行未更新身份或恢复对应输入");
                search.Text = "Other";
                Require(!selected.IsSearchMatch, "定位测试目标没有被筛选隐藏");
                Require(view.FocusVariableByName("FLAG") && search.Text == string.Empty
                    && ReferenceEquals(grid.SelectedItem, selected) && selected.IsSearchMatch && input.Text == "尚未提交",
                    "外页定位返回成功但目标不可见，或丢失真实身份与输入");
                search.Text = "Other";
                Require(!view.FocusVariableByName("Missing") && search.Text == "Other", "定位不存在对象擅自清除筛选");
                Require(!Directory.Exists(root), "筛选意外持久化配置");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        /// <summary>失败时提供具体只读边界诊断。</summary>
        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
