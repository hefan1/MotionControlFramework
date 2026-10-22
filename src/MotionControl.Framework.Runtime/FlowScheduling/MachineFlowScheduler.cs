using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Runtime.Machine;

namespace MotionControl.Framework.Runtime.FlowScheduling
{
    /// <summary>先运行可选设备初始化，再并行运行本设备配置的各工位步骤。</summary>
    internal sealed class MachineFlowScheduler
    {
        /// <summary>用于启动前检查逻辑轴状态的本机运动卡集合。</summary>
        private readonly MotionCardGroup _motionCards;

        /// <summary>本设备需要并行执行的工位流程，构造后不再变更。</summary>
        private readonly IReadOnlyList<IStationFlow> _stationFlows;

        /// <summary>本设备可选的启动前初始化流程；为空时直接启动各工位。</summary>
        private readonly IEquipmentInitializationFlow _equipmentInitializationFlow;

        /// <summary>保护当前运行任务和取消令牌，避免重复启动或并发暂停。</summary>
        private readonly object _runSync = new object();

        /// <summary>当前这一轮所有工位共用的暂停令牌。</summary>
        private CancellationTokenSource _runCancellation;

        /// <summary>当前并行工位任务；全部退出后由暂停流程清空。</summary>
        private Task _runTask;

        /// <summary>当前控制卡连接周期内，设备初始化是否已经完成。</summary>
        private bool _equipmentInitializationCompleted;

        /// <summary>恢复文件记录初始化曾完成；仍需设备流程重新检查现场状态。</summary>
        private bool _initializationRecoveryRequested;

        /// <summary>检查并保存本机的轴、工位流程和可选启动前初始化流程。</summary>
        public MachineFlowScheduler(
            MotionCardGroup motionCards,
            IEnumerable<IStationFlow> stationFlows,
            IEquipmentInitializationFlow equipmentInitializationFlow)
        {
            if (motionCards == null)
            {
                throw new ArgumentNullException(nameof(motionCards));
            }

            if (stationFlows == null)
            {
                throw new ArgumentNullException(nameof(stationFlows));
            }

            var flowList = stationFlows.ToList();
            var stationNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var flow in flowList)
            {
                if (flow == null)
                {
                    throw new ArgumentException("工位流程列表中包含空对象。", nameof(stationFlows));
                }

                if (string.IsNullOrWhiteSpace(flow.StationName))
                {
                    throw new ArgumentException("工位流程没有配置 StationName。", nameof(stationFlows));
                }

                if (!stationNames.Add(flow.StationName))
                {
                    throw new ArgumentException(
                        "工位名称重复：'" + flow.StationName + "'。请检查设备流程配置。",
                        nameof(stationFlows));
                }
            }

            _motionCards = motionCards;
            _stationFlows = flowList.AsReadOnly();
            _equipmentInitializationFlow = equipmentInitializationFlow;
        }

        /// <summary>当前连接周期内存在尚未完成的可选初始化时返回 true。</summary>
        public bool HasPendingInitialization
        {
            get
            {
                lock (_runSync)
                {
                    return _equipmentInitializationFlow != null && !_equipmentInitializationCompleted;
                }
            }
        }

        /// <summary>没有初始化流程或本连接周期内初始化已完成时返回 true。</summary>
        public bool EquipmentInitializationCompleted
        {
            get
            {
                lock (_runSync)
                {
                    return _equipmentInitializationFlow == null || _equipmentInitializationCompleted;
                }
            }
        }

        /// <summary>接收上次正常关闭时的初始化完成标记，后续仍必须重新核实现场状态。</summary>
        public void RequestInitializationRecovery(bool initializationCompletedBeforeRestart)
        {
            lock (_runSync)
            {
                if (_runTask != null && !_runTask.IsCompleted)
                {
                    throw new InvalidOperationException("设备流程仍在运行，不能设置初始化恢复状态。");
                }

                _initializationRecoveryRequested = initializationCompletedBeforeRestart;
            }
        }

