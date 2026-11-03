using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.TaskEngine.Resources;
using Newtonsoft.Json;

namespace MotionControl.Host.Wpf.Recovery
{
    /// <summary>在单机配置目录中读写受控暂停后的本地恢复文件。</summary>
    public sealed class MachineRecoveryStore
    {
        /// <summary>按设备编号管理 JSON 配置文件的存储服务。</summary>
        private readonly MachineConfigurationStore _configurationStore;

        /// <summary>当前恢复文件所属的设备编号。</summary>
        private readonly string _machineId;

        /// <summary>恢复文件在设备配置目录中的相对路径。</summary>
        private const string RecoveryFilePath = "Runtime/RecoverySnapshot.json";

        /// <summary>自动生产尚未完成安全暂停时保留的独立标记；不纳入配置签名。</summary>
        private const string RunMarkerPath = "Runtime/AutomaticRun.pending";

        /// <summary>清料开始及完成的独立证据；保留原运行标记和恢复快照。</summary>
        private const string ClearanceMarkerPath = "Runtime/ControlledClearance.pending";
        /// <summary>清料动作到达反馈检查点的证据；不代表最终停止、报警和现场核查已经结算。</summary>
        private const string ClearanceActionsObservedPath = "Runtime/ControlledClearance.actions-observed";
        /// <summary>人工核查记录目录；每次确认独立追加，不解除原资源隔离。</summary>
        private const string ClearanceInspectionDirectory = "Runtime/ControlledClearanceInspections";
        /// <summary>旧版单条现场核查证据继续阻止续跑。</summary>
        private const string LegacyClearanceInspectionPath = "Runtime/ControlledClearance.inspected";
        /// <summary>旧版阶段证据也继续拦截，不能因为名称变化而丢失保护。</summary>
        private const string LegacyClearanceCompletedPath = "Runtime/ControlledClearance.completed";

        /// <summary>返回本机未完成运行标记的完整路径。</summary>
        private string GetRunMarkerPath()
        { return Path.Combine(_configurationStore.GetMachineDirectory(_machineId), RunMarkerPath.Replace('/', Path.DirectorySeparatorChar)); }

        /// <summary>返回清料证据的完整路径。</summary>
        private string GetClearanceEvidencePath(string relativePath)
        { return Path.Combine(_configurationStore.GetMachineDirectory(_machineId), relativePath.Replace('/', Path.DirectorySeparatorChar)); }

        /// <summary>只把明确不存在视为无文件；读取错误继续向上报告。</summary>
        private static bool EvidenceExists(string path)
        {
            try { File.GetAttributes(path); return true; }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }

        /// <summary>任何清料证据都保守拦截旧断点；完成动作不等于现场已安全恢复。</summary>
        public bool HasClearanceEvidence()
        {
            return EvidenceExists(GetClearanceEvidencePath(ClearanceMarkerPath))
                || EvidenceExists(GetClearanceEvidencePath(ClearanceActionsObservedPath))
                || EvidenceExists(GetClearanceEvidencePath(ClearanceInspectionDirectory))
                || EvidenceExists(GetClearanceEvidencePath(LegacyClearanceInspectionPath))
                || EvidenceExists(GetClearanceEvidencePath(LegacyClearanceCompletedPath));
        }

        /// <summary>动作前独立创建并刷新证据，不改动原有运行标记或恢复文件。</summary>
        public void MarkClearanceStarted()
        {
            if (HasClearanceEvidence()) throw new InvalidOperationException("已有清料证据尚未处置，禁止再次开始清料。");
            WriteNewClearanceEvidence(ClearanceMarkerPath, "清料已开始；现场状态已变化或未知，旧工位断点禁止续跑。\n");
        }

        /// <summary>记录动作到达反馈检查点；此记录不宣称最终安全收尾，原始证据仍保留。</summary>
        public void MarkClearanceActionsObserved()
        {
            if (!EvidenceExists(GetClearanceEvidencePath(ClearanceMarkerPath)))
                throw new InvalidOperationException("清料开始证据不存在，不能记录动作反馈阶段。");
            WriteNewClearanceEvidence(ClearanceActionsObservedPath,
                "清料动作和反馈曾到达检查点；最终停止、报警和收尾状态尚未由此记录确认。现场、资源和旧工位断点仍须核查，禁止据此续跑。\n");
        }

