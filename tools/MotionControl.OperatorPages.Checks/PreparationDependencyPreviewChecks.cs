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
            Action beforeSessionReply = null;
            var page = new EquipmentPreparationView(() => { loadCalls++; return Task.CompletedTask; }, null,
                (observedRuntime, session) => { beforeSessionReply?.Invoke(); return taskPublished; });
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
            Require(text.Text.Contains("[TaskBase]"), "新一轮没有恢复当前代次的准备范围。");
            taskSelection.Requires.Add("Missing");
            page.SetLanguage("en-US");
            Require(text.Text.Contains("unknown") && !text.Text.Contains("[Base]"), "依赖损坏时仍沿用旧范围。");
            Require(steps.Sum(step => step.ActionCalls) == executionCalls && taskBase.ActionCalls == 0
                && taskSelection.ActionCalls == 0 && loadCalls == 0 && card.Calls == 0,
                "只读预览执行了设备准备检查、装载或控制器调用。");
            runtime.ReleaseTaskEngineControl(taskOwner);
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
