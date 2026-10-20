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

        /// <summary>检查所有逻辑轴是否停稳且无报警、限位；可选初始化可处理未回零标记。</summary>
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

                if (status.AlarmActive
                    || status.PositiveLimitActive
                    || status.NegativeLimitActive
                    || status.PositiveSoftLimitActive
                    || status.NegativeSoftLimitActive)
                {
                    string reason = status.AlarmMessage;
                    throw new InvalidOperationException(
                        "逻辑轴 '" + axis.Name + "' 当前存在报警或限位，禁止启动自动流程。"
                        + (string.IsNullOrWhiteSpace(reason) ? string.Empty : "原因：" + reason + "。"));
                }

                if (!allowHomingRequired && status.RequiresHomingAfterLimitAlarm)
                {
                    throw new InvalidOperationException(
                        "逻辑轴 '" + axis.Name
                        + "' 曾触发硬限位，恢复后必须先完成回零，才能启动自动流程。");
                }
            }
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

        /// <summary>先执行未完成的设备初始化，再并行启动所有工位流程。</summary>
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

                _runCancellation = new CancellationTokenSource();
                _runTask = RunAllStationsAsync(_runCancellation);
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
