using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.Framework.Runtime.Variables;
using MotionControl.Host.Wpf.Recovery;

namespace MotionControl.ZMotionSimulatorCheck
{
    /// <summary>离屏检查设备变量和受控暂停恢复文件的跨进程保存行为。</summary>
    internal static class MachineRecoveryChecks
    {
        /// <summary>检查值和更新时间恢复、类型变化保护以及配置变化后的流程续跑拦截。</summary>
        public static Task RunAsync()
        {
            CheckDeviceVariableValuesRestoreByNameAndType();
            CheckRecoveryFileRoundTripAndConfigurationChange();
            CheckCoordinatorRestoresOnlyKnownStepsAndInvalidatesOnStart();
            return Task.CompletedTask;
        }

        /// <summary>确认变量当前值、有效标记和更新时间跨新 Store 实例恢复。</summary>
        private static void CheckDeviceVariableValuesRestoreByNameAndType()
        {
            var definitions = new[]
            {
                CreateDefinition("Vision.OffsetX", "Number", "0", true),
                CreateDefinition("Vision.Result", "String", null, false),
                CreateDefinition("Product.Count", "UInt32", "1", true)
            };
            var originalStore = new DeviceVariableStore(definitions);
            originalStore.SetNumber("Vision.OffsetX", 1.25);
            originalStore.SetString("Vision.Result", "OK");
            originalStore.SetUInt32("Product.Count", 4294967295);
            IReadOnlyList<DeviceVariableSnapshot> savedValues = originalStore.GetSnapshot();

            var restartedStore = new DeviceVariableStore(definitions);
            IReadOnlyList<string> restoreWarnings = restartedStore.RestoreCurrentValues(savedValues);
            Ensure(restoreWarnings.Count == 0, "相同名称和类型的变量恢复时出现警告。");
            Ensure(restartedStore.GetNumber("Vision.OffsetX") == 1.25, "小数变量没有恢复原运行值。");
            Ensure(restartedStore.GetString("Vision.Result") == "OK", "字符串变量没有恢复原运行值。");
            Ensure(restartedStore.GetUInt32("Product.Count") == uint.MaxValue, "UInt32 最大值没有准确恢复。");

            DeviceVariableSnapshot savedNumber = savedValues.Single(
                snapshot => snapshot.Name == "Vision.OffsetX");
            DeviceVariableSnapshot restoredNumber = restartedStore.GetSnapshot().Single(
                snapshot => snapshot.Name == "Vision.OffsetX");
            Ensure(
                restoredNumber.LastUpdated == savedNumber.LastUpdated,
                "变量最后更新时间没有一并恢复。");

            var changedDefinitions = new[]
            {
                CreateDefinition("Vision.OffsetX", "String", "配置初值", true),
                CreateDefinition("Vision.Result", "String", null, false),
                CreateDefinition("Product.Count", "UInt32", "1", true),
                CreateDefinition("New.Value", "Boolean", "true", false)
            };
            var changedStore = new DeviceVariableStore(changedDefinitions);
            IReadOnlyList<string> changedWarnings = changedStore.RestoreCurrentValues(savedValues);
            Ensure(
                changedWarnings.Any(warning => warning.Contains("Vision.OffsetX")),
                "变量类型改动时没有报告跳过旧值。");
            Ensure(
                changedStore.GetString("Vision.OffsetX") == "配置初值",
                "变量类型变化后仍覆盖了当前配置初值。");
            Ensure(changedStore.GetBoolean("New.Value"), "新增变量没有保留当前配置初值。");
        }

