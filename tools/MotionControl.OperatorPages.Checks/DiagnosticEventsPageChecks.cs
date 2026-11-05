using System;
using System.Windows.Controls;
using MotionControl.Host.Wpf.Operation;

namespace MotionControl.OperatorPages.Checks
{
    /// <summary>诊断事件服务未接入时的只读子页检查。</summary>
    internal static class DiagnosticEventsPageChecks
    {
        /// <summary>切页不请求导航，不生成记录或开放不可用的支持包导出。</summary>
        internal static void Run()
        {
            var page = new CurrentAlarmsView();
            int navigations = 0;
            page.NavigationRequested += (sender, e) => navigations++;
            var tabs = (TabControl)page.FindName("AlarmTabs");
            tabs.SelectedItem = page.FindName("DiagnosticEventsTab");
            var unavailable = (TextBlock)page.FindName("DiagnosticEventsUnavailable");
            var export = (Button)page.FindName("ExportDiagnosticPackageButton");
            Assert(unavailable.Text.Contains("未接入") && !export.IsEnabled, "缺少服务时没有明确提示或错误开放导出。");
            page.SetLanguage("en-US");
            Assert(unavailable.Text.ToLowerInvariant().Contains("not") && !export.IsEnabled, "英文缺省或导出限制不正确。");
            tabs.SelectedItem = page.FindName("CurrentAlarmsTab");
            Assert(navigations == 0, "查看诊断事件错误触发了设备页面导航。");
        }

        /// <summary>检查失败时保留简短原因。</summary>
        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
