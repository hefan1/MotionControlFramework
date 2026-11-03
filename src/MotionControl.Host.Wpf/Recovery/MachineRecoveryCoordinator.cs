using System;
using System.Collections.Generic;
using System.Linq;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.Framework.Runtime.Machine;
using MotionControl.Framework.Runtime.Variables;

namespace MotionControl.Host.Wpf.Recovery
{
    /// <summary>协调单机变量恢复、工位步骤恢复、暂停保存和重新启动时的断点失效。</summary>
    public sealed class MachineRecoveryCoordinator
    {
        /// <summary>旧恢复文件没有记录原因时，加载和工位检查共用的说明。</summary>
        private const string MissingResumePointWarning =
            "已恢复设备变量；当前没有有效的工位流程续跑点，请确认现场状态后再启动，流程将从当前程序的初始步骤开始。";

        /// <summary>负责设备恢复 JSON 文件读写和配置校验。</summary>
        private readonly MachineRecoveryStore _recoveryStore;

        /// <summary>与设备变量监控页面和工艺流程共用的运行变量表。</summary>
        private readonly DeviceVariableStore _variableStore;

        /// <summary>当前设备编号，用于创建本机恢复文档。</summary>
        private readonly string _machineId;

        /// <summary>当前进程加载或最近保存的设备恢复文档。</summary>
        private MachineRecoveryDocument _document;

        /// <summary>本次加载已报告的恢复限制；仅供首次工位恢复检查去除重复提示。</summary>
        private string _loadWarningMessage;

        /// <summary>仅标记由当前进程创建；重启后遗留标记不能自动清除。</summary>
        private bool _ownsRunMarker;
        /// <summary>正常暂停时仍占用资源；仅本会话允许携带原标记继续。</summary>
        private bool _pausedWithHeldResources;
        /// <summary>任务运行标记在完整收尾屏障建立前不能因会话释放而被清除。</summary>
        private bool _taskRunNeedsSettlement;
        /// <summary>首次建立当前任务运行标记时的会话身份；跨会话不得借用标记。</summary>
        private object _taskRunSession;
        /// <summary>标记所属运行时实例；设备重新连接后不能复用旧会话状态。</summary>
        private MachineRuntime _taskRunRuntime;
        /// <summary>生产标记所属 C# 工位流程运行时。</summary>
        private MachineRuntime _stationFlowRuntime;
        /// <summary>生产标记所属连接周期；断卡重连不能复用旧周期的现场状态。</summary>
        private Guid? _stationFlowConnectionSession;
        /// <summary>当前运行标记是否由本进程的设备准备创建，而非生产任务。</summary>
        private bool _markerFromPreparation;
        /// <summary>准备标记所属的 Runtime，重连或新进程不得沿用。</summary>
        private MachineRuntime _preparationRunRuntime;
        /// <summary>取得当前任务会话身份，供同会话续跑核对。</summary>
        private Func<object> _currentTaskSession;
        /// <summary>确认指定会话的暂停或故障收尾是否已结算。</summary>
        private Func<object, bool> _isTaskSessionSettled;
        /// <summary>串行化运行标记及检查点的创建、保存和移除。</summary>
        private readonly object _runRecordSync = new object();

        /// <summary>恢复数据不可信时的运行拦截原因；空值表示文件不存在或恢复完整。</summary>
        public string RecoveryBlockReason { get; private set; }

        /// <summary>创建绑定到单机配置和运行变量表的恢复协调器。</summary>
        public MachineRecoveryCoordinator(
            MachineRecoveryStore recoveryStore,
            DeviceVariableStore variableStore,
            string machineId)
        {
            _recoveryStore = recoveryStore
                ?? throw new ArgumentNullException(nameof(recoveryStore));
            _variableStore = variableStore
                ?? throw new ArgumentNullException(nameof(variableStore));
            if (string.IsNullOrWhiteSpace(machineId))
            {
                throw new ArgumentException("设备编号不能为空。", nameof(machineId));
            }

            _machineId = machineId;
        }

