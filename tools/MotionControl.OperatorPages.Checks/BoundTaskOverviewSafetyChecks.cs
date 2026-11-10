using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.Preparation;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.Framework.Runtime.Alarms;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.Framework.Runtime.Machine;
using MotionControl.Framework.Runtime.Variables;
using MotionControl.Host.Wpf;
using MotionControl.Host.Wpf.Operation;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Configuration;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Execution;
using MotionControl.TaskEngine.Machine;
using MotionControl.TaskEngine.MachineDevice;
using MotionControl.TaskEngine.Resources;
using MotionControl.TaskEngine.Wpf.Simulation;

namespace MotionControl.OperatorPages.Checks
{
    /// <summary>使用正式任务会话的真实资源隔离记录，检查总览警示与工位来源的不同生命周期。</summary>
    internal static partial class BoundTaskLoadChecks
    {
        /// <summary>准备换代撤下旧工位但保留当前连接的资源警示；切换连接或任务后不沿用旧摘要。</summary>
        internal static async Task RunOverviewSafetySourceChecksAsync()
        {
            const string id = "OverviewSafety";
            const string taskName = "ROC28保留资源任务";
            var root = Path.Combine(Path.GetTempPath(), "OverviewSafety-" + Guid.NewGuid().ToString("N"));
            var store = new MachineConfigurationStore(root);
            var window = new MainWindow();
            var cards = new MotionCardGroup(new[] { new LoadCard() });
            var runtime = new MachineRuntime(cards, new IStationFlow[0], new MachineAlarmService(), new SafeCheckSafety());
            var otherRuntime = new MachineRuntime(new MotionCardGroup(new[] { new LoadCard() }),
                new IStationFlow[0], new MachineAlarmService(), new SafeCheckSafety());
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var type = typeof(MainWindow).Assembly.GetType("MotionControl.Host.Wpf.Tasks.TaskMonitorLauncher", true);
            var launcher = Activator.CreateInstance(type, flags, null, new object[] { store, id, window, null }, null);
            // 只调用正式页面命令，并读取其实际持有的会话，不改写会话或资源状态。
            Task Invoke(string name, params object[] arguments) => (Task)type.GetMethod(name, flags).Invoke(launcher, arguments);
            TaskMonitorSession Session() => (TaskMonitorSession)type.GetField("_session", flags).GetValue(launcher);
            TaskMachineOperations Operations() => (TaskMachineOperations)type.GetField("_taskOperations", flags).GetValue(launcher);
            Exception testError = null;
            Exception cleanupError = null;
            bool preparationChanged = false;
            try
            {
                WriteOverviewSafetyMachine(store, id, taskName);
                await runtime.ConnectAsync(CancellationToken.None);
                await otherRuntime.ConnectAsync(CancellationToken.None);
                await Invoke("SetMachineContextAsync", runtime, cards, new NoIo(),
                    new DeviceVariableStore(new DeviceVariableDefinition[0]), new IStationPreparation[0]);
                window.SetTaskMonitorPage((UserControl)launcher);
                window.SetMachineRuntime(runtime);
                window.SetMotionCardConnectionStatus(true);
                await Invoke("LoadTaskSessionAsync");
                var retainedSession = Session();
                Require(retainedSession != null, "资源警示检查没有装载正式任务会话。");
                await Invoke("ResetFromHostAsync", true);
                await Invoke("RunFromHostAsync", true);
                await WaitForOverviewResourceBodyAsync(retainedSession);
                await Operations().AbortAsync();
                var blocked = retainedSession.TaskDevice.Resources.Read().Single();
                Require(blocked.State == TaskResourceState.Blocked && blocked.TaskName == taskName,
                    "资源范围终止后没有生成真实的待核查记录。");

                var overview = (EquipmentOverviewView)typeof(MainWindow).GetField("_equipmentOverviewPage", flags).GetValue(window);
                var safetyText = (TextBlock)overview.FindName("SafetySummaryText");
                var safetyPanel = (FrameworkElement)overview.FindName("SafetySummaryPanel");
                var stations = (ListView)overview.FindName("StationTable");
                var flowButton = (Button)overview.FindName("SelectedFlowButton");
                overview.RefreshDisplay();
                string retainedWarning = (string)type.GetMethod("ReadOverviewSafetySummary", flags).Invoke(launcher, null);
                Require(!string.IsNullOrWhiteSpace(retainedWarning) && retainedWarning.Contains(taskName)
                    && safetyPanel.Visibility == Visibility.Visible && safetyText.Text.Contains(retainedWarning),
                    "有效发布时总览未展示真实待核查资源警示。");
                Require(stations.Items.Count == 1, "有效发布时总览没有显示资源任务工位。");
                stations.SelectedIndex = 0;
                Require(flowButton.IsEnabled, "有效发布时总览的工位定位不可用。");

                runtime.ConfigurePreparations(new[] { new CheckPreparation("replacement") });
                preparationChanged = true;
                Require(type.GetProperty("CurrentPublishedTaskSession", flags).GetValue(launcher) == null,
                    "准备换代后旧任务仍是有效发布来源。");
                overview.RefreshDisplay();
                bool warningRetained = safetyPanel.Visibility == Visibility.Visible && safetyText.Text.Contains(retainedWarning);
                bool stationRemoved = stations.Items.Count == 0 && stations.SelectedIndex == -1 && !flowButton.IsEnabled
                    && !(bool)type.GetMethod("FocusStationByName", flags).Invoke(launcher, new object[] { "OverviewResource" });
                bool resourceRetained = ReferenceEquals(Session(), retainedSession)
                    && retainedSession.TaskDevice.Resources.Read().Single().State == TaskResourceState.Blocked;

                // 旧会话仍保留隔离，另一个真实连接成为总览来源时也不能把旧摘要带过去。
                window.SetMachineRuntime(otherRuntime);
                window.SetMotionCardConnectionStatus(true);
                overview.RefreshDisplay();
                bool otherConnectionRejected = !safetyText.Text.Contains(taskName);
                window.SetMachineRuntime(runtime);
                overview.RefreshDisplay();
                bool warningRestored = safetyPanel.Visibility == Visibility.Visible && safetyText.Text.Contains(retainedWarning);

                // 这里只有无轴、无输出的命名区域，按现有双重核查接口明确恢复后再安全关闭。
                Require(retainedSession.TaskDevice.TryRecoverResources(blocked.RequestId,
                    "仅内存检查：延时任务已退出，命名区域没有轴或输出，已核对资源记录。", out string recoveryReason), recoveryReason);
                Require(retainedSession.TaskDevice.Resources.Read().Count == 0, "明确恢复后资源隔离仍未解除。");
                await Reject(Invoke("CloseTaskSessionAsync", false), "设备准备配置未能恢复");
                preparationChanged = false;
                await Invoke("LoadTaskSessionAsync");
                overview.RefreshDisplay();
                bool newTaskRejectedOldWarning = !ReferenceEquals(Session(), retainedSession)
                    && type.GetProperty("CurrentPublishedTaskSession", flags).GetValue(launcher) != null
                    && !safetyText.Text.Contains(taskName) && Session().TaskDevice.Resources.Read().Count == 0;

                Require(warningRetained && warningRestored && stationRemoved && resourceRetained
                    && otherConnectionRejected && newTaskRejectedOldWarning,
                    "RED: 工位来源失效后资源警示没有按保留会话隔离。原连接警示保留=" + warningRetained
                    + "；切回原连接警示恢复=" + warningRestored + "；旧工位已撤下=" + stationRemoved
                    + "；真实隔离仍保留=" + resourceRetained + "；其他连接拒绝旧警示=" + otherConnectionRejected
                    + "；新任务拒绝旧警示=" + newTaskRejectedOldWarning);
            }
            catch (Exception error) { testError = error; }
            finally
            {
                try
                {
                    window.SetMachineRuntime(runtime);
                    var session = Session();
                    if (session != null)
                    {
                        await Operations().AbortAsync();
                        foreach (var resource in session.TaskDevice.Resources.Read().Where(item => item.State == TaskResourceState.Blocked).ToArray())
                            Require(session.TaskDevice.TryRecoverResources(resource.RequestId,
                                "仅内存检查收尾：任务已退出，命名区域没有轴或输出，已核对资源记录。", out string reason), reason);
                        if (preparationChanged)
                            await Reject(Invoke("CloseTaskSessionAsync", false), "设备准备配置未能恢复");
                        else await Invoke("CloseTaskSessionAsync", false);
                    }
                    if (runtime.State != MachineRunState.Disconnected)
                    {
                        await runtime.StopStationFlowsForShutdownAsync(CancellationToken.None);
                        await runtime.DisconnectAsync(CancellationToken.None);
                    }
                    if (otherRuntime.State != MachineRunState.Disconnected)
                        await otherRuntime.DisconnectAsync(CancellationToken.None);
                }
                catch (Exception error) { cleanupError = error; }
                window.Close();
                if (cleanupError == null)
                {
                    try { if (Directory.Exists(root)) Directory.Delete(root, true); }
                    catch (Exception error) { cleanupError = error; }
                }
            }
            if (cleanupError != null)
                throw new AggregateException("资源警示检查收尾未确认，保留测试目录和资源证据：" + root,
                    testError == null ? new[] { cleanupError } : new[] { testError, cleanupError });
            if (testError != null) ExceptionDispatchInfo.Capture(testError).Throw();
        }

