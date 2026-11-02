using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Contracts.Preparation;

namespace MotionControl.Framework.Runtime.Machine
{
    /// <summary>独立的工位准备操作。这里只负责顺序、状态和命令互斥，具体工艺由准备项提供。</summary>
    public sealed partial class MachineRuntime
    {
        /// <summary>准备动作开始前记录不确定现场；由 Host 配置。</summary>
        private Action _beforePreparation;
        /// <summary>准备成功或受控停稳后提交现场记录；由 Host 配置。</summary>
        private Action _afterSafePreparation;
        /// <summary>本次准备已创建记录且尚未完成安全收尾。</summary>
        private bool _preparationRecordPending;

        /// <summary>连接前接入准备动作的持久标记；两个回调必须成对提供。</summary>
        public void SetPreparationRecordCallbacks(Action before, Action afterSafe)
        {
            if (State != MachineRunState.Disconnected) throw new InvalidOperationException("只能在连接前配置准备记录。");
            if ((before == null) != (afterSafe == null)) throw new ArgumentException("准备记录回调必须成对提供。");
            _beforePreparation = before;
            _afterSafePreparation = afterSafe;
        }

        /// <summary>调用方持有整机命令锁时提交安全结束记录，失败保留待核对标记。</summary>
        private void CompletePreparationRecord()
        {
            if (!_preparationRecordPending) return;
            _afterSafePreparation();
            _preparationRecordPending = false;
        }
        /// <summary>保护准备项、只读快照和后台操作引用。</summary>
        private readonly object _preparationSync = new object();
        /// <summary>已经按依赖排序的准备项，不包含任何设备专用名称。</summary>
        private IStationPreparation[] _preparations = new IStationPreparation[0];
        /// <summary>各工位最近一次准备结果。</summary>
        private readonly Dictionary<string, StationPreparationSnapshot> _preparationStates =
            new Dictionary<string, StationPreparationSnapshot>(StringComparer.OrdinalIgnoreCase);
        /// <summary>当前准备操作是否仍在执行或停止收尾。</summary>
        private bool _preparing;
        /// <summary>取消当前准备步骤；必须等后台任务和轴停止后才能归还命令权。</summary>
        private CancellationTokenSource _preparationCancellation;
        /// <summary>当前准备操作及其受控停止收尾。</summary>
        private Task _preparationTask = Task.CompletedTask;
        /// <summary>最近一次停轴收尾；超时后仍保留引用，禁止与尚未结束的停轴命令交错。</summary>
        private Task _preparationStopTask = Task.CompletedTask;
        /// <summary>停稳尚未确认时保持准备占用，只允许重新执行停止确认。</summary>
        private bool _preparationStopUnconfirmed;
        /// <summary>尚在收尾的停止请求；期间不得把设备占用交给新的生产或手动命令。</summary>
        private int _preparationStopRequests;
        /// <summary>准备后是否已进入生产；正常生产暂停续跑不要求机构返回初始位置。</summary>
        private bool _preparationProductionStarted;
        /// <summary>框架准备执行失败的锁存报警，工位及具体原因由运行结果提供。</summary>
        private static readonly MachineAlarmDefinition PreparationFailedAlarm = new MachineAlarmDefinition(
            "PREPARATION_FAILED", "工位准备未完成", MachineAlarmKind.StopMachine,
            "查看设备准备页的工位和失败步骤。", "处理原因后报警复位，再重试工位回零。");

        /// <summary>准备步骤变化通知；可能在后台线程触发，界面应切回自身调度线程。</summary>
        public event EventHandler PreparationChanged;

        /// <summary>返回各工位不可变准备快照，顺序与依赖排序一致。</summary>
        public IReadOnlyList<StationPreparationSnapshot> PreparationStations
        {
            get { lock (_preparationSync) return _preparations.Select(p => _preparationStates[p.StationId]).ToArray(); }
        }

        /// <summary>正在准备或尚未停止收尾时为真。</summary>
        public bool IsPreparing { get { lock (_preparationSync) return _preparing; } }