        /// <summary>返回恢复文件是否记录初始化已完成；设备流程仍须重新检查现场硬件状态。</summary>
        public bool HasCompletedInitializationCheckpoint
        {
            get { return _document != null && _document.EquipmentInitializationCompleted; }
        }

        /// <summary>安装只读的任务会话身份及收尾查询；查询失败时禁止续跑。</summary>
        public void SetTaskSessionSettlementQueries(Func<object> currentSession, Func<object, bool> isSettled)
        {
            if (currentSession == null) throw new ArgumentNullException(nameof(currentSession));
            if (isSettled == null) throw new ArgumentNullException(nameof(isSettled));
            lock (_runRecordSync)
            {
                _currentTaskSession = currentSession;
                _isTaskSessionSettled = isSettled;
            }
        }

        /// <summary>仅允许标记所属的同一任务会话在完成收尾后继续；不删除运行标记。</summary>
        private bool CanResumeCurrentTaskSession(MachineRuntime runtime)
        {
            if (!_taskRunNeedsSettlement || _taskRunSession == null || runtime == null
                || !ReferenceEquals(_taskRunRuntime, runtime)
                || !runtime.IsTaskEngineControlClaimed || runtime.State != MachineRunState.Ready
                || _currentTaskSession == null || _isTaskSessionSettled == null)
                return false;
            try
            {
                object current = _currentTaskSession();
                return ReferenceEquals(current, _taskRunSession) && _isTaskSessionSettled(current);
            }
            catch (Exception error)
            {
                System.Diagnostics.Trace.TraceError("核查任务会话收尾失败，继续保留运行保护：" + error);
                return false;
            }
        }

        /// <summary>只允许本运行时已确认收尾的准备标记在本进程复用。</summary>
        private bool CanReusePreparationMarker(MachineRuntime runtime)
        {
            return _markerFromPreparation && runtime != null
                && ReferenceEquals(_preparationRunRuntime, runtime)
                && runtime.IsPreparationRecordReusable;
        }

        /// <summary>仅同一连接周期的工位流程确认完成暂停和报警收尾后可续跑。</summary>
        private bool CanResumeStationFlows(MachineRuntime runtime)
        {
            return runtime != null && ReferenceEquals(_stationFlowRuntime, runtime)
                && runtime.ControlOwner == MachineControlOwner.StationFlows
                && runtime.State == MachineRunState.Ready
                && _stationFlowConnectionSession.HasValue
                && runtime.ConnectionSessionId == _stationFlowConnectionSession
                && runtime.IsStationFlowSettlementConfirmed;
        }

        /// <summary>绑定当前生产入口和连接周期，避免旧任务会话或断卡重连复用标记。</summary>
        private void BindProductionSession(MachineRuntime runtime)
        {
            _taskRunNeedsSettlement = runtime != null && runtime.IsTaskEngineControlClaimed;
            _taskRunSession = _taskRunNeedsSettlement ? _currentTaskSession?.Invoke() : null;
            _taskRunRuntime = _taskRunNeedsSettlement ? runtime : null;
            _stationFlowRuntime = runtime != null && runtime.ControlOwner == MachineControlOwner.StationFlows ? runtime : null;
            _stationFlowConnectionSession = _stationFlowRuntime == null ? null : runtime.ConnectionSessionId;
        }

