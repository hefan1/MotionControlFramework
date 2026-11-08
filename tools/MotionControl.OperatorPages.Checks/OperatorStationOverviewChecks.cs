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
    /// <summary>验证任务工位进入操作员总览和报警定位时的来源边界。</summary>
    internal static class OperatorStationOverviewChecks
    {
        /// <summary>核对真实来源缺失、读取失败和报警精确定位均不触发硬件动作。</summary>
        internal static void Run()
        {
            var labelMethod = typeof(EquipmentOverviewView).Assembly.GetType("MotionControl.Host.Wpf.Operation.OperationUi").GetMethod("StepLabelKey", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static, null, new[] { typeof(MachineRunState?), typeof(StationOverviewData) }, null);
            if ((string)labelMethod.Invoke(null, new object[] { MachineRunState.Running, new StationOverviewData { StateText = "已完成" } }) != "当前步骤" || (string)labelMethod.Invoke(null, new object[] { MachineRunState.Running, new StationOverviewData() }) != "正在执行步骤")
                throw new InvalidOperationException("独立工位状态的步骤标签错误继承整机运行状态。");
            var failingService = new AlarmRestartRecoveryChecks.FailingAlarmService();
            var failingRuntime = new MachineRuntime(new MotionCardGroup(new IMotionCard[] { new GuardCard() }), new IStationFlow[0], failingService, new GuardSafety());
            var failingOverview = new EquipmentOverviewView();
            failingOverview.SetRuntime(failingRuntime);
            failingOverview.SetConnectionStatus(true, null);
            failingRuntime.SetRecoveryBlockReason("DO41 pending");
            failingOverview.RefreshDisplay();
            typeof(MachineRuntime).GetField("_clearing", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(failingRuntime, true);
            failingService.Fail = true;
            failingOverview.RefreshDisplay();
            var failedNext = (TextBlock)failingOverview.FindName("NextActionText");
            if (!((TextBlock)failingOverview.FindName("MachineStateText")).Text.Contains("读取失败")
                || !failedNext.Text.Contains("未知") || !failedNext.Text.Contains("alarm-enumeration-failed")
                || !failedNext.ToolTip.ToString().Contains("alarm-enumeration-failed")
                || !((TextBlock)failingOverview.FindName("SafetySummaryText")).Text.Contains("DO41")
                || ((Button)failingOverview.FindName("AutomaticStartActionButton")).Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("总览读取失败保留旧状态或丢失错误和恢复摘要。");
            failingRuntime.SetRecoveryBlockReason(null);
            failingOverview.RefreshDisplay();
            if (((Button)failingOverview.FindName("AutomaticStartActionButton")).Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("无恢复记录时读取故障仍提供设备准备动作。");
            failingService.Fail = false;
            failingOverview.RefreshDisplay();
            if (failedNext.ToolTip != null || ((TextBlock)failingOverview.FindName("MachineStateText")).Text.Contains("读取失败"))
                throw new InvalidOperationException("读取恢复后保留旧故障。");
            var card = new GuardCard { IsConnected = true };
            var alarms = new MachineAlarmService();
            MachineRuntime ConnectedTaskRuntime(string recovery = null)
            {
                var machine = new MachineRuntime(new MotionCardGroup(new IMotionCard[] { card }),
                    new IStationFlow[] { new ReadOnlyFlow("未启用的旧代码工位") }, alarms, new GuardSafety(),
                    controlOwner: MachineControlOwner.TaskEngine);
                machine.SetRecoveryBlockReason(recovery);
                machine.ConnectAsync(System.Threading.CancellationToken.None).GetAwaiter().GetResult();
                return machine;
            }
            var runtime = ConnectedTaskRuntime();
            object taskSessionIdentity = new object();
            var data = new List<StationOverviewData> {
                new StationOverviewData { Id = "Press", Name = "压合工位", CurrentStep = "等待夹具到位", Detail = "夹紧到位信号未满足" },
                new StationOverviewData { Id = "Inspect", Name = "检测工位", CurrentStep = "等待来料" }
            };
            var overview = new EquipmentOverviewView();
            overview.SetTaskSessionIdentityProvider(() => taskSessionIdentity);
            overview.SetRuntime(runtime);
            overview.SetConnectionStatus(true, null);
            overview.SetTaskStationProvider(() => data);
            var cards = (ItemsControl)overview.FindName("StationTable");
            if (cards.Items.Count != 2 || ((FrameworkElement)overview.FindName("AggregateMetrics")).Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("任务工位未显示，或仍显示生产统计。");
            data[0].StatusReadError = new InvalidOperationException("工位状态读取失败");
            data[0].NextAction = "不应显示的旧建议";
            data[0].WaitingInputName = "DI_OLD";
            overview.RefreshDisplay();
            if (!((TextBlock)overview.FindName("SelectedStationStepText")).Text.Contains("未知")
                || !((TextBlock)overview.FindName("SelectedStationWaitText")).Text.Contains("未知")
                || !((TextBlock)overview.FindName("SelectedStationNextText")).Text.Contains("未知")
                || !((string[])((ListBoxItem)cards.Items[0]).Content)[2].Contains("未知")
                || ((string[])((ListBoxItem)cards.Items[0]).Content)[4] != "读取失败"
                || ((TextBlock)overview.FindName("SelectedStationText")).Text.Contains("不应显示的旧建议")
                || ((Button)overview.FindName("SelectedInputButton")).IsEnabled)
                throw new InvalidOperationException("工位状态读取失败时仍把步骤、等待或建议显示为当前结论。");
            data[0].StatusReadError = null;
            data[0].NextAction = null;
            data[0].WaitingInputName = null;
            overview.RefreshDisplay();
            var safetyPanel = (FrameworkElement)overview.FindName("SafetySummaryPanel");
            var safetyText = (TextBlock)overview.FindName("SafetySummaryText");
            overview.SetTaskSafetySummaryProvider(() => "待确认资源：1 组；保持输出状态待核查");
            if (safetyPanel.Visibility != Visibility.Visible || !safetyText.Text.Contains("待确认资源"))
                throw new InvalidOperationException("任务资源待确认时，总览没有显示只读核查提示。");
            if (!((TextBlock)overview.FindName("NextActionText")).Text.Contains("资源状态待核查")
                || ((FrameworkElement)overview.FindName("AutomaticStartActionButton")).Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("资源待确认时仍错误引导回零或直接恢复。");
            overview.SetTaskSafetySummaryProvider(() => { throw new InvalidOperationException("资源读取故障"); });
            if (safetyPanel.Visibility != Visibility.Visible || !safetyText.Text.Contains("资源状态读取失败"))
                throw new InvalidOperationException("资源读取失败被隐藏或误显示为正常。");
            overview.SetTaskSafetySummaryProvider(() => null);
            if (safetyPanel.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("没有待确认资源时仍显示旧资源提示。");
            runtime = ConnectedTaskRuntime("DO41 保持输出未确认");
            overview.SetRuntime(runtime);
            overview.SetTaskSessionStateProvider(() => false);
            overview.SetTaskStationProvider(() => null);
            overview.RefreshDisplay();
            if (!safetyText.Text.Contains("DO41") || !((TextBlock)overview.FindName("MachineStateText")).Text.Contains("恢复信息未确认")
                || ((FrameworkElement)overview.FindName("AutomaticStartActionButton")).Visibility != Visibility.Collapsed
                || !((TextBlock)overview.FindName("NextActionText")).Text.Contains("核查现场"))
                throw new InvalidOperationException("重启无任务会话时恢复阻断提示缺失。");
            if (!((TextBlock)overview.FindName("TaskRunStatusText")).Text.Contains("工位数据未知")
                || !((TextBlock)overview.FindName("NextActionText")).Text.Contains("清料须按设备配置并通过检查")
                || ((System.Windows.Media.SolidColorBrush)((Border)overview.FindName("AlertBanner")).Background).Color == System.Windows.Media.Color.FromRgb(240, 247, 244))
                throw new InvalidOperationException("恢复阻断仍展示绿色正常报警、读取任务指引或绝对禁止清料。");
            OperatorNavigationEventArgs safetyNavigation = null;
            overview.NavigationRequested += (sender, e) => safetyNavigation = e;
            var safetyAction = (Button)overview.FindName("SafetySummaryActionButton");
            safetyAction.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (safetyNavigation?.Page != "设备准备" || !safetyAction.Content.ToString().Contains("恢复诊断"))
                throw new InvalidOperationException("恢复阻断未导航至只读恢复诊断。");
            overview.SetLanguage("en-US");            if (!safetyAction.Content.ToString().Contains("recovery diagnostics")) throw new InvalidOperationException("恢复诊断按钮英文缺失。");
            if (!((TextBlock)overview.FindName("MachineStateText")).Text.Contains("Recovery"))
                throw new InvalidOperationException("恢复阻断英文提示缺失。");
            if (!((TextBlock)overview.FindName("TaskRunStatusText")).Text.Contains("unknown") || !((TextBlock)overview.FindName("NextActionText")).Text.Contains("Material clearance requires")) throw new InvalidOperationException("英文恢复工位与清料提示不完整。");
            overview.SetLanguage("zh-CN");            var runtimeStateField = typeof(MachineRuntime).GetField("_state", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var originalRuntimeState = runtime.State;
            foreach (var activeState in new[] { MachineRunState.Running, MachineRunState.Preparing, MachineRunState.Pausing, MachineRunState.Alarmed })
            {
                runtimeStateField.SetValue(runtime, activeState);
                overview.RefreshDisplay();
                if (((TextBlock)overview.FindName("MachineStateText")).Text.Contains("恢复信息未确认") || !safetyText.Text.Contains("DO41"))
                    throw new InvalidOperationException("恢复摘要覆盖了进行中动作或报警状态。");
            }
            runtimeStateField.SetValue(runtime, originalRuntimeState);
            var clearingField = typeof(MachineRuntime).GetField("_clearing", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            clearingField.SetValue(runtime, true);
            overview.RefreshDisplay();
            if (((TextBlock)overview.FindName("MachineStateText")).Text.Contains("恢复信息未确认"))
                throw new InvalidOperationException("恢复摘要覆盖了清料动作。");
            var clearingNext = ((TextBlock)overview.FindName("NextActionText")).Text;
            if (!((TextBlock)overview.FindName("MachineStateText")).Text.Contains("清料中") || !clearingNext.Contains("停止清料")
                || !clearingNext.Contains("结束后核查现场") || clearingNext.Contains("完成后等待启动"))
                throw new InvalidOperationException("清料错误显示设备准备或结束后可启动。");
            overview.SetLanguage("en-US");
            if (!((TextBlock)overview.FindName("MachineStateText")).Text.Contains("clearance")
                || !((TextBlock)overview.FindName("NextActionText")).Text.Contains("stop clearance"))
                throw new InvalidOperationException("英文清料专属指导缺失。");
            overview.SetLanguage("zh-CN");
            clearingField.SetValue(runtime, false);
            var longRecovery = "DO41 保持输出未确认：" + new string('资', 6000);
            runtime = ConnectedTaskRuntime(longRecovery);
            overview.SetRuntime(runtime);
            overview.RefreshDisplay();
            overview.Measure(new Size(1024, 768));
            overview.Arrange(new Rect(0, 0, 1024, 768));
            overview.UpdateLayout();
            if (safetyText.Text.Length > 240 || safetyText.ActualHeight > 120
                || !Equals(safetyText.ToolTip, longRecovery))
                throw new InvalidOperationException("长恢复原因撑满总览或丢失完整诊断。");
            overview.SetConnectionStatus(false, "断连");
            if (!safetyText.Text.Contains("DO41") || !((TextBlock)overview.FindName("NextActionText")).Text.Contains("控制器"))
                throw new InvalidOperationException("恢复阻断覆盖了断连诊断。");
            runtime = ConnectedTaskRuntime();
            overview.SetRuntime(runtime);
            overview.SetTaskSafetySummaryProvider(() => "待确认资源");
            safetyAction.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (safetyNavigation?.Page != "工位运行" || !safetyAction.Content.ToString().Contains("工位运行"))
                throw new InvalidOperationException("普通资源核查导航未保留工位运行。");
            overview.SetTaskSafetySummaryProvider(() => null);
            overview.SetConnectionStatus(true, null);
            overview.SetTaskStationProvider(() => { throw new InvalidOperationException("任务来源读取失败"); });
            if (cards.Items.Count != 0)
                throw new InvalidOperationException("任务来源失败后仍保留旧工位作为实时数据。");
            overview.SetTaskStationProvider(() => data);
            overview.SetConnectionStatus(false, "连接中断");
            if (cards.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("断连后仍显示任务工位运行。");

            alarms.ReportCondition(new MachineAlarmDefinition("CLAMP_TIMEOUT", "夹紧到位超时", MachineAlarmKind.StopMachine),
                "Press", true, "检查夹具和输入");
            var alarmPage = new CurrentAlarmsView();
            alarmPage.SetRuntime(runtime);
            alarmPage.SetTaskStationProvider(() => data);
            var locate = (Button)alarmPage.FindName("FlowButton");
            if (!locate.IsEnabled) throw new InvalidOperationException("报警的稳定工位标识不能定位。");
            OperatorNavigationEventArgs request = null;
            alarmPage.NavigationRequested += (sender, e) => request = e;
            locate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (request == null || request.Page != "工位运行" || request.StationName != "Press")
                throw new InvalidOperationException("任务报警未直接进入统一工位运行路径。");
            data.Add(new StationOverviewData { Id = "Other", Name = "Press", CurrentStep = "不能错误显示的其他动作" });
            alarmPage.RefreshDisplay(true);
            if (locate.IsEnabled || ((TextBlock)alarmPage.FindName("WhereText")).Text.Contains("当前动作"))
                throw new InvalidOperationException("来源歧义时错误关联了另一工位动作。");
            alarmPage.SetTaskStationProvider(() => null);
            if (locate.IsEnabled || card.Calls != 0)
                throw new InvalidOperationException("无工位来源仍允许伪定位，或查看触发了硬件操作。");
        }
    }
}
