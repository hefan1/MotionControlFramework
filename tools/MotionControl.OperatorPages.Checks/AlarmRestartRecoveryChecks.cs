using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Runtime.Alarms;
using MotionControl.Framework.Runtime.Machine;
using MotionControl.Host.Wpf.Operation;

namespace MotionControl.OperatorPages.Checks
{
    /// <summary>检查当前会话报警与跨重启恢复阻断的独立展示。</summary>
    internal static class AlarmRestartRecoveryChecks
    {
        /// <summary>真实控件覆盖正常、恢复阻断、停机报警、读取异常和异常恢复，均不操作硬件。</summary>
        internal static void Run()
        {
            var card = new GuardCard();
            var service = new FailingAlarmService();
            var runtime = new MachineRuntime(new MotionCardGroup(new IMotionCard[] { card }),
                new IStationFlow[0], service, new GuardSafety());
            var view = new CurrentAlarmsView();
            view.SetRuntime(runtime);
            var empty = (TextBlock)view.FindName("EmptyText");
            var source = (TextBlock)view.FindName("SourceText");
            var grid = (DataGrid)view.FindName("AlarmGrid");
            Require(runtime.State == MachineRunState.Disconnected && empty.Text.Contains("现场报警状态未知")
                && !source.Text.Contains("仍未确认"), "未连接的初始Runtime被误报现场无报警或恢复阻断。");
            runtime.SetRecoveryBlockReason("AutomaticRun.pending / DO41 guard");
            view.RefreshDisplay();
            Require(empty.Text.Contains("软件报警服务未记录报警") && source.Text.Contains("设备维护人员")
                && source.Text.Contains("按设备规程处理") && !source.Text.Contains("后重新准备")
                && source.Text.Contains("AutomaticRun.pending"), "空报警列表未保留恢复证据或处理责任。");
            view.SetLanguage("en-US");
            Require(empty.Text.Contains("software service") && source.Text.Contains("Maintenance")
                && source.Text.Contains("follow the equipment procedures") && !source.Text.Contains("then prepare again")
                && source.Text.Contains("does not confirm recovery"), "英文丢失会话边界或恢复责任。");
            service.Inner.ReportCondition(new MachineAlarmDefinition("STOP_RESTART", "停止反馈未知", MachineAlarmKind.StopMachine), "工位A", true, "保留输出");
            view.RefreshDisplay();
            Require(grid.Items.Count == 1 && empty.Visibility == Visibility.Collapsed
                && source.Text.Contains("AutomaticRun.pending"), "停机报警与恢复阻断未同时保留。");
            service.Fail = true;
            view.RefreshDisplay();
            Require(((TextBlock)view.FindName("FilterCountText")).Text == "Filter matches: unknown", "读取失败仍显示确定的筛选数。");
            Require(empty.Text.Contains("unknown") && source.Text.Contains("alarm-enumeration-failed")
                && source.Text.Contains("Maintenance") && source.Text.Contains("AutomaticRun.pending")
                && grid.Items.Count == 0 && !((Button)view.FindName("IoButton")).IsEnabled,
                "读取失败伪装正常、保留可操作旧报警或隐藏恢复责任。");
            view.SetLanguage("zh-CN");
            Require(((TextBlock)view.FindName("FilterCountText")).Text == "筛选匹配：未知", "中文读取失败未显示筛选数量未知。");
            Require(empty.Text.Contains("未知") && source.Text.Contains("设备维护人员"), "中文读取失败隐藏恢复说明。");
            service.Fail = false;
            view.RefreshDisplay();
            Require(grid.Items.Count == 1 && source.Text.Contains("设备恢复信息仍未确认")
                && !source.Text.Contains("alarm-enumeration-failed"), "读取恢复后未还原真实报警与恢复阻断。");
            service.Inner.ReportCondition(new MachineAlarmDefinition("STOP_RESTART", "停止反馈未知", MachineAlarmKind.StopMachine), "工位A", false, "事件结束");
            service.Inner.ResetStopAlarms();
            view.RefreshDisplay();
            Require(empty.Text.Contains("软件报警服务未记录报警") && runtime.RecoveryBlockReason != null,
                "报警复位错误清除了独立恢复阻断。");
            runtime.SetRecoveryBlockReason(null);
            view.RefreshDisplay();
            Require(empty.Text.Contains("现场报警状态未知") && !source.Text.Contains("仍未确认")
                && ((TextBlock)view.FindName("CountText")).Text == "留存报警记录 0 条（停机 0，非停机 0）" && card.Calls == 0,
                "恢复阻断解除后残留旧提示或只读页面调用硬件。");
        }
        /// <summary>明确失败原因，避免检查静默通过。</summary>
        private static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }

        /// <summary>保留真实报警语义，仅注入报警集合读取失败。</summary>
        internal sealed class FailingAlarmService : IMachineAlarmService
        {
            /// <summary>真实内存报警服务。</summary>
            internal readonly MachineAlarmService Inner = new MachineAlarmService();
            /// <summary>切换读取故障。</summary>
            internal bool Fail;
            /// <summary>读取故障抛出原始可定位错误，不能返回空集合。</summary>
            public IReadOnlyCollection<MachineAlarmRecord> Alarms { get { if (Fail) throw new InvalidOperationException("alarm-enumeration-failed"); return Inner.Alarms; } }
            /// <summary>透传锁存状态。</summary>
            public bool HasLatchedStopAlarm => Inner.HasLatchedStopAlarm;
            /// <summary>透传列表变化通知。</summary>
            public event EventHandler AlarmsChanged { add { Inner.AlarmsChanged += value; } remove { Inner.AlarmsChanged -= value; } }
            /// <summary>透传停机事件。</summary>
            public event EventHandler<MachineAlarmRaisedEventArgs> StopAlarmRaised { add { Inner.StopAlarmRaised += value; } remove { Inner.StopAlarmRaised -= value; } }
            /// <summary>保持真实条件报告语义。</summary>
            public void ReportCondition(MachineAlarmDefinition definition, string sourceName, bool conditionActive, string detail) { Inner.ReportCondition(definition, sourceName, conditionActive, detail); }
            /// <summary>保持真实复位语义。</summary>
            public void ResetStopAlarms() { Inner.ResetStopAlarms(); }
            /// <summary>保持真实复位前置校验。</summary>
            public void EnsureStopAlarmConditionsRecovered() { Inner.EnsureStopAlarmConditionsRecovered(); }
        }
    }
}
