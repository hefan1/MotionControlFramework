using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.Preparation;
using MotionControl.Framework.Contracts.Safety;
using MotionControl.Framework.Runtime.Alarms;
using MotionControl.Framework.Runtime.Machine;
using MotionControl.Host.Wpf.Operation;

namespace MotionControl.OperatorPages.Checks
{
    /// <summary>使用真实准备页和无轴 Runtime 检查旧来源的页面错误；只验证离屏界面逻辑，不验证运动或正式 Host 屏幕。</summary>
    internal static class PreparationContextResidualChecks
    {
        /// <summary>分别执行来源保留、来源变化、错误发布交错和真实停止收尾，汇总失败以便看到所有残留入口。</summary>
        internal static async Task RunAsync()
        {
            var failures = new List<Exception>();
            await RunCaseAsync("同一准备来源保留本次错误", SameContextRetainsErrorAsync, failures);
            await RunCaseAsync("同 Runtime 更换准备定义清除旧错误", ChangedPreparationClearsCompletedErrorAsync, failures);
            await RunCaseAsync("旧异步失败不能回写新 Runtime", LateFailureDoesNotEnterNewRuntimeAsync, failures);
            await RunCaseAsync("旧异步失败不能回写新连接", LateFailureDoesNotEnterNewConnectionAsync, failures);
            await RunCaseAsync("旧异步失败不能回写新准备定义", LateFailureDoesNotEnterNewPreparationAsync, failures);
            await RunCaseAsync("错误发布期间换准备定义不能给旧错误盖新来源", ChangedPreparationWhilePublishingErrorAsync, failures);
            await RunCaseAsync("旧停止异常不能回写新 Runtime", LateStopFailureDoesNotEnterNewRuntimeAsync, failures);
            if (failures.Count > 0)
                throw new AggregateException("设备准备页面来源隔离检查失败；这是离屏 UI 逻辑检查。", failures);
        }

