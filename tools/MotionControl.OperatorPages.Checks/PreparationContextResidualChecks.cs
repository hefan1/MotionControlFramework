using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.Preparation;
using MotionControl.Framework.Runtime.Alarms;
using MotionControl.Framework.Runtime.Machine;
using MotionControl.Host.Wpf.Operation;

namespace MotionControl.OperatorPages.Checks
{
    /// <summary>使用真实准备页和无轴 Runtime 检查旧来源的页面错误；只验证离屏界面逻辑，不验证运动或正式 Host 屏幕。</summary>
    internal static class PreparationContextResidualChecks
    {
        /// <summary>分别执行来源保留和四种来源变化，汇总失败以便一次看到所有残留入口。</summary>
        internal static async Task RunAsync()
        {
            var failures = new List<Exception>();
            await RunCaseAsync("同一准备来源保留本次错误", SameContextRetainsErrorAsync, failures);
            await RunCaseAsync("同 Runtime 更换准备定义清除旧错误", ChangedPreparationClearsCompletedErrorAsync, failures);
            await RunCaseAsync("旧异步失败不能回写新 Runtime", LateFailureDoesNotEnterNewRuntimeAsync, failures);
            await RunCaseAsync("旧异步失败不能回写新连接", LateFailureDoesNotEnterNewConnectionAsync, failures);
            await RunCaseAsync("旧异步失败不能回写新准备定义", LateFailureDoesNotEnterNewPreparationAsync, failures);
            if (failures.Count > 0)
                throw new AggregateException("设备准备页面来源隔离检查失败；这是离屏 UI 逻辑检查。", failures);
        }

        /// <summary>单项失败不会掩盖其他来源变化的结果，仍以非零检查结果交给总入口。</summary>
        private static async Task RunCaseAsync(string name, Func<Task> check, List<Exception> failures)
        {
            try { await check(); }
            catch (Exception error) { failures.Add(new InvalidOperationException(name, error)); }
        }

        /// <summary>同一 Runtime、连接和准备定义的刷新不能把仍属于本次操作的诊断清掉。</summary>
        private static async Task SameContextRetainsErrorAsync()
        {
            const string errorMarker = "当前准备范围的配置读取失败";
            var card = new GuardCard { IsConnected = true };
            MachineRuntime runtime = await CreateRuntimeAsync(card, "当前工位准备");
            try
            {
                var page = CreatePage(runtime, () => Task.FromException(new InvalidOperationException(errorMarker)));
                ClickPrepareAll(page);
                await WaitForSubmissionCompletionAsync(page);
                AssertErrorContains(page, errorMarker, "本次配置读取错误没有显示在摘要及工程诊断中。");

                page.SetLanguage("en-US");
                page.SetLanguage("zh-CN");
                AssertErrorContains(page, errorMarker, "来源没有变化，普通刷新却清除了本次错误。");
                AssertStationName(page, "当前工位准备");
                Require(card.Calls == 0 && !runtime.IsPreparing, "页面错误检查意外连接控制器或进入设备准备。");
            }
            finally { await DisconnectWithoutDeviceCallsAsync(runtime, card); }
        }

        /// <summary>正式任务关闭和重新装载会在同一 Runtime 安装新准备定义，旧页面错误不能随同名工位保留。</summary>
        private static async Task ChangedPreparationClearsCompletedErrorAsync()
        {
            const string oldError = "上一准备会话的配置读取失败";
            var card = new GuardCard { IsConnected = true };
            MachineRuntime runtime = await CreateRuntimeAsync(card, "旧会话工位准备");
            try
            {
                var page = CreatePage(runtime, () => Task.FromException(new InvalidOperationException(oldError)));
                ClickPrepareAll(page);
                await WaitForSubmissionCompletionAsync(page);
                AssertErrorContains(page, oldError, "未建立需要隔离的旧页面错误。");

                // 使用 Runtime 的正式安装入口；两个准备项故意使用同一个工位 ID。
                runtime.ConfigurePreparations(new[] { new DisplayOnlyPreparation("新会话工位准备") });
                page.SetLanguage("zh-CN");
                AssertStationName(page, "新会话工位准备");
                AssertErrorDoesNotContain(page, oldError, "准备定义已经更新，仍把上一会话的错误显示为当前错误。");
                Require(card.Calls == 0 && !runtime.IsPreparing, "更换准备显示时意外触发了控制器或准备动作。");
            }
            finally { await DisconnectWithoutDeviceCallsAsync(runtime, card); }
        }