        /// <summary>当前配置的全部准备项均已完成；没有准备项的旧设备保持原有行为。</summary>
        public bool IsPrepared
        {
            get { lock (_preparationSync) return !_preparing && _preparationStates.Values.All(s => s.IsPrepared); }
        }

        /// <summary>空闲时安装当前执行入口的准备项；更换后全部资格失效，不执行任何设备动作。</summary>
        public void ConfigurePreparations(IEnumerable<IStationPreparation> preparations)
        {
            IStationPreparation[] ordered = SortPreparations(preparations ?? Enumerable.Empty<IStationPreparation>());
            if (!_commandLock.Wait(0)) throw new InvalidOperationException("设备命令正在执行，不能更新准备配置。");
            try
            {
                EnsureNoPreparationInProgress("更新准备配置");
                if (State == MachineRunState.Running || State == MachineRunState.Pausing || _flowScheduler.IsRunning)
                    throw new InvalidOperationException("生产仍在运行，不能更新准备配置。");
                EnsureNoManualOperationsInProgress("更新准备配置");
                lock (_preparationSync)
                {
                    _preparations = ordered;
                    _preparationStates.Clear();
                    foreach (IStationPreparation item in ordered)
                        _preparationStates.Add(item.StationId, new StationPreparationSnapshot(item.StationId,
                            item.StationName, StationPreparationState.NotPrepared, "尚未准备", "请执行工位回零。"));
                    _preparationProductionStarted = false;
                }
            }
            finally { _commandLock.Release(); }
            RaisePreparationChanged();
        }

