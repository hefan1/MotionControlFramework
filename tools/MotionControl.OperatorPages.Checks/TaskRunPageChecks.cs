using System;
using System.IO;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.Framework.Runtime.Alarms;
using MotionControl.Framework.Runtime.Machine;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Host.Wpf;
using MotionControl.TaskEngine.Machine;
using MotionControl.TaskEngine.Wpf.Simulation;

namespace MotionControl.OperatorPages.Checks
{
    /// <summary>检查正式任务运行入口的空状态、错误提示和诊断折叠，不连接设备。</summary>
    internal static class TaskRunPageChecks
    {
        /// <summary>检查无设备和缺少配置时，操作员可看到原因与下一步。</summary>
        internal static void Run()
        {
            var host = new MainWindow();
            try
            {
                var type = typeof(MainWindow).Assembly.GetType("MotionControl.Host.Wpf.Tasks.TaskMonitorLauncher", true);
                CheckPausedGuidance(type);
                CheckBlockedGuidance(type);
                CheckClearancePreparationDisplay();
                var store = new MachineConfigurationStore(Path.Combine(Path.GetTempPath(), "TaskRunPageChecks"));
                var page = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic,
                    null, new object[] { store, "MissingMachine", host, null }, null);
                var guidance = Field<TextBlock>(type, page, "_nextAction");
                var alarm = Field<TextBlock>(type, page, "_alarmState");
                var details = Field<Expander>(type, page, "_diagnostics");
                var empty = Field<TextBlock>(type, page, "_emptyState");
                Require(string.IsNullOrEmpty(guidance.Text)
                    && Field<TextBlock>(type, page, "_runStatus").Text.Contains("连接"), "未连接提示重复或缺失。");
                Require(alarm.Text.Contains("未确认"), "没有报警来源却当成无报警。");
                Require(empty.Text.Contains("未确认") && !details.IsExpanded, "未连接状态或诊断默认折叠错误。");

                var card = new ConnectedReadOnlyCard();
                var group = new MotionCardGroup(new IMotionCard[] { card });
                var runtime = new MachineRuntime(group, new IStationFlow[] { new ReadOnlyFlow() }, new MachineAlarmService(), new GuardSafety());
                type.GetField("_runtime", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(page, runtime);
                type.GetField("_motionCards", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(page, group);
                var updateNext = type.GetMethod("UpdateNextAction", BindingFlags.Instance | BindingFlags.NonPublic);
                updateNext.Invoke(page, new object[] { true, TaskMachineState.Stopped, true });
                Require(guidance.Text.Contains("核查待确认资源") && !guidance.Text.Contains("先在设备准备页"),
                    "资源待确认时页面错误引导操作员先回零。 ");
                updateNext.Invoke(page, new object[] { true, TaskMachineState.Stopped, false });
                Require(!guidance.Text.Contains("核查待确认资源"), "没有隔离资源时错误提示仍待核查。 ");
                var refresh = type.GetMethod("RefreshConnectionDisplay", BindingFlags.Instance | BindingFlags.NonPublic);
                var lost = type.GetMethod("MarkControllerConnectionLost", BindingFlags.Instance | BindingFlags.NonPublic);
                var monitor = Field<ContentControl>(type, page, "_monitorContent");
                refresh.Invoke(page, null);
                Require(empty.Text.Contains("尚未读取") && monitor.Visibility == Visibility.Collapsed, "连接后没有显示未读取状态。");
                runtime.SetRecoveryBlockReason("上次资源状态待确认");
                refresh.Invoke(page, null);
                Require(empty.Text.Contains("恢复信息待确认") && empty.Text.Contains("先核查")
                    && Field<TextBlock>(type, page, "_runStatus").Text.Contains("恢复信息待确认")
                    && !Field<TextBlock>(type, page, "_runStatus").Text.Contains("本页控制"),
                    "恢复信息未确认时任务空状态错误引导直接控制。");
                runtime.SetRecoveryBlockReason(null);
                lost.Invoke(page, null);
                Require(empty.Text.Contains("未确认") && monitor.Visibility == Visibility.Collapsed, "无会话断线未立即更新状态。");
                type.GetField("_controllerConnectionLost", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(page, false);
                type.GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(page, TaskMonitorExample.Create());
                refresh.Invoke(page, null);
                Require(monitor.Visibility == Visibility.Visible && empty.Visibility == Visibility.Collapsed, "已读取时没有显示任务监控。");
                lost.Invoke(page, null);
                var alarmGrid = Field<FrameworkElement>(type, page, "_alarmGrid");
                Require(monitor.Visibility == Visibility.Collapsed && alarmGrid.Visibility == Visibility.Collapsed
                    && empty.Text.Contains("未确认") && alarm.Text.Contains("未确认"), "已读取会话断线后仍显示旧监控或报警。");
                refresh.Invoke(page, null);
                Require(monitor.Visibility == Visibility.Collapsed, "心跳恢复前旧会话被重新当作实时状态。");
                type.GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(page, null);
                var setContext = type.GetMethod("SetMachineContextAsync", BindingFlags.Instance | BindingFlags.NonPublic);
                // 反射调用不自动填充可选参数；第五项表示当前设备未配置 C# 准备项。
                ((Task)setContext.Invoke(page, new object[] { runtime, group, null, null, null })).GetAwaiter().GetResult();
                Require(empty.Text.Contains("尚未读取") && monitor.Visibility == Visibility.Collapsed, "重新建立上下文后未检查会话状态。");

                var setSuccess = type.GetMethod("SetSuccessText", BindingFlags.Instance | BindingFlags.NonPublic);
                var updateSuccess = type.GetMethod("UpdateSuccessFeedback", BindingFlags.Instance | BindingFlags.NonPublic);
                var status = Field<TextBlock>(type, page, "_status");
                setSuccess.Invoke(page, new object[] { "任务执行已启动" });
                updateSuccess.Invoke(page, new object[] { TaskMachineState.Running, true });
                Require(status.Text.Contains("已启动"), "运行中错误清除了启动成功提示。");
                updateSuccess.Invoke(page, new object[] { TaskMachineState.Faulted, true });
                Require(string.IsNullOrEmpty(status.Text), "任务故障后仍显示启动成功。");
                setSuccess.Invoke(page, new object[] { "任务执行已启动" });
                updateSuccess.Invoke(page, new object[] { TaskMachineState.Running, false });
                Require(string.IsNullOrEmpty(status.Text), "连接中断后仍显示启动成功。");

                var showError = type.GetMethod("ShowError", BindingFlags.Instance | BindingFlags.NonPublic);
                string missingPath = Path.Combine("C:\\", "private", "AxisParameters.json");
                showError.Invoke(page, new object[] { new FileNotFoundException("配置丢失", missingPath), "任务目录读取失败", "任务配置检查建议" });
                string message = Field<TextBlock>(type, page, "_status").Text;
                Require(message.Contains("AxisParameters.json") && !message.Contains(missingPath), "错误摘要未保留文件名或暴露了目录。");
                Require(Field<TextBox>(type, page, "_diagnosticText").Text.Contains(missingPath), "完整路径未保留在诊断区。");
                Require(Field<WrapPanel>(type, page, "_nextStepButtons").Children.Count == 1, "缺少轴映射的定位入口。");
                Require(!details.IsExpanded, "错误发生后诊断区不应遮挡操作页面。");
                updateSuccess.Invoke(page, new object[] { TaskMachineState.Faulted, true });
                Require(status.Text == message, "状态变化隐藏了真实错误提示。");

                showError.Invoke(page, new object[] { new InvalidOperationException("任务资源状态待确认。"), "任务停止失败", "任务运行复位建议" });
                Require(status.Text.Contains("核查任务资源") && status.Text.Contains("重试停止并关闭")
                    && status.Text.Contains("保持输出不会自动切换") && !status.Text.Contains("重新复位"),
                    "停止未完成时错误引导操作员直接复位或忽略保持输出。 ");

                var showResourceError = type.GetMethod("ShowResourceRecoveryOpenError", BindingFlags.Instance | BindingFlags.NonPublic);
                showResourceError.Invoke(page, new object[] { new InvalidOperationException("窗口初始化失败。") });
                Require(status.Text.Contains("资源核查窗口打开失败") && status.Text.Contains("窗口初始化失败")
                    && status.Text.Contains("资源仍待确认") && status.Text.Contains("重试核查")
                    && !status.Text.Contains("任务停止失败") && !status.Text.Contains("重试停止"),
                    "资源核查窗口故障被错误描述为任务停止失败。 ");
            }
            finally { host.Close(); }
        }

        /// <summary>清料活动及异常必须与回零区分，显示和语言切换不启动任何设备动作。</summary>
        private static void CheckClearancePreparationDisplay()
        {
            var card = new ConnectedReadOnlyCard();
            var runtime = new MachineRuntime(new MotionCardGroup(new IMotionCard[] { card }),
                new IStationFlow[] { new ReadOnlyFlow() }, new MachineAlarmService(), new GuardSafety());
            var runtimeType = typeof(MachineRuntime);
            runtimeType.GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(runtime, MachineRunState.Preparing);
            runtimeType.GetField("_preparing", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(runtime, true);
            runtimeType.GetField("_clearing", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(runtime, true);
            var page = new MotionControl.Host.Wpf.Operation.EquipmentPreparationView(null, null);
            page.SetRuntime(runtime);
            page.SetAutomaticStartReadiness("工位准备尚未完成", false);
            Require(((TextBlock)page.FindName("ReadOnlyChecksText")).Text.Contains("工位准备尚未完成"), "准备页没有显示来自 Host 的实际启动阻断原因。");
            page.SetAutomaticStartReadiness(null, false);
            Require(!((TextBlock)page.FindName("ReadOnlyChecksText")).Text.Contains("工位准备尚未完成"), "启动阻断解除后准备页保留了旧原因。");
            var summary = (TextBlock)page.FindName("SummaryText");
            var guidance = (TextBlock)page.FindName("GuidanceText");
            var stop = (Button)page.FindName("StopPreparationButton");
            Require(summary.Text.Contains("清料") && guidance.Text.Contains("核查现场") && stop.Content.ToString() == "停止清料"
                && stop.IsEnabled && !((Button)page.FindName("PrepareAllButton")).IsEnabled, "清料执行时错误显示或允许回零。");
            runtimeType.GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(runtime, MachineRunState.Alarmed);
            page.SetRuntime(runtime);
            Require(summary.Text == "清料状态待确认" && guidance.Text.Contains("未确认安全结束"), "清料失败时错误报告仍在正常执行。");
            page.SetLanguage("en-US");
            Require(summary.Text.Contains("requires inspection") && stop.Content.ToString() == "Stop clearance", "清料状态没有随语言切换。");
            runtimeType.GetField("_clearing", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(runtime, false);
            page.SetRuntime(runtime);
            Require(stop.Content.ToString() == "Stop preparation", "退出清料后未恢复回零停止文案。");
        }

        /// <summary>待确认资源应先提示现场核查，不误导操作员先回零或关闭保持输出。</summary>
        private static void CheckBlockedGuidance(Type type)
        {
            var method = type.GetMethod("GetBlockedResourceGuidance", BindingFlags.Static | BindingFlags.NonPublic);
            string stopped = (string)method.Invoke(null, new object[] { TaskMachineState.Stopped, false });
            string paused = (string)method.Invoke(null, new object[] { TaskMachineState.Paused, false });
            string english = (string)method.Invoke(null, new object[] { TaskMachineState.Stopped, true });
            Require(stopped.Contains("核查待确认资源") && stopped.Contains("不会自动关闭") && !stopped.Contains("先回零"),
                "已停止的隔离资源没有给出安全核查入口。 ");
            Require(paused.Contains("停止任务") && paused.Contains("停稳"), "任务未终止时没有提示先停止并确认停稳。 ");
            Require(english.Contains("Inspect blocked resources"), "英文状态缺少资源核查入口。 ");
        }

        /// <summary>仅正常停稳且无报警时优先给出复位操作提示，其他保护原因不能隐藏。</summary>
        private static void CheckPausedGuidance(Type type)
        {
            var method = type.GetMethod("IsSettledPauseAwaitingReset", BindingFlags.Static | BindingFlags.NonPublic);
            string pendingMarker = "上次运行尚未确认安全收尾，不能再次启动。请联系维护人员。";
            Func<TaskMachineState, MachineRunState, bool, bool, string, bool> showPause = (task, runtime, settled, alarm, reason) =>
                (bool)method.Invoke(null, new object[] { task, runtime, settled, alarm, reason });
            Require(showPause(TaskMachineState.Paused, MachineRunState.Paused, true, false, pendingMarker), "已停稳的正常暂停没有显示复位提示。");
            Require(!showPause(TaskMachineState.Paused, MachineRunState.Paused, false, false, pendingMarker), "尚未结算的暂停隐藏了维护提示。");
            Require(!showPause(TaskMachineState.Faulted, MachineRunState.Alarmed, true, false, pendingMarker), "故障状态隐藏了维护提示。");
            Require(!showPause(TaskMachineState.Paused, MachineRunState.Paused, true, true, pendingMarker), "存在停机报警时隐藏了维护提示。");
            Require(!showPause(TaskMachineState.Paused, MachineRunState.Paused, true, false, "任务资源尚待确认"), "其他恢复保护原因被隐藏。");
        }

        /// <summary>读取被检查页面的控件字段，不修改设备状态。</summary>
        private static T Field<T>(Type type, object page, string name) where T : class
        {
            return (T)type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(page);
        }

        /// <summary>在断言失败时给出明确原因。</summary>
        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        /// <summary>只暴露已连接状态，测试页面不得调用硬件生命周期。</summary>
        private sealed class ConnectedReadOnlyCard : IMotionCard
        {
            /// <summary>检查卡标识。</summary>
            public string ControllerId { get { return "ConnectionDisplayCheck"; } }
            /// <summary>提供稳定连接状态，不代表真实控制器。</summary>
            public bool IsConnected { get { return true; } }
            /// <summary>测试无需轴。</summary>
            public IReadOnlyCollection<IAxis> Axes { get { return new IAxis[0]; } }
            /// <summary>不允许页面建立连接。</summary>
            public Task ConnectAsync(CancellationToken token) { throw new InvalidOperationException("观察页不得连接卡。"); }
            /// <summary>不允许页面断开连接。</summary>
            public Task DisconnectAsync(CancellationToken token) { throw new InvalidOperationException("观察页不得断开卡。"); }
        }
    }
}