        /// <summary>新控制卡连接周期开始时，重置设备初始化步骤和本机完成标记。</summary>
        public void ResetForNewConnection()
        {
            lock (_runSync)
            {
                if (_runTask != null && !_runTask.IsCompleted)
                {
                    throw new InvalidOperationException("设备流程仍在运行，不能开始新的控制卡连接周期。");
                }

                if (_equipmentInitializationFlow != null)
                {
                    _equipmentInitializationFlow.ResetForNewConnection();
                }

                _equipmentInitializationCompleted = false;
            }
        }

        /// <summary>设备复位时清除初始化流程报警，并保留暂停时的当前步骤。</summary>
        public async Task ResetEquipmentInitializationAsync(CancellationToken cancellationToken)
        {
            if (_equipmentInitializationFlow == null)
            {
                return;
            }

            await _equipmentInitializationFlow.ResetAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>检查所有逻辑轴是否停稳、无报警且限位状态可确认；可选初始化可处理未回零标记。</summary>
        public async Task EnsureAxesReadyForAutomaticStartAsync(
            bool allowHomingRequired,
            CancellationToken cancellationToken)
        {
            foreach (IAxis axis in _motionCards.Axes)
            {
                AxisStatus status = await axis.ReadStatusAsync(cancellationToken).ConfigureAwait(false);
                if (status.Moving)
                {
                    throw new InvalidOperationException(
                        "逻辑轴 '" + axis.Name + "' 仍在运动，禁止启动自动流程；请先停止手动动作并确认轴已停稳。");
                }

                var limitStatusMessage = BuildLimitStatusMessage(status);
                var hasUnknownLimitStatus = !status.PositiveLimitActive.HasValue
                    || !status.NegativeLimitActive.HasValue
                    || !status.PositiveSoftLimitActive.HasValue
                    || !status.NegativeSoftLimitActive.HasValue;
                if (status.AlarmActive || !string.IsNullOrWhiteSpace(limitStatusMessage) || hasUnknownLimitStatus)
                {
                    var reasons = new List<string>();
                    if (status.AlarmActive)
                    {
                        reasons.Add(string.IsNullOrWhiteSpace(status.AlarmMessage)
                            ? "轴或驱动报警有效"
                            : status.AlarmMessage);
                    }

                    if (!string.IsNullOrWhiteSpace(limitStatusMessage))
                    {
                        reasons.Add(limitStatusMessage);
                    }

                    if (hasUnknownLimitStatus)
                    {
                        if (string.IsNullOrWhiteSpace(limitStatusMessage))
                        {
                            reasons.Add("有方向的硬限位或软限位状态无法确认");
                        }
                    }

                    throw new InvalidOperationException(
                        "逻辑轴 '" + axis.Name + "' 当前存在报警或限位，禁止启动自动流程。"
                        + "原因：" + string.Join("；", reasons) + "。");
                }

                if (!allowHomingRequired && status.RequiresHomingAfterLimitAlarm)
                {
                    throw new InvalidOperationException(
                        "逻辑轴 '" + axis.Name
                        + "' 曾触发硬限位，恢复后必须先完成回零，才能启动自动流程。");
                }
            }
        }

        /// <summary>组合当前已触发的正负硬限位和软限位原因，并附上适配器提供的来源。</summary>
        private static string BuildLimitStatusMessage(AxisStatus status)
        {
            var messages = new List<string>();
            if (status.PositiveLimitActive == true)
            {
                messages.Add("正向硬限位有效");
            }
            else if (!status.PositiveLimitActive.HasValue)
            {
                messages.Add("正向硬限位状态未知");
            }

            if (status.NegativeLimitActive == true)
            {
                messages.Add("负向硬限位有效");
            }
            else if (!status.NegativeLimitActive.HasValue)
            {
                messages.Add("负向硬限位状态未知");
            }

            if (status.PositiveSoftLimitActive == true)
            {
                messages.Add("正向软限位有效");
            }
            else if (!status.PositiveSoftLimitActive.HasValue)
            {
                messages.Add("正向软限位状态未知");
            }

            if (status.NegativeSoftLimitActive == true)
            {
                messages.Add("负向软限位有效");
            }
            else if (!status.NegativeSoftLimitActive.HasValue)
            {
                messages.Add("负向软限位状态未知");
            }

            if (messages.Count > 0 && !string.IsNullOrWhiteSpace(status.LimitStatusSource))
            {
                messages.Add("状态来源：" + status.LimitStatusSource);
            }

            return string.Join("、", messages);
        }

        /// <summary>返回按配置顺序排列的工位流程只读列表。</summary>
        public IReadOnlyList<IStationFlow> StationFlows
        {
            get { return _stationFlows; }
        }

        /// <summary>至少一个工位流程任务仍未结束时返回 true。</summary>
        public bool IsRunning
        {
            get
            {
                lock (_runSync)
                {
                    return _runTask != null && !_runTask.IsCompleted;
                }
            }
        }

        /// <summary>把设备初始化和工位调度放到后台执行，避免初始化同步代码卡住界面线程。</summary>
        public Task Start()
        {
            lock (_runSync)
            {
                if (_runTask != null && !_runTask.IsCompleted)
                {
                    throw new InvalidOperationException("工位流程已经在运行。");
                }

                if (_runCancellation != null)
                {
                    _runCancellation.Dispose();
                }

                var runCancellation = new CancellationTokenSource();
                _runCancellation = runCancellation;
                // 不把暂停令牌交给 Task.Run；即使刚启动就暂停，也让调度方法运行并正常收尾。
                _runTask = Task.Run(() => RunAllStationsAsync(runCancellation));
                return _runTask;
            }
        }

        /// <summary>从报警通知等同步事件中请求暂停，不等待当前初始化或工位步骤退出。</summary>
        public void RequestPause()
        {
            lock (_runSync)
            {
                if (_runCancellation != null && _runTask != null && !_runTask.IsCompleted)
                {
                    _runCancellation.Cancel();
                }
            }
        }

        /// <summary>请求当前初始化或所有工位暂停，并在指定时限内等待步骤退出。</summary>
        public async Task PauseAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (timeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout), "暂停等待时限必须大于 0。");
            }

            CancellationTokenSource cancellation;
            Task runTask;
            Exception cancellationError = null;

            lock (_runSync)
            {
                cancellation = _runCancellation;
                runTask = _runTask;
                if (cancellation == null || runTask == null)
                {
                    return;
                }

                try
                {
                    cancellation.Cancel();
                }
                catch (Exception error)
                {
                    cancellationError = error;
                }
            }

            Exception runError = null;
            try
            {
                using (var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    var timeoutTask = Task.Delay(timeout, waitCancellation.Token);
                    var completedTask = await Task.WhenAny(runTask, timeoutTask).ConfigureAwait(false);
                    if (completedTask != runTask && !runTask.IsCompleted)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        throw new TimeoutException(
                            "等待工位流程响应暂停超时；流程仍在后台运行，不能复位或断开运动控制卡。");
                    }

                    waitCancellation.Cancel();
                }

                await runTask.ConfigureAwait(false);
            }
            catch (Exception error)
            {
                runError = error;
            }
            finally
            {
                if (runTask.IsCompleted)
                {
                    lock (_runSync)
                    {
                        if (ReferenceEquals(_runCancellation, cancellation))
                        {
                            _runCancellation = null;
                            _runTask = null;
                            cancellation.Dispose();
                        }
                    }
                }
            }

            if (runError is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw runError;
            }

            if (cancellationError != null && runError != null)
            {
                throw new AggregateException("请求暂停和等待工位退出时都发生错误。", cancellationError, runError);
            }

            if (runError != null)
            {
                throw new InvalidOperationException("工位流程暂停失败。", runError);
            }

            if (cancellationError != null)
            {
                throw new InvalidOperationException("请求工位流程暂停时发生错误。", cancellationError);
            }
        }

        /// <summary>初始化完成后并行启动所有工位，并在工位结束或出错时请求其他工位退出。</summary>
        private async Task RunAllStationsAsync(CancellationTokenSource cancellation)
        {
            try
            {
                await RunEquipmentInitializationAsync(cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return;
            }

            var stationTasks = _stationFlows
                .Select(flow => Task.Run(() => RunStationAsync(flow, cancellation.Token), cancellation.Token))
                .ToArray();

            var firstCompleted = await Task.WhenAny(stationTasks).ConfigureAwait(false);
            if (cancellation.IsCancellationRequested)
            {
                var pauseErrors = await WaitForStationsToStopAsync(stationTasks, true).ConfigureAwait(false);
                if (pauseErrors.Count > 0)
                {
                    throw new AggregateException("暂停工位流程时发生错误。", pauseErrors);
                }

                return;
            }

            var cancellationErrors = new List<Exception>();
            try
            {
                cancellation.Cancel();
            }
            catch (Exception cancellationError)
            {
                cancellationErrors.Add(cancellationError);
            }

            var failures = await WaitForStationsToStopAsync(stationTasks, false).ConfigureAwait(false);
            failures.AddRange(cancellationErrors);

            if (!firstCompleted.IsFaulted && !firstCompleted.IsCanceled)
            {
                failures.Insert(0, new InvalidOperationException(
                    "工位流程意外结束：自动运行期间 ExecuteCurrentStepAsync 不应正常返回整个流程。"));
            }

            if (failures.Count == 0)
            {
                failures.Add(new InvalidOperationException("工位流程异常结束，但没有提供具体错误信息。"));
            }

            throw new AggregateException("一个或多个工位流程发生错误，其他工位已请求暂停。", failures);
        }

        /// <summary>顺序执行未完成的设备初始化，并在启动工位前重新检查所有逻辑轴。</summary>
        private async Task RunEquipmentInitializationAsync(CancellationToken cancellationToken)
        {
            if (_equipmentInitializationFlow == null || !HasPendingInitialization)
            {
                return;
            }

            if (await CanSkipInitializationAfterRestartAsync(cancellationToken).ConfigureAwait(false))
            {
                await EnsureAxesReadyForAutomaticStartAsync(false, cancellationToken).ConfigureAwait(false);
                lock (_runSync)
                {
                    _equipmentInitializationCompleted = true;
                }

                return;
            }

            while (!_equipmentInitializationFlow.IsCompleted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await _equipmentInitializationFlow
                        .ExecuteCurrentStepAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception stepError)
                {
                    throw new InvalidOperationException(
                        "设备初始化步骤 '" + _equipmentInitializationFlow.CurrentStepName + "' 执行失败。",
                        stepError);
                }
            }

            await EnsureAxesReadyForAutomaticStartAsync(false, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            lock (_runSync)
            {
                _equipmentInitializationCompleted = true;
            }
        }

        /// <summary>仅在恢复文件有效且设备流程通过实时状态检查时跳过本次初始化。</summary>
        private async Task<bool> CanSkipInitializationAfterRestartAsync(CancellationToken cancellationToken)
        {
            bool recoveryWasRequested;
            lock (_runSync)
            {
                recoveryWasRequested = _initializationRecoveryRequested;
                _initializationRecoveryRequested = false;
            }

            if (!recoveryWasRequested)
            {
                return false;
            }

            var recoverableFlow = _equipmentInitializationFlow as IRecoverableEquipmentInitializationFlow;
            if (recoverableFlow == null)
            {
                return false;
            }

            return await recoverableFlow
                .CanSkipAfterRestartAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>重复执行单个工位的当前步骤，并保留步骤执行异常。</summary>
        private static async Task RunStationAsync(IStationFlow stationFlow, CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await stationFlow.ExecuteCurrentStepAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception stepError)
                {
                    throw new InvalidOperationException(
                        "工位 '" + stationFlow.StationName + "' 的步骤 '" + stationFlow.CurrentStepName + "' 执行失败。",
                        stepError);
                }

                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        /// <summary>等待所有工位退出，并收集暂停或故障期间产生的异常。</summary>
        private static async Task<List<Exception>> WaitForStationsToStopAsync(
            Task[] stationTasks,
            bool cancellationWasRequested)
        {
            try
            {
                await Task.WhenAll(stationTasks).ConfigureAwait(false);
                return new List<Exception>();
            }
            catch (Exception waitError)
            {
                var failures = stationTasks
                    .Where(task => task.IsFaulted)
                    .SelectMany(task => task.Exception.InnerExceptions)
                    .ToList();

                if (failures.Count == 0 && !cancellationWasRequested)
                {
                    failures.Add(waitError);
                }

                return failures;
            }
        }
    }
}
