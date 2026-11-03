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
            Require(SpinWait.SpinUntil(() => device.ReadResources().Any(item => item.State == TaskResourceState.Held), 5000), "任务未持有资源。");
            run.Cancel(); Require(run.Completion.Wait(TimeSpan.FromSeconds(5)), "任务取消未退出。");
            string id = device.ReadResources().Single(item => item.State == TaskResourceState.Blocked).RequestId;
            var sample = ResourceRecoverySample.Capture(device, id);
            Require(sample.Description.Contains("Axis:X") && sample.Description.Contains("Sensor") && sample.Description.Contains("Clamp"), "资源或轴 IO 采样没有展示。");
            VerifyDialogGate(device, sample);
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
            Require(device.ReadResources().Single().State == TaskResourceState.Blocked, "检查失败移除了资源隔离。");
            device.WriteOutput("Clamp", false);
            Require(sample.TryConfirm(device, true, "已检查现场并确认机构停止", () => true, out reason), reason);
            Require(device.ReadResources().Count == 0 && device.ReadOutput("Clamp") == false, "明确核查后没有解除目标资源或意外切换输出。");
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

        /// <summary>断言检查结果并保留失败原因。</summary>
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