        /// <summary>加载运行值，并按设备变量定义校验是否还能使用旧流程断点。</summary>
        public void Load(out string warningMessage)
        {
            _loadWarningMessage = null;
            RecoveryBlockReason = null;
            try
            {
                if (_recoveryStore.HasUnfinishedAutomaticRun())
                    RecoveryBlockReason = "上次设备运行或准备动作未确认安全停止，设备与在制品状态未知。请联系维护人员核对现场和恢复记录；禁止直接清除运行标记。";
            }
            catch (Exception error)
            {
                RecoveryBlockReason = "无法核查上次运行标记，请联系维护人员检查存储：" + error.Message;
            }
            try
            {
                string resourceWarning = _recoveryStore.InspectTaskResources();
                if (!string.IsNullOrWhiteSpace(resourceWarning))
                    RecoveryBlockReason = string.IsNullOrWhiteSpace(RecoveryBlockReason) ? resourceWarning : RecoveryBlockReason + Environment.NewLine + resourceWarning;
            }
            catch (Exception error)
            {
                System.Diagnostics.Trace.TraceError("核查任务资源记录失败，设备状态未知：" + error);
                string resourceWarning = "任务资源记录损坏或无法读取，设备状态未知。请联系维护人员检查现场和存储。";
                RecoveryBlockReason = string.IsNullOrWhiteSpace(RecoveryBlockReason) ? resourceWarning : RecoveryBlockReason + Environment.NewLine + resourceWarning;
            }
            _document = _recoveryStore.Load(out warningMessage);
            if (_document == null)
            {
                if (!string.IsNullOrWhiteSpace(warningMessage) && RecoveryBlockReason == null) RecoveryBlockReason = warningMessage;
                if (RecoveryBlockReason != null) warningMessage = RecoveryBlockReason;
                return;
            }

            // 签名不匹配既可能是配置变化，也可能是旧版把运行记录纳入签名。
            // 无法证明兼容时保留原件并阻断，不能在退出时用新签名覆盖证据。
            if (_document.ConfigurationSignatureMismatch && RecoveryBlockReason == null)
                RecoveryBlockReason = warningMessage;

            var validCheckpoints = _document.DeviceVariables
                .Where(checkpoint => checkpoint != null)
                .ToList();
            if (validCheckpoints.Count != _document.DeviceVariables.Count)
            {
                _document.CanResume = false;
                _document.ResumeBlockedReason = "恢复文件包含空的设备变量记录，流程续跑已关闭。";
                warningMessage = _document.ResumeBlockedReason;
                if (RecoveryBlockReason == null) RecoveryBlockReason = warningMessage;
            }

            var savedVariables = validCheckpoints
                .Select(checkpoint => new DeviceVariableSnapshot(
                    checkpoint.Name,
                    checkpoint.DataType,
                    checkpoint.CurrentValue,
                    checkpoint.HasValue,
                    checkpoint.LastUpdated,
                    string.Empty,
                    false))
                .ToList();
            IReadOnlyList<string> variableWarnings = _variableStore.RestoreCurrentValues(savedVariables);
            var missingNames = _variableStore.GetSnapshot()
                .Where(item => !savedVariables.Any(saved => string.Equals(saved.Name, item.Name, StringComparison.OrdinalIgnoreCase)))
                .Select(item => item.Name).ToList();
            var recoveryWarnings = variableWarnings.ToList();
            if (missingNames.Count > 0)
                recoveryWarnings.Add("恢复文件缺少当前设备变量：" + string.Join("、", missingNames) + "。");
            if (recoveryWarnings.Count > 0)
            {
                _document.CanResume = false;
                _document.EquipmentInitializationCompleted = false;
                _document.ResumeBlockedReason =
                    "部分设备变量未能按当前定义恢复；为避免在不完整数据上续跑，流程恢复已关闭。";
                warningMessage = _document.ResumeBlockedReason
                    + Environment.NewLine + string.Join(Environment.NewLine, recoveryWarnings);
                if (RecoveryBlockReason == null) RecoveryBlockReason = warningMessage;
            }
            else if (!_document.CanResume
                && string.IsNullOrWhiteSpace(warningMessage)
                && _document.StationFlows.Count > 0)
            {
                warningMessage = string.IsNullOrWhiteSpace(_document.ResumeBlockedReason)
                    ? MissingResumePointWarning
                    : _document.ResumeBlockedReason;
            }

            if (RecoveryBlockReason != null)
            {
                _document.CanResume = false;
                _document.EquipmentInitializationCompleted = false;
                // 变量恢复的具体错误也要保留，方便维护人员定位；阻断原因不因细节而丢失。
                warningMessage = string.IsNullOrWhiteSpace(warningMessage)
                    || string.Equals(warningMessage, RecoveryBlockReason, StringComparison.Ordinal)
                    ? RecoveryBlockReason
                    : warningMessage + Environment.NewLine + RecoveryBlockReason;
            }
            _loadWarningMessage = warningMessage;
        }

