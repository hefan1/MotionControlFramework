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
            _document = _recoveryStore.Load(out warningMessage);
            if (_document == null)
            {
                if (!string.IsNullOrWhiteSpace(warningMessage) && RecoveryBlockReason == null) RecoveryBlockReason = warningMessage;
                if (RecoveryBlockReason != null) warningMessage = RecoveryBlockReason;
                return;
            }

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
                warningMessage = RecoveryBlockReason;
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
                if (_ownsRunMarker && string.IsNullOrWhiteSpace(RecoveryBlockReason))
                {
                    _recoveryStore.ClearAutomaticRunMarker();
                    _ownsRunMarker = false;
                }
            }
        }

        /// <summary>在自动流程进入运行状态前同步写入独立标记，不覆盖既有恢复记录。</summary>
        public void InvalidateBeforeAutomaticRun() { InvalidateBeforeAutomaticRun(null); }

        /// <summary>保留正常暂停的资源标记；隔离或未知时拒绝继续运行。</summary>
        public void InvalidateBeforeAutomaticRun(MachineRuntime runtime)
        {
            lock (_runRecordSync)
            {
                if (!string.IsNullOrWhiteSpace(RecoveryBlockReason)) throw new InvalidOperationException(RecoveryBlockReason);
                TaskResourceRecoveryState resourceState = runtime == null ? TaskResourceRecoveryState.Clear : runtime.GetTaskResourceRecoveryState();
                if (resourceState == TaskResourceRecoveryState.Blocked || resourceState == TaskResourceRecoveryState.Unknown)
                    throw new InvalidOperationException("任务资源尚待确认或状态未知，不能启动；请联系维护人员核对现场。");
                if (_ownsRunMarker)
                {
                    if (_pausedWithHeldResources && runtime != null)
                    {
                        _pausedWithHeldResources = false;
                        return;
                    }
                    throw new InvalidOperationException("上次运行尚未完成安全暂停和恢复记录保存，不能再次启动。");
                }
                if (resourceState == TaskResourceRecoveryState.Held)
                    throw new InvalidOperationException("任务仍持有资源但运行标记不属于当前会话，不能启动。");
                _recoveryStore.MarkAutomaticRun();
                _ownsRunMarker = true;
            }
        }

        /// <summary>准备动作开始前使用同一持久标记，意外退出后保留旧恢复证据。</summary>
        public void MarkBeforePreparation() { InvalidateBeforeAutomaticRun(); }

        /// <summary>工位准备要求任务资源完全释放，不能借回零跨过待确认区域。</summary>
        public void MarkBeforePreparation(MachineRuntime runtime)
        {
            if (runtime != null && runtime.GetTaskResourceRecoveryState() != TaskResourceRecoveryState.Clear)
                throw new InvalidOperationException("任务资源仍被占用、待确认或状态未知，不能执行工位准备。");
            InvalidateBeforeAutomaticRun(runtime);
        }

        /// <summary>准备成功或受控停稳后保存当前变量，并撤销可能过时的旧流程断点。</summary>
        public void SaveAfterSafePreparation()
        {
            lock (_runRecordSync)
            {
                if (!_ownsRunMarker || !string.IsNullOrWhiteSpace(RecoveryBlockReason))
                    throw new InvalidOperationException("准备记录尚未建立或恢复信息不可信，不能解除标记。");
                var document = CreateDocumentWithCurrentVariables(null);
                document.CanResume = false;
                document.ResumeBlockedReason = "设备准备已改变现场状态；旧工位断点不能直接续跑，请按设备工艺核对。";
                SaveDocument(document);
                _recoveryStore.ClearAutomaticRunMarker();
                _ownsRunMarker = false;
            }
        }

        /// <summary>软件关闭前保存最新变量值；仅在整机已暂停时创建新的流程续跑点。</summary>
        public void SaveForShutdown(MachineRuntime runtime, bool machineIsPaused)
        {
            lock (_runRecordSync)
            {
                if (machineIsPaused && (runtime == null || runtime.State != MachineRunState.Paused || runtime.IsPreparing))
                    throw new InvalidOperationException("设备尚未确认暂停，不能保存暂停记录。");
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
                if (_ownsRunMarker && machineIsPaused && string.IsNullOrWhiteSpace(RecoveryBlockReason))
                {
                    _recoveryStore.ClearAutomaticRunMarker();
                    _ownsRunMarker = false;
                }
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