        /// <summary>独占执行全部或指定工位准备；成功后仅进入就绪，不启动生产。</summary>
        public async Task PrepareAsync(string stationId, CancellationToken cancellationToken)
        {
            Task operation;
            await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                EnsureNoPreparationInProgress("启动另一项准备");
                EnsureRecoveryTrusted();
                lock (_preparationSync)
                    if (_preparationProductionStarted)
                        throw new InvalidOperationException("当前生产会话已运行，不能重新回零；请保留当前工步继续生产，或结束会话并核对在制品后重新准备。");
                EnsureOneOfStates(new[] { MachineRunState.Ready, MachineRunState.ResetRequired, MachineRunState.Paused },
                    "设备当前不能执行工位回零；有报警时请先处理并复位。");
                if (_flowScheduler.IsRunning) throw new InvalidOperationException("生产流程尚未退出，不能准备设备。");
                EnsureNoManualOperationsInProgress("准备设备");
                if (!_motionCards.IsConnected) throw new InvalidOperationException("控制器未连接，不能准备设备。");
                if (!IsEmergencyStopMonitoringConfigured()) throw new InvalidOperationException("设备准备必须配置有效的急停输入。");
                if (await UpdateEmergencyStopAlarmAsync(cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("急停输入仍有效，不能准备设备。");
                _alarmService.EnsureStopAlarmConditionsRecovered();
                if (_alarmService.HasLatchedStopAlarm) throw new InvalidOperationException("停机报警尚未复位，不能准备设备。");
                await EnsurePreparationAxesStoppedAsync(cancellationToken, true).ConfigureAwait(false);
                IStationPreparation[] selected;
                lock (_preparationSync)
                {
                    selected = SelectPreparations(stationId);
                    if (selected.Length == 0) throw new InvalidOperationException("当前入口未配置对应的工位准备流程。");
                }
                // 持久标记必须在设备步骤、使能或轴运动之前创建；写失败不进入准备状态。
                if (_beforePreparation != null) { _beforePreparation(); _preparationRecordPending = true; }
                lock (_preparationSync)
                {
                    _preparing = true;
                    _preparationStopUnconfirmed = false;
                    _preparationProductionStarted = false;
                    _preparationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    foreach (IStationPreparation item in selected)
                        if (!_preparationStates[item.StationId].IsPrepared)
                            _preparationStates[item.StationId] = new StationPreparationSnapshot(item.StationId,
                                item.StationName, StationPreparationState.Waiting, "等待准备", "等待前序工位和条件检查。");
                }
                SetStatus(MachineRunState.Preparing, null);
                try { StartEmergencyStopMonitor(); }
                catch
                {
                    lock (_preparationSync) { _preparing = false; _preparationCancellation.Dispose(); _preparationCancellation = null; }
                    SetStatus(MachineRunState.Alarmed, new InvalidOperationException("设备准备安全监控未能启动。"));
                    throw;
                }
                lock (_preparationSync)
                {
                    CancellationTokenSource runCancellation = _preparationCancellation;
                    operation = _preparationTask = Task.Run(() => RunPreparationAsync(selected, runCancellation));
                }
            }
            finally { _commandLock.Release(); }
            RaisePreparationChanged();
            await operation.ConfigureAwait(false);
        }

        /// <summary>停止准备并等待步骤退出及所有轴停稳；超时仍保持占用，禁止继续动作。</summary>
        public async Task StopPreparationAsync(CancellationToken cancellationToken)
        {
            Task operation;
            bool recoveringStop;
            await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                lock (_preparationSync)
                {
                    if (!_preparing) return;
                    operation = _preparationTask;
                    recoveringStop = _preparationStopUnconfirmed && operation.IsCompleted;
                    _preparationStopRequests++;
                }
            }
            finally { _commandLock.Release(); }
            try
            {
                RequestPreparationStop();
                await StopPreparationAxesAsync().ConfigureAwait(false);
                Task completed = await Task.WhenAny(operation, Task.Delay(_pauseTimeout, cancellationToken)).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (!ReferenceEquals(completed, operation))
                    throw new TimeoutException("设备准备停止超时；准备任务仍占用设备，请检查当前步骤与轴状态。");
                try { await operation.ConfigureAwait(false); }
                catch (OperationCanceledException) { /* 操作员停止已由准备快照记录，轴停稳确认在上方完成。 */ }
                catch (Exception) when (recoveringStop) { /* 原准备失败仍保留为报警，此处仅重新确认停止结果。 */ }
                if (recoveringStop) lock (_preparationSync) _preparationStopUnconfirmed = false;
            }
            catch (Exception error)
            {
                lock (_preparationSync) { _preparationStopUnconfirmed = true; _preparing = true; }
                SetStatus(MachineRunState.Alarmed, error);
                throw error;
            }
            finally
            {
                Exception finishError = null;
                await _commandLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    bool lastStop;
                    lock (_preparationSync)
                    {
                        _preparationStopRequests--;
                        lastStop = _preparationStopRequests == 0 && !_preparationStopUnconfirmed && operation.IsCompleted;
                    }
                    if (lastStop && !operation.IsFaulted && !_alarmService.HasLatchedStopAlarm && State != MachineRunState.Alarmed)
                    {
                        try { CompletePreparationRecord(); }
                        catch (Exception error) { finishError = error; }
                    }
                    lock (_preparationSync) if (lastStop) _preparing = false;
                    if (finishError != null) SetStatus(MachineRunState.Alarmed, finishError);
                }
                finally { _commandLock.Release(); }
                RaisePreparationChanged();
                RaiseStatusChanged();
                if (finishError != null) throw new InvalidOperationException("准备安全结束记录保存失败，请联系维护人员。", finishError);
            }
        }

        /// <summary>报警事件中异步受控停止准备，并将停止异常留在整机诊断中。</summary>
        private async Task StopPreparationAfterAlarmAsync()
        {
            try { await StopPreparationAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception error) { SetStatus(MachineRunState.Alarmed, error); }
        }

        /// <summary>只请求取消，不等待后台步骤，供停机报警事件使用。</summary>
        private void RequestPreparationStop()
        {
            lock (_preparationSync)
                if (_preparationCancellation != null) _preparationCancellation.Cancel();
        }