        /// <summary>核对全部工位步骤后一次性恢复；首次检查不重复报告加载阶段已说明的原因。</summary>
        public bool TryRestoreStationFlows(IStationFlow[] stationFlows, out string warningMessage)
        {
            string loadWarningMessage = _loadWarningMessage;
            _loadWarningMessage = null;
            warningMessage = string.Empty;
            if (_document == null)
            {
                return false;
            }

            if (!_document.CanResume)
            {
                if (_document.StationFlows.Count > 0)
                {
                    warningMessage = string.IsNullOrWhiteSpace(_document.ResumeBlockedReason)
                        ? MissingResumePointWarning
                        : _document.ResumeBlockedReason;
                    if (string.Equals(loadWarningMessage, warningMessage, StringComparison.Ordinal)
                        || (!string.IsNullOrWhiteSpace(loadWarningMessage)
                            && loadWarningMessage.StartsWith(warningMessage + Environment.NewLine, StringComparison.Ordinal)))
                    {
                        warningMessage = string.Empty;
                    }
                }

                return false;
            }

            if (stationFlows == null || stationFlows.Length == 0
                || _document.StationFlows.Count != stationFlows.Length)
            {
                return BlockFlowRecovery(
                    "当前工位列表与上次暂停时不同；流程不会自动续跑。",
                    out warningMessage);
            }

            var checkpoints = new Dictionary<string, StationFlowCheckpoint>(StringComparer.OrdinalIgnoreCase);
            foreach (StationFlowCheckpoint checkpoint in _document.StationFlows)
            {
                if (checkpoint == null
                    || string.IsNullOrWhiteSpace(checkpoint.StationName)
                    || checkpoints.ContainsKey(checkpoint.StationName))
                {
                    return BlockFlowRecovery(
                        "恢复文件中的工位名称为空或重复；流程不会自动续跑。",
                        out warningMessage);
                }

                checkpoints.Add(checkpoint.StationName, checkpoint);
            }

            var recoverableFlows = new List<IRecoverableStationFlow>();
            var stepNumbers = new List<int>();
            foreach (IStationFlow stationFlow in stationFlows)
            {
                StationFlowCheckpoint checkpoint;
                IRecoverableStationFlow recoverableFlow = stationFlow as IRecoverableStationFlow;
                if (stationFlow == null
                    || recoverableFlow == null
                    || !checkpoints.TryGetValue(stationFlow.StationName, out checkpoint)
                    || !recoverableFlow.CanRestoreStep(checkpoint.StepNumber))
                {
                    string stationName = stationFlow == null ? "未知工位" : stationFlow.StationName;
                    return BlockFlowRecovery(
                        "工位 '" + stationName + "' 不支持恢复到保存的步骤；流程不会自动续跑。",
                        out warningMessage);
                }

                recoverableFlows.Add(recoverableFlow);
                stepNumbers.Add(checkpoint.StepNumber);
            }

            for (int index = 0; index < recoverableFlows.Count; index++)
            {
                recoverableFlows[index].RestoreStep(stepNumbers[index]);
            }

            return true;
        }