        /// <summary>等待真实资源正文进入延时，再终止才能证明资源已经占用而非仅排队。</summary>
        private static async Task WaitForOverviewResourceBodyAsync(TaskMonitorSession session)
        {
            var elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed < TimeSpan.FromSeconds(5))
            {
                if (session.TaskDevice.Resources.Read().Any(item => item.State == TaskResourceState.Held)
                    && session.Machine.GetSnapshot().Stations.Any(station => station.Run?.Nodes
                        .Any(node => node.NodeId == "hold" && node.State == TaskNodeState.Waiting) == true)) return;
                await Task.Delay(10);
            }
            throw new InvalidOperationException("资源任务未在时限内进入实际持有区域的延时步骤。");
        }

        /// <summary>生成仅有命名区域与延时的机器，资源警示检查不配置轴、IO 或运动动作。</summary>
        private static void WriteOverviewSafetyMachine(MachineConfigurationStore store, string id, string taskName)
        {
            store.SaveJson(id, "HardwareConfig/AxisParameters.json", new { schemaVersion = 1, axes = new object[0] });
            store.SaveJson(id, "HardwareConfig/IoMapping.json", new { schemaVersion = 1, digitalInputs = new object[0], digitalOutputs = new object[0] });
            var definition = new TaskDefinition
            {
                TaskId = "OverviewResource", Name = taskName, Revision = 1,
                Root = new SequenceNode
                {
                    Id = "root", Name = "资源警示检查", Children = new List<TaskNode>
                    {
                        new ResourceScopeNode
                        {
                            Id = "scope", Name = "命名区域", Resources = new List<string> { "OverviewZone" },
                            Body = new SequenceNode
                            {
                                Id = "body", Name = "持有区域", Children = new List<TaskNode>
                                { new DelayNode { Id = "hold", Name = "等待终止", Milliseconds = 10000 } }
                            }
                        }
                    }
                }
            };
            string tasks = Path.Combine(store.GetMachineDirectory(id), "Tasks");
            Directory.CreateDirectory(tasks);
            File.WriteAllText(Path.Combine(tasks, "OverviewResource.json"), TaskDefinitionJson.Serialize(definition));
            var configuration = new TaskMachineConfiguration
            {
                MachineId = id, Name = "总览资源警示检查", Revision = 1, NamedResources = new List<string> { "OverviewZone" },
                Stations = new List<StationTaskConfiguration>
                { new StationTaskConfiguration { StationId = "OverviewResource", Name = taskName, TaskId = "OverviewResource", Mode = StationTaskMode.Once } }
            };
            new TaskMachineConfigurationStore(store).Save(id, configuration, new[] { definition },
                new TaskResourceCatalogLoader(store).Load(id, configuration.NamedResources));
        }
    }
}
