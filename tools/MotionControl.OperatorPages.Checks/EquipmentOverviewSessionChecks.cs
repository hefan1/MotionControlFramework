using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Runtime.Alarms;
using MotionControl.Framework.Runtime.Machine;
using MotionControl.Host.Wpf.Operation;

namespace MotionControl.OperatorPages.Checks
{
    /// <summary>从真正的总览刷新与定位入口检查连接变化，只使用内存工位和卡。</summary>
    internal static class EquipmentOverviewSessionChecks
    {
        internal static void Run()
        {
            var token = CancellationToken.None;
            var card = new GuardCard { IsConnected = true };
            var flow = new WaitingFlow();
            var alarms = new MachineAlarmService();
            var runtime = new MachineRuntime(new MotionCardGroup(new[] { card }), new IStationFlow[] { flow }, alarms, new GuardSafety());
            runtime.ConnectAsync(token).GetAwaiter().GetResult();
            alarms.ReportCondition(new MachineAlarmDefinition("NOTE", "会话提醒", MachineAlarmKind.NonStop), "工位", true, "测试记录");
            var page = new EquipmentOverviewView();
            page.SetConnectionStatus(true, "连接状态缓存");
            page.SetRuntime(runtime);
            var input = (Button)page.FindName("SelectedInputButton");
            var detail = (TextBlock)page.FindName("SelectedStationText");
            var alarm = (TextBlock)page.FindName("AlertText");
            OperatorNavigationEventArgs target = null;
            page.NavigationRequested += (sender, args) => target = args;

            void ClickInput() { input.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
            ClickInput();
            Require(target?.IoPointName == "DI_A", "有效工位输入没有定位。");
            flow.InputName = "DI_B";
            target = null;
            ClickInput();
            Require(target == null && detail.Text.Contains("DI_B"), "等待输入改变后仍沿旧选择定向。");

            flow.OnRead = () => card.IsConnected = false;
            page.RefreshDisplay();
            Require(!input.IsEnabled && detail.Text.Contains("未知") && !detail.Text.Contains("DI_B")
                && alarm.Text.Contains("当前会话报警 1 条") && alarm.Text.Contains("现场报警状态未知"),
                "工位读取中掉线仍显示当前步骤、等待输入或现场报警结论。");
            int reads = flow.StepReads;
            ClickInput();
            page.RefreshDisplay();
            Require(target == null && flow.StepReads == reads, "会话不可用时仍采集步骤或执行定向导航。");
            flow.OnRead = null;

            // 两个刷新之间断开并重连同一 Runtime，即使信号名称相同也不能沿用旧行。
            card.IsConnected = true;
            page.RefreshDisplay();
            card.IsConnected = false;
            runtime.DisconnectAsync(token).GetAwaiter().GetResult();
            card.IsConnected = true;
            runtime.ConnectAsync(token).GetAwaiter().GetResult();
            var stations = (ListView)page.FindName("StationTable");
            target = null;
            ClickInput();
            Require(target == null && !input.IsEnabled && stations.SelectedIndex == -1
                && detail.Text.Contains("重新选择当前工位"), "连接会话变化后总览未要求重选。选中行=" + stations.SelectedIndex
                    + "，输入按钮可用=" + input.IsEnabled + "，详情=" + detail.Text);
            stations.SelectedIndex = 0;
            Require(input.IsEnabled && detail.Text.Contains("DI_B"), "操作员明确选择新连接工位后没有恢复只读定位。");
            ClickInput();
            Require(target?.IoPointName == "DI_B" && card.Calls == 0, "新会话没有恢复只读定位，或页面操作了卡。");

            var taskCard = new GuardCard { IsConnected = true };
            var taskRuntime = new MachineRuntime(new MotionCardGroup(new[] { taskCard }), new IStationFlow[0],
                alarms, new GuardSafety(), controlOwner: MachineControlOwner.TaskEngine);
            taskRuntime.ConnectAsync(token).GetAwaiter().GetResult();
            object taskSessionIdentity = new object();
            var taskPage = new EquipmentOverviewView();
            taskPage.SetConnectionStatus(true, null);
            var identityProvider = typeof(EquipmentOverviewView).GetMethod("SetTaskSessionIdentityProvider");
            Require(identityProvider != null, "总览没有接收任务会话身份的入口。");
            identityProvider.Invoke(taskPage, new object[] { new Func<object>(() => taskSessionIdentity) });
            taskPage.SetRuntime(taskRuntime);
            taskPage.SetTaskStationProvider(() => new[] { new StationOverviewData
                { Id = "S01", Name = "旧工位名称", CurrentStep = "旧会话步骤", WaitingInputName = "DI_Old" } });
            var taskStations = (ListView)taskPage.FindName("StationTable");
            var taskDetail = (TextBlock)taskPage.FindName("SelectedStationText");
            var taskStatus = (TextBlock)taskPage.FindName("TaskRunStatusText");
            var taskFlow = (Button)taskPage.FindName("SelectedFlowButton");
            var taskInput = (Button)taskPage.FindName("SelectedInputButton");
            OperatorNavigationEventArgs taskTarget = null;
            taskPage.NavigationRequested += (sender, args) => taskTarget = args;
            Require(taskStations.SelectedIndex == 0 && taskFlow.IsEnabled, "首次载入任务会话没有显示当前工位。");
            taskPage.RefreshDisplay();
            Require(taskStations.SelectedIndex == 0, "同一任务会话内刷新丢失了有效工位选择。");
            taskSessionIdentity = new object();
            taskPage.SetTaskStationProvider(() => new[] { new StationOverviewData
                { Id = "S01", Name = "新工位名称", CurrentStep = "新会话步骤", WaitingInputName = "DI_New" } });
            Require(taskStations.SelectedIndex == -1 && !taskFlow.IsEnabled && taskDetail.Text.Contains("重新选择当前工位")
                && !taskDetail.Text.Contains("旧工位名称"), "任务会话更换后仍显示或定向到同名旧工位。");
            taskStations.SelectedIndex = 0;
            Require(taskDetail.Text.Contains("新工位名称") && taskDetail.Text.Contains("新会话步骤")
                && taskFlow.IsEnabled && taskInput.IsEnabled,
                "明确选择后仍显示旧任务会话详情。选中行=" + taskStations.SelectedIndex
                    + "，流程按钮可用=" + taskFlow.IsEnabled + "，详情=" + taskDetail.Text);
            taskInput.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(taskTarget?.IoPointName == "DI_New" && taskCard.Calls == 0,
                "明确选择新工位后仍沿用旧会话等待输入或调用了硬件。");
            taskTarget = null;
            taskFlow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(taskTarget?.StationName == "S01" && taskCard.Calls == 0, "新会话的只读工位定位错误或调用了硬件。");
            bool taskLoaded = true;
            taskPage.SetTaskSessionStateProvider(() => taskLoaded);
            taskLoaded = false; taskSessionIdentity = null; taskPage.RefreshDisplay();
            Require(taskStations.Items.Count == 0 && !taskFlow.IsEnabled && taskStatus.Text.Contains("尚未读取")
                && string.IsNullOrWhiteSpace(taskDetail.Text), "关闭任务后提示从空列表重新选择工位。");
            taskLoaded = true; taskSessionIdentity = new object(); taskPage.RefreshDisplay();
            Require(taskStations.SelectedIndex == -1 && !taskFlow.IsEnabled && taskDetail.Text.Contains("重新选择当前工位"),
                "重新读取同 ID 工位后沿用了关闭前的选择。");
            taskStations.SelectedIndex = 0;
            identityProvider.Invoke(taskPage, new object[] { null });
            Require(taskStations.Items.Count == 0 && taskStations.SelectedIndex == -1
                && !taskFlow.IsEnabled && taskDetail.Text.Contains("身份未确认")
                && taskStatus.Text.Contains("身份未确认") && !taskStatus.Text.Contains("采样期间发生变化"),
                "任务身份提供器缺失时仍显示可被误认成当前的工位数据。");
            identityProvider.Invoke(taskPage, new object[] { new Func<object>(() => null) });
            Require(taskStations.Items.Count == 0 && taskStations.SelectedIndex == -1
                && !taskFlow.IsEnabled && taskDetail.Text.Contains("身份未确认")
                && taskStatus.Text.Contains("身份未确认") && !taskStatus.Text.Contains("采样期间发生变化"),
                "任务身份提供器返回空会话时仍显示当前工位数据。");
            identityProvider.Invoke(taskPage, new object[] { new Func<object>(() => throw new InvalidOperationException("身份读取失败")) });
            Require(taskStations.Items.Count == 0 && taskStations.SelectedIndex == -1
                && !taskFlow.IsEnabled && taskDetail.Text.Contains("身份未确认")
                && taskStatus.Text.Contains("身份读取失败") && !taskStatus.Text.Contains("采样期间发生变化"),
                "任务身份读取失败时仍显示当前工位数据。");
            taskSessionIdentity = new object();
            identityProvider.Invoke(taskPage, new object[] { new Func<object>(() => taskSessionIdentity) });
            Require(taskStations.SelectedIndex == -1 && !taskFlow.IsEnabled
                && taskDetail.Text.Contains("重新选择当前工位"),
                "任务身份恢复后没有要求重新选择工位。");
            taskStations.SelectedIndex = 0;
            Require(taskDetail.Text.Contains("新工位名称") && taskFlow.IsEnabled,
                "身份恢复后明确选择不能恢复新会话定位。");
            taskSessionIdentity = new object();
            taskStations.SelectedIndex = -1;
            taskStations.SelectedIndex = 0;
            Require(taskStations.SelectedIndex == -1 && !taskFlow.IsEnabled
                && !taskDetail.Text.Contains("新工位名称"),
                "任务身份在刷新周期内改变后，选择事件仍展示旧会话详情或开放定位。");
            var staleSessionStation = new StationOverviewData
                { Id = "S01", Name = "采样旧会话名称", CurrentStep = "采样旧步骤", WaitingInputName = "DI_Stale" };
            taskPage.SetTaskStationProvider(() =>
            {
                taskSessionIdentity = new object();
                return new[] { staleSessionStation };
            });
            Require(taskStations.Items.Count == 0 && taskStations.SelectedIndex == -1
                && !taskDetail.Text.Contains("采样旧会话名称") && !taskFlow.IsEnabled,
                "工位采样期间替换任务会话后仍把旧会话数据发布为当前列表。");

            var recoveryTaskCard = new GuardCard { IsConnected = true };
            var recoveryTaskRuntime = new MachineRuntime(new MotionCardGroup(new[] { recoveryTaskCard }), new IStationFlow[0],
                alarms, new GuardSafety(), controlOwner: MachineControlOwner.TaskEngine);
            recoveryTaskRuntime.SetRecoveryBlockReason("保留恢复现场");
            recoveryTaskRuntime.ConnectAsync(token).GetAwaiter().GetResult();
            object recoveryTaskIdentity = new object();
            var recoveryTaskPage = new EquipmentOverviewView();
            recoveryTaskPage.SetTaskSessionIdentityProvider(() => recoveryTaskIdentity);
            recoveryTaskPage.SetRuntime(recoveryTaskRuntime);
            recoveryTaskPage.SetTaskSessionStateProvider(() => true);
            recoveryTaskPage.SetTaskStationProvider(() => new[] { new StationOverviewData
                { Id = "S01", Name = "恢复现场工位", CurrentStep = "等待确认" } });
            var recoveryTaskStatus = (TextBlock)recoveryTaskPage.FindName("TaskRunStatusText");
            var recoveryTaskSafety = (TextBlock)recoveryTaskPage.FindName("SafetySummaryText");
            var recoveryTaskNextAction = (TextBlock)recoveryTaskPage.FindName("NextActionText");
            recoveryTaskPage.SetTaskSafetySummaryProvider(() =>
            {
                recoveryTaskIdentity = new object();
                return "旧会话待核查资源";
            });
            Require(recoveryTaskStatus.Text.Contains("采样期间发生变化")
                && recoveryTaskSafety.Text.Contains("保留恢复现场") && !recoveryTaskSafety.Text.Contains("旧会话待核查资源")
                && recoveryTaskNextAction.Text.Contains("恢复") && !recoveryTaskNextAction.Text.Contains("资源状态待核查"),
                "任务身份在采样中变化后仍显示旧会话资源摘要或覆盖恢复提示。摘要=" + recoveryTaskSafety.Text
                    + "；下一步=" + recoveryTaskNextAction.Text);

            // GuardCard 故意拒绝断卡，保留 IsConnected=true，但 Runtime 已撤销会话。
            try { runtime.DisconnectAsync(token).GetAwaiter().GetResult(); }
            catch (Exception) when (!runtime.ConnectionSessionId.HasValue && card.Calls == 1) { }
            Require(card.IsConnected && !runtime.ConnectionSessionId.HasValue, "没有建立断卡失败的测试前提。");
            reads = flow.StepReads;
            page.RefreshDisplay();
            Require(!input.IsEnabled && detail.Text.Contains("未知") && flow.StepReads == reads && card.Calls == 1,
                "卡仍报告在线时忽略了失效的 Runtime 会话。");

            var lifecycleCard = new GuardCard { IsConnected = true };
            var lifecycleFlow = new WaitingFlow();
            var lifecycleRuntime = new MachineRuntime(new MotionCardGroup(new[] { lifecycleCard }),
                new IStationFlow[] { lifecycleFlow }, alarms, new GuardSafety());
            lifecycleRuntime.ConnectAsync(token).GetAwaiter().GetResult();
            var lifecyclePage = new EquipmentOverviewView();
            lifecyclePage.SetConnectionStatus(true, null);
            lifecyclePage.SetRuntime(lifecycleRuntime);
            var lifecycleStations = (ListView)lifecyclePage.FindName("StationTable");
            var lifecycleDetail = (TextBlock)lifecyclePage.FindName("SelectedStationText");
            var lifecycleInput = (Button)lifecyclePage.FindName("SelectedInputButton");
            Require(lifecycleStations.SelectedIndex == 0, "首次建立 Runtime 后没有保留原有的首工位默认选择。");
            lifecyclePage.SetRuntime(null);
            var replacementCard = new GuardCard { IsConnected = true };
            var replacementRuntime = new MachineRuntime(new MotionCardGroup(new[] { replacementCard }),
                new IStationFlow[] { lifecycleFlow }, alarms, new GuardSafety());
            replacementRuntime.ConnectAsync(token).GetAwaiter().GetResult();
            lifecyclePage.SetRuntime(replacementRuntime);
            Require(lifecycleStations.SelectedIndex == -1 && !lifecycleInput.IsEnabled
                && lifecycleDetail.Text.Contains("重新选择当前工位"),
                "正式 Host Runtime 从实例切到 null 再创建新实例后，总览自动恢复了旧工位选择。");

            var ownershipCard = new GuardCard { IsConnected = true };
            var ownershipFlow = new WaitingFlow();
            var ownershipRuntime = new MachineRuntime(new MotionCardGroup(new[] { ownershipCard }),
                new IStationFlow[] { ownershipFlow }, alarms, new GuardSafety());
            ownershipRuntime.ConnectAsync(token).GetAwaiter().GetResult();
            var ownershipPage = new EquipmentOverviewView();
            ownershipPage.SetConnectionStatus(true, null);
            ownershipPage.SetRuntime(ownershipRuntime);
            var ownershipInput = (Button)ownershipPage.FindName("SelectedInputButton");
            OperatorNavigationEventArgs ownershipTarget = null;
            ownershipPage.NavigationRequested += (sender, args) => ownershipTarget = args;
            ownershipFlow.OnRead = () => ownershipRuntime.AcquireTaskEngineControlAsync(new object(), token).GetAwaiter().GetResult();
            ownershipInput.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(ownershipTarget == null && ownershipCard.Calls == 0,
                "工位采样期间控制权切换后仍按旧的 C# 工位来源导航。");
        }

        private static void Require(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }

        private sealed class WaitingFlow : IStationFlow, IStationInputWaitState
        {
            internal string InputName = "DI_A";
            internal int StepReads;
            internal Action OnRead;
            public string StationName => "工位";
            public string CurrentStepName { get { StepReads++; OnRead?.Invoke(); return "当前步骤"; } }
            public string WaitingInputName => InputName;
            public Task ExecuteCurrentStepAsync(CancellationToken token) { throw new InvalidOperationException("只读页面不应运行工位。"); }
            public Task ResetAsync(CancellationToken token) { throw new InvalidOperationException("只读页面不应复位。"); }
        }
    }
}