        /// <summary>在整机受控暂停后保存当前有效变量值和各工位步骤。</summary>
        public void SavePausedCheckpoint(MachineRuntime runtime)
        {
            lock (_runRecordSync)
            {
                if (runtime == null || runtime.State != MachineRunState.Paused || runtime.IsPreparing)
                    throw new InvalidOperationException("只有设备已受控暂停且准备已退出，才能保存暂停记录。");
                // 任务侧停止回调与故障分类尚无完整屏障，即使当前资源为空也不能
                // 证明所有在途动作已安全结束。保留原记录及跨进程运行证据。
                if (_markerFromPreparation || _taskRunNeedsSettlement || runtime.IsTaskEngineControlClaimed)
                {
                    _pausedWithHeldResources = false;
                    return;
                }
                // 尚有持有或隔离资源时保留运行前标记和旧记录，重启后不能把现场当成空区域。
                TaskResourceRecoveryState resourceState = runtime.GetTaskResourceRecoveryState();
                if (resourceState != TaskResourceRecoveryState.Clear)
                {
                    _pausedWithHeldResources = _ownsRunMarker && resourceState == TaskResourceRecoveryState.Held;
                    return;
                }
                _pausedWithHeldResources = false;
                var document = CreateDocumentWithCurrentVariables(runtime);
                CaptureStationFlowCheckpoints(document, runtime);
                SaveDocument(document);
                // 快照可供维护核对，但暂停不证明现场输出和在制品安全，仍保留独立运行标记。
            }
        }

        /// <summary>在自动流程进入运行状态前同步写入独立标记，不覆盖既有恢复记录。</summary>
        public void InvalidateBeforeAutomaticRun() { InvalidateBeforeAutomaticRun(null); }

        /// <summary>保留正常暂停的资源标记；隔离或未知时拒绝继续运行。</summary>
        public string GetAutomaticStartBlockReason(MachineRuntime runtime)
        {
            lock (_runRecordSync)
            {
                if (!string.IsNullOrWhiteSpace(RecoveryBlockReason)) return RecoveryBlockReason;
                TaskResourceRecoveryState resources = runtime == null ? TaskResourceRecoveryState.Clear : runtime.GetTaskResourceRecoveryState();
                if (resources == TaskResourceRecoveryState.Blocked || resources == TaskResourceRecoveryState.Unknown)
                    return "任务资源尚待确认或状态未知，不能启动；请联系维护人员核对现场。";
                if (_ownsRunMarker && !(_pausedWithHeldResources && runtime != null)
                    && !CanResumeCurrentTaskSession(runtime)
                    && !CanResumeStationFlows(runtime)
                    && !(CanReusePreparationMarker(runtime) && runtime.State == MachineRunState.Ready && runtime.IsPrepared))
                    return "上次运行尚未确认安全收尾，不能再次启动。请停止当前任务或设备，并联系维护人员核查设备、在制品和恢复记录。";
                if (!_ownsRunMarker && resources == TaskResourceRecoveryState.Held)
                    return "任务仍持有资源但运行标记不属于当前会话，不能启动。";
                return null;
            }
        }

        /// <summary>持久化运行前复用相同的保护原因，界面提示不替代此处的最终检查。</summary>
        public void InvalidateBeforeAutomaticRun(MachineRuntime runtime)
        {
            lock (_runRecordSync)
            {
                string reason = GetAutomaticStartBlockReason(runtime);
                if (reason != null) throw new InvalidOperationException(reason);
                if (_ownsRunMarker)
                {
                    if (CanResumeCurrentTaskSession(runtime)) return;
                    if (CanResumeStationFlows(runtime)) return;
                    if (CanReusePreparationMarker(runtime) && runtime.State == MachineRunState.Ready && runtime.IsPrepared)
                    {
                        // 准备标记成为生产标记；任务执行必须重新绑定当前任务会话。
                        _markerFromPreparation = false;
                        _preparationRunRuntime = null;
                        BindProductionSession(runtime);
                        return;
                    }
                    if (_pausedWithHeldResources && runtime != null)
                    {
                        _pausedWithHeldResources = false;
                        return;
                    }
                    throw new InvalidOperationException("上次运行尚未确认安全收尾，不能再次启动。请停止当前任务或设备，并联系维护人员核查设备、在制品和恢复记录。");
                }
                _recoveryStore.MarkAutomaticRun();
                _ownsRunMarker = true;
                _markerFromPreparation = false;
                BindProductionSession(runtime);
            }
        }

