using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.Host.Wpf.Configuration;

namespace MotionControl.OperatorPages.Checks
{
    /// <summary>隔离验证报警搜索与编辑、文件、语言展示的边界。</summary>
    internal static class V14AlarmCatalogSearchChecks
    {
        /// <summary>搜索真实定义及未提交输入，核对选择、指导和原文件不受影响。</summary>
        internal static void Run()
        {
            string root = Path.Combine(Path.GetTempPath(), "AlarmSearch-" + Guid.NewGuid().ToString("N"));
            try
            {
                var configuration = new MachineConfigurationStore(root);
                var store = new MachineAlarmCatalogStore(configuration);
                var document = new MachineAlarmCatalogDocument();
                document.Alarms.Add(new MachineAlarmCatalogEntry { Code = "CLAMP_TIMEOUT", Message = "夹紧超时", CheckGuidance = "检查真实夹紧反馈", RecoveryGuidance = "" });
                document.Alarms.Add(new MachineAlarmCatalogEntry { Code = "OTHER", Message = "另一报警" });
                store.Save("SearchMachine", document);
                string path = Path.Combine(configuration.GetMachineDirectory("SearchMachine"), MachineAlarmCatalogStore.RelativeFilePath);
                string original = File.ReadAllText(path);
                var view = new AlarmCatalogConfigurationView(store, "SearchMachine");
                var grid = (DataGrid)view.FindName("AlarmGrid");
                var search = (TextBox)view.FindName("SearchBox");
                var results = (ListBox)view.FindName("SearchResults");
                var guidance = (TextBlock)view.FindName("SearchGuidanceText");
                // 离屏控件尚未进入 Loaded；显式求值现有绑定，避免把空 Items 的选择失败误认为搜索问题。
                Require(view.Alarms.Count == 2, "报警定义载入失败：" + ((TextBlock)view.FindName("StatusText")).Text);
                // DataContext 连续切换会排队传播；先让 Dispatcher 完成绑定任务。
                var frame = new System.Windows.Threading.DispatcherFrame();
                view.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
                System.Windows.Threading.Dispatcher.PushFrame(frame);
                grid.GetBindingExpression(ItemsControl.ItemsSourceProperty).UpdateTarget();
                Require(grid.Items.Count == 2, "夹具未装载主报警列表绑定。");
                grid.SelectedItem = view.Alarms[0];
                Require(ReferenceEquals(grid.SelectedItem, view.Alarms[0]), "夹具未建立初始编辑选择。");
                search.Text = "clamp_timeout";
                Require(results.Items.Count == 1 && ReferenceEquals(grid.SelectedItem, view.Alarms[0]), "代码搜索改变主选择或未忽略大小写。");
                search.Text = "夹紧";
                Require(results.Items.Count == 1 && !view.HasUnsavedChanges, "说明搜索误标记草稿。");
                view.Resources["报警库指导未配置"] = "未配置";
                results.SelectedIndex = 0;
                view.RefreshLocalizedText();
                Require(guidance.Text.Contains("检查真实夹紧反馈") && guidance.Text.Contains("未配置"), "真实指导或缺项提示丢失。");
                object selectedResult = results.SelectedItem;
                view.Resources["报警库指导未配置"] = "Not configured";
                view.RefreshLocalizedText();
                Require(ReferenceEquals(selectedResult, results.SelectedItem) && guidance.Text.Contains("Not configured"), "语言刷新丢失结果选择或缺项翻译。");
                ((TextBox)view.FindName("MessageTextBox")).Text = "尚未提交的草稿";
                search.Text = "尚未提交";
                Require(results.Items.Count == 1 && view.HasUnsavedChanges, "草稿表单无法检索或丢失脏状态。");
                Require(view.Alarms[0].Message == "夹紧超时" && File.ReadAllText(path) == original, "搜索提交了表单或改写文件。");
                search.Text = "NO_MATCH_123";
                Require(results.Items.Count == 0 && ReferenceEquals(grid.SelectedItem, view.Alarms[0]), "空结果改变编辑选择。");
                // 通过真实清除按钮的路由事件验证清除路径。
                view.Resources["报警库清除搜索"] = "Clear search fixture";
                var clear = FindClearButton(view);
                Require(clear != null, "未找到清除筛选按钮。");
                clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(search.Text.Length == 0 && results.Items.Count == 2 && view.HasUnsavedChanges, "清除筛选丢失草稿或未恢复全部结果。");
                Require(File.ReadAllText(path) == original, "只读操作修改了原文件。");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        /// <summary>按明确事件处理方法找到页面清除按钮，不依赖显示语言。</summary>
        private static Button FindClearButton(DependencyObject parent)
        {
            foreach (object child in LogicalTreeHelper.GetChildren(parent))
            {
                var button = child as Button;
                if (button != null && (button.Content as string) == ((parent as FrameworkElement)?.TryFindResource("报警库清除搜索") as string)) return button;
                var element = child as DependencyObject;
                if (element == null) continue;
                var found = FindClearButton(element);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>失败时给出具体边界诊断。</summary>
        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
