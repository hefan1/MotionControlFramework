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
            var card = new GuardCard();
            var alarms = new MachineAlarmService();
            var runtime = new MachineRuntime(new MotionCardGroup(new IMotionCard[] { card }),
                new IStationFlow[] { new ReadOnlyFlow("未启用的旧代码工位") }, alarms, new GuardSafety(),
                controlOwner: MachineControlOwner.TaskEngine);
            var data = new List<StationOverviewData> {
                new StationOverviewData { Id = "Press", Name = "压合工位", CurrentStep = "等待夹具到位", Detail = "夹紧到位信号未满足" },
                new StationOverviewData { Id = "Inspect", Name = "检测工位", CurrentStep = "等待来料" }
            };
            var overview = new EquipmentOverviewView();
            overview.SetRuntime(runtime);
            overview.SetConnectionStatus(true, null);
            overview.SetTaskStationProvider(() => data);
            var cards = (ItemsControl)overview.FindName("StationCards");
            if (cards.Items.Count != 2 || ((FrameworkElement)overview.FindName("AggregateMetrics")).Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("任务工位未显示，或仍显示生产统计。");
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