        /// <summary>准备动作开始前使用同一持久标记，意外退出后保留旧恢复证据。</summary>
        public void MarkBeforePreparation() { MarkBeforePreparation(null); }

        /// <summary>工位准备要求任务资源完全释放，不能借回零跨过待确认区域。</summary>
        public void MarkBeforePreparation(MachineRuntime runtime)
        {
            lock (_runRecordSync)
            {
                if (!string.IsNullOrWhiteSpace(RecoveryBlockReason)) throw new InvalidOperationException(RecoveryBlockReason);
                if (runtime != null && runtime.GetTaskResourceRecoveryState() != TaskResourceRecoveryState.Clear)
                    throw new InvalidOperationException("任务资源仍被占用、待确认或状态未知，不能执行工位准备。");
                if (_ownsRunMarker)
                {
                    if (CanReusePreparationMarker(runtime)) return;
                    throw new InvalidOperationException("已有未确认的生产或准备记录，不能执行工位准备。");
                }
                _recoveryStore.MarkAutomaticRun();
                _ownsRunMarker = true;
                _markerFromPreparation = true;
                _preparationRunRuntime = runtime;
            }
        }

        /// <summary>准备收尾后保存变量并撤销旧断点；运行标记保留至进程结束。</summary>
        public void SaveAfterSafePreparation()
        {
            lock (_runRecordSync)
            {
                if (!_ownsRunMarker || !_markerFromPreparation || !string.IsNullOrWhiteSpace(RecoveryBlockReason))
                    throw new InvalidOperationException("准备记录尚未建立或恢复信息不可信，不能保存准备结果。");
                var document = CreateDocumentWithCurrentVariables(null);
                document.CanResume = false;
                document.ResumeBlockedReason = "设备准备已改变现场状态；旧工位断点不能直接续跑，请按设备工艺核对。";
                SaveDocument(document);
            }
        }

        /// <summary>软件关闭前保存最新变量值；仅在整机已暂停时创建新的流程续跑点。</summary>
        public void SaveForShutdown(MachineRuntime runtime, bool machineIsPaused)
        {
            lock (_runRecordSync)
            {
                if (machineIsPaused && (runtime == null || runtime.State != MachineRunState.Paused || runtime.IsPreparing))
                    throw new InvalidOperationException("设备尚未确认暂停，不能保存暂停记录。");
                if (_markerFromPreparation || _taskRunNeedsSettlement || (runtime != null && runtime.IsTaskEngineControlClaimed)) return;
                if (runtime != null && runtime.GetTaskResourceRecoveryState() != TaskResourceRecoveryState.Clear) return;
                // 未完成安全暂停时保留独立标记和原记录，不能把当前不确定状态写成可恢复。
                if (_ownsRunMarker && !machineIsPaused) return;
                var document = CreateDocumentWithCurrentVariables(runtime);
                if (machineIsPaused)
                {
                    CaptureStationFlowCheckpoints(document, runtime);
                }
                else
                {
                    PreserveStationFlowCheckpoints(document);
                }

                SaveDocument(document);
                // 保存停机快照不能代替现场状态核对，保留运行标记供下次启动检查。
            }
        }

        /// <summary>创建包含当前全部设备变量状态的新恢复文档。</summary>
        private MachineRecoveryDocument CreateDocumentWithCurrentVariables(MachineRuntime runtime)
        {
            var document = new MachineRecoveryDocument
            {
                MachineId = _machineId,
                EquipmentInitializationCompleted = runtime != null
                    && runtime.EquipmentInitializationCompleted,
                DeviceVariables = _variableStore.GetSnapshot()
                    .Select(snapshot => new DeviceVariableCheckpoint
                    {
                        Name = snapshot.Name,
                        DataType = snapshot.DataType,
                        CurrentValue = snapshot.CurrentValue,
                        HasValue = snapshot.HasValue,
                        LastUpdated = snapshot.LastUpdated
                    })
                    .ToList()
            };
            return document;
        }

