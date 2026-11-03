using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
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
            CheckRuntimeRecordsDoNotChangeConfigurationSignature();
            CheckCoordinatorRestoresOnlyKnownStepsAndInvalidatesOnStart();
            CheckUntrustedRecoveryDoesNotOverwrite();
            CheckResourceRecordErrorPresentation();
            CheckClearanceEvidenceKeepsOldRecoveryBlocked();
            return Task.CompletedTask;
        }

        /// <summary>清料开始、阶段记录或中断均保留旧记录，任何新会话不能据此恢复旧流程。</summary>
        private static void CheckClearanceEvidenceKeepsOldRecoveryBlocked()
        {
            string root = Path.Combine(Path.GetTempPath(), "ClearanceEvidence_" + Guid.NewGuid().ToString("N"));
            try
            {
                var configuration = new MachineConfigurationStore(root);
                var store = new MachineRecoveryStore(configuration, "Machine01");
                Directory.CreateDirectory(configuration.GetMachineDirectory("Machine01"));
                store.Save(new MachineRecoveryDocument { CanResume = true, EquipmentInitializationCompleted = true });
                string directory = Path.Combine(configuration.GetMachineDirectory("Machine01"), "Runtime");
                string oldSnapshot = File.ReadAllText(Path.Combine(directory, "RecoverySnapshot.json"));
                Func<MachineRecoveryCoordinator> create = () => new MachineRecoveryCoordinator(store,
                    new DeviceVariableStore(new DeviceVariableDefinition[0]), "Machine01");
                var coordinator = create();
                coordinator.Load(out string warning);
                coordinator.InvalidateBeforeAutomaticRun(null);
                string oldMarker = File.ReadAllText(Path.Combine(directory, "AutomaticRun.pending"));
                coordinator.MarkBeforeClearance();
                Ensure(File.Exists(Path.Combine(directory, "ControlledClearance.pending")), "清料开始前未保存证据。");
                Ensure(!File.Exists(Path.Combine(directory, "ControlledClearance.actions-observed")), "清料未到反馈检查点却写入阶段记录。");
                Ensure(coordinator.GetAutomaticStartBlockReason(null) != null, "清料开始后允许了旧生产续跑。");
                bool refusedDuplicate = false;
                try { coordinator.MarkBeforeClearance(); } catch (InvalidOperationException) { refusedDuplicate = true; }
                Ensure(refusedDuplicate, "当前清料证据未结算时允许重复开始。");

                // 新进程只能看到已开始的证据，不能假定动作已经安全收尾。
                var interrupted = create(); interrupted.Load(out warning);
                Ensure(interrupted.RecoveryBlockReason.Contains("清料记录"), "清料中断后未阻止新会话续跑。");
                bool refusedForeignCompletion = false;
                try { interrupted.SaveClearanceActionsObserved(); } catch (InvalidOperationException) { refusedForeignCompletion = true; }
                Ensure(refusedForeignCompletion, "其他会话能替原清料记录完成。");

                coordinator.SaveClearanceActionsObserved();
                string observedPath = Path.Combine(directory, "ControlledClearance.actions-observed");
                Ensure(File.Exists(observedPath), "清料动作检查点未追加证据。");
                Ensure(File.ReadAllText(observedPath).Contains("尚未由此记录确认")
                    && File.Exists(Path.Combine(directory, "ControlledClearance.pending")),
                    "动作阶段记录错误地声称最终停止已确认或丢失待确认标记。");
                bool refusedEmpty = false;
                try { coordinator.RecordClearanceInspection(" "); } catch (ArgumentException) { refusedEmpty = true; }
                string inspectionsDirectory = Path.Combine(directory, "ControlledClearanceInspections");
                Ensure(refusedEmpty && !Directory.Exists(inspectionsDirectory),
                    "空的现场确认被记录。");
                const string inspectionNote = "已现场检查夹具和料位\n保持输出已核对";
                coordinator.RecordClearanceInspection(inspectionNote);
                string[] firstInspections = Directory.GetFiles(inspectionsDirectory, "*.json");
                Ensure(firstInspections.Length == 1, "现场核查没有独立生成一条记录。");
                string firstInspection = File.ReadAllText(firstInspections[0]);
                var inspection = Newtonsoft.Json.Linq.JObject.Parse(firstInspection);
                Ensure((string)inspection["Confirmation"] == inspectionNote && ((string)inspection["Notice"]).Contains("不代表"),
                    "现场确认内容未保存或错误声称资源已经解除。");
                coordinator.RecordClearanceInspection("重新核查并确认条件");
                string[] repeatedInspections = Directory.GetFiles(inspectionsDirectory, "*.json");
                Ensure(repeatedInspections.Length == 2 && File.ReadAllText(firstInspections[0]) == firstInspection,
                    "再次核查未追加独立证据或覆盖了上次确认。");
                Ensure(File.ReadAllText(Path.Combine(directory, "RecoverySnapshot.json")) == oldSnapshot
                    && File.ReadAllText(Path.Combine(directory, "AutomaticRun.pending")) == oldMarker,
                    "清料覆盖了旧恢复快照或生产标记。");
                var restarted = create(); restarted.Load(out warning);
                Ensure(restarted.RecoveryBlockReason.Contains("清料记录"), "清料完成后错误地放行旧断点。");
                bool refusedRestartedInspection = false;
                try { restarted.RecordClearanceInspection("跨会话确认"); } catch (InvalidOperationException) { refusedRestartedInspection = true; }
                Ensure(refusedRestartedInspection, "新进程错误借用了旧会话的清料核查凭证。");
                restarted.SaveForShutdown(null, false);
                Ensure(File.ReadAllText(Path.Combine(directory, "RecoverySnapshot.json")) == oldSnapshot,
                    "清料后的关闭保存覆盖了旧恢复快照。");

                // 加载与开始之间若出现其他证据，不能覆盖、开始动作或误报为干净状态。
                var otherStore = new MachineRecoveryStore(configuration, "Machine02");
                var raced = new MachineRecoveryCoordinator(otherStore,
                    new DeviceVariableStore(new DeviceVariableDefinition[0]), "Machine02");
                raced.Load(out warning);
                string otherDirectory = Path.Combine(configuration.GetMachineDirectory("Machine02"), "Runtime");
                Directory.CreateDirectory(otherDirectory);
                string competing = Path.Combine(otherDirectory, "ControlledClearance.pending");
                File.WriteAllText(competing, "未完整写入的旧证据");
                bool refusedCompeting = false;
                try { raced.MarkBeforeClearance(); } catch (InvalidOperationException) { refusedCompeting = true; }
                Ensure(refusedCompeting && File.ReadAllText(competing) == "未完整写入的旧证据"
                    && raced.GetAutomaticStartBlockReason(null) != null,
                    "并发出现的不完整证据被覆盖或未阻止自动启动。");

                // 动作反馈阶段刚写入时发生取消/断电，甚至开始标记不可读，也不能据阶段文件放行。
                foreach (string fileName in new[] { "ControlledClearance.actions-observed", "ControlledClearance.completed", "ControlledClearance.inspected" })
                {
                    string id = fileName.EndsWith("completed", StringComparison.Ordinal) ? "Machine03"
                        : fileName.EndsWith("inspected", StringComparison.Ordinal) ? "Machine06" : "Machine04";
                    string evidenceDirectory = Path.Combine(configuration.GetMachineDirectory(id), "Runtime");
                    Directory.CreateDirectory(evidenceDirectory);
                    File.WriteAllText(Path.Combine(evidenceDirectory, fileName), "部分或旧版清料记录");
                    var onlyStage = new MachineRecoveryCoordinator(new MachineRecoveryStore(configuration, id),
                        new DeviceVariableStore(new DeviceVariableDefinition[0]), id);
                    onlyStage.Load(out warning);
                    Ensure(onlyStage.GetAutomaticStartBlockReason(null) != null && warning.Contains("清料记录"),
                        "单独的阶段证据被当成清料安全完成：" + fileName);
                }
                // 目录已创建但记录尚未写完时，崩溃后空目录也代表未知状态。
                string emptyInspectionDirectory = Path.Combine(configuration.GetMachineDirectory("Machine07"), "Runtime", "ControlledClearanceInspections");
                Directory.CreateDirectory(emptyInspectionDirectory);
                var emptyInspection = new MachineRecoveryCoordinator(new MachineRecoveryStore(configuration, "Machine07"),
                    new DeviceVariableStore(new DeviceVariableDefinition[0]), "Machine07");
                emptyInspection.Load(out warning);
                Ensure(emptyInspection.GetAutomaticStartBlockReason(null) != null && warning.Contains("清料记录"),
                    "未完成核查写入留下的空目录未阻止续跑。");

                // 阶段文件在写入前被占用时，失败不得覆盖证据或释放 pending。
                var failureStore = new MachineRecoveryStore(configuration, "Machine05");
                var failedWrite = new MachineRecoveryCoordinator(failureStore,
                    new DeviceVariableStore(new DeviceVariableDefinition[0]), "Machine05");
                failedWrite.Load(out warning);
                failedWrite.MarkBeforeClearance();
                string failedDirectory = Path.Combine(configuration.GetMachineDirectory("Machine05"), "Runtime");
                string failedStage = Path.Combine(failedDirectory, "ControlledClearance.actions-observed");
                File.WriteAllText(failedStage, "其他会话证据");
                bool refusedOverwrite = false;
                try { failedWrite.SaveClearanceActionsObserved(); } catch (IOException) { refusedOverwrite = true; }
                Ensure(refusedOverwrite && File.ReadAllText(failedStage) == "其他会话证据"
                    && File.Exists(Path.Combine(failedDirectory, "ControlledClearance.pending"))
                    && failedWrite.GetAutomaticStartBlockReason(null) != null,
                    "阶段写入失败丢失 pending、覆盖文件或放行旧断点。");
                string competingInspection = Path.Combine(failedDirectory, "ControlledClearanceInspections");
                File.WriteAllText(competingInspection, "其他会话的核查证据");
                bool refusedInspectionOverwrite = false;
                try { failedWrite.RecordClearanceInspection("核查"); } catch (IOException) { refusedInspectionOverwrite = true; }
                Ensure(refusedInspectionOverwrite && File.ReadAllText(competingInspection) == "其他会话的核查证据"
                    && File.Exists(Path.Combine(failedDirectory, "ControlledClearance.pending"))
                    && failedWrite.GetAutomaticStartBlockReason(null) != null,
                    "核查记录写入冲突时覆盖了证据、丢失 pending 或放行旧断点。");
            }
            finally
            {
                string directory = Path.GetFullPath(root);
                string tempPrefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!directory.StartsWith(tempPrefix, StringComparison.OrdinalIgnoreCase)
                    || !Path.GetFileName(directory).StartsWith("ClearanceEvidence_", StringComparison.Ordinal))
                    throw new InvalidOperationException("清料证据测试目录不在预期的临时目录内。");
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        /// <summary>验证运行记录不改变签名、配置和草稿仍生效，旧签名无法证明兼容时保留原记录。</summary>
        private static void CheckRuntimeRecordsDoNotChangeConfigurationSignature()
        {
            string root = Path.Combine(Path.GetTempPath(), "RecoverySignature_" + Guid.NewGuid().ToString("N"));
            try
            {
                var configuration = new MachineConfigurationStore(root);
                string machine = configuration.GetMachineDirectory("Machine01");
                string parameters = Path.Combine(machine, "Parameters");
                string drafts = Path.Combine(machine, "Drafts");
                string runtime = Path.Combine(machine, "Runtime");
                Directory.CreateDirectory(parameters);
                Directory.CreateDirectory(drafts);
                Directory.CreateDirectory(runtime);
                string parameterFile = Path.Combine(parameters, "Axis.json");
                string draftFile = Path.Combine(drafts, "Task.json");
                File.WriteAllText(parameterFile, "{\"axis\":1}");
                File.WriteAllText(draftFile, "{\"draft\":1}");
                var store = new MachineRecoveryStore(configuration, "Machine01");
                string initial = store.GetConfigurationSignature();
                Ensure(initial == CalculateLegacySignature(machine), "旧版没有额外运行 JSON 时签名应保持一致。");
                store.Save(new MachineRecoveryDocument { CanResume = true, EquipmentInitializationCompleted = true });
                string snapshotFile = Path.Combine(runtime, "RecoverySnapshot.json");
                string original = File.ReadAllText(snapshotFile);
                string recordDirectory = Path.Combine(runtime, "Production", "Evidence");
                Directory.CreateDirectory(recordDirectory);
                string recordFile = Path.Combine(recordDirectory, "Record.json");
                File.WriteAllText(recordFile, "{\"count\":1}");
                Ensure(store.GetConfigurationSignature() == initial, "新增生产证据改变了配置签名。");
                File.WriteAllText(recordFile, "{\"count\":2}");
                File.WriteAllText(Path.Combine(runtime, "Event.json"), "{\"event\":1}");
                Ensure(store.GetConfigurationSignature() == initial, "修改运行记录改变了配置签名。");
                var loaded = store.Load(out string warning);
                Ensure(loaded.CanResume && string.IsNullOrEmpty(warning), "只有运行记录变化时错误阻止续跑。");

                File.AppendAllText(draftFile, " ");
                Ensure(store.GetConfigurationSignature() != initial && !store.Load(out warning).CanResume, "草稿变更没有阻止续跑。");
                File.WriteAllText(draftFile, "{\"draft\":1}");
                File.AppendAllText(parameterFile, " ");
                Ensure(store.GetConfigurationSignature() != initial && !store.Load(out warning).CanResume, "配置变更没有阻止续跑。");
                File.WriteAllText(parameterFile, "{\"axis\":1}");

                // 模拟旧版曾将运行记录算进签名；无法从哈希证明旧配置未变，必须阻止并保留原文件。
                var legacy = new MachineRecoveryDocument { MachineId = "Machine01", CanResume = true,
                    EquipmentInitializationCompleted = true, ConfigurationSignature = CalculateLegacySignature(machine) };
                configuration.SaveJson("Machine01", "Runtime/RecoverySnapshot.json", legacy);
                string legacyFile = File.ReadAllText(snapshotFile);
                Ensure(legacy.ConfigurationSignature != store.GetConfigurationSignature(), "回归样例没有形成旧版不兼容签名。");
                var coordinator = new MachineRecoveryCoordinator(store,
                    new DeviceVariableStore(new DeviceVariableDefinition[0]), "Machine01");
                coordinator.Load(out warning);
                Ensure(!string.IsNullOrWhiteSpace(coordinator.RecoveryBlockReason), "旧版签名无法证明兼容时没有阻断。");
                coordinator.SaveForShutdown(null, false);
                Ensure(File.ReadAllText(snapshotFile) == legacyFile, "无法证明兼容时覆盖了原恢复记录。");
                Ensure(original != legacyFile, "旧版恢复记录样例没有变化。");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        /// <summary>独立复现旧版签名算法，用于检验旧快照兼容边界。</summary>
        private static string CalculateLegacySignature(string machineDirectory)
        {
            string prefix = machineDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string recoveryPath = Path.GetFullPath(Path.Combine(machineDirectory, "Runtime", "RecoverySnapshot.json"));
            string[] files = Directory.GetFiles(machineDirectory, "*.json", SearchOption.AllDirectories)
                .Where(path => !string.Equals(Path.GetFullPath(path), recoveryPath, StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
            using (var content = new MemoryStream())
            {
                foreach (string file in files)
                {
                    byte[] name = Encoding.UTF8.GetBytes(file.Substring(prefix.Length).Replace(Path.DirectorySeparatorChar, '/'));
                    content.Write(name, 0, name.Length);
                    content.WriteByte(0);
                    byte[] bytes = File.ReadAllBytes(file);
                    content.Write(bytes, 0, bytes.Length);
                    content.WriteByte(0xFF);
                }
                using (SHA256 sha = SHA256.Create())
                    return BitConverter.ToString(sha.ComputeHash(content.ToArray())).Replace("-", string.Empty);
            }
        }

        /// <summary>损坏或不可读取的资源记录继续拦截，界面只显示中文，诊断保留完整异常。</summary>
        private static void CheckResourceRecordErrorPresentation()
        {
            string root = Path.Combine(Path.GetTempPath(), "ResourceWarning_" + Guid.NewGuid().ToString("N"));
            var store = new MachineConfigurationStore(root);
            string guards = Path.Combine(store.GetMachineDirectory("Machine01"), "Runtime", "ResourceGuards");
            using (var writer = new StringWriter())
            using (var listener = new TextWriterTraceListener(writer))
            {
                Trace.Listeners.Add(listener);
                try
                {
                    string session = Path.Combine(guards, "session");
                    Directory.CreateDirectory(session);
                    File.WriteAllText(Path.Combine(session, "bad.guard"), "{");
                    var coordinator = new MachineRecoveryCoordinator(new MachineRecoveryStore(store, "Machine01"),
                        new DeviceVariableStore(new DeviceVariableDefinition[0]), "Machine01");
                    coordinator.Load(out string warning);
                    Trace.Flush();
                    Ensure(warning.Contains("记录损坏或无法读取") && warning == coordinator.RecoveryBlockReason
                        && !warning.Contains("Newtonsoft") && !warning.Contains("Error parsing"), "损坏记录提示未简化或未拦截。");
                    Ensure(writer.ToString().Contains("JsonReaderException"), "完整解析异常未写入诊断日志。");
                    writer.GetStringBuilder().Clear();
                    Directory.Delete(guards, true);
                    File.WriteAllText(guards, "不是目录");
                    coordinator.Load(out warning);
                    Trace.Flush();
                    Ensure(warning.Contains("记录损坏或无法读取") && !string.IsNullOrWhiteSpace(coordinator.RecoveryBlockReason),
                        "无法读取记录目录时未按未知状态拦截。");
                    Ensure(writer.ToString().Contains("Exception"), "读取异常未保留诊断。");
                }
                finally
                {
                    Trace.Listeners.Remove(listener);
                    if (Directory.Exists(root)) Directory.Delete(root, true);
                }
            }
        }

        /// <summary>损坏、错误设备或缺失变量时保留恢复原件，首次无文件不阻止运行。</summary>
        private static void CheckUntrustedRecoveryDoesNotOverwrite()
        {
            string root = Path.Combine(Path.GetTempPath(), "RecoveryUntrusted_" + Guid.NewGuid().ToString("N"));
            var store = new MachineConfigurationStore(root);
            var source = new MachineRecoveryStore(store, "Machine01");
            Func<MachineRecoveryCoordinator> create = () => new MachineRecoveryCoordinator(source,
                new DeviceVariableStore(new[] { CreateDefinition("Part.Occupied", "Boolean", "false", false) }), "Machine01");
            string warning;
            var first = create(); first.Load(out warning);
            Ensure(first.RecoveryBlockReason == null, "首次无恢复文件不应拦截。");
            string directory = Path.Combine(store.GetMachineDirectory("Machine01"), "Runtime");
            Directory.CreateDirectory(directory);
            string file = Path.Combine(directory, "RecoverySnapshot.json");
            foreach (string text in new[] { "{无效", "{\"schemaVersion\":2}", "{\"schemaVersion\":1,\"machineId\":\"Other\",\"deviceVariables\":[],\"stationFlows\":[]}",
                "{\"schemaVersion\":1,\"machineId\":\"Machine01\",\"deviceVariables\":[],\"stationFlows\":[]}" })
            {
                File.WriteAllText(file, text, new UTF8Encoding(false));
                var coordinator = create(); coordinator.Load(out warning);
                Ensure(!string.IsNullOrEmpty(coordinator.RecoveryBlockReason), "不可信恢复文件没有触发门禁。");
                coordinator.SaveForShutdown(null, false);
                Ensure(File.ReadAllText(file) == text, "不可信恢复文件被退出保存覆盖。");
            }
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

                string savedPath = Path.Combine(configurationStore.GetMachineDirectory(machineId), "Runtime", "RecoverySnapshot.json");
                string beforeShutdown = File.ReadAllText(savedPath);
                validCoordinator.InvalidateBeforeAutomaticRun();
                Ensure(File.ReadAllText(savedPath) == beforeShutdown, "生产启动前覆盖了原恢复记录。");
                validCoordinator.SaveForShutdown(null, false);
                Ensure(File.ReadAllText(savedPath) == beforeShutdown, "未受控停稳的关闭覆盖了原恢复记录。");
                var shutdownVariableStore = new DeviceVariableStore(variableDefinitions);
                var shutdownCoordinator = new MachineRecoveryCoordinator(
                    recoveryStore,
                    shutdownVariableStore,
                    machineId);
                shutdownCoordinator.Load(out warningMessage);
                Ensure(!string.IsNullOrWhiteSpace(shutdownCoordinator.RecoveryBlockReason)
                    && shutdownCoordinator.RecoveryBlockReason.Contains("未确认安全停止"), "意外退出后的运行标记未阻止重新启动。");
                Ensure(shutdownVariableStore.GetString("Vision.Result") == "OK",
                    "软件关闭保存后，新 Store 实例没有从恢复 JSON 载入变量值。");
                shutdownCoordinator.SaveForShutdown(null, false);
                Ensure(File.ReadAllText(savedPath) == beforeShutdown, "重启后的不可信记录被关闭操作覆盖。");

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
