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
                var store = new MachineConfigurationStore(Path.Combine(Path.GetTempPath(), "TaskRunPageChecks"));
                var page = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic,
                    null, new object[] { store, "MissingMachine", host }, null);
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
                var refresh = type.GetMethod("RefreshConnectionDisplay", BindingFlags.Instance | BindingFlags.NonPublic);
                var lost = type.GetMethod("MarkControllerConnectionLost", BindingFlags.Instance | BindingFlags.NonPublic);
                var monitor = Field<ContentControl>(type, page, "_monitorContent");
                refresh.Invoke(page, null);
                Require(empty.Text.Contains("尚未读取") && monitor.Visibility == Visibility.Collapsed, "连接后没有显示未读取状态。");
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
            }
            finally { host.Close(); }
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
