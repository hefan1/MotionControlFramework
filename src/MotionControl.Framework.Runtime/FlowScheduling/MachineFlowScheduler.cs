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

        /// <summary>按逻辑轴名称记录启动前需要回零的工位。</summary>
        private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _stationsRequiringHomedAxes;

        /// <summary>保护当前运行任务和取消令牌，避免重复启动或并发暂停。</summary>
        private readonly object _runSync = new object();

        /// <summary>当前这一轮所有工位共用的暂停令牌。</summary>
        private CancellationTokenSource _runCancellation;

        /// <summary>当前并行工位任务；全部退出后由暂停流程清空。</summary>
        private Task _runTask;
        /// <summary>已清理的最近一次调度是否完成安全收尾或只被轴未回零前置检查拒绝。</summary>
        private bool _lastRunSettledSuccessfully = true;
        /// <summary>取消回调失败会锁存，不能仅因调度任务退出就认定相关停止请求成功。</summary>
        private bool _stopRequestFailed;
        /// <summary>每次启动递增的运行代次，供迟到的暂停超时检查区分新旧运行。</summary>
        private long _runGeneration;

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
            _stationsRequiringHomedAxes = BuildHomedAxisRequirements(motionCards, flowList);
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
                _runTask = null;
                if (_runCancellation != null) { _runCancellation.Dispose(); _runCancellation = null; }

                if (_equipmentInitializationFlow != null)
                {
                    _equipmentInitializationFlow.ResetForNewConnection();
                }

                _equipmentInitializationCompleted = false;
                _lastRunSettledSuccessfully = true;
                _stopRequestFailed = false;
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

        /// <summary>检查所有逻辑轴是否停稳、无报警且限位状态可确认；同时汇总全部必需回零轴。</summary>
        public async Task EnsureAxesReadyForAutomaticStartAsync(
            bool allowHomingRequired,
            CancellationToken cancellationToken)
        {
            var missingHomedRequirements = new List<KeyValuePair<string, IReadOnlyList<string>>>();
            foreach (IAxis axis in _motionCards.Axes)
            {
                AxisStatus status = await axis.ReadStatusAsync(cancellationToken).ConfigureAwait(false);
                if (!AxisStopFeedback.IsConfirmedStopped(status))
                {
                    throw new InvalidOperationException(
                        "逻辑轴 '" + axis.Name + "' 仍在运动或速度状态未知，禁止启动自动流程；请先确认轴已停稳。");
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
                    throw new AxisRequiresHomingException(
                        "逻辑轴 '" + axis.Name
                        + "' 曾触发硬限位，恢复后必须先完成回零，才能启动自动流程。");
                }

                IReadOnlyList<string> stations;
                if (!allowHomingRequired
                    && _stationsRequiringHomedAxes.TryGetValue(axis.Name, out stations)
                    && !status.Homed)
                {
                    missingHomedRequirements.Add(
                        new KeyValuePair<string, IReadOnlyList<string>>(axis.Name, stations));
                }
            }

            if (missingHomedRequirements.Count > 0)
            {
                throw CreateHomingRequiredException(missingHomedRequirements);
            }
        }

        /// <summary>无待执行初始化时，在启动前汇总检查全部自动工位所需轴是否已回零。</summary>
        public async Task EnsureRequiredStationAxesHomedForAutomaticStartAsync(CancellationToken cancellationToken)
        {
            var missingHomedRequirements = new List<KeyValuePair<string, IReadOnlyList<string>>>();
            foreach (KeyValuePair<string, IReadOnlyList<string>> requirement in _stationsRequiringHomedAxes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IAxis axis = _motionCards.GetAxis(requirement.Key);
                AxisStatus status = await axis.ReadStatusAsync(cancellationToken).ConfigureAwait(false);
                if (status == null) throw new InvalidOperationException("读取逻辑轴 '" + axis.Name + "' 回零状态失败。");
                if (!status.Homed)
                {
                    missingHomedRequirements.Add(
                        new KeyValuePair<string, IReadOnlyList<string>>(axis.Name, requirement.Value));
                }
            }

            if (missingHomedRequirements.Count > 0)
            {
                throw CreateHomingRequiredException(missingHomedRequirements);
            }
        }

        /// <summary>汇总各工位声明的回零要求，并在 Runtime 创建时校验逻辑轴确实已映射。</summary>
        private static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildHomedAxisRequirements(
            MotionCardGroup motionCards,
            IReadOnlyList<IStationFlow> stationFlows)
        {
            var stationsByAxis = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (IStationFlow station in stationFlows)
            {
                var requirements = station as IStationAxisRequirements;
                if (requirements == null)
                {
                    continue;
                }

                if (requirements.RequiredHomedAxes == null)
                {
                    throw new ArgumentException(
                        "工位 '" + station.StationName + "' 的回零轴清单不能为空。",
                        nameof(stationFlows));
                }

                var stationAxes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string axisName in requirements.RequiredHomedAxes)
                {
                    if (string.IsNullOrWhiteSpace(axisName))
                    {
                        throw new ArgumentException(
                            "工位 '" + station.StationName + "' 包含空的回零轴名称。",
                            nameof(stationFlows));
                    }

                    if (!stationAxes.Add(axisName))
                    {
                        continue;
                    }

                    try
                    {
                        motionCards.GetAxis(axisName);
                    }
                    catch (KeyNotFoundException error)
                    {
                        throw new ArgumentException(
                            "工位 '" + station.StationName + "' 需要回零的逻辑轴未映射：'" + axisName + "'。",
                            nameof(stationFlows),
                            error);
                    }

                    List<string> stations;
                    if (!stationsByAxis.TryGetValue(axisName, out stations))
                    {
                        stations = new List<string>();
                        stationsByAxis.Add(axisName, stations);
                    }

                    stations.Add(station.StationName);
                }
            }

            return stationsByAxis.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<string>)pair.Value.AsReadOnly(),
                StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>按工位显示顺序整理未回零轴，并给出适合当前设备配置的处理方法。</summary>
        private StationAxesNotHomedException CreateHomingRequiredException(
            IReadOnlyList<KeyValuePair<string, IReadOnlyList<string>>> requirements)
        {
            var orderedRequirements = (requirements ?? Array.Empty<KeyValuePair<string, IReadOnlyList<string>>>())
                .OrderBy(GetRequirementStationOrder)
                .ThenBy(GetRequirementAxisOrder)
                .ToList();
            return new StationAxesNotHomedException(
                orderedRequirements,
                _equipmentInitializationFlow != null);
        }

        /// <summary>返回该轴最先关联工位在流程列表中的位置，使跨页面原因顺序保持一致。</summary>
        private int GetRequirementStationOrder(KeyValuePair<string, IReadOnlyList<string>> requirement)
        {
            for (int stationIndex = 0; stationIndex < _stationFlows.Count; stationIndex++)
            {
                if (requirement.Value != null
                    && requirement.Value.Contains(_stationFlows[stationIndex].StationName, StringComparer.OrdinalIgnoreCase))
                {
                    return stationIndex;
                }
            }

            return int.MaxValue;
        }

        /// <summary>返回轴在其首个关联工位回零清单中的位置，保持同一工位的轴顺序。</summary>
        private int GetRequirementAxisOrder(KeyValuePair<string, IReadOnlyList<string>> requirement)
        {
            if (requirement.Value == null)
            {
                return int.MaxValue;
            }

            for (int stationIndex = 0; stationIndex < _stationFlows.Count; stationIndex++)
            {
                IStationFlow station = _stationFlows[stationIndex];
                if (!requirement.Value.Contains(station.StationName, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var axisRequirements = station as IStationAxisRequirements;
                if (axisRequirements?.RequiredHomedAxes == null)
                {
                    return int.MaxValue;
                }

                int axisIndex = 0;
                foreach (string axisName in axisRequirements.RequiredHomedAxes)
                {
                    if (string.Equals(axisName, requirement.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        return axisIndex;
                    }

                    axisIndex++;
                }
            }

            return int.MaxValue;
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

        /// <summary>工位正常退出或仅被轴回零前置检查拒绝，才可作为同连接恢复条件之一。</summary>
        public bool IsSettlementConfirmed
        {
            get
            {
                lock (_runSync)
                    return !_stopRequestFailed && (_runTask == null ? _lastRunSettledSuccessfully : IsRunSettled(_runTask));
            }
        }

        /// <summary>仅将已完成的普通退出及明确的未回零前置检查视为已结算；其他故障保持保守拦截。</summary>
        private static bool IsRunSettled(Task run)
        {
            if (run.Status == TaskStatus.RanToCompletion) return true;
            if (!run.IsFaulted || run.Exception == null) return false;
            var errors = run.Exception.Flatten().InnerExceptions;
            return errors.Count != 0 && errors.All(error => error is StationAxesNotHomedException || error is AxisRequiresHomingException);
        }

        /// <summary>轴限位后尚未回零的明确前置拒绝；本次不会启动生产工位。</summary>
        private sealed class AxisRequiresHomingException : InvalidOperationException
        {
            /// <summary>记录需要回零的轴和处理说明。</summary>
            internal AxisRequiresHomingException(string message) : base(message) { }
        }

        /// <summary>把设备初始化和工位调度放到后台执行，避免初始化同步代码卡住界面线程。</summary>
        public Task Start(Task activation = null)
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
                _lastRunSettledSuccessfully = false;
                _runGeneration++;
                // 不把暂停令牌交给 Task.Run；即使刚启动就暂停，也让调度方法运行并正常收尾。
                _runTask = Task.Run(async () =>
                {
                    // 启动许可发布前先登记可取消的运行；门禁持锁时不调用任何工位流程。
                    if (activation != null) await activation.ConfigureAwait(false);
                    if (runCancellation.IsCancellationRequested) return;
                    await RunAllStationsAsync(runCancellation).ConfigureAwait(false);
                });
                return _runTask;
            }
        }

        /// <summary>从报警通知等同步事件中请求暂停，不等待当前初始化或工位步骤退出。</summary>
        public long RequestPause()
        {
            lock (_runSync)
            {
                if (_runCancellation != null && _runTask != null && !_runTask.IsCompleted)
                {
                    try { _runCancellation.Cancel(); }
                    catch { _stopRequestFailed = true; throw; }
                    return _runGeneration;
                }
                return -1;
            }
        }

        /// <summary>检查指定代次是否仍在运行，旧周期的异步超时不能作用于新周期。</summary>
        public bool IsRunActive(long generation)
        {
            lock (_runSync) return generation >= 0 && generation == _runGeneration && _runTask != null && !_runTask.IsCompleted;
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
                    _stopRequestFailed = true;
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
                            _lastRunSettledSuccessfully = !_stopRequestFailed && runError == null && IsRunSettled(runTask);
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