        /// <summary>先核对全部所选工位前提，再按依赖执行；任何失败或取消都不得给出完成资格。</summary>
        private async Task RunPreparationAsync(IStationPreparation[] selected, CancellationTokenSource cancellation)
        {
            CancellationToken token = cancellation.Token;
            IStationPreparation active = null;
            Exception failure = null;
            bool stopped = false;
            bool stopUnconfirmed = false;
            try
            {
                foreach (IStationPreparation item in selected)
                {
                    active = item;
                    token.ThrowIfCancellationRequested();
                    await item.ValidateStartAsync(token).ConfigureAwait(false);
                }
                foreach (IStationPreparation item in selected)
                {
                    active = item;
                    token.ThrowIfCancellationRequested();
                    bool alreadyReady;
                    lock (_preparationSync) alreadyReady = _preparationStates[item.StationId].IsPrepared;
                    if (alreadyReady)
                    {
                        await item.ValidateReadyAsync(token).ConfigureAwait(false);
                        continue;
                    }
                    foreach (string dependency in item.Dependencies)
                    {
                        IStationPreparation required = _preparations.Single(p => string.Equals(p.StationId, dependency, StringComparison.OrdinalIgnoreCase));
                        bool dependencyReady;
                        lock (_preparationSync) dependencyReady = _preparationStates[required.StationId].IsPrepared;
                        if (!dependencyReady) throw new InvalidOperationException("请先完成“" + required.StationName + "”回零，再准备“" + item.StationName + "”。");
                        try { await required.ValidateReadyAsync(token).ConfigureAwait(false); }
                        catch
                        {
                            InvalidatePreparationAndDependents(required.StationId, "前序工位当前姿态不满足准备条件。");
                            throw;
                        }
                    }
                    SetPreparationSnapshot(item, StationPreparationState.Preparing, "正在准备", string.Empty);
                    bool acceptingProgress = true;
                    var progress = new PreparationProgressSink(value =>
                    {
                        lock (_preparationSync)
                        {
                            if (value == null || token.IsCancellationRequested || !acceptingProgress) return;
                            _preparationStates[item.StationId] = new StationPreparationSnapshot(item.StationId,
                                item.StationName, StationPreparationState.Preparing, value.Action, value.Detail);
                        }
                        RaisePreparationChanged();
                    });
                    try { await item.ExecuteAsync(progress, token).ConfigureAwait(false); }
                    finally { lock (_preparationSync) acceptingProgress = false; }
                    token.ThrowIfCancellationRequested();
                    await item.ValidateReadyAsync(token).ConfigureAwait(false);
                    await EnsurePreparationAxesStoppedAsync(token, true).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    SetPreparationSnapshot(item, StationPreparationState.Ready, "准备完成", "等待启动生产。");
                }
                token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { stopped = true; }
            catch (Exception error) { failure = error; }
            finally
            {
                if (stopped || failure != null)
                {
                    try { await StopPreparationAxesAsync().ConfigureAwait(false); }
                    catch (Exception stopError)
                    {
                        stopUnconfirmed = true;
                        failure = failure == null ? stopError : new AggregateException(failure, stopError);
                    }
                    if (active != null)
                    {
                        InvalidatePreparationAndDependents(active.StationId, "准备未完成。");
                        SetPreparationSnapshot(active, failure == null ? StationPreparationState.Stopped : StationPreparationState.Failed,
                            failure == null ? "回零已停止" : "准备失败", failure == null ? "检查现场后重新执行工位回零。" : failure.Message);
                    }
                    foreach (IStationPreparation item in selected)
                    {
                        bool waiting;
                        lock (_preparationSync) waiting = _preparationStates[item.StationId].State == StationPreparationState.Waiting;
                        if (waiting) SetPreparationSnapshot(item, StationPreparationState.NotPrepared, "尚未准备", "前序准备未完成。");
                    }
                }
                try { await StopEmergencyStopMonitorAsync().ConfigureAwait(false); }
                catch (Exception monitorError)
                {
                    failure = failure == null ? monitorError : new AggregateException(failure, monitorError);
                    if (active != null)
                    {
                        InvalidatePreparationAndDependents(active.StationId, "准备监控结束异常，请检查原因。");
                        SetPreparationSnapshot(active, StationPreparationState.Failed, "准备失败", monitorError.Message);
                    }
                }
                await _commandLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    bool stoppedAtCompletion = false;
                    lock (_preparationSync)
                    {
                        // 与停止请求在同一把锁中提交最终结果，避免最后一步刚完成就取消时误标成功。
                        if (token.IsCancellationRequested && !stopped && failure == null)
                        {
                            stopped = true;
                            stoppedAtCompletion = true;
                        }
                        _preparationStopUnconfirmed |= stopUnconfirmed;
                        _preparationCancellation = null;
                    }
                    if (stoppedAtCompletion && active != null)
                    {
                        InvalidatePreparationAndDependents(active.StationId, "准备完成确认前收到停止请求。");
                        SetPreparationSnapshot(active, StationPreparationState.Stopped, "回零已停止", "检查现场后重新执行工位回零。");
                    }
                    bool canCompleteRecord;
                    lock (_preparationSync) canCompleteRecord = failure == null && !_preparationStopUnconfirmed && _preparationStopRequests == 0;
                    if (canCompleteRecord && !_alarmService.HasLatchedStopAlarm && State != MachineRunState.Alarmed)
                    {
                        try { CompletePreparationRecord(); }
                        catch (Exception recordError)
                        {
                            failure = new InvalidOperationException("准备安全结束记录保存失败，请联系维护人员。", recordError);
                            if (active != null)
                            {
                                InvalidatePreparationAndDependents(active.StationId, "准备记录未能保存。");
                                SetPreparationSnapshot(active, StationPreparationState.Failed, "准备失败", failure.Message);
                            }
                        }
                    }
                    lock (_preparationSync) _preparing = _preparationStopUnconfirmed || _preparationStopRequests != 0;
                    if (failure != null || _alarmService.HasLatchedStopAlarm || State == MachineRunState.Alarmed)
                        SetStatus(MachineRunState.Alarmed, failure ?? LastError);
                    else SetStatus(stopped ? MachineRunState.ResetRequired : MachineRunState.Ready, null);
                }
                finally { _commandLock.Release(); cancellation.Dispose(); }
                if (failure != null)
                {
                    string source = active == null ? "设备准备" : active.StationName;
                    _alarmService.ReportCondition(PreparationFailedAlarm, source, true, failure.Message);
                    // 这是一次执行失败记录，不伪造传感器条件；重试仍会重新检查真实前提。
                    _alarmService.ReportCondition(PreparationFailedAlarm, source, false, "检查设备准备页，处理原因后复位并重试。");
                }
                RaisePreparationChanged();
            }
            if (failure != null) throw new InvalidOperationException("工位准备未完成：" + failure.Message, failure);
            if (stopped) throw new OperationCanceledException(token);
        }

