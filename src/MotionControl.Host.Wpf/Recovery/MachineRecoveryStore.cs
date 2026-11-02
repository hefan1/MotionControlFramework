using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MotionControl.Framework.Runtime.Configuration;

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

        /// <summary>返回本机未完成运行标记的完整路径。</summary>
        private string GetRunMarkerPath()
        { return Path.Combine(_configurationStore.GetMachineDirectory(_machineId), RunMarkerPath.Replace('/', Path.DirectorySeparatorChar)); }

        /// <summary>只把明确不存在视为无标记；权限与读取异常必须阻止启动。</summary>
        public bool HasUnfinishedAutomaticRun()
        {
            try { File.GetAttributes(GetRunMarkerPath()); return true; }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
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
                document.CanResume = false;
                document.EquipmentInitializationCompleted = false;
                document.ResumeBlockedReason = "设备配置自上次暂停后发生变化；变量中名称和类型仍匹配的值已保留，流程不会自动续跑。";
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

        /// <summary>计算设备配置 JSON 文件的稳定校验值，排除恢复文件自身。</summary>
        public string GetConfigurationSignature()
        {
            string machineDirectory = _configurationStore.GetMachineDirectory(_machineId);
            if (!Directory.Exists(machineDirectory))
            {
                return string.Empty;
            }

            string recoveryFullPath = Path.GetFullPath(Path.Combine(
                machineDirectory,
                RecoveryFilePath.Replace('/', Path.DirectorySeparatorChar)));
            string[] configurationFiles = Directory.GetFiles(
                    machineDirectory,
                    "*.json",
                    SearchOption.AllDirectories)
                .Where(path => !string.Equals(
                    Path.GetFullPath(path),
                    recoveryFullPath,
                    StringComparison.OrdinalIgnoreCase))
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
