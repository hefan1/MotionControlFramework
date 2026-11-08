using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.Preparation;
using MotionControl.Framework.Contracts.Safety;
using MotionControl.Framework.Runtime.Alarms;
using MotionControl.Framework.Runtime.Machine;
using MotionControl.Host.Wpf.Operation;

namespace MotionControl.OperatorPages.Checks
{
    /// <summary>以隔离准备项核对依赖拓扑、验收状态和任务定义未发布的显示边界。</summary>
    internal static class PreparationDependencyPreviewChecks
    {
        internal static void Run()
        {
            int loadCalls = 0;
            bool taskPublished = false;
            bool sessionReadFails = false;
            Action beforeSessionReply = null;
            var page = new EquipmentPreparationView(() => { loadCalls++; return Task.CompletedTask; }, null,
                (observedRuntime, session) => { beforeSessionReply?.Invoke(); if (sessionReadFails) throw new InvalidOperationException("准备会话检查故障"); return taskPublished; });
            var basis = new Step("Base");
            var middle = new Step("Middle", "Base");
            var last = new Step("Selected", "Middle", "Base");
            var other = new Step("Other");
            var steps = new[] { last, other, middle, basis };
            var card = new GuardCard { IsConnected = true };
            var runtime = new MachineRuntime(new MotionCardGroup(new[] { card }), Array.Empty<IStationFlow>(),
                new MachineAlarmService(), new SafeInput(), controlOwner: MachineControlOwner.StationFlows);
            runtime.ConfigurePreparations(steps);
            runtime.ConnectAsync(CancellationToken.None).GetAwaiter().GetResult();
            // 通过真实 Runtime 完成一个无轴、无 IO 的测试前置项，此后的页面操作只允许观察。
            runtime.PrepareAsync("Base", CancellationToken.None).GetAwaiter().GetResult();
            int executionCalls = steps.Sum(step => step.ActionCalls);
            page.SetRuntime(runtime);
            var grid = (DataGrid)page.FindName("StationsGrid");
            var text = (TextBlock)page.FindName("DependencyPreviewText");
            grid.SelectedItem = grid.Items.Cast<object>().Single(row =>
                Equals(row.GetType().GetProperty("Id").GetValue(row), "Selected"));
            Require(text.Text.Contains("1. Base [Base] · 已准备，仅复核")
                && text.Text.Contains("2. Middle [Middle] · 需执行") && text.Text.Contains("3. Selected [Selected] · 需执行")
                && !text.Text.Contains("Other"), "所选准备范围、排序或已完成前置项的处理说明不正确。");
            Require(!runtime.ReadPreparationSelection("Missing", out long projectionVersion).Any()
                && projectionVersion == runtime.PreparationConfigurationVersion, "缺少工位时未返回当前配置的空投影。");
            page.SetLanguage("en-US");
            Require(text.Text.Contains("Prepared; recheck only") && text.Text.Contains("3. Selected"), "英文准备详情未同步更新。");
            var taskOwner = new object();
            runtime.AcquireTaskEngineControlAsync(taskOwner, CancellationToken.None).GetAwaiter().GetResult();
            page.SetLanguage("en-US");
            Require(text.Text.Contains("No task preparation session") && !text.Text.Contains("[Base]"), "未发布的任务会话显示了设备准备定义。");
            Require(grid.Items.Count == 0 && ((TextBlock)page.FindName("SummaryText")).Text.Contains("unconfirmed")
                && !((Button)page.FindName("PrepareSelectedButton")).IsEnabled
                && ((Button)page.FindName("PrepareAllButton")).IsEnabled,
                "任务准备会话未发布时显示了其他入口的工位，或阻断了按需读取后执行全部回零的入口。");
            var taskBase = new Step("TaskBase");
            var taskSelection = new Step("TaskSelect", "TaskBase");
            runtime.ConfigurePreparations(new[] { taskSelection, taskBase });
            taskPublished = true;
            page.SetLanguage("en-US");
            grid.SelectedItem = grid.Items.Cast<object>().Single(row =>
                Equals(row.GetType().GetProperty("Id").GetValue(row), "TaskSelect"));
            Require(text.Text.Contains("Task session preparation order") && text.Text.Contains("[TaskBase]")
                && !text.Text.Contains("[Base]"), "已发布的任务来源没有区分。");
            // 同一连接和 owner，在读取投影后的发布会话核对中短暂换定义，再装回同一批对象。
            long safetyVersion = runtime.SafetyObservationVersion;
            long preparationVersion = runtime.PreparationConfigurationVersion;
            int replies = 0;
            beforeSessionReply = () =>
            {
                if (++replies != 2) return;
                runtime.ConfigurePreparations(new[] { new Step("Replacement") });
                runtime.ConfigurePreparations(new[] { taskSelection, taskBase });
            };
            page.SetLanguage("en-US");
            Require(runtime.PreparationConfigurationVersion != preparationVersion && runtime.SafetyObservationVersion == safetyVersion
                && text.Text.Contains("unknown") && !text.Text.Contains("[TaskBase]"), "准备配置往返后仍发布旧依赖范围。");
            beforeSessionReply = null;
            page.SetLanguage("en-US");
            Require(grid.SelectedItem == null, "准备配置代次改变后沿用了旧工位选择。");
            grid.SelectedItem = grid.Items.Cast<object>().Single(row =>
                Equals(row.GetType().GetProperty("Id").GetValue(row), "TaskSelect"));
            Require(text.Text.Contains("[TaskBase]"), "新一轮没有恢复当前代次的准备范围。");
            taskSelection.Requires.Add("Missing");
            page.SetLanguage("en-US");
            Require(text.Text.Contains("unknown") && !text.Text.Contains("[Base]"), "依赖损坏时仍沿用旧范围。");
            sessionReadFails = true;
            page.SetLanguage("en-US");
            Require(grid.Items.Count == 0 && grid.SelectedItem == null
                && !((Button)page.FindName("PrepareSelectedButton")).IsEnabled
                && !((Button)page.FindName("PrepareAllButton")).IsEnabled
                && ((TextBox)page.FindName("DiagnosticText")).Text.Contains("准备会话检查故障"),
                "准备会话读取异常后没有保留诊断，或仍展示工位/允许回零。");
            Require(steps.Sum(step => step.ActionCalls) == executionCalls && taskBase.ActionCalls == 0
                && taskSelection.ActionCalls == 0 && loadCalls == 0 && card.Calls == 0,
                "只读预览执行了设备准备检查、装载或控制器调用。");
            runtime.ReleaseTaskEngineControl(taskOwner);
        }

