using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Framework.Runtime.Alarms;
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
            Require(navigation != null && navigation.Page == "IO监控" && navigation.StationName == null
                && navigation.IoPointName == null, "报警缺少连接周期来源时只能打开普通 IO 页面。");
            var alarmService = new MachineAlarmService();
            alarmService.ReportConditionGroup(new[] { stop, notice }, "压装", true, "传感器超时", "夹紧等待", "夹紧到位");
            alarmService.ReportConditionGroup(new[] { stop }, "压装", false, "超时事件结束", "夹紧等待", "夹紧到位");
            snapshot.Alarms.Clear();
            snapshot.Alarms.AddRange(alarmService.Alarms);
            view.RefreshDisplay();
            grid.SelectedItem = grid.Items.Cast<AlarmDisplayRow>().Single(row => row.Alarm.Definition.Code == "STOP_GUIDE");
            navigation = null;
            ((Button)view.FindName("IoButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(navigation != null && navigation.IoPointName == null && navigation.StationName == null
                && ((TextBox)view.FindName("DiagnosticText")).Text.Contains("夹紧到位"),
                "历史关联输入应保留供核查，但不能自动定位为当前连接的输入。");
            alarmService.ReportConditionGroup(new[] { notice }, "压装", true, "另一传感器超时", "第二等待", "另一传感器");
            alarmService.ReportConditionGroup(new[] { notice }, "压装", true, "再次上报", "夹紧等待", "夹紧到位");
            Require(alarmService.Alarms.Single(alarm => alarm.Definition.Code == "NOTICE_GUIDE").RelatedInputName == null,
                "同一报警涉及多个输入后，不应定位到任一输入。");
            alarmService.ReportConditionGroup(new[] { notice }, "压装", false, "已恢复", "夹紧等待", "夹紧到位");
            Require(alarmService.Alarms.Single(alarm => alarm.Definition.Code == "NOTICE_GUIDE").Detail == "另一传感器超时",
                "一个条件恢复时错误清除另一个条件，或显示已恢复条件的过期详情。");
            alarmService.ReportConditionGroup(new[] { notice }, "压装", false, "重复恢复", "夹紧等待", "夹紧到位");
            Require(alarmService.Alarms.Any(alarm => alarm.Definition.Code == "NOTICE_GUIDE"), "重复恢复错误清除了其他活动条件。");
            alarmService.ReportConditionGroup(new[] { notice }, "压装", false, "已恢复", "第二等待", "另一传感器");
            Require(!alarmService.Alarms.Any(alarm => alarm.Definition.Code == "NOTICE_GUIDE"), "所有条件恢复后仍残留非停机报警。");
            alarmService.ReportConditionGroup(new[] { stop }, "压装", true, "条件甲", "甲", "输入甲");
            alarmService.ReportConditionGroup(new[] { stop }, "压装", true, "条件乙", "乙", "输入乙");
            alarmService.ReportConditionGroup(new[] { stop }, "压装", false, "甲已结束", "甲", "输入甲");
            Require(alarmService.Alarms.Single(alarm => alarm.Definition.Code == "STOP_GUIDE").ConditionActive,
                "停机报警的其他条件仍有效时错误允许复位。");
            bool resetRejected = false;
            try { alarmService.ResetStopAlarms(); }
            catch (InvalidOperationException) { resetRejected = true; }
            Require(resetRejected, "仍有停机条件有效时没有拒绝复位。");
            alarmService.ReportConditionGroup(new[] { stop }, "压装", false, "乙已结束", "乙", "输入乙");
            alarmService.ResetStopAlarms();
            Require(!alarmService.Alarms.Any(alarm => alarm.Definition.Code == "STOP_GUIDE"), "全部停机条件结束后无法复位。");
            snapshot.Alarms.Clear();
            snapshot.Alarms.Add(new MachineAlarmRecord(stop, "压装", "", true, now, now));
            snapshot.Alarms.Add(new MachineAlarmRecord(notice, "下料", "", true, now, now));
            view.RefreshDisplay();
            grid.SelectedItem = grid.Items.Cast<AlarmDisplayRow>().Single(row => row.Alarm.Definition.Code == "STOP_GUIDE");
            // 超时事件结束并不证明被等待的传感器已触发。
            snapshot.Alarms[0] = new MachineAlarmRecord(stop, "压装", "DI 仍未触发", false, now, now.AddSeconds(1));
            view.RefreshDisplay();
            Require(after.Text.Contains("检查故障原因和相关信号") && !after.Text.Contains("条件已恢复")
                && after.Text.Contains("检查夹具后按设备流程处理。"), "待复位处理提示错误声称传感器已恢复，或隐藏了报警库指导。");
            Require(((AlarmDisplayRow)grid.SelectedItem).ConditionText == "记录：待复位", "超时事件被误显示为现场条件已恢复。");
            view.SetLanguage("en-US");
            Require(after.Text.Contains("related signals") && !after.Text.Contains("has recovered")
                && ((AlarmDisplayRow)grid.SelectedItem).ConditionText == "Reset pending", "英文待复位提示误称现场条件已恢复。");
            view.SetLanguage("zh-CN");
            grid.SelectedItem = grid.Items.Cast<AlarmDisplayRow>().Single(row => row.Alarm.Definition.Code == "NOTICE_GUIDE");
            Require(after.Text.Contains("收到条件恢复上报") && after.Text.Contains("更换料框。"), "非停机状态或报警库指导被隐藏。");
            view.SetLanguage("en-US");
            Require(after.Text.Contains("recovery is reported") && after.Text.Contains("更换料框。"), "切换语言后状态或报警库指导丢失。");
        }

        /// <summary>断言报警详情满足现场提示要求。</summary>
        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