        /// <summary>确认恢复 JSON 可读回，并在设备配置变化后禁止恢复旧流程步骤。</summary>
        private static void CheckRecoveryFileRoundTripAndConfigurationChange()
        {
            string temporaryRoot = Path.Combine(
                Path.GetTempPath(),
                "MotionControlRecoveryCheck_" + Guid.NewGuid().ToString("N"));
            try
            {
                var configurationStore = new MachineConfigurationStore(temporaryRoot);
                string machineId = "RecoveryCheck";
                string machineDirectory = configurationStore.GetMachineDirectory(machineId);
                string parameterDirectory = Path.Combine(machineDirectory, "Parameters");
                Directory.CreateDirectory(parameterDirectory);
                string deviceConfigurationPath = Path.Combine(parameterDirectory, "Device.json");
                File.WriteAllText(
                    deviceConfigurationPath,
                    "{\"schemaVersion\":1,\"axis\":\"X\"}" + Environment.NewLine,
                    new UTF8Encoding(false));

                var recoveryStore = new MachineRecoveryStore(configurationStore, machineId);
                var document = new MachineRecoveryDocument
                {
                    MachineId = machineId,
                    CanResume = true,
                    EquipmentInitializationCompleted = true
                };
                document.DeviceVariables.Add(new DeviceVariableCheckpoint
                {
                    Name = "Vision.Result",
                    DataType = "String",
                    CurrentValue = "OK",
                    HasValue = true,
                    LastUpdated = new DateTime(2026, 10, 22, 14, 30, 0)
                });
                document.StationFlows.Add(new StationFlowCheckpoint
                {
                    StationName = "检测工位",
                    StepNumber = 20
                });

                recoveryStore.Save(document);
                string warningMessage;
                MachineRecoveryDocument loadedDocument = recoveryStore.Load(out warningMessage);
                Ensure(loadedDocument != null, "恢复 JSON 保存后没有重新加载出来。");
                Ensure(loadedDocument.CanResume, "相同设备配置下错误地阻止了流程续跑。");
                Ensure(loadedDocument.EquipmentInitializationCompleted,
                    "恢复 JSON 没有保留已完成启动初始化的记录。");
                Ensure(loadedDocument.DeviceVariables.Single().CurrentValue == "OK", "恢复 JSON 中变量值不正确。");
                Ensure(
                    loadedDocument.DeviceVariables.Single().LastUpdated == new DateTime(2026, 10, 22, 14, 30, 0),
                    "恢复 JSON 没有保留变量最后更新时间。");
                Ensure(loadedDocument.StationFlows.Single().StepNumber == 20, "恢复 JSON 中工位步骤不正确。");
                Ensure(string.IsNullOrEmpty(warningMessage), "配置未变化时意外产生了恢复警告。");

                File.AppendAllText(deviceConfigurationPath, " ", new UTF8Encoding(false));
                MachineRecoveryDocument changedDocument = recoveryStore.Load(out warningMessage);
                Ensure(changedDocument != null, "配置变化后仍应读取兼容的变量值。");
                Ensure(!changedDocument.CanResume, "设备配置变化后仍允许恢复旧流程步骤。");
                Ensure(!changedDocument.EquipmentInitializationCompleted,
                    "设备配置变化后仍允许跳过设备启动初始化。");
                Ensure(
                    warningMessage.Contains("配置") && warningMessage.Contains("变化"),
                    "配置变化时没有给出不能续跑的原因。");
                Ensure(
                    changedDocument.DeviceVariables.Single().CurrentValue == "OK",
                    "配置变化后误丢弃了可按名称和类型恢复的设备变量。");

                var coordinator = new MachineRecoveryCoordinator(
                    recoveryStore,
                    new DeviceVariableStore(new[] { CreateDefinition("Vision.Result", "String", null, false) }),
                    machineId);
                string loadWarning;
                coordinator.Load(out loadWarning);
                Ensure(loadWarning.Contains("配置") && loadWarning.Contains("变化"),
                    "配置变化后，加载阶段没有报告不能恢复的原因。");
                string flowWarning;
                bool resumed = coordinator.TryRestoreStationFlows(
                    new IStationFlow[] { new RecoverableStationFlowForCheck("检测工位") }, out flowWarning);
                Ensure(!resumed && string.IsNullOrEmpty(flowWarning),
                    "同次启动的工位检查重复报告了加载阶段已说明的恢复原因。");
                coordinator.TryRestoreStationFlows(
                    new IStationFlow[] { new RecoverableStationFlowForCheck("检测工位") }, out flowWarning);
                Ensure(flowWarning.Contains("配置") && flowWarning.Contains("变化"),
                    "后续重新连接时错误地永久屏蔽了恢复原因。");

                var mismatchCoordinator = new MachineRecoveryCoordinator(
                    recoveryStore,
                    new DeviceVariableStore(new[] { CreateDefinition("Vision.Result", "Number", "0", false) }),
                    machineId);
                mismatchCoordinator.Load(out loadWarning);
                Ensure(loadWarning.Contains("Vision.Result") && loadWarning.Contains(Environment.NewLine),
                    "变量类型变化时没有报告具体变量恢复问题。");
                mismatchCoordinator.TryRestoreStationFlows(
                    new IStationFlow[] { new RecoverableStationFlowForCheck("检测工位") }, out flowWarning);
                Ensure(string.IsNullOrEmpty(flowWarning),
                    "已包含详细变量错误的加载警告后，工位检查又重复报告同一恢复限制。");

                // 旧版本可能保存了无续跑资格的工位步骤，但没有记录阻止续跑的原因。
                document.CanResume = false;
                document.EquipmentInitializationCompleted = false;
                document.ResumeBlockedReason = null;
                recoveryStore.Save(document);
                var oldDocumentCoordinator = new MachineRecoveryCoordinator(
                    recoveryStore,
                    new DeviceVariableStore(new[] { CreateDefinition("Vision.Result", "String", null, false) }),
                    machineId);
                oldDocumentCoordinator.Load(out loadWarning);
                Ensure(loadWarning.Contains("确认现场状态") && loadWarning.Contains("初始步骤"),
                    "旧恢复文件缺少原因时，没有说明现场确认和从初始步骤开始。");
                var oldDocumentFlow = new RecoverableStationFlowForCheck("检测工位");
                resumed = oldDocumentCoordinator.TryRestoreStationFlows(
                    new IStationFlow[] { oldDocumentFlow }, out flowWarning);
                Ensure(!resumed && oldDocumentFlow.RecoveryStepNumber == 10 && string.IsNullOrEmpty(flowWarning),
                    "旧恢复文件缺少原因时，误恢复了流程或重复提示同一限制。");
                oldDocumentCoordinator.TryRestoreStationFlows(
                    new IStationFlow[] { oldDocumentFlow }, out flowWarning);
                Ensure(flowWarning == loadWarning, "后续检查未重新报告旧恢复文件的续跑限制。");
            }
            finally
            {
                if (Directory.Exists(temporaryRoot))
                {
                    Directory.Delete(temporaryRoot, true);
                }
            }
        }

