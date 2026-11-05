using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using MotionControl.Host.Wpf.Operation;

namespace MotionControl.OperatorPages.Checks
{
    /// <summary>部分启动条件摘要的页面导航与只读边界检查。</summary>
    internal static class PreparationStartConditionsChecks
    {
        /// <summary>切换只读子页不装载任务、不执行准备，且不将部分覆盖解释为允许启动。</summary>
        internal static void Run()
        {
            int configurationCalls = 0;
            int navigationCalls = 0;
            var page = new EquipmentPreparationView(() => { configurationCalls++; return Task.CompletedTask; }, _ => navigationCalls++);
            page.SetRuntime(null);
            ((Button)page.FindName("StartConditionsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var main = (FrameworkElement)page.FindName("PreparationContent");
            var details = (FrameworkElement)page.FindName("StartConditionsContent");
            var text = (TextBlock)page.FindName("StartConditionsMessage");
            Assert(main.Visibility == Visibility.Collapsed && details.Visibility == Visibility.Visible, "未进入启动条件只读子页。");
            Assert(text.Text.Contains("覆盖不完整") && text.Text.Contains("不能判断整体可启动")
                && ((Button)page.FindName("RefreshStartConditionsButton")).IsEnabled,
                "未接入的摘要被显示为可用或可以判断启动。");
            page.SetLanguage("en-US");
            Assert(text.Text.Contains("incomplete coverage") && text.Text.Contains("cannot determine"), "英文缺省说明不正确。");
            ((Button)page.FindName("ReturnToPreparationButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(main.Visibility == Visibility.Visible && details.Visibility == Visibility.Collapsed, "返回未恢复准备页。");
            Assert(configurationCalls == 0 && navigationCalls == 0, "查看只读子页意外装载配置或改变页面会话。");
        }

        /// <summary>条件不满足时保留可定位的失败说明。</summary>
        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
