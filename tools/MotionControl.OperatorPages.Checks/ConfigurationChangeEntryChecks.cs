using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Controls;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.Framework.Runtime.Variables;
using MotionControl.Framework.Wpf.Variables;
using MotionControl.Host.Wpf.Configuration;

namespace MotionControl.OperatorPages.Checks
{
    /// <summary>用隔离配置验证变量和报警库实际保存回执及记录故障。</summary>
    internal static class ConfigurationChangeEntryChecks
    {
        /// <summary>核对真实保存阶段、独立记录失败提示及不同设备的记录隔离。</summary>
        internal static void Run()
        {
            string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            string root = Path.Combine(temp, "ConfigurationEntry-" + Guid.NewGuid().ToString("N"));
            try
            {
                var files = new MachineConfigurationStore(root);
                var alarmStore = new MachineAlarmCatalogStore(files);
                alarmStore.Save("AlarmGood", new MachineAlarmCatalogDocument());
                var alarmPage = new AlarmCatalogConfigurationView(alarmStore, "AlarmGood");
                var journalSetter = typeof(AlarmCatalogConfigurationView).GetMethod("SetChangeJournal");
                Require(journalSetter != null, "RED: alarm catalog does not expose a configuration history connection.");
                var alarmJournal = new ConfigurationChangeJournal(files, "AlarmGood");
                journalSetter.Invoke(alarmPage, new object[] { alarmJournal });
                alarmPage.Alarms.Add(new MachineAlarmCatalogEntry { Code = "A1", Message = "测试报警", Kind = MachineAlarmKind.StopMachine });
                SaveAlarm(alarmPage);
                var alarms = alarmJournal.ReadRecent(10, out var diagnostics);
                Require(diagnostics.Count == 0 && alarms.Count == 1 && alarms[0].Stage == ConfigurationChangeStage.SaveFiles
                    && alarms[0].Outcome == ConfigurationChangeOutcome.Succeeded && alarms[0].ObjectId == MachineAlarmCatalogStore.RelativeFilePath
                    && alarms[0].Actor == null && alarms[0].BeforeVersion == null && alarms[0].AfterVersion == null,
                    "报警库保存未记录真实成功阶段，或凭空填写人员和版本。");
                alarmPage.Alarms.Add(new MachineAlarmCatalogEntry { Code = "A1", Message = "重复", Kind = MachineAlarmKind.StopMachine });
                SaveAlarm(alarmPage);
                alarms = alarmJournal.ReadRecent(10, out diagnostics);
                Require(alarms.Count == 2 && alarms.Any(row => row.Stage == ConfigurationChangeStage.Check
                    && row.Outcome == ConfigurationChangeOutcome.Failed && !string.IsNullOrWhiteSpace(row.Detail)),
                    "RED: alarm catalog validation failure is mislabeled as a file write failure.");
                Require(alarmStore.Load("AlarmGood").Alarms.Count == 1, "失败的报警库保存覆盖了有效文件。");

                alarmStore.Save("AlarmIoFail", new MachineAlarmCatalogDocument());
                var alarmIoPage = new AlarmCatalogConfigurationView(alarmStore, "AlarmIoFail");
                var alarmIoJournal = new ConfigurationChangeJournal(files, "AlarmIoFail");
                journalSetter.Invoke(alarmIoPage, new object[] { alarmIoJournal });
                alarmIoPage.Alarms.Add(new MachineAlarmCatalogEntry { Code = "IO_FAIL", Message = "文件锁定", Kind = MachineAlarmKind.StopMachine });
                string lockedFile = Path.Combine(files.GetMachineDirectory("AlarmIoFail"), "Parameters", "AlarmCatalog.json");
                using (new FileStream(lockedFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) SaveAlarm(alarmIoPage);
                var ioResults = alarmIoJournal.ReadRecent(10, out diagnostics);
                Require(ioResults.Count == 1 && ioResults[0].Stage == ConfigurationChangeStage.SaveFiles
                    && ioResults[0].Outcome == ConfigurationChangeOutcome.Failed && !string.IsNullOrWhiteSpace(ioResults[0].Detail),
                    "真正的文件写入失败没有记录为独立的文件阶段。");

                alarmStore.Save("AlarmAuditFail", new MachineAlarmCatalogDocument());
                var alarmAuditPage = new AlarmCatalogConfigurationView(alarmStore, "AlarmAuditFail");
                journalSetter.Invoke(alarmAuditPage, new object[] { new ConfigurationChangeJournal(files, "AlarmAuditFail") });
                alarmAuditPage.Alarms.Add(new MachineAlarmCatalogEntry { Code = "A2", Message = "审计故障测试", Kind = MachineAlarmKind.StopMachine });
                BlockJournal(files, "AlarmAuditFail");
                SaveAlarm(alarmAuditPage);
                Require(alarmStore.Load("AlarmAuditFail").Alarms.Count == 1
                    && ((TextBlock)alarmAuditPage.FindName("ChangeHistoryWarningText")).Text.Contains("变更记录未保存"),
                    "报警库文件已保存但记录失败时，结果或独立提示丢失。");

                var variableStore = new DeviceVariableStore(Array.Empty<DeviceVariableDefinition>());
                var variablePage = new DeviceVariableView(variableStore, files, "VariableGood");
                SaveVariables(variablePage);
                var variableJournal = new ConfigurationChangeJournal(files, "VariableGood");
                var variables = variableJournal.ReadRecent(10, out diagnostics);
                Require(diagnostics.Count == 0 && variables.Count == 2
                    && variables.Any(row => row.Stage == ConfigurationChangeStage.SaveFiles && row.Outcome == ConfigurationChangeOutcome.Succeeded)
                    && variables.Any(row => row.Stage == ConfigurationChangeStage.Apply && row.Outcome == ConfigurationChangeOutcome.Succeeded)
                    && variables.All(row => row.ObjectId == "Parameters/DeviceVariables.json" && row.Actor == null
                        && row.BeforeVersion == null && row.AfterVersion == null)
                    && !string.IsNullOrWhiteSpace(variables[0].OperationId) && variables[0].OperationId == variables[1].OperationId,
                    "RED: variable file save and in-memory application are not recorded as separate stages.");
                Require(variableStore.GetDefinitions().Any(item => item.Name == "TestVariable"), "变量定义未应用到内存。");

                var failedStore = new DeviceVariableStore(Array.Empty<DeviceVariableDefinition>());
                var failedPage = new DeviceVariableView(failedStore, files, "VariableAuditFail");
                BlockJournal(files, "VariableAuditFail");
                SaveVariables(failedPage);
                var warningText = failedPage.FindName("ChangeHistoryWarningText") as TextBlock;
                Require(failedStore.GetDefinitions().Any(item => item.Name == "TestVariable")
                    && File.Exists(Path.Combine(files.GetMachineDirectory("VariableAuditFail"), "Parameters", "DeviceVariables.json"))
                    && warningText != null && warningText.Text.Contains("变更记录未保存"),
                    "RED: successful variable changes lack an independent persistent history warning.");
                failedStore.SetBoolean("TestVariable", true);
                failedPage.SetMachineRuntime(null);
                Require(warningText.Text.Contains("变更记录未保存"), "RED: status refresh erased the history failure warning.");
                warningText.Text += "\n" + string.Concat(Enumerable.Repeat("长记录错误路径 / long audit error path ", 30)) + "END";
                failedPage.Measure(new Size(824, 600));
                failedPage.Arrange(new Rect(0, 0, 824, 600));
                failedPage.UpdateLayout();
                var warningScroll = failedPage.FindName("ChangeHistoryWarningScroll") as ScrollViewer;
                Require(warningScroll != null && warningScroll.ScrollableHeight > 0,
                    "RED: long configuration history errors are clipped without a scrollable warning area.");
                warningScroll.ScrollToBottom(); failedPage.UpdateLayout();
                Require(warningScroll.VerticalOffset >= warningScroll.ScrollableHeight - 1 && warningText.Text.EndsWith("END"),
                    "长记录错误的末尾无法查看。");

                var partialStore = new DeviceVariableStore(Array.Empty<DeviceVariableDefinition>());
                var partialPage = new DeviceVariableView(partialStore, files, "VariablePartialAudit");
                string partialDir = Path.Combine(files.GetMachineDirectory("VariablePartialAudit"), "Runtime", "ConfigurationChanges");
                Directory.CreateDirectory(partialDir);
                for (int index = 0; index < ConfigurationChangeJournal.MaximumRecords - 1; index++)
                    File.WriteAllText(Path.Combine(partialDir, "slot-" + index.ToString("D4") + ".pending"), "pending");
                SaveVariables(partialPage);
                var partialJournal = new ConfigurationChangeJournal(files, "VariablePartialAudit");
                var partial = partialJournal.ReadRecent(10, out diagnostics);
                var partialWarning = (TextBlock)partialPage.FindName("ChangeHistoryWarningText");
                Require(partial.Count == 1 && partial[0].Stage == ConfigurationChangeStage.SaveFiles
                    && partialStore.GetDefinitions().Any(item => item.Name == "TestVariable")
                    && partialWarning.Text.Contains("内存更新阶段") && !partialWarning.Text.Contains("文件保存阶段"),
                    "仅内存阶段记录失败时未准确区分缺失阶段与业务结果。");
                CheckVariableApplyException(files);
            }
            finally
            {
                if (Path.GetDirectoryName(Path.GetFullPath(root)) != temp) throw new InvalidOperationException("临时检查目录越界。");
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        /// <summary>在隔离窗口中注入内存更新通知异常，并验证业务提醒不会被后续刷新覆盖。</summary>
        private static void CheckVariableApplyException(MachineConfigurationStore files)
        {
            var store = new DeviceVariableStore(Array.Empty<DeviceVariableDefinition>());
            var page = new DeviceVariableView(store, files, "VariableApplyError");
            var owner = new Window { Content = page, ShowInTaskbar = false };
            new System.Windows.Interop.WindowInteropHelper(owner).EnsureHandle();
            EventHandler failAfterUpdate = (sender, args) => throw new InvalidOperationException("injected observer failure");
            store.VariablesChanged += failAfterUpdate;
            bool dismissed = false;
            uint thread = GetCurrentThreadId();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            timer.Tick += (sender, args) => EnumThreadWindows(thread, (window, parameter) =>
            {
                var name = new StringBuilder(64);
                GetClassName(window, name, name.Capacity);
                if (name.ToString() == "#32770") { dismissed = PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero); return false; }
                return true;
            }, IntPtr.Zero);
            try { timer.Start(); SaveVariables(page); }
            finally { timer.Stop(); store.VariablesChanged -= failAfterUpdate; owner.Close(); }
            var warning = (TextBlock)page.FindName("DefinitionApplyWarningText");
            var container = (ScrollViewer)page.FindName("DefinitionApplyWarningScroll");
            var rows = new ConfigurationChangeJournal(files, "VariableApplyError").ReadRecent(10, out var diagnostics);
            Require(dismissed && rows.Count == 2 && rows.Any(row => row.Stage == ConfigurationChangeStage.SaveFiles
                && row.Outcome == ConfigurationChangeOutcome.Succeeded)
                && rows.Any(row => row.Stage == ConfigurationChangeStage.Apply && row.Outcome == ConfigurationChangeOutcome.Failed
                    && row.Detail.Contains("injected observer failure")) && store.GetDefinitions().Any(item => item.Name == "TestVariable")
                && container.Visibility == Visibility.Visible && warning.Text.Contains("内存") && warning.Text.Contains("待核查"),
                "文件已保存后更新通知异常时，没有保留内存状态待核查的真实回执。");
            store.SetBoolean("TestVariable", true);
            page.SetMachineRuntime(null);
            Require(container.Visibility == Visibility.Visible && warning.Text.Contains("待核查"), "变量刷新覆盖了内存状态待核查提示。");
        }

        /// <summary>只枚举当前测试 UI 线程的对话框，防止测试使用鼠标或影响其他进程。</summary>
        private delegate bool WindowEnumerator(IntPtr window, IntPtr parameter);
        /// <summary>枚举测试线程创建的窗口。</summary>
        [DllImport("user32.dll")]
        private static extern bool EnumThreadWindows(uint threadId, WindowEnumerator callback, IntPtr parameter);
        /// <summary>读取窗口类名以识别测试中的系统提示框。</summary>
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr window, StringBuilder name, int maximum);
        /// <summary>向测试线程的提示框投递关闭消息，不使用鼠标。</summary>
        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        /// <summary>获取当前测试线程标识，避免触及其他窗口。</summary>
        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        /// <summary>调用正式报警库保存入口，不连接设备。</summary>
        private static void SaveAlarm(AlarmCatalogConfigurationView page)
        {
            typeof(AlarmCatalogConfigurationView).GetMethod("SaveAlarms_Click", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(page, new object[] { null, new RoutedEventArgs() });
        }

        /// <summary>调用正式变量定义保存入口，不修改现场值。</summary>
        private static void SaveVariables(DeviceVariableView page)
        {
            var definitions = new List<DeviceVariableDefinition> { new DeviceVariableDefinition { Name = "TestVariable", DataType = "Boolean" } };
            typeof(DeviceVariableView).GetMethod("SaveDefinitions", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(page, new object[] { definitions, "变量新增成功" });
        }

        /// <summary>只在临时设备目录制造记录路径冲突，保留其他配置写入能力。</summary>
        private static void BlockJournal(MachineConfigurationStore files, string machineId)
        {
            string runtime = Path.Combine(files.GetMachineDirectory(machineId), "Runtime");
            Directory.CreateDirectory(runtime);
            File.WriteAllText(Path.Combine(runtime, "ConfigurationChanges"), "blocked");
        }

        /// <summary>输出具体失败原因。</summary>
        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