        /// <summary>生产启动前检查全部资格；首次启动还实时核对姿态，正常暂停续跑不要求回到初始位置。</summary>
        private async Task EnsurePreparationsReadyForStartAsync(CancellationToken token)
        {
            IStationPreparation[] preparations;
            bool productionStarted;
            lock (_preparationSync)
            {
                string missing = string.Join("、", _preparationStates.Values.Where(s => !s.IsPrepared).Select(s => s.StationName));
                if (_preparing || missing.Length != 0)
                    throw new InvalidOperationException("设备尚未准备完成，请先在设备准备页完成工位回零：" + missing);
                preparations = _preparations;
                productionStarted = _preparationProductionStarted;
            }
            if (productionStarted) return;
            foreach (IStationPreparation item in preparations)
            {
                try { await item.ValidateReadyAsync(token).ConfigureAwait(false); }
                catch
                {
                    InvalidatePreparationAndDependents(item.StationId, "启动前现场检查未通过，请重新核对准备条件。");
                    throw;
                }
            }
        }

        /// <summary>给只读页面提供准备资格提示；实时验收只在持有整机命令锁的启动中执行。</summary>
        private string GetPreparationStartBlockReason()
        {
            lock (_preparationSync)
            {
                if (_preparing) return "设备准备仍在执行，请等待完成。";
                string missing = string.Join("、", _preparationStates.Values.Where(s => !s.IsPrepared).Select(s => s.StationName));
                return missing.Length == 0 ? null : "请先在设备准备页完成工位回零：" + missing;
            }
        }