        /// <summary>单项失败不会掩盖其他来源变化的结果，仍以非零检查结果交给总入口。</summary>
        private static async Task RunCaseAsync(string name, Func<Task> check, List<Exception> failures)
        {
            Console.WriteLine("[准备来源开始] " + name);
            try { await check(); Console.WriteLine("[准备来源通过] " + name); }
            catch (Exception error)
            {
                Console.Error.WriteLine("[准备来源失败] " + name + "\n" + error);
                failures.Add(new InvalidOperationException(name, error));
            }
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

        /// <summary>同源校验通过后才更换定义，错误归属仍须使用提交来源，不能重新读取并贴上新版本。</summary>
        private static async Task ChangedPreparationWhilePublishingErrorAsync()
        {
            const string oldError = "发布错误摘要期间才过期的旧准备异常";
            var card = new GuardCard { IsConnected = true };
            MachineRuntime runtime = await CreateRuntimeAsync(card, "发布前准备工位");
            try
            {
                var error = new PreparationChangesWhileReadingMessageException(runtime, oldError, "发布后新准备工位");
                var page = CreatePage(runtime, () => Task.FromException(error));
                ClickPrepareAll(page);
                await WaitForSubmissionCompletionAsync(page);
                AssertStationName(page, "发布后新准备工位");
                AssertErrorDoesNotContain(page, oldError, "旧错误在发布摘要期间过期，却被标记成了新准备定义的错误。");
                Require(card.Calls == 0 && !runtime.IsPreparing, "错误发布交错检查意外执行了控制器或准备动作。");
            }
            finally { await DisconnectWithoutDeviceCallsAsync(runtime, card); }
        }

        /// <summary>用真实无轴准备等待停止，再返回旧失败；页面换源不能把旧停止错误显示到新设备。</summary>
        private static async Task LateStopFailureDoesNotEnterNewRuntimeAsync()
        {
            const string oldError = "旧设备准备停止期间的步骤异常";
            var oldCard = new GuardCard { IsConnected = true };
            var newCard = new GuardCard { IsConnected = true };
            var preparation = new PendingPreparation();
            var oldRuntime = new MachineRuntime(new MotionCardGroup(new[] { oldCard }), Array.Empty<IStationFlow>(),
                new MachineAlarmService(), new ReadableSafetyInput(), pauseTimeout: TimeSpan.FromSeconds(5),
                controlOwner: MachineControlOwner.StationFlows);
            oldRuntime.ConfigurePreparations(new[] { preparation });
            await RunRuntimeCommandAsync(oldRuntime.ConnectAsync, "旧无轴 Runtime 未能及时建立连接。");
            MachineRuntime newRuntime = await CreateRuntimeAsync(newCard, "新设备准备工位");
            Task preparationTask = null;
            Task stopHandlerCompletion = null;
            var failures = new List<Exception>();
            try
            {
                // 先从真实 Runtime 启动无轴准备；页面只执行停止，避免混入页面 Prepare 的异常回写。
                Console.WriteLine("[停止来源阶段] 启动真实无轴准备，等待进入步骤。");
                preparationTask = Task.Run(() => oldRuntime.PrepareAsync(preparation.StationId, CancellationToken.None));
                await WaitForTaskAsync(preparation.Entered, "真实准备没有进入受控等待。");
                var page = CreatePage(oldRuntime, null);
                var stopButton = (Button)page.FindName("StopPreparationButton");
                Require(stopButton.IsEnabled, "真实准备运行时停止按钮没有开放。");
                stopHandlerCompletion = TrackAsyncVoidCompletion(() =>
                    stopButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
                await WaitForTaskAsync(preparation.StopObserved, "真实停止未请求原准备令牌取消。");
                Require(!preparationTask.IsCompleted, "旧准备未保持等待，不能检查迟到停止失败。");

                Console.WriteLine("[停止来源阶段] 已观察到真实停止取消，换入新 Runtime 后释放旧异常。");
                page.SetRuntime(newRuntime);
                AssertStationName(page, "新设备准备工位");
                preparation.Fail(oldError);
                await RequirePreparationFailureAsync(preparationTask, oldError);
                Console.WriteLine("[停止来源阶段] 旧准备任务已失败，等待页面停止事件返回。");
                await WaitForTaskAsync(stopHandlerCompletion, "停止按钮的异步处理未结束。");

                Console.WriteLine("[停止来源阶段] 页面停止事件已结束，核对新页面与旧 Runtime 证据。");
                AssertStationName(page, "新设备准备工位");
                AssertErrorDoesNotContain(page, oldError, "旧停止操作的迟到异常污染了新设备页面。");
                Require(oldRuntime.LastError != null && oldRuntime.LastError.ToString().Contains(oldError)
                    && oldRuntime.AlarmService.HasLatchedStopAlarm,
                    "隔离页面错误时丢失了旧 Runtime 的停止异常或报警证据。");
                Require(oldCard.Calls == 0 && newCard.Calls == 0 && !newRuntime.IsPreparing,
                    "无轴停止异常检查意外访问控制器或启动新设备准备。");
            }
            catch (Exception error) { failures.Add(error); }
            finally
            {
                // 无论断言是否失败都解除步骤屏障，并完整观察准备与页面停止的终态。
                Console.WriteLine("[停止来源阶段] 开始有界清理，原断言失败仍保留。");
                preparation.Fail(oldError);
                try
                {
                    bool settled = true;
                    if (preparationTask != null)
                        settled &= await RecordCleanupFailureAsync(
                            () => RequirePreparationFailureAsync(preparationTask, oldError), failures);
                    if (stopHandlerCompletion != null)
                        settled &= await RecordCleanupFailureAsync(
                            () => WaitForTaskAsync(stopHandlerCompletion, "测试清理时停止页面处理仍未结束。"), failures);
                    // Runtime 首次失败会保留停止待确认；再次走正式停止只核查收尾，不清报警或恢复证据。
                    if (settled)
                    {
                        Console.WriteLine("[停止来源阶段] 原准备和页面事件均已结束，正式重试停止收尾。");
                        settled = await RecordCleanupFailureAsync(() => RunRuntimeCommandAsync(
                            oldRuntime.StopPreparationAsync, "旧准备停止重试未完成。"), failures);
                    }
                    if (settled)
                    {
                        Console.WriteLine("[停止来源阶段] 原准备停止收尾已结束，断开旧内存连接。");
                        await RecordCleanupFailureAsync(() => DisconnectWithoutDeviceCallsAsync(oldRuntime, oldCard), failures);
                    }
                    else Console.Error.WriteLine("[停止来源清理失败] 旧操作仍未确认结束；不再叠加断开命令，整项检查必须失败。");
                }
                finally
                {
                    Console.WriteLine("[停止来源阶段] 断开新内存连接。");
                    await RecordCleanupFailureAsync(() => DisconnectWithoutDeviceCallsAsync(newRuntime, newCard), failures);
                }
            }
            Console.WriteLine("[停止来源阶段] 有界清理结束。");
            if (failures.Count > 0) throw new AggregateException("停止来源检查或清理失败；保留全部原始异常。", failures);
        }

        /// <summary>保留清理失败而不覆盖原断言，并向调用者说明是否已确认该清理步骤结束。</summary>
        private static async Task<bool> RecordCleanupFailureAsync(Func<Task> cleanup, List<Exception> failures)
        {
            try { await cleanup(); return true; }
            catch (Exception error)
            {
                Console.Error.WriteLine("[准备来源清理失败] " + error);
                failures.Add(error);
                return false;
            }
        }

        /// <summary>Runtime 命令没有 WPF 线程要求；在线程池调用并限时，避免同步清理阶段阻塞调度器。</summary>
        private static async Task RunRuntimeCommandAsync(Func<CancellationToken, Task> command, string timeoutMessage)
        {
            Task pending = Task.Run(async () =>
            {
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                    await command(timeout.Token).ConfigureAwait(false);
            });
            await WaitForTaskAsync(pending, timeoutMessage);
        }

        /// <summary>有界等待实际任务，不用固定延时猜测 async void 页面处理已经结束。</summary>
        private static async Task WaitForTaskAsync(Task task, string timeoutMessage)
        {
            if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5))) != task)
            {
                // 超时只报告未结束；后台以后返回的异常仍输出，不能悄悄丢失诊断。
                _ = task.ContinueWith(failed => Console.Error.WriteLine("[准备来源迟到异常] " + failed.Exception),
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                throw new TimeoutException(timeoutMessage);
            }
            await task;
        }

