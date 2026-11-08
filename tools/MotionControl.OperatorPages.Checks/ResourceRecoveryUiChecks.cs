using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Controls;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.Host.Wpf.Tasks;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Device.InMemory;
using MotionControl.TaskEngine.Execution;
using MotionControl.TaskEngine.Resources;

namespace MotionControl.OperatorPages.Checks
{
    /// <summary>检查资源核查页的采样与显式确认门禁，不连接真实设备。</summary>
    internal static class ResourceRecoveryUiChecks
    {
        /// <summary>使用真实任务资源范围制造待确认记录，逐项核查失败时不会释放。</summary>
        internal static void Run()
        {
            var catalog = new TaskResourceCatalog(new[] { new TaskAxisInfo("X", "Card", "mm", -10, 10) },
                new[] { "Sensor" }, new[] { "Clamp" }, new Dictionary<string, DeviceVariableType>(), new TaskTeachPoint[0]);
            var device = new InMemoryTaskDevice(catalog);
            device.ConfigureAxis("X", 0, true, true); device.SetInput("Sensor", false); device.WriteOutput("Clamp", false);
            var scope = new ResourceScopeNode { Id = "scope", Name = "工作区", Resources = new List<string> { "Axis:X", "Output:Clamp" },
                Body = new SequenceNode { Id = "body", Name = "范围内", Children = new List<TaskNode> {
                    new DelayNode { Id = "delay", Name = "保持", Milliseconds = 10000 } } } };
            var task = new TaskDefinition { TaskId = "RecoveryUi", Name = "核查工位", Root = new SequenceNode {
                Id = "root", Name = "步骤", Children = new List<TaskNode> { scope } } };
            TaskRun run = new TaskExecutionEngine().Start(task, device);
            Require(SpinWait.SpinUntil(() => device.ReadResources().Any(item => item.State == TaskResourceState.Held)
                && run.GetSnapshot().Nodes.Any(item => item.NodeId == "delay" && item.State == TaskNodeState.Waiting), 5000),
                "任务未进入资源范围内的等待步骤。 ");
            run.Cancel(); Require(run.Completion.Wait(TimeSpan.FromSeconds(5)), "任务取消未退出。");
            string id = device.ReadResources().Single(item => item.State == TaskResourceState.Blocked).RequestId;
            var sample = ResourceRecoverySample.Capture(device, id);
            Require(sample.Description.Contains("Axis:X") && sample.Description.Contains("Sensor") && sample.Description.Contains("Clamp"), "资源或轴 IO 采样没有展示。");
            VerifyDialogGate(device, sample);
            VerifyContextInvalidation(device, sample);
            VerifyContextInvalidation(device, sample, true);
            string reason;
            Require(!sample.TryConfirm(device, false, "已检查", () => true, out reason), "未勾选确认却解除隔离。");
            Require(!sample.TryConfirm(device, true, " ", () => true, out reason), "未填写检查说明却解除隔离。");
            Require(!sample.TryConfirm(device, true, "已检查", () => false, out reason), "会话无效时解除隔离。");
            device.SetInput("Sensor", true);
            Require(!sample.TryConfirm(device, true, "已检查", () => true, out reason) && reason.Contains("变化"), "输入变化后仍解除隔离。");
            device.SetInput("Sensor", null);
            Require(!sample.TryConfirm(device, true, "已检查", () => true, out reason) && reason.Contains("未知"), "未知输入被放行。");
            device.SetInput("Sensor", false); device.WriteOutput("Clamp", true);
            Require(!sample.TryConfirm(device, true, "已检查", () => true, out reason) && reason.Contains("仍有效"), "有效输出被放行。");
            var activeSample = ResourceRecoverySample.Capture(device, id);
            Require(activeSample.ActiveOutputs.SequenceEqual(new[] { "Clamp" }) && activeSample.Description.Contains("Clamp：有效")
                && activeSample.Description.Contains("Sensor：无效"), "有效保持输出未显示，或中断了其他信号采样。");
            VerifyActiveOutputGate(device, activeSample);
            Require(device.ReadResources().Single().State == TaskResourceState.Blocked, "检查失败移除了资源隔离。");
            device.WriteOutput("Clamp", false);
            Require(!activeSample.TryConfirm(device, true, "已检查", () => true, out reason) && reason.Contains("重新读取"), "保持输出变化后仍沿用旧采样。");
            Require(sample.TryConfirm(device, true, "已检查现场并确认机构停止", () => true, out reason), reason);
            Require(device.ReadResources().Count == 0 && device.ReadOutput("Clamp") == false, "明确核查后没有解除目标资源或意外切换输出。");
        }

