using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.Host.Wpf.Configuration;
using MotionControl.TaskEngine.Configuration;

namespace MotionControl.ConfigurationChangeUi.Checks
{
    /// <summary>在临时工程和独立 STA 中检查设备工程页的真实导入回执，不启动 Host 或设备。</summary>
    internal static class EquipmentProjectHistoryChecks
    {
        /// <summary>创建与用户配置无关的单次隔离目录，分别验证三种导入结果。</summary>
        [STAThread]
        private static void Main()
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            string temp = Path.GetFullPath(Path.GetTempPath());
            string root = Path.Combine(temp, "equipment-history-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var store = new MachineConfigurationStore(root);
                var source = Fixture(store, "Source", "IncomingAxis");
                string package = Path.Combine(root, "import.mcpkg.json");
                source.Export(package);
                Scenario(store, package, "Success");
                Scenario(store, package, "Denied");
                Scenario(store, package, "AuditFailed");
                Scenario(store, package, "Restored");
                CachedPageLifecycle(store);
                Console.WriteLine("设备工程应用与历史查询五组独立检查通过。");
            }
            finally
            {
                if (Path.GetDirectoryName(Path.GetFullPath(root)) != temp.TrimEnd(Path.DirectorySeparatorChar))
                    throw new InvalidOperationException("隔离目录位置无效。");
                Directory.Delete(root, true);
            }
        }

        /// <summary>建立最小的有效单机工程；仅允许测试定义和选定控制器配置。</summary>
        private static MachineProjectPackageService Fixture(MachineConfigurationStore store, string id, string axis)
        {
            store.SaveJson(id, "HardwareConfig/MotionCards.json", new { schemaVersion = 1, motionCardVendor = "正运动",
                motionCardType = "总线型", axisCount = 8, motionCards = new[] { new { controllerId = "Card", adapterType = "ZMotion",
                connectionConfigurationFile = "HardwareConfig/Connection.json" } } });
            store.SaveJson(id, "HardwareConfig/Connection.json", new { schemaVersion = 1 });
            store.SaveJson(id, "HardwareConfig/AxisParameters.json", new { schemaVersion = 1, axes = new[] { new {
                controllerId = "Card", axisNumber = 0, name = axis, engineeringUnit = "mm", unitsPerEngineeringUnit = 1,
                minimumPosition = -10, maximumPosition = 10, softLimitEnabled = true, manualSpeed = 1 } } });
            store.SaveJson(id, "HardwareConfig/IoMapping.json", new { schemaVersion = 1, digitalInputs = new[] {
                new { controllerId = "Card", name = "急停", channel = 0, activeHigh = true } }, digitalOutputs = new object[0] });
            store.SaveJson(id, "HardwareConfig/SafetyInterlock.json", new { schemaVersion = 1, emergencyStopInputName = "急停" });
            store.SaveJson(id, "Parameters/Equipment.json", new { schemaVersion = 1, definition = "Demo", executionOwner = "StationFlows" });
            return new MachineProjectPackageService(store, id, new[] { "Demo" }, new[] { "ZMotion" }, null,
                card => card.AxisCount == 8 && card.CardCount == 1 && card.Backend == "ZMotion" ? null : "卡型不匹配");
        }

