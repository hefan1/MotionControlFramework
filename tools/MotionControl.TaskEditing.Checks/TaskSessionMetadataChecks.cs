using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Controls;
using MotionControl.TaskEngine.Wpf.Editing;
using MotionControl.TaskEngine.Wpf.Simulation;
using MotionControl.TaskEngine.Wpf.Views;

namespace MotionControl.TaskEditing.Checks
{
    /// <summary>核对已发布任务会话的来源身份和配置页只读展示。</summary>
    internal static class TaskSessionMetadataChecks
    {
        /// <summary>验证会话身份只发布一次，修订冻结且断开来源不会保留旧显示。</summary>
        internal static void Run()
        {
            var type = typeof(TaskMonitorSession);
            var publish = type.GetMethod("Publish", new[] { typeof(string) });
            var published = type.GetProperty("PublishedMetadata");
            Require(publish != null && published != null, "RED: published task session metadata is not implemented.");
            var session = TaskMonitorExample.Create();
            TaskMonitorSession second = null;
            try
            {
                Require(published.GetValue(session) == null, "未发布的会话显示了有效身份。");
                // 执行库已在构造时独立冻结；发布前显示库的意外修改不能改变执行修订。
                var early = session.Library.First();
                int executedRevision = early.Value.Revision;
                early.Value.Revision = executedRevision + 100;
                var before = DateTimeOffset.UtcNow;
                object metadata = publish.Invoke(session, new object[] { "MachineSessionCheck" });
                var after = DateTimeOffset.UtcNow;
                early.Value.Revision = executedRevision;
                Require(ReferenceEquals(metadata, published.GetValue(session)), "页面读取的不是发布时冻结的元数据。");
                var metadataType = metadata.GetType();
                var id = (Guid)metadataType.GetProperty("SessionId").GetValue(metadata);
                var time = (DateTimeOffset)metadataType.GetProperty("PublishedAtUtc").GetValue(metadata);
                var revisions = (IReadOnlyDictionary<string, int>)metadataType.GetProperty("TaskRevisions").GetValue(metadata);
                Require(id != Guid.Empty && time >= before && time <= after && time.Offset == TimeSpan.Zero,
                    "会话标识或发布时刻不是真实发布结果。");
                Require((string)metadataType.GetProperty("MachineId").GetValue(metadata) == "MachineSessionCheck"
                    && (string)metadataType.GetProperty("Source").GetValue(metadata) == session.Source,
                    "会话设备或任务来源与冻结会话不一致。");
                Require(revisions.Count == session.Library.Count && revisions.Count > 0, "冻结任务修订数量错误。");
                Require(revisions[early.Key] == executedRevision, "RED: published revision differs from the execution snapshot.");
                var machineRevision = metadataType.GetProperty("MachineConfigurationRevision");
                Require(machineRevision != null, "RED: execution manifest revision is missing.");
                var configurationField = session.Machine.GetType().GetField("_configuration", BindingFlags.Instance | BindingFlags.NonPublic);
                var executedConfiguration = configurationField.GetValue(session.Machine);
                int expectedMachineRevision = (int)executedConfiguration.GetType().GetProperty("Revision").GetValue(executedConfiguration);
                Require((int)machineRevision.GetValue(metadata) == expectedMachineRevision, "会话清单修订与实际执行清单不一致。");
                var first = session.Library.First();
                int revision = first.Value.Revision;
                Require(revisions[first.Key] == revision, "未记录会话内任务修订。");
                first.Value.Revision = revision + 100;
                Require(revisions[first.Key] == revision, "会话修订被后续可变对象修改。");
                first.Value.Revision = revision;
                bool duplicateRejected = false;
                try { publish.Invoke(session, new object[] { "MachineSessionCheck" }); }
                catch (TargetInvocationException error) { duplicateRejected = error.InnerException is InvalidOperationException; }
                Require(duplicateRejected && ReferenceEquals(metadata, published.GetValue(session)), "重复发布改变了会话身份。");
                second = TaskMonitorExample.Create();
                object next = publish.Invoke(second, new object[] { "MachineSessionCheck" });
                Require((Guid)metadataType.GetProperty("SessionId").GetValue(next) != id, "重新读取沿用了旧会话标识。");

                using (var test = new Workspace())
                {
                    var page = new TaskConfigurationView(test.Configuration, Workspace.Id, () => null);
                    var setter = typeof(TaskConfigurationView).GetMethod("SetCurrentTaskSessionProvider");
                    Require(setter != null, "任务配置页未接入已发布会话来源。");
                    TaskMonitorSession current = session;
                    setter.Invoke(page, new object[] { new Func<TaskMonitorSession>(() => current) });
                    Refresh(page);
                    var card = (TextBlock)page.FindName("SessionSummaryText");
                    var details = (TextBlock)page.FindName("SourceSummaryText");
                    Require(card.Text.Contains(id.ToString()) && card.Text.Contains("工程版本未提供")
                        && details.Text.Contains("设备任务清单 R" + expectedMachineRevision)
                        && details.Text.Contains(id.ToString()) && details.Text.Contains(first.Key + " R" + revision),
                        "配置页未展示实际会话、冻结修订及未知工程版本。");
                    current = null;
                    Refresh(page);
                    Require(!card.Text.Contains(id.ToString()) && !details.Text.Contains(id.ToString()),
                        "会话失效后配置页保留了旧会话身份。");
                    setter.Invoke(page, new object[] { new Func<TaskMonitorSession>(() => throw new InvalidOperationException("读取故障证据")) });
                    Refresh(page);
                    Require(card.Text.Contains("读取失败") && !card.Text.Contains(id.ToString()), "会话来源读取失败被当作有效会话。");
                    setter.Invoke(page, new object[] { new Func<TaskMonitorSession>(() => session) });
                    page.SetLanguage("en-US");
                    Refresh(page);
                    Require(card.Text.Contains(id.ToString()) && card.Text.Contains("project version not provided"), "英文页面丢失真实会话或未知工程版本。");
                }

                var view = new TaskMonitorView(session);
                var validityProvider = typeof(TaskMonitorView).GetMethod("SetSessionValidityProvider");
                Require(validityProvider != null, "RED: task monitor cannot invalidate session identity.");
                bool valid = true;
                validityProvider.Invoke(view, new object[] { new Func<bool>(() => valid) });
                view.HideSourcePath();
                var sourceText = (TextBlock)view.FindName("SourceText");
                Require(sourceText.Text.Contains(id.ToString()) && sourceText.Text.Contains("工程版本未提供"),
                    "任务运行页未使用同一发布会话身份。");
                int references = 0;
                view.ReferenceRequested += (sender, args) => references++;
                var referenceClick = typeof(TaskMonitorView).GetMethod("Reference_Click", BindingFlags.Instance | BindingFlags.NonPublic);
                var referenceButton = new Button { Tag = new TaskReferenceEventArgs("轴映射配置", "测试轴") };
                referenceClick.Invoke(view, new object[] { referenceButton, null });
                Require(references == 1, "有效会话的关联入口没有响应。");
                valid = false;
                referenceClick.Invoke(view, new object[] { referenceButton, null });
                Require(references == 1, "RED: stale session reference navigates after its validity changes.");
                view.Refresh();
                Require(!sourceText.Text.Contains(id.ToString()) && sourceText.Text.Contains("未确认"),
                    "失效会话仍被任务运行页标为当前来源。");
                // 先展示有效身份，再同时注入失效与快照故障，确认旧身份不会留在页面。
                valid = true;
                view.Refresh();
                var referencesPanel = (Panel)view.FindName("ReferencesPanel");
                var retainedReference = new Button { Content = "旧关联", Tag = new TaskReferenceEventArgs("轴映射配置", "测试轴") };
                referencesPanel.Children.Add(retainedReference);
                var deviceField = session.Machine.GetType().GetField("_device", BindingFlags.Instance | BindingFlags.NonPublic);
                object actualDevice = deviceField.GetValue(session.Machine);
                try
                {
                    deviceField.SetValue(session.Machine, null);
                    valid = false;
                    view.Refresh();
                    Require(!sourceText.Text.Contains(id.ToString()) && sourceText.Text.Contains("未确认"),
                        "RED: stale session identity survives a simultaneous snapshot failure.");
                    var detailsList = (ItemsControl)view.FindName("FlowDetails");
                    Require(detailsList.ItemsSource == null, "RED: stale definition details survive an invalid session snapshot failure.");
                    Require(!retainedReference.IsEnabled, "RED: stale reference remains visibly enabled after invalidation and snapshot failure.");
                }
                finally { deviceField.SetValue(session.Machine, actualDevice); }
                valid = true;
                view.Refresh();
                referenceClick.Invoke(view, new object[] { referenceButton, null });
                Require(sourceText.Text.Contains(id.ToString()) && references == 2, "会话确认恢复后未恢复关联入口。");
                var cacheType = typeof(TaskMonitorView).Assembly.GetType("MotionControl.TaskEngine.Wpf.Views.TaskProjectionDetailCache", true);
                var cache = Activator.CreateInstance(cacheType, true);
                var setMetadata = cacheType.GetMethod("SetSessionMetadata", BindingFlags.Instance | BindingFlags.NonPublic);
                Require(setMetadata != null, "任务引用详情未接入会话身份。");
                setMetadata.Invoke(cache, new[] { metadata });
                var read = cacheType.GetMethod("Read", BindingFlags.Instance | BindingFlags.NonPublic);
                var rows = (System.Collections.IEnumerable)read.Invoke(cache, new object[] { null, null, null, null, null, false });
                string values = string.Join("\n", rows.Cast<object>().Select(row => (string)row.GetType().GetProperty("Value").GetValue(row)));
                Require(values.Contains(id.ToString()) && !values.Contains("会话标识未提供"), "引用详情与已发布会话身份矛盾。");
                var setValidity = cacheType.GetMethod("SetSessionValidity", BindingFlags.Instance | BindingFlags.NonPublic);
                Require(setValidity != null, "引用详情未提供失效会话提示。");
                setValidity.Invoke(cache, new object[] { false });
                rows = (System.Collections.IEnumerable)read.Invoke(cache, new object[] { null, null, null, null, null, false });
                values = string.Join("\n", rows.Cast<object>().Select(row => (string)row.GetType().GetProperty("Value").GetValue(row)));
                Require(!values.Contains(id.ToString()) && values.Contains("未确认"), "失效会话的引用详情仍声称是当前身份。");
            }
            finally
            {
                session.Machine.CloseAsync().GetAwaiter().GetResult();
                if (second != null) second.Machine.CloseAsync().GetAwaiter().GetResult();
            }
        }

        /// <summary>让配置页同步读取当前会话，不执行设备操作。</summary>
        private static void Refresh(TaskConfigurationView page)
        {
            typeof(TaskConfigurationView).GetMethod("RefreshPermission", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(page, new object[] { null, EventArgs.Empty });
        }

        /// <summary>保留会话来源检查的具体失败原因。</summary>
        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