        /// <summary>准备失败必须携带独立设置的原始标记，其他异常或意外成功都不能算本场景通过。</summary>
        private static async Task RequirePreparationFailureAsync(Task task, string marker)
        {
            try { await WaitForTaskAsync(task, "无轴准备未返回，测试不能安全结束。"); }
            catch (Exception error)
            {
                if (error.ToString().Contains(marker)) return;
                throw;
            }
            throw new InvalidOperationException("应失败的无轴准备意外成功，未覆盖旧停止异常。");
        }

        /// <summary>保留原 WPF 调度行为，只观察被触发 async void 方法的完成通知。</summary>
        private static Task TrackAsyncVoidCompletion(Action action)
        {
            SynchronizationContext previous = SynchronizationContext.Current;
            if (previous == null) throw new InvalidOperationException("停止页面检查需要已有 WPF 调度上下文。");
            var tracking = new AsyncVoidCompletionContext(previous);
            SynchronizationContext.SetSynchronizationContext(tracking);
            try { action(); }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
            return tracking.Completion;
        }

        /// <summary>建立真实 Runtime 的连接会话和准备列表；检查卡没有轴，也不访问驱动生命周期。</summary>
        private static async Task<MachineRuntime> CreateRuntimeAsync(GuardCard card, string stationName)
        {
            var runtime = new MachineRuntime(new MotionCardGroup(new[] { card }), Array.Empty<IStationFlow>(),
                new MachineAlarmService(), new GuardSafety(), controlOwner: MachineControlOwner.StationFlows);
            runtime.ConfigurePreparations(new[] { new DisplayOnlyPreparation(stationName) });
            await RunRuntimeCommandAsync(runtime.ConnectAsync, "无轴 Runtime 未能及时建立连接。");
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
            await RunRuntimeCommandAsync(token => runtime.State == MachineRunState.Disconnected
                ? Task.CompletedTask : runtime.DisconnectAsync(token), "内存连接清理未结束，检查不能声称安全收尾。");
        }