        /// <summary>提交期间同名工位换定义时必须拒绝旧选择，且全部准备仍可按需读取。</summary>
        internal static async Task RunSubmissionAsync()
        {
            var card = new GuardCard { IsConnected = true };
            var runtime = new MachineRuntime(new MotionCardGroup(new[] { card }), Array.Empty<IStationFlow>(),
                new MachineAlarmService(), new SafeInput(), controlOwner: MachineControlOwner.StationFlows);
            var original = new Step("SameId");
            var replacement = new Step("SameId");
            runtime.ConfigurePreparations(new[] { original });
            await runtime.ConnectAsync(CancellationToken.None);
            var release = new TaskCompletionSource<bool>();
            bool loading = false;
            var page = new EquipmentPreparationView(async () => { loading = true; await release.Task; }, null);
            page.SetRuntime(runtime);
            var grid = (DataGrid)page.FindName("StationsGrid");
            grid.SelectedItem = grid.Items.Cast<object>().Single();
            ((Button)page.FindName("PrepareSelectedButton")).RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
            Require(loading, "所选准备没有进入预期的异步提交阶段。");
            runtime.ConfigurePreparations(new[] { replacement });
            release.SetResult(true);
            var errorText = (TextBlock)page.FindName("ErrorText");
            for (int i = 0; i < 200 && string.IsNullOrWhiteSpace(errorText.Text); i++) await Task.Delay(10);
            Require(!string.IsNullOrWhiteSpace(errorText.Text) && original.ActionCalls == 0 && replacement.ActionCalls == 0
                && grid.SelectedItem == null && card.Calls == 0, "同 ID 的准备定义更换后仍执行了旧选择。");

            var releaseAll = new TaskCompletionSource<bool>();
            bool loadingAll = false;
            var allPage = new EquipmentPreparationView(async () => { loadingAll = true; await releaseAll.Task; }, null);
            allPage.SetRuntime(runtime);
            ((Button)allPage.FindName("PrepareAllButton")).RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
            Require(loadingAll, "全部准备没有进入预期的异步提交阶段。");
            var replacementAll = new Step("SameId");
            runtime.ConfigurePreparations(new[] { replacementAll });
            releaseAll.SetResult(true);
            var allError = (TextBlock)allPage.FindName("ErrorText");
            for (int i = 0; i < 200 && string.IsNullOrWhiteSpace(allError.Text); i++) await Task.Delay(10);
            Require(!string.IsNullOrWhiteSpace(allError.Text) && replacement.ActionCalls == 0 && replacementAll.ActionCalls == 0,
                "全部准备范围变化后执行了操作员未确认的新定义。");

            var changedRuntime = new MachineRuntime(new MotionCardGroup(new[] { new GuardCard { IsConnected = true } }),
                Array.Empty<IStationFlow>(), new MachineAlarmService(), new SafeInput(), controlOwner: MachineControlOwner.StationFlows);
            var oldOwnerStep = new Step("SameId");
            var newOwnerStep = new Step("SameId");
            changedRuntime.ConfigurePreparations(new[] { oldOwnerStep });
            await changedRuntime.ConnectAsync(CancellationToken.None);
            var changedPage = new EquipmentPreparationView(null, null, (observed, session) => true);
            changedPage.SetRuntime(changedRuntime);
            var newOwner = new object();
            await changedRuntime.AcquireTaskEngineControlAsync(newOwner, CancellationToken.None);
            changedRuntime.ConfigurePreparations(new[] { newOwnerStep });
            ((Button)changedPage.FindName("PrepareAllButton")).RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
            var changedError = (TextBlock)changedPage.FindName("ErrorText");
            for (int i = 0; i < 200 && string.IsNullOrWhiteSpace(changedError.Text); i++) await Task.Delay(10);
            Require(!string.IsNullOrWhiteSpace(changedError.Text) && oldOwnerStep.ActionCalls == 0 && newOwnerStep.ActionCalls == 0,
                "入口变化后把仍显示的旧范围当成未读取，执行了未确认的新入口准备。");
            changedRuntime.ReleaseTaskEngineControl(newOwner);

            long staleVersion = runtime.PreparationConfigurationVersion;
            long safetyVersion = runtime.SafetyObservationVersion;
            runtime.ConfigurePreparations(new[] { new Step("SameId") });
            try
            {
                await runtime.PrepareAsync("SameId", runtime.ConnectionSessionId, staleVersion,
                    runtime.ControlOwner, safetyVersion, CancellationToken.None);
                throw new InvalidOperationException("准备配置变化后命令锁没有拒绝旧代次。");
            }
            catch (InvalidOperationException error)
            {
                Require(error.Message.Contains("范围或会话已变化"), "命令锁未报告准备范围变化：" + error.Message);
            }

            var taskRuntime = new MachineRuntime(new MotionCardGroup(new[] { new GuardCard { IsConnected = true } }),
                Array.Empty<IStationFlow>(), new MachineAlarmService(), new SafeInput(), controlOwner: MachineControlOwner.TaskEngine);
            await taskRuntime.ConnectAsync(CancellationToken.None);
            var taskStep = new Step("TaskStation");
            bool published = false;
            var taskPage = new EquipmentPreparationView(() =>
            {
                taskRuntime.ConfigurePreparations(new[] { taskStep });
                published = true;
                return Task.CompletedTask;
            }, null, (observed, session) => published);
            taskPage.SetRuntime(taskRuntime);
            ((Button)taskPage.FindName("PrepareAllButton")).RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
            for (int i = 0; i < 200 && (taskStep.ActionCalls < 2 || !taskRuntime.IsPrepared); i++) await Task.Delay(10);
            Require(published && taskStep.ActionCalls > 0 && taskRuntime.IsPrepared,
                "任务准备未发布时无法按需装载并执行全部准备。");
        }

        private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

        private sealed class SafeInput : IMachineSafetyStatus
        {
            public Task<bool> ReadEmergencyStopActiveAsync(CancellationToken token) { return Task.FromResult(false); }
        }

        /// <summary>测试准备项无设备动作；统计正式准备调用以辨别只读页面的额外调用。</summary>
        private sealed class Step : IStationPreparation
        {
            public string StationId { get; }
            public string StationName => StationId;
            internal readonly List<string> Requires;
            internal int ActionCalls;
            public IReadOnlyCollection<string> Dependencies => Requires;
            internal Step(string id, params string[] dependencies) { StationId = id; Requires = dependencies.ToList(); }
            public Task ValidateStartAsync(CancellationToken token) { ActionCalls++; return Task.CompletedTask; }
            public Task ExecuteAsync(IProgress<StationPreparationProgress> progress, CancellationToken token)
            { ActionCalls++; return Task.CompletedTask; }
            public Task ValidateReadyAsync(CancellationToken token) { ActionCalls++; return Task.CompletedTask; }
        }
    }
}
