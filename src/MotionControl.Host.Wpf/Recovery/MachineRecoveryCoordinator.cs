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
        /// <summary>负责设备恢复 JSON 文件读写和配置校验。</summary>
        private readonly MachineRecoveryStore _recoveryStore;

        /// <summary>与设备变量监控页面和工艺流程共用的运行变量表。</summary>
        private readonly DeviceVariableStore _variableStore;

        /// <summary>当前设备编号，用于创建本机恢复文档。</summary>
        private readonly string _machineId;

        /// <summary>当前进程加载或最近保存的设备恢复文档。</summary>
        private MachineRecoveryDocument _document;

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
            _document = _recoveryStore.Load(out warningMessage);
            if (_document == null)
            {
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
            if (variableWarnings.Count > 0)
            {
                _document.CanResume = false;
                _document.EquipmentInitializationCompleted = false;
                _document.ResumeBlockedReason =
                    "部分设备变量未能按当前定义恢复；为避免在不完整数据上续跑，流程恢复已关闭。";
                warningMessage = _document.ResumeBlockedReason
                    + Environment.NewLine + string.Join(Environment.NewLine, variableWarnings);
            }
            else if (!_document.CanResume
                && string.IsNullOrWhiteSpace(warningMessage)
                && _document.StationFlows.Count > 0)
            {
                warningMessage = string.IsNullOrWhiteSpace(_document.ResumeBlockedReason)
                    ? "已恢复设备变量；当前没有有效的工位流程续跑点，请确认现场状态后再启动。"
                    : _document.ResumeBlockedReason;
            }
        }

        /// <summary>核对全部工位步骤后一次性恢复，任何工位不匹配时都不做部分续跑。</summary>
        public bool TryRestoreStationFlows(IStationFlow[] stationFlows, out string warningMessage)
        {
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
                        ? "已恢复设备变量，但工位没有有效的续跑点；流程将从当前程序的初始步骤开始。"
                        : _document.ResumeBlockedReason;
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
            var document = CreateDocumentWithCurrentVariables(runtime);
            CaptureStationFlowCheckpoints(document, runtime);
            SaveDocument(document);
        }

        /// <summary>自动流程真正进入运行状态前使旧暂停断点失效，防止异常退出时使用过期步骤。</summary>
        public void InvalidateBeforeAutomaticRun()
        {
            var document = CreateDocumentWithCurrentVariables(null);
            document.CanResume = false;
            document.ResumeBlockedReason = "自动流程已重新启动；上次暂停断点已失效，需要再次暂停后才能生成新断点。";
            SaveDocument(document);
        }

        /// <summary>软件关闭前保存最新变量值；仅在整机已暂停时创建新的流程续跑点。</summary>
        public void SaveForShutdown(MachineRuntime runtime, bool machineIsPaused)
        {
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
            _recoveryStore.Save(document);
            _document = document;
        }
    }
}