        /// <summary>进入确认后的同一页面入口，分别检查门禁拒绝、正常提交与审计失败。</summary>
        private static void Scenario(MachineConfigurationStore store, string package, string id)
        {
            var target = Fixture(store, id, "OldAxis");
            var journal = new ConfigurationChangeJournal(store, id);
            int gateCalls = 0, restartCalls = 0, serviceGateReads = 0;
            var page = new EquipmentProjectView(target, applying =>
            {
                if (!applying) return null;
                // 第三次是备份和中断标记创建后的写入阶段，由原服务执行备份恢复。
                if (++serviceGateReads == 3 && id == "Restored")
                    throw new AggregateException("应用回调异常", new InvalidOperationException("读取配置失败",
                        new IOException("隔离文件读取根因")), new IOException("另一条原始失败原因"));
                return id == "Denied" ? "测试门禁拒绝" : null;
            },
                () => { gateCalls++; return Task.FromResult<IDisposable>(null); }, () => restartCalls++, journal);
            var tabs = (TabControl)page.FindName("DetailsTabs");
            var history = (TabItem)page.FindName("ChangeHistoryTab");
            tabs.SelectedItem = history;
            Run(page, "RefreshHistoryAsync");
            var message = (TextBlock)page.FindName("ChangeHistoryMessage");
            Check(message.Text.Contains("尚无持久记录") && gateCalls == 0 && restartCalls == 0, "历史只读空状态不正确。");
            string machine = store.GetMachineDirectory(id);
            string historyDirectory = Path.Combine(machine, "Runtime", "ConfigurationChanges");
            Check(!Directory.Exists(historyDirectory), "只读查询创建了记录目录。");
            tabs.SelectedItem = page.FindName("PreviewTab");
            var preview = target.Preview(package);
            typeof(EquipmentProjectView).GetField("_preview", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(page, preview);
            if (id == "AuditFailed")
            {
                Directory.CreateDirectory(Path.GetDirectoryName(historyDirectory));
                File.WriteAllText(historyDirectory, "记录目录不可用");
            }
            Run(page, "ApplyConfirmedAsync");
            string status = ((TextBlock)page.FindName("StatusText")).Text + ((TextBox)page.FindName("StatusDetailsText")).Text;
            Check(gateCalls == 1 && restartCalls == 1 && !((Button)page.FindName("ApplyButton")).IsEnabled, "门禁或重启要求不一致。");
            string axisText = File.ReadAllText(Path.Combine(machine, "HardwareConfig", "AxisParameters.json"));
            bool denied = id == "Denied";
            bool failed = denied || id == "Restored";
            Check(axisText.Contains(failed ? "OldAxis" : "IncomingAxis"), "实际配置结果与预期不符。");
            tabs.SelectedItem = history;
            Run(page, "RefreshHistoryAsync");
            Check(gateCalls == 1 && restartCalls == 1 && !((Button)page.FindName("ExportHistoryButton")).IsEnabled, "只读历史查询产生副作用。");
            if (id == "AuditFailed")
            {
                Check(status.Contains("工程已应用") && status.Contains("重新打开") && status.Contains("记录未保存") &&
                    !status.Contains("未能完成应用") && message.Text.Contains("读取失败"), "审计失败遮盖了真实工程结果或伪装成无记录。");
                return;
            }
            IReadOnlyList<string> notices;
            var row = journal.ReadRecent(10, out notices).Single();
            Check(row.Stage == ConfigurationChangeStage.Apply && row.ObjectId == id && row.Source == "MachineProjectPackageService.Apply"
                && row.TimeUtc != default(DateTimeOffset) && row.BeforeVersion == null && row.AfterVersion == null && row.Actor == null,
                "记录的来源、设备或版本不正确。");
            Check(row.Outcome == (failed ? ConfigurationChangeOutcome.Failed : ConfigurationChangeOutcome.Succeeded)
                && (denied ? row.Detail.Contains("测试门禁拒绝") : id == "Restored" ? row.Detail.Contains("已恢复原配置")
                    : row.Detail.Contains("备份")), "记录结果未保留原因。");
            if (id == "Restored")
                foreach (string reason in new[] { "已恢复原配置", "读取配置失败", "隔离文件读取根因", "另一条原始失败原因" })
                    Check(status.Contains(reason) && row.Detail.Contains(reason), "应用/恢复详情未保留原因：" + reason);
            string detail = ((TextBox)page.FindName("HistoryDetail")).Text;
            Check(detail.Contains(row.Source) && detail.Contains("未提供") && detail.Contains(id), "记录详情不完整。");
        }

        /// <summary>控制旧请求的完成时机，验证整页卸载、重进重读，以及先后迟到的成功和错误。</summary>
        private static void CachedPageLifecycle(MachineConfigurationStore store)
        {
            const string id = "Cached";
            var journal = new ConfigurationChangeJournal(store, id);
            var page = new EquipmentProjectView(Fixture(store, id, "X"), applying => throw new Exception("查询不应检查设备"),
                () => throw new Exception("查询不应申请门禁"), () => throw new Exception("查询不应要求重启"), journal);
            ((TabControl)page.FindName("DetailsTabs")).SelectedItem = page.FindName("ChangeHistoryTab");
            Run(page, "RefreshHistoryAsync");
            var message = (TextBlock)page.FindName("ChangeHistoryMessage");
            var detail = (TextBox)page.FindName("HistoryDetail");
            var display = typeof(EquipmentProjectView).GetMethod("DisplayHistoryQueryAsync", BindingFlags.NonPublic | BindingFlags.Instance);
            var pendingOk = new TaskCompletionSource<(IReadOnlyList<ConfigurationChangeRecord>, IReadOnlyList<string>)>();
            var pendingError = new TaskCompletionSource<(IReadOnlyList<ConfigurationChangeRecord>, IReadOnlyList<string>)>();
            var oldOk = (Task)display.Invoke(page, new object[] { pendingOk.Task });
            var oldError = (Task)display.Invoke(page, new object[] { pendingError.Task });
            page.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            string detached = message.Text;
            pendingError.SetException(new IOException("来自已卸载页面的错误"));
            Pump(oldError);
            Check(message.Text == detached && detail.Text.Length == 0, "卸载后迟到的异常修改了缓存页。");
            var current = new ConfigurationChangeRecord { RecordId = Guid.NewGuid(), TimeUtc = DateTimeOffset.UtcNow,
                MachineId = id, ObjectType = "测试工程", ObjectId = id, Stage = ConfigurationChangeStage.Apply,
                Outcome = ConfigurationChangeOutcome.Succeeded, Source = "隔离测试" };
            Check(journal.TryAppend(current) == null, "测试记录未写入。");
            page.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            // 等待 Loaded 真正启动的磁盘查询，测试不调用刷新代替生命周期处理。
            var ready = new TaskCompletionSource<bool>();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (source, args) => { if (detail.Text.Contains(current.RecordId.ToString())) ready.TrySetResult(true); };
            timer.Start();
            try { Pump(ready.Task); }
            finally { timer.Stop(); }
            string visible = detail.Text;
            pendingOk.SetResult((Array.Empty<ConfigurationChangeRecord>(), Array.Empty<string>()));
            Pump(oldOk);
            Check(detail.Text == visible && !message.Text.Contains("尚无"), "卸载前的旧空结果覆盖了重进后的真实记录。");
        }

        /// <summary>驱动 STA 调度器完成真实异步页面任务，不显示窗口或操作设备。</summary>
        private static void Run(EquipmentProjectView page, string method)
        {
            var task = (Task)typeof(EquipmentProjectView).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(page, null);
            Pump(task);
        }

        /// <summary>在有界等待内处理当前 STA 的任务续体。</summary>
        private static void Pump(Task task)
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var frame = new DispatcherFrame();
            var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            timeout.Tick += (sender, args) => frame.Continue = false;
            timeout.Start();
            task.ContinueWith(done => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
            timeout.Stop();
            Check(task.IsCompleted, "页面查询或应用超时。");
            task.GetAwaiter().GetResult();
        }

        /// <summary>在失败处报告可定位的预期行为。</summary>
        private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    }
}
