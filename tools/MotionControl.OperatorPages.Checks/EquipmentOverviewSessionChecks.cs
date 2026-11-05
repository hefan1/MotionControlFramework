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
            ClickInput();
            Require(target == null && input.IsEnabled, "旧连接行跨会话发出了定位或新一轮无法恢复。");
            ClickInput();
            Require(target?.IoPointName == "DI_B" && card.Calls == 0, "新会话没有恢复只读定位，或页面操作了卡。");

            // GuardCard 故意拒绝断卡，保留 IsConnected=true，但 Runtime 已撤销会话。
            try { runtime.DisconnectAsync(token).GetAwaiter().GetResult(); }
            catch (Exception) when (!runtime.ConnectionSessionId.HasValue && card.Calls == 1) { }
            Require(card.IsConnected && !runtime.ConnectionSessionId.HasValue, "没有建立断卡失败的测试前提。");
            reads = flow.StepReads;
            page.RefreshDisplay();
            Require(!input.IsEnabled && detail.Text.Contains("未知") && flow.StepReads == reads && card.Calls == 1,
                "卡仍报告在线时忽略了失效的 Runtime 会话。");
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