        /// <summary>追加本次核查说明；仅记录当时观察与人工确认，不释放旧资源或运行标记。</summary>
        public void MarkClearanceInspected(string confirmation)
        {
            if (string.IsNullOrWhiteSpace(confirmation) || confirmation.Length > 16384)
                throw new ArgumentException("清料现场核查说明不能为空，且不得超过 16384 字。", nameof(confirmation));
            if (!EvidenceExists(GetClearanceEvidencePath(ClearanceMarkerPath)))
                throw new InvalidOperationException("清料开始证据不存在，不能记录现场核查。");
            string record = JsonConvert.SerializeObject(new
            {
                MachineId = _machineId,
                RecordedAtUtc = DateTime.UtcNow,
                Confirmation = confirmation.Trim(),
                Notice = "这是一次现场核查记录，不代表原资源隔离已解除或允许设备续跑。"
            }, Formatting.Indented);
            // 不覆盖历史核查；即使记录过程中状态变化，下一次核查也必须单独留痕。
            string path = ClearanceInspectionDirectory + "/" + Guid.NewGuid().ToString("N") + ".json";
            WriteNewClearanceEvidence(path, record + "\n");
        }

        /// <summary>以独占创建和持久刷新写入证据，任何失败均不得执行或确认后续动作。</summary>
        private void WriteNewClearanceEvidence(string relativePath, string message)
        {
            string path = GetClearanceEvidencePath(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(message);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
        }

        /// <summary>只把明确不存在视为无标记；权限与读取异常必须阻止启动。</summary>
        public bool HasUnfinishedAutomaticRun()
        {
            try { File.GetAttributes(GetRunMarkerPath()); return true; }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }

        /// <summary>核查上次任务会话的具名资源记录；异常或损坏由调用方按未知状态拦截。</summary>
        public string InspectTaskResources()
        {
            return TaskResourceJournal.Inspect(_configurationStore.GetMachineDirectory(_machineId), _machineId);
        }

        /// <summary>生产启动前原子创建并刷新标记；失败时调用者不能启动生产。</summary>
        public void MarkAutomaticRun()
        {
            string path = GetRunMarkerPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                byte[] bytes = Encoding.UTF8.GetBytes("设备运行或准备动作未确认安全结束，请联系维护人员核对现场与恢复记录。\n");
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
        }

        /// <summary>仅在当前会话安全暂停且检查点写入成功后移除运行标记。</summary>
        public void ClearAutomaticRunMarker() { File.Delete(GetRunMarkerPath()); }

        /// <summary>创建绑定到指定设备配置目录的恢复存储。</summary>
        public MachineRecoveryStore(MachineConfigurationStore configurationStore, string machineId)
        {
            _configurationStore = configurationStore
                ?? throw new ArgumentNullException(nameof(configurationStore));
            if (string.IsNullOrWhiteSpace(machineId))
            {
                throw new ArgumentException("设备编号不能为空。", nameof(machineId));
            }

            _machineId = machineId;
        }

        /// <summary>读取恢复文件，并在设备配置变化时禁止恢复旧流程步。</summary>
        public MachineRecoveryDocument Load(out string warningMessage)
        {
            warningMessage = string.Empty;
            string fullPath = Path.Combine(
                _configurationStore.GetMachineDirectory(_machineId),
                RecoveryFilePath.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                // 只有明确的文件不存在才当作首次启动；权限错误等不能伪装成空机。
                File.GetAttributes(fullPath);
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            catch (Exception error)
            {
                warningMessage = "设备变量恢复文件无法读取，当前使用配置初值。原因：" + error.Message;
                return null;
            }

            MachineRecoveryDocument document;
            try { document = _configurationStore.LoadJson<MachineRecoveryDocument>(_machineId, RecoveryFilePath); }
            catch (Exception error)
            {
                warningMessage = "设备变量恢复文件无法读取，当前使用配置初值。原因：" + error.Message;
                return null;
            }

            if (document.SchemaVersion != 1)
            {
                warningMessage = "设备变量恢复文件版本不受支持，当前使用配置初值。";
                return null;
            }

            if (!string.Equals(document.MachineId, _machineId, StringComparison.OrdinalIgnoreCase))
            {
                warningMessage = "设备变量恢复文件属于其他设备，未加载该文件。";
                return null;
            }

            if (document.DeviceVariables == null)
            {
                warningMessage = "设备恢复文件缺少变量记录，不能确认现场状态。";
                return null;
            }

            if (document.StationFlows == null)
            {
                warningMessage = "设备恢复文件缺少工位记录，不能确认现场状态。";
                return null;
            }

            string currentSignature = GetConfigurationSignature();
            if (!string.Equals(
                document.ConfigurationSignature,
                currentSignature,
                StringComparison.OrdinalIgnoreCase))
            {
                document.ConfigurationSignatureMismatch = true;
                document.CanResume = false;
                document.EquipmentInitializationCompleted = false;
                document.ResumeBlockedReason = "设备配置或旧版恢复校验范围发生变化，无法确认原恢复记录仍适用；请联系维护人员核对，流程不会自动续跑。";
                warningMessage = document.ResumeBlockedReason;
            }

            return document;
        }

        /// <summary>保存当前文档并刷新设备配置校验值和保存时间。</summary>
        public void Save(MachineRecoveryDocument document)
        {
            if (document == null)
            {
                throw new ArgumentNullException(nameof(document));
            }

            document.SchemaVersion = 1;
            document.MachineId = _machineId;
            document.ConfigurationSignature = GetConfigurationSignature();
            document.SavedAt = DateTime.Now;
            if (document.DeviceVariables == null)
            {
                document.DeviceVariables = new System.Collections.Generic.List<DeviceVariableCheckpoint>();
            }

            if (document.StationFlows == null)
            {
                document.StationFlows = new System.Collections.Generic.List<StationFlowCheckpoint>();
            }

            _configurationStore.SaveJson(_machineId, RecoveryFilePath, document);
        }

        /// <summary>计算设备配置和草稿 JSON 的稳定校验值；整个 Runtime 只含运行状态，不参与校验。</summary>
        public string GetConfigurationSignature()
        {
            string machineDirectory = _configurationStore.GetMachineDirectory(_machineId);
            if (!Directory.Exists(machineDirectory))
            {
                return string.Empty;
            }

            // 不遍历 Runtime：生产记录不断变化，且运行数据不可读不应伪装成配置变化。
            // 其他目录仍使用原有的排序与哈希格式，以兼容没有额外运行 JSON 的旧快照。
            string[] configurationFiles = Directory.GetFiles(machineDirectory, "*.json", SearchOption.TopDirectoryOnly)
                .Concat(Directory.GetDirectories(machineDirectory)
                    .Where(path => !string.Equals(Path.GetFileName(path), "Runtime", StringComparison.OrdinalIgnoreCase))
                    .SelectMany(path => Directory.GetFiles(path, "*.json", SearchOption.AllDirectories)))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            using (var content = new MemoryStream())
            {
                string machinePrefix = machineDirectory.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                foreach (string configurationFile in configurationFiles)
                {
                    string relativePath = configurationFile.Substring(machinePrefix.Length)
                        .Replace(Path.DirectorySeparatorChar, '/');
                    byte[] relativePathBytes = Encoding.UTF8.GetBytes(relativePath);
                    content.Write(relativePathBytes, 0, relativePathBytes.Length);
                    content.WriteByte(0);

                    byte[] fileBytes = File.ReadAllBytes(configurationFile);
                    content.Write(fileBytes, 0, fileBytes.Length);
                    content.WriteByte(0xFF);
                }

                using (SHA256 sha256 = SHA256.Create())
                {
                    byte[] hash = sha256.ComputeHash(content.ToArray());
                    return BitConverter.ToString(hash).Replace("-", string.Empty);
                }
            }
        }
    }
}