        /// <summary>确认协调器恢复已知步骤、拒绝未知步骤，并在新运行开始时作废旧断点。</summary>
        private static void CheckCoordinatorRestoresOnlyKnownStepsAndInvalidatesOnStart()
        {
            string temporaryRoot = Path.Combine(
                Path.GetTempPath(),
                "MotionControlCoordinatorCheck_" + Guid.NewGuid().ToString("N"));
            try
            {
                var configurationStore = new MachineConfigurationStore(temporaryRoot);
                string machineId = "CoordinatorCheck";
                string machineDirectory = configurationStore.GetMachineDirectory(machineId);
                string parameterDirectory = Path.Combine(machineDirectory, "Parameters");
                Directory.CreateDirectory(parameterDirectory);
                File.WriteAllText(
                    Path.Combine(parameterDirectory, "Machine.json"),
                    "{\"schemaVersion\":1}" + Environment.NewLine,
                    new UTF8Encoding(false));

                var recoveryStore = new MachineRecoveryStore(configurationStore, machineId);
                var variableDefinitions = new[]
                {
                    CreateDefinition("Vision.Result", "String", null, false)
                };
                var initialVariableStore = new DeviceVariableStore(variableDefinitions);
                initialVariableStore.SetString("Vision.Result", "OK");
                var document = new MachineRecoveryDocument
                {
                    MachineId = machineId,
                    CanResume = true,
                    DeviceVariables = initialVariableStore.GetSnapshot()
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
                document.StationFlows.Add(new StationFlowCheckpoint
                {
                    StationName = "工位A",
                    StepNumber = 20
                });
                document.StationFlows.Add(new StationFlowCheckpoint
                {
                    StationName = "工位B",
                    StepNumber = 99
                });
                recoveryStore.Save(document);

                var variableStore = new DeviceVariableStore(variableDefinitions);
                var coordinator = new MachineRecoveryCoordinator(recoveryStore, variableStore, machineId);
                string warningMessage;
                coordinator.Load(out warningMessage);
                Ensure(variableStore.GetString("Vision.Result") == "OK", "恢复协调器没有从 JSON 恢复设备变量值。");
                var stationA = new RecoverableStationFlowForCheck("工位A");
                var stationB = new RecoverableStationFlowForCheck("工位B");
                bool invalidStepRestored = coordinator.TryRestoreStationFlows(
                    new IStationFlow[] { stationA, stationB },
                    out warningMessage);
                Ensure(!invalidStepRestored, "恢复到未知步骤时没有拒绝续跑。");
                Ensure(stationA.RecoveryStepNumber == 10, "其中一个工位步骤无效时发生了部分恢复。");
                Ensure(warningMessage.Contains("工位B"), "加载成功后新发现的工位恢复错误被错误地屏蔽。");

                document.StationFlows[1].StepNumber = 20;
                recoveryStore.Save(document);
                var validVariableStore = new DeviceVariableStore(variableDefinitions);
                var validCoordinator = new MachineRecoveryCoordinator(
                    recoveryStore,
                    validVariableStore,
                    machineId);
                validCoordinator.Load(out warningMessage);
                stationA = new RecoverableStationFlowForCheck("工位A");
                stationB = new RecoverableStationFlowForCheck("工位B");
                bool validStepsRestored = validCoordinator.TryRestoreStationFlows(
                    new IStationFlow[] { stationA, stationB },
                    out warningMessage);
                Ensure(validStepsRestored, "所有步骤有效时没有恢复流程。");
                Ensure(stationA.RecoveryStepNumber == 20 && stationB.RecoveryStepNumber == 20,
                    "协调器没有恢复全部工位步骤。");

                validCoordinator.InvalidateBeforeAutomaticRun();
                validCoordinator.SaveForShutdown(null, false);
                var shutdownVariableStore = new DeviceVariableStore(variableDefinitions);
                var shutdownCoordinator = new MachineRecoveryCoordinator(
                    recoveryStore,
                    shutdownVariableStore,
                    machineId);
                shutdownCoordinator.Load(out warningMessage);
                Ensure(shutdownVariableStore.GetString("Vision.Result") == "OK",
                    "软件关闭保存后，新 Store 实例没有从恢复 JSON 载入变量值。");

                var restartedCoordinator = new MachineRecoveryCoordinator(
                    recoveryStore,
                    variableStore,
                    machineId);
                restartedCoordinator.Load(out warningMessage);
                stationA = new RecoverableStationFlowForCheck("工位A");
                bool staleStepRestored = restartedCoordinator.TryRestoreStationFlows(
                    new IStationFlow[] { stationA, new RecoverableStationFlowForCheck("工位B") },
                    out warningMessage);
                Ensure(!staleStepRestored, "自动流程启动后仍保留旧暂停步骤续跑资格。");
                Ensure(stationA.RecoveryStepNumber == 10, "旧断点作废后仍覆盖了工位初始步骤。");
            }
            finally
            {
                if (Directory.Exists(temporaryRoot))
                {
                    Directory.Delete(temporaryRoot, true);
                }
            }
        }

        /// <summary>构造单条回归用设备变量定义。</summary>
        private static DeviceVariableDefinition CreateDefinition(
            string name,
            string dataType,
            string initialValue,
            bool allowOperatorWrite)
        {
            return new DeviceVariableDefinition
            {
                Name = name,
                DataType = dataType,
                InitialValue = initialValue,
                AllowOperatorWrite = allowOperatorWrite
            };
        }

        /// <summary>检查条件并用清楚的恢复上下文报告失败。</summary>
        private static void Ensure(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        /// <summary>提供不连接运动卡的工位替身，记录协调器是否恢复了步骤。</summary>
        private sealed class RecoverableStationFlowForCheck : IStationFlow, IRecoverableStationFlow
        {
            /// <summary>检查中使用的当前步骤编号。</summary>
            private int _stepNumber = 10;

            /// <summary>创建指定名称且初始位于第 10 步的可恢复工位替身。</summary>
            public RecoverableStationFlowForCheck(string stationName)
            {
                StationName = stationName;
            }

            /// <summary>返回当前替身工位名称。</summary>
            public string StationName { get; private set; }

            /// <summary>返回当前替身步骤，供恢复检查断言使用。</summary>
            public int RecoveryStepNumber { get { return _stepNumber; } }

            /// <summary>返回当前替身步骤说明。</summary>
            public string CurrentStepName { get { return "步骤 " + _stepNumber; } }

            /// <summary>只接受检查用的第 10 步和第 20 步。</summary>
            public bool CanRestoreStep(int stepNumber)
            {
                return stepNumber == 10 || stepNumber == 20;
            }

            /// <summary>恢复替身步骤，不调用任何硬件接口。</summary>
            public void RestoreStep(int stepNumber)
            {
                if (!CanRestoreStep(stepNumber))
                {
                    throw new ArgumentOutOfRangeException(nameof(stepNumber));
                }

                _stepNumber = stepNumber;
            }

            /// <summary>该替身不执行工艺动作，确保恢复检查完全离屏。</summary>
            public Task ExecuteCurrentStepAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }

            /// <summary>清理替身状态而不改变当前步骤编号。</summary>
            public Task ResetAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }
        }
    }
}