        /// <summary>等待任务配置期间页面换入另一个 Runtime，旧配置读取失败不能覆盖新设备的页面内容。</summary>
        private static async Task LateFailureDoesNotEnterNewRuntimeAsync()
        {
            const string oldError = "旧 Runtime 的迟到配置异常";
            var oldCard = new GuardCard { IsConnected = true };
            var newCard = new GuardCard { IsConnected = true };
            MachineRuntime oldRuntime = await CreateRuntimeAsync(oldCard, "旧 Runtime 工位");
            MachineRuntime newRuntime = await CreateRuntimeAsync(newCard, "新 Runtime 工位");
            var pendingRead = new TaskCompletionSource<bool>();
            try
            {
                var page = CreatePage(oldRuntime, () => pendingRead.Task);
                ClickPrepareAll(page);
                Require(!PrepareAllButton(page).IsEnabled, "未进入等待配置返回的真实页面命令。");
                page.SetRuntime(newRuntime);
                AssertStationName(page, "新 Runtime 工位");

                pendingRead.SetException(new InvalidOperationException(oldError));
                await WaitForSubmissionCompletionAsync(page);
                AssertStationName(page, "新 Runtime 工位");
                AssertErrorDoesNotContain(page, oldError, "旧命令的迟到异常污染了新 Runtime 的错误摘要或诊断。");
                Require(oldCard.Calls == 0 && newCard.Calls == 0 && !oldRuntime.IsPreparing && !newRuntime.IsPreparing,
                    "旧配置读取失败后错误执行了某一设备的准备。");
            }
            finally
            {
                pendingRead.TrySetCanceled();
                await DisconnectWithoutDeviceCallsAsync(oldRuntime, oldCard);
                await DisconnectWithoutDeviceCallsAsync(newRuntime, newCard);
            }
        }

        /// <summary>即使 Runtime 和工位 ID 相同，重连后的连接会话也不能接受上次异步读取的失败。</summary>
        private static async Task LateFailureDoesNotEnterNewConnectionAsync()
        {
            const string oldError = "旧连接的迟到配置异常";
            var card = new GuardCard { IsConnected = true };
            MachineRuntime runtime = await CreateRuntimeAsync(card, "连接内工位准备");
            var pendingRead = new TaskCompletionSource<bool>();
            try
            {
                var page = CreatePage(runtime, () => pendingRead.Task);
                ClickPrepareAll(page);
                Require(!PrepareAllButton(page).IsEnabled, "未进入等待配置返回的真实页面命令。");
                Guid? oldSession = runtime.ConnectionSessionId;

                // 复用现有无轴设施切换内存连接，GuardCard 的连接和断开方法均禁止被调用。
                await DisconnectWithoutDeviceCallsAsync(runtime, card);
                card.IsConnected = true;
                await runtime.ConnectAsync(CancellationToken.None);
                Require(runtime.ConnectionSessionId.HasValue && runtime.ConnectionSessionId != oldSession,
                    "测试未建立不同的真实 Runtime 连接会话。");
                page.SetLanguage("zh-CN");

                pendingRead.SetException(new InvalidOperationException(oldError));
                await WaitForSubmissionCompletionAsync(page);
                AssertStationName(page, "连接内工位准备");
                AssertErrorDoesNotContain(page, oldError, "旧连接的异步失败被显示到了新连接页面。");
                Require(card.Calls == 0 && !runtime.IsPreparing, "重连来源检查意外执行了控制器调用或准备动作。");
            }
            finally
            {
                pendingRead.TrySetCanceled();
                await DisconnectWithoutDeviceCallsAsync(runtime, card);
            }
        }

        /// <summary>异步配置读取未完成时安装同名新准备项，旧异常不能冒充新准备范围的错误。</summary>
        private static async Task LateFailureDoesNotEnterNewPreparationAsync()
        {
            const string oldError = "旧准备定义的迟到配置异常";
            var card = new GuardCard { IsConnected = true };
            MachineRuntime runtime = await CreateRuntimeAsync(card, "旧定义工位准备");
            var pendingRead = new TaskCompletionSource<bool>();
            try
            {
                var page = CreatePage(runtime, () => pendingRead.Task);
                ClickPrepareAll(page);
                Require(!PrepareAllButton(page).IsEnabled, "未进入等待配置返回的真实页面命令。");
                runtime.ConfigurePreparations(new[] { new DisplayOnlyPreparation("新定义工位准备") });
                page.SetLanguage("zh-CN");

                pendingRead.SetException(new InvalidOperationException(oldError));
                await WaitForSubmissionCompletionAsync(page);
                AssertStationName(page, "新定义工位准备");
                AssertErrorDoesNotContain(page, oldError, "旧准备定义的迟到异常污染了新定义的错误摘要或诊断。");
                Require(card.Calls == 0 && !runtime.IsPreparing, "配置读取失败后仍执行了新旧准备定义。");
            }
            finally
            {
                pendingRead.TrySetCanceled();
                await DisconnectWithoutDeviceCallsAsync(runtime, card);
            }
        }

        /// <summary>建立真实 Runtime 的连接会话和准备列表；检查卡没有轴，也不访问驱动生命周期。</summary>
        private static async Task<MachineRuntime> CreateRuntimeAsync(GuardCard card, string stationName)
        {
            var runtime = new MachineRuntime(new MotionCardGroup(new[] { card }), Array.Empty<IStationFlow>(),
                new MachineAlarmService(), new GuardSafety(), controlOwner: MachineControlOwner.StationFlows);
            runtime.ConfigurePreparations(new[] { new DisplayOnlyPreparation(stationName) });
            await runtime.ConnectAsync(CancellationToken.None);
            return runtime;
        }