        /// <summary>生产实际取得运行资格后记录会话阶段，避免暂停恢复要求回到开机位置。</summary>
        private void MarkPreparationProductionStarted()
        { lock (_preparationSync) _preparationProductionStarted = true; }

        /// <summary>手动动作或新连接使准备资格失效；不得清空设备载具、产品或生产工步。</summary>
        private void InvalidatePreparations(string reason)
        {
            lock (_preparationSync)
            {
                foreach (IStationPreparation item in _preparations)
                    _preparationStates[item.StationId] = new StationPreparationSnapshot(item.StationId,
                        item.StationName, StationPreparationState.NotPrepared, "需要重新准备", reason);
                // 保留生产历史，避免手动改变机构后重新回零却续跑旧生产工步。
            }
            RaisePreparationChanged();
        }

        /// <summary>指定工位失效时一并撤销依赖它的工位资格，不让旧完成状态跨越变化的前提。</summary>
        private void InvalidatePreparationAndDependents(string stationId, string reason)
        {
            var invalid = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { stationId };
            lock (_preparationSync)
            {
                foreach (IStationPreparation item in _preparations)
                {
                    if (!invalid.Contains(item.StationId) && !item.Dependencies.Any(invalid.Contains)) continue;
                    invalid.Add(item.StationId);
                    _preparationStates[item.StationId] = new StationPreparationSnapshot(item.StationId,
                        item.StationName, StationPreparationState.NotPrepared, "需要重新准备", reason);
                }
            }
            RaisePreparationChanged();
        }

        /// <summary>更新单个工位不可变状态，再通知显示层。</summary>
        private void SetPreparationSnapshot(IStationPreparation item, StationPreparationState state, string action, string detail)
        {
            lock (_preparationSync)
                _preparationStates[item.StationId] = new StationPreparationSnapshot(item.StationId, item.StationName, state, action, detail);
            RaisePreparationChanged();
        }

        /// <summary>阻止准备期间切换入口、复位、断卡或插入手动动作。</summary>
        private void EnsureNoPreparationInProgress(string operation)
        { if (IsPreparing) throw new InvalidOperationException("设备准备仍在执行或停止中，不能" + operation + "。"); }

        /// <summary>准备开始或完成时确认所有轴停稳；状态读取失败不按停止处理。</summary>
        private async Task EnsurePreparationAxesStoppedAsync(CancellationToken token, bool checkAlarm)
        {
            foreach (IAxis axis in _motionCards.Axes)
            {
                AxisStatus status = await axis.ReadStatusAsync(token).ConfigureAwait(false);
                if (status.Moving) throw new InvalidOperationException("逻辑轴“" + axis.Name + "”仍在运动，设备准备不能继续。");
                if (checkAlarm && status.AlarmActive)
                    throw new InvalidOperationException("逻辑轴“" + axis.Name + "”存在报警：" + status.AlarmMessage);
            }
        }

        /// <summary>取消或故障时逐轴请求受控停止，并再次读取停稳结果；停止失败保留明确错误。</summary>
        private async Task StopPreparationAxesAsync()
        {
            Task stopTask;
            lock (_preparationSync)
            {
                if (_preparationStopTask.IsCompleted)
                    _preparationStopTask = Task.Run(() => StopPreparationAxesCoreAsync());
                stopTask = _preparationStopTask;
            }
            if (!ReferenceEquals(await Task.WhenAny(stopTask, Task.Delay(_pauseTimeout)).ConfigureAwait(false), stopTask))
                throw new TimeoutException("设备准备停轴命令尚未结束，请继续保持停止并检查控制器。");
            await stopTask.ConfigureAwait(false);
        }

        /// <summary>向每根轴发起停止并汇总结果，单轴失败不阻止其余轴收到停止请求。</summary>
        private async Task StopPreparationAxesCoreAsync()
        {
            var errors = new List<Exception>();
            Exception[] results = await Task.WhenAll(_motionCards.Axes.Select(StopPreparationAxisAsync)).ConfigureAwait(false);
            errors.AddRange(results.Where(error => error != null));
            try { await EnsurePreparationAxesStoppedAsync(CancellationToken.None, false).ConfigureAwait(false); }
            catch (Exception error) { errors.Add(error); }
            if (errors.Count != 0) throw new AggregateException("设备准备未能确认所有轴停稳。", errors);
        }