        /// <summary>将违反页面来源契约的行为交给检查入口报告，不吞掉错误。</summary>
        private static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }

        /// <summary>只在第一次读取摘要时切换真实准备定义；诊断输出不会提前触发这一交错。</summary>
        private sealed class PreparationChangesWhileReadingMessageException : Exception
        {
            /// <summary>通过正式配置入口更换准备定义的 Runtime。</summary>
            private readonly MachineRuntime _runtime;
            /// <summary>不能进入新页面的独立旧错误标记。</summary>
            private readonly string _marker;
            /// <summary>用于确认页面已切换的新工位名称。</summary>
            private readonly string _stationName;
            /// <summary>确保本场景只更换一次准备版本。</summary>
            private bool _changed;
            /// <summary>记录真实 Runtime 和本场景的可见标记。</summary>
            internal PreparationChangesWhileReadingMessageException(MachineRuntime runtime, string marker, string stationName)
            { _runtime = runtime; _marker = marker; _stationName = stationName; }
            /// <summary>模拟同源判定之后、错误发布之前到达的新准备定义。</summary>
            public override string Message
            {
                get
                {
                    if (!_changed)
                    {
                        _changed = true;
                        _runtime.ConfigurePreparations(new[] { new DisplayOnlyPreparation(_stationName) });
                    }
                    return _marker;
                }
            }
            /// <summary>原始诊断只输出标记，不调用 Message，以便精确定位发布摘要时的交错。</summary>
            public override string ToString() { return _marker; }
        }

        /// <summary>固定可读的离线安全输入，仅供真实无轴准备路径通过前置读取，不声明现场安全验证。</summary>
        private sealed class ReadableSafetyInput : IMachineSafetyStatus
        {
            /// <summary>返回本例明确布置的非急停状态，不访问 IO 或硬件。</summary>
            public Task<bool> ReadEmergencyStopActiveAsync(CancellationToken cancellationToken)
            { return Task.FromResult(false); }
        }

        /// <summary>只在内存中等待释放的准备项，用实际取消令牌确认停止已进入等待收尾阶段。</summary>
        private sealed class PendingPreparation : IStationPreparation
        {
            /// <summary>通知测试真实准备已经执行到等待点。</summary>
            private readonly TaskCompletionSource<bool> _entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>通知测试 Runtime 已为本次准备请求停止。</summary>
            private readonly TaskCompletionSource<bool> _stopObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>由测试释放并返回原始异常的等待点，不包含轴或 IO 操作。</summary>
            private readonly TaskCompletionSource<bool> _completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>本次准备的固定身份。</summary>
            public string StationId { get { return "PendingStopStation"; } }
            /// <summary>旧页面明确显示的工位名称。</summary>
            public string StationName { get { return "旧设备停止等待工位"; } }
            /// <summary>无轴逻辑检查没有依赖工位。</summary>
            public IReadOnlyCollection<string> Dependencies { get { return Array.Empty<string>(); } }
            /// <summary>公开已进入步骤的同步屏障，避免用延时猜测流程位置。</summary>
            internal Task Entered { get { return _entered.Task; } }
            /// <summary>公开已经收到真实取消令牌的同步屏障。</summary>
            internal Task StopObserved { get { return _stopObserved.Task; } }
            /// <summary>本例没有设备前提；Runtime 仍执行原来的安全和状态检查。</summary>
            public Task ValidateStartAsync(CancellationToken cancellationToken) { return Task.CompletedTask; }
            /// <summary>等待已声明的失败，取消只作为停止屏障，不提前完成以保留真实收尾等待。</summary>
            public async Task ExecuteAsync(IProgress<StationPreparationProgress> progress, CancellationToken cancellationToken)
            {
                using (cancellationToken.Register(() => _stopObserved.TrySetResult(true)))
                {
                    _entered.TrySetResult(true);
                    await _completion.Task.ConfigureAwait(false);
                }
            }
            /// <summary>本例一定以故障结束，不能产生准备完成结论。</summary>
            public Task ValidateReadyAsync(CancellationToken cancellationToken)
            { throw new InvalidOperationException("停止异常检查不应到达准备完成校验。"); }
            /// <summary>释放等待并保留原始失败；清理时重复调用也不会修改已经完成的结果。</summary>
            internal void Fail(string message) { _completion.TrySetException(new InvalidOperationException(message)); }
        }

        /// <summary>转发到真实 WPF 同步上下文，只把 async void 生命周期转换为可等待的测试屏障。</summary>
        private sealed class AsyncVoidCompletionContext : SynchronizationContext
        {
            /// <summary>真正执行页面续体的原 WPF 同步上下文。</summary>
            private readonly SynchronizationContext _inner;
            /// <summary>收到最后一个 async void 完成通知时结束。</summary>
            private readonly TaskCompletionSource<bool> _completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>当前观察的异步方法数，不修改 Runtime 或页面忙状态。</summary>
            private int _operations;
            /// <summary>保留原调度入口。</summary>
            internal AsyncVoidCompletionContext(SynchronizationContext inner) { _inner = inner; }
            /// <summary>页面事件真正返回后完成的任务。</summary>
            internal Task Completion { get { return _completion.Task; } }
            /// <summary>记录进入真实 async void 事件处理。</summary>
            public override void OperationStarted() { Interlocked.Increment(ref _operations); }
            /// <summary>只有事件处理完成后才释放测试等待。</summary>
            public override void OperationCompleted()
            { if (Interlocked.Decrement(ref _operations) == 0) _completion.TrySetResult(true); }
            /// <summary>页面续体仍通过原 WPF 调度器执行，不改变线程或回调顺序。</summary>
            public override void Post(SendOrPostCallback callback, object state) { _inner.Post(callback, state); }
        }

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