        /// <summary>只在已有配置读取委托处注入失败或延迟，页面及 Runtime 逻辑均使用正式实现。</summary>
        private static EquipmentPreparationView CreatePage(MachineRuntime runtime, Func<Task> ensureConfiguration)
        {
            var page = new EquipmentPreparationView(ensureConfiguration, null);
            page.SetRuntime(runtime);
            return page;
        }

        /// <summary>通过真实按钮触发命令，确认测试没有靠调用禁用按钮进入生产中不可用的分支。</summary>
        private static void ClickPrepareAll(EquipmentPreparationView page)
        {
            Button button = PrepareAllButton(page);
            Require(button.IsEnabled, "测试前设备准备按钮未开放，不能按本场景提交。");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }

        /// <summary>等页面命令解除忙状态；有界异步等待由既有 WPF 调度器入口泵送。</summary>
        private static async Task WaitForSubmissionCompletionAsync(EquipmentPreparationView page)
        {
            for (int attempt = 0; attempt < 200; attempt++)
            {
                if (PrepareAllButton(page).IsEnabled) return;
                await Task.Delay(10);
            }
            throw new InvalidOperationException("设备准备页面命令没有在检查时限内完成。");
        }

        /// <summary>读取用户可见的全部准备按钮，不访问页面私有忙状态。</summary>
        private static Button PrepareAllButton(EquipmentPreparationView page)
        { return (Button)page.FindName("PrepareAllButton"); }

        /// <summary>核对表格绑定的真实工位名称，证明检查结束时仍显示本次来源。</summary>
        private static void AssertStationName(EquipmentPreparationView page, string expectedName)
        {
            var grid = (DataGrid)page.FindName("StationsGrid");
            object row = grid.Items.Cast<object>().Single();
            string actualName = row.GetType().GetProperty("Name").GetValue(row) as string;
            Require(actualName == expectedName, "准备页未显示当前来源工位：" + expectedName + "；实际：" + actualName);
        }

        /// <summary>有效错误必须同时保留操作员摘要和维护诊断，不能只保存隐藏字段。</summary>
        private static void AssertErrorContains(EquipmentPreparationView page, string marker, string message)
        {
            Require(((TextBlock)page.FindName("ErrorText")).Text.Contains(marker)
                && ((TextBox)page.FindName("DiagnosticText")).Text.Contains(marker), message);
        }

        /// <summary>允许页面提示重新选择当前范围，但旧来源的原始失败不能作为当前摘要或诊断显示。</summary>
        private static void AssertErrorDoesNotContain(EquipmentPreparationView page, string marker, string message)
        {
            string summary = ((TextBlock)page.FindName("ErrorText")).Text;
            string diagnostic = ((TextBox)page.FindName("DiagnosticText")).Text;
            Require(!summary.Contains(marker) && !diagnostic.Contains(marker),
                message + " 当前摘要：" + summary + "；当前诊断：" + diagnostic);
        }

        /// <summary>结束检查的内存连接；先标明检查卡离线，禁止测试清理调用控制器方法。</summary>
        private static async Task DisconnectWithoutDeviceCallsAsync(MachineRuntime runtime, GuardCard card)
        {
            card.IsConnected = false;
            if (runtime.State != MachineRunState.Disconnected)
                await runtime.DisconnectAsync(CancellationToken.None);
        }

        /// <summary>将违反页面来源契约的行为交给检查入口报告，不吞掉错误。</summary>
        private static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }

        /// <summary>仅提供工位身份建立正式准备快照，任何执行或设备条件读取都属于测试错误。</summary>
        private sealed class DisplayOnlyPreparation : IStationPreparation
        {
            /// <summary>同名替换场景始终使用相同 ID，防止仅按名称清理的实现误通过。</summary>
            public string StationId { get { return "StationA"; } }
            /// <summary>不同来源的可见名称，用来确认表格已切换到新准备定义。</summary>
            public string StationName { get; }
            /// <summary>本检查没有依赖工位，也不编排实际回零。</summary>
            public IReadOnlyCollection<string> Dependencies { get { return Array.Empty<string>(); } }

            /// <summary>为真实 Runtime 的准备列表建立一个只用于显示的工位。</summary>
            internal DisplayOnlyPreparation(string stationName) { StationName = stationName; }
            /// <summary>配置读取失败后不应再进入设备前置检查。</summary>
            public Task ValidateStartAsync(CancellationToken cancellationToken)
            { throw new InvalidOperationException("页面来源检查不得执行准备前置条件。"); }
            /// <summary>本组回归没有运动和 IO，意外执行必须立即失败。</summary>
            public Task ExecuteAsync(IProgress<StationPreparationProgress> progress, CancellationToken cancellationToken)
            { throw new InvalidOperationException("页面来源检查不得执行工位准备。"); }
            /// <summary>显示来源核对不应创建准备完成结论。</summary>
            public Task ValidateReadyAsync(CancellationToken cancellationToken)
            { throw new InvalidOperationException("页面来源检查不得确认设备已准备。"); }
        }
    }
}
