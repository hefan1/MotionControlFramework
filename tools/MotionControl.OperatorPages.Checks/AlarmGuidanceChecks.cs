using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Host.Wpf.Operation;

namespace MotionControl.OperatorPages.Checks
{
    /// <summary>验证报警库指导不能遮盖实时报警状态。</summary>
    internal static class AlarmGuidanceChecks
    {
        /// <summary>分别检查停机条件有效、已恢复待复位及非停机提醒。</summary>
        internal static void Run()
        {
            DateTime now = DateTime.UtcNow;
            var stop = new MachineAlarmDefinition("STOP_GUIDE", "夹紧超时", MachineAlarmKind.StopMachine,
                recoveryGuidance: "检查夹具后按设备流程处理。");
            var notice = new MachineAlarmDefinition("NOTICE_GUIDE", "料框将满", MachineAlarmKind.NonStop,
                recoveryGuidance: "更换料框。");
            var snapshot = new OperationSnapshot { IsPreview = true, HasRuntime = true };
            snapshot.Stations.Add(new StationOverviewData { Name = "压装", WaitingInputName = "另一传感器" });
            snapshot.Alarms.Add(new MachineAlarmRecord(stop, "压装", "", true, now, now));
            snapshot.Alarms.Add(new MachineAlarmRecord(notice, "下料", "", true, now, now));
            var view = new CurrentAlarmsView();
            view.SetPreview(snapshot);
            var grid = (DataGrid)view.FindName("AlarmGrid");
            var after = (TextBlock)view.FindName("AfterText");
            grid.SelectedItem = grid.Items.Cast<AlarmDisplayRow>().Single(row => row.Alarm.Definition.Code == "STOP_GUIDE");
            Require(after.Text.Contains("条件仍有效") && after.Text.Contains("检查夹具后按设备流程处理。"), "有效停机条件或报警库指导被隐藏。");
            OperatorNavigationEventArgs navigation = null;
            view.NavigationRequested += (sender, args) => navigation = args;
            ((Button)view.FindName("IoButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(navigation != null && navigation.Page == "IO监控" && navigation.StationName == "压装"
                && navigation.IoPointName == null, "报警未记录关联输入时，不能把工位当前等待的其他输入当成报警信号。");
            // 超时事件结束并不证明被等待的传感器已触发。
            snapshot.Alarms[0] = new MachineAlarmRecord(stop, "压装", "DI 仍未触发", false, now, now.AddSeconds(1));
            view.RefreshDisplay();
            Require(after.Text.Contains("检查故障原因和相关信号") && !after.Text.Contains("条件已恢复")
                && after.Text.Contains("检查夹具后按设备流程处理。"), "待复位处理提示错误声称传感器已恢复，或隐藏了报警库指导。");
            Require(((AlarmDisplayRow)grid.SelectedItem).ConditionText == "等待复位", "超时事件被误显示为现场条件已恢复。");
            view.SetLanguage("en-US");
            Require(after.Text.Contains("related signals") && !after.Text.Contains("has recovered")
                && ((AlarmDisplayRow)grid.SelectedItem).ConditionText == "Awaiting reset", "英文待复位提示误称现场条件已恢复。");
            view.SetLanguage("zh-CN");
            grid.SelectedItem = grid.Items.Cast<AlarmDisplayRow>().Single(row => row.Alarm.Definition.Code == "NOTICE_GUIDE");
            Require(after.Text.Contains("非停机提醒") && after.Text.Contains("更换料框。"), "非停机状态或报警库指导被隐藏。");
            view.SetLanguage("en-US");
            Require(after.Text.Contains("automatically") && after.Text.Contains("更换料框。"), "切换语言后状态或报警库指导丢失。");
        }

        /// <summary>断言报警详情满足现场提示要求。</summary>
        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