        /// <summary>保存所有可恢复工位的步骤编号；任何工位不支持时关闭整机自动续跑。</summary>
        private static void CaptureStationFlowCheckpoints(
            MachineRecoveryDocument document,
            MachineRuntime runtime)
        {
            bool canResume = runtime != null && runtime.StationFlows.Count > 0;
            string blockedReason = canResume
                ? string.Empty
                : "当前没有配置可恢复的工位流程。";

            if (runtime != null && runtime.HasPendingEquipmentInitialization)
            {
                canResume = false;
                document.EquipmentInitializationCompleted = false;
                blockedReason = "设备启动初始化尚未完成；变量已保存，流程将在现场确认后重新初始化。";
            }

            if (runtime != null)
            {
                foreach (IStationFlow stationFlow in runtime.StationFlows)
                {
                    IRecoverableStationFlow recoverableFlow = stationFlow as IRecoverableStationFlow;
                    if (recoverableFlow == null || recoverableFlow.RecoveryStepNumber <= 0)
                    {
                        canResume = false;
                        blockedReason = "工位 '" + stationFlow.StationName
                            + "' 未显式提供有效的流程恢复步骤。";
                        continue;
                    }

                    document.StationFlows.Add(new StationFlowCheckpoint
                    {
                        StationName = stationFlow.StationName,
                        StepNumber = recoverableFlow.RecoveryStepNumber
                    });
                }
            }

            document.CanResume = canResume;
            document.ResumeBlockedReason = blockedReason;
        }

        /// <summary>关闭时保留旧步骤；配置变化后不再允许按旧步骤续跑。</summary>
        private void PreserveStationFlowCheckpoints(MachineRecoveryDocument document)
        {
            if (_document == null)
            {
                document.CanResume = false;
                document.ResumeBlockedReason = "尚无受控暂停时保存的流程断点。";
                return;
            }

            bool allCheckpointsValid = true;
            foreach (StationFlowCheckpoint checkpoint in _document.StationFlows)
            {
                if (checkpoint == null)
                {
                    allCheckpointsValid = false;
                    continue;
                }

                document.StationFlows.Add(new StationFlowCheckpoint
                {
                    StationName = checkpoint.StationName,
                    StepNumber = checkpoint.StepNumber
                });
            }

            bool configurationMatches = string.Equals(
                _document.ConfigurationSignature,
                _recoveryStore.GetConfigurationSignature(),
                StringComparison.OrdinalIgnoreCase);
            document.EquipmentInitializationCompleted =
                _document.EquipmentInitializationCompleted && configurationMatches;
            document.CanResume = _document.CanResume && configurationMatches && allCheckpointsValid;
            document.ResumeBlockedReason = document.CanResume
                ? _document.ResumeBlockedReason
                : !allCheckpointsValid
                    ? "恢复文件包含无效工位记录，流程不会自动续跑。"
                    : string.IsNullOrWhiteSpace(_document.ResumeBlockedReason)
                        ? "设备配置发生变化；流程不会按旧步骤自动续跑。"
                        : _document.ResumeBlockedReason;
        }

        /// <summary>记录阻止续跑的原因，并保留变量恢复结果供维护人员查看。</summary>
        private bool BlockFlowRecovery(string reason, out string warningMessage)
        {
            _document.CanResume = false;
            _document.ResumeBlockedReason = reason;
            warningMessage = reason;
            return false;
        }

        /// <summary>先成功写入恢复文件，再替换当前进程中的恢复文档。</summary>
        private void SaveDocument(MachineRecoveryDocument document)
        {
            // 不用缺损恢复结果覆盖原证据；修复必须由现场人员明确处理。
            if (!string.IsNullOrWhiteSpace(RecoveryBlockReason)) return;
            _recoveryStore.Save(document);
            _document = document;
        }
    }
}