        /// <summary>停止一根轴并返回可定位错误，便于其他轴继续完成停止。</summary>
        private static async Task<Exception> StopPreparationAxisAsync(IAxis axis)
        {
            try { await axis.StopAsync(AxisStopMode.Controlled, CancellationToken.None).ConfigureAwait(false); return null; }
            catch (Exception error) { return new InvalidOperationException("准备停止时逻辑轴“" + axis.Name + "”停止失败。", error); }
        }

        /// <summary>检查工位标识与依赖并排序，拒绝缺失、重复和循环依赖。</summary>
        private static IStationPreparation[] SortPreparations(IEnumerable<IStationPreparation> preparations)
        {
            var source = preparations.ToArray();
            var byId = new Dictionary<string, IStationPreparation>(StringComparer.OrdinalIgnoreCase);
            foreach (IStationPreparation item in source)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.StationId) || string.IsNullOrWhiteSpace(item.StationName)
                    || item.Dependencies == null) throw new ArgumentException("工位准备标识、名称或依赖配置无效。");
                if (byId.ContainsKey(item.StationId)) throw new ArgumentException("工位准备标识重复：" + item.StationId);
                byId.Add(item.StationId, item);
            }
            var ordered = new List<IStationPreparation>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (IStationPreparation item in source) VisitPreparation(item, byId, visited, visiting, ordered);
            return ordered.ToArray();
        }

        /// <summary>选中工位时同时加入其传递依赖，再沿已校验的拓扑顺序执行。</summary>
        private IStationPreparation[] SelectPreparations(string stationId)
        {
            if (string.IsNullOrWhiteSpace(stationId)) return _preparations;
            if (!_preparations.Any(item => string.Equals(item.StationId, stationId, StringComparison.OrdinalIgnoreCase)))
                return new IStationPreparation[0];
            var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { stationId };
            for (int index = _preparations.Length - 1; index >= 0; index--)
            {
                IStationPreparation item = _preparations[index];
                if (selected.Contains(item.StationId))
                    foreach (string dependency in item.Dependencies) selected.Add(dependency);
            }
            return _preparations.Where(item => selected.Contains(item.StationId)).ToArray();
        }

        /// <summary>按依赖递归加入工位，同时检查循环和不存在的前序工位。</summary>
        private static void VisitPreparation(IStationPreparation item, IDictionary<string, IStationPreparation> byId,
            ISet<string> visited, ISet<string> visiting, IList<IStationPreparation> ordered)
        {
            if (visited.Contains(item.StationId)) return;
            if (!visiting.Add(item.StationId)) throw new ArgumentException("工位准备存在循环依赖：" + item.StationName);
            foreach (string id in item.Dependencies)
            {
                IStationPreparation required;
                if (string.IsNullOrWhiteSpace(id) || !byId.TryGetValue(id, out required))
                    throw new ArgumentException("工位“" + item.StationName + "”依赖的准备项不存在：" + id);
                VisitPreparation(required, byId, visited, visiting, ordered);
            }
            visiting.Remove(item.StationId); visited.Add(item.StationId); ordered.Add(item);
        }

        /// <summary>通知外部准备快照变化；调用方不持有准备状态锁。</summary>
        private void RaisePreparationChanged()
        { var handler = PreparationChanged; if (handler != null) handler(this, EventArgs.Empty); }

        /// <summary>同步接收后台步骤进度，避免异步投递的旧进度覆盖完成状态。</summary>
        private sealed class PreparationProgressSink : IProgress<StationPreparationProgress>
        {
            /// <summary>将进度提交给当前 Runtime 的回调。</summary>
            private readonly Action<StationPreparationProgress> _report;
            /// <summary>创建仅服务当前准备操作的进度接收器。</summary>
            internal PreparationProgressSink(Action<StationPreparationProgress> report) { _report = report; }
            /// <summary>立即提交当前步骤说明。</summary>
            public void Report(StationPreparationProgress value) { _report(value); }
        }
    }
}