        /// <summary>有效输出允许只读查看，但不能借此放行资源或自动切换输出。</summary>
        private static void VerifyActiveOutputGate(InMemoryTaskDevice device, ResourceRecoverySample sample)
        {
            var dialog = new ResourceRecoveryDialog(device, () => true, false);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var resources = (ComboBox)typeof(ResourceRecoveryDialog).GetField("_resources", flags).GetValue(dialog);
            var button = (Button)typeof(ResourceRecoveryDialog).GetField("_recoverButton", flags).GetValue(dialog);
            var confirm = (CheckBox)typeof(ResourceRecoveryDialog).GetField("_confirmed", flags).GetValue(dialog);
            var note = (TextBox)typeof(ResourceRecoveryDialog).GetField("_confirmation", flags).GetValue(dialog);
            resources.SelectedIndex = 0;
            typeof(ResourceRecoveryDialog).GetField("_sample", flags).SetValue(dialog, sample);
            confirm.IsChecked = true; note.Text = "现场已核查";
            Require(!button.IsEnabled && device.ReadOutput("Clamp") == true, "有效保持输出时允许解除或自动切换输出。");
            dialog.Close();
        }

        /// <summary>未采样、未勾选或未填写说明时，对话框不得允许提交。</summary>
        private static void VerifyDialogGate(InMemoryTaskDevice device, ResourceRecoverySample sample)
        {
            var dialog = new ResourceRecoveryDialog(device, () => true, false);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var resources = (ComboBox)typeof(ResourceRecoveryDialog).GetField("_resources", flags).GetValue(dialog);
            var button = (Button)typeof(ResourceRecoveryDialog).GetField("_recoverButton", flags).GetValue(dialog);
            var confirm = (CheckBox)typeof(ResourceRecoveryDialog).GetField("_confirmed", flags).GetValue(dialog);
            var note = (TextBox)typeof(ResourceRecoveryDialog).GetField("_confirmation", flags).GetValue(dialog);
            Require(resources.Items.Count == 1 && !button.IsEnabled, "未列出待确认资源，或未选择即允许提交。");
            resources.SelectedIndex = 0; confirm.IsChecked = true; note.Text = "现场已核查";
            Require(!button.IsEnabled, "尚未采样即允许提交。");
            typeof(ResourceRecoveryDialog).GetField("_sample", flags).SetValue(dialog, sample);
            confirm.IsChecked = false; Require(!button.IsEnabled, "未勾选即允许提交。");
            confirm.IsChecked = true; note.Text = " "; Require(!button.IsEnabled, "未填写说明即允许提交。");
            note.Text = "现场已核查"; Require(button.IsEnabled, "完整核查后未允许提交。");
            resources.SelectedIndex = -1; Require(!button.IsEnabled, "切换资源后仍保留确认权限。");
            dialog.Close();
        }

        /// <summary>断开后主动清除旧采样和确认；即使重新连接，也不能继续使用旧窗口放行。</summary>
        private static void VerifyContextInvalidation(InMemoryTaskDevice device, ResourceRecoverySample sample, bool recoveryPending = false)
        {
            bool currentSession = true;
            var dialog = new ResourceRecoveryDialog(device, () => currentSession, false);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var resources = (ComboBox)typeof(ResourceRecoveryDialog).GetField("_resources", flags).GetValue(dialog);
            var read = (Button)typeof(ResourceRecoveryDialog).GetField("_readButton", flags).GetValue(dialog);
            var release = (Button)typeof(ResourceRecoveryDialog).GetField("_recoverButton", flags).GetValue(dialog);
            var confirmed = (CheckBox)typeof(ResourceRecoveryDialog).GetField("_confirmed", flags).GetValue(dialog);
            var confirmation = (TextBox)typeof(ResourceRecoveryDialog).GetField("_confirmation", flags).GetValue(dialog);
            var text = (TextBox)typeof(ResourceRecoveryDialog).GetField("_sampleText", flags).GetValue(dialog);
            resources.SelectedIndex = 0;
            typeof(ResourceRecoveryDialog).GetField("_sample", flags).SetValue(dialog, sample);
            text.Text = sample.Description;
            confirmed.IsChecked = true;
            confirmation.Text = "已到现场核查";
            Require(release.IsEnabled, "会话有效且核查完整时未允许确认。");

            typeof(ResourceRecoveryDialog).GetField("_recoverInProgress", flags).SetValue(dialog, recoveryPending);
            currentSession = false;
            dialog.RefreshContext();
            Require(typeof(ResourceRecoveryDialog).GetField("_sample", flags).GetValue(dialog) == null,
                "会话失效后仍持有原采样。");
            Require(!read.IsEnabled && !release.IsEnabled && confirmed.IsChecked == false
                && confirmation.Text.Length == 0 && text.Text.Contains("已失效"),
                "会话失效未清除确认或仍将旧采样作为当前值展示。");
            if (recoveryPending)
            {
                var status = (TextBlock)typeof(ResourceRecoveryDialog).GetField("_status", flags).GetValue(dialog);
                Require(status.Text.Contains("最终结果不能在当前窗口确认"),
                    "解除请求未返回时，窗口失效后错误承诺了资源的最终状态。");
            }
            currentSession = true;
            dialog.RefreshContext();
            Require(!read.IsEnabled && !release.IsEnabled && text.Text.Contains("已失效"),
                "旧窗口在新连接中自动恢复可用。");
            Require(device.ReadResources().Single().State == TaskResourceState.Blocked,
                "窗口失效意外解除原资源隔离。");
            dialog.Close();
        }

        /// <summary>断言检查结果并保留失败原因。</summary>
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
