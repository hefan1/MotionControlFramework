using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

namespace MotionControl.TaskEngine.Configuration
{
    public sealed partial class MachineProjectPackageService
    {
        /// <summary>工程应用中断标记；存在时 Host 必须阻止连接和任务启动。</summary>
        public const string PendingRelativePath = "Runtime/EquipmentProjectApplyPending.json";

        /// <summary>工程应用或回滚尚未确认时为真；读取失败不能被视为不存在。</summary>
        public bool HasPendingApply
        {
            get
            {
                string path = PendingPath();
                try { File.GetAttributes(path); return true; }
                catch (FileNotFoundException) { return false; }
                catch (DirectoryNotFoundException) { return false; }
            }
        }

        /// <summary>列出该设备已保留的工程导入备份，不恢复或删除任何文件。</summary>
        public IReadOnlyList<string> ListBackups()
        {
            string root = _store.GetMachineDirectory(_machineId), directory = Path.Combine(root, "Backups");
            try
            {
                if ((File.GetAttributes(directory) & FileAttributes.Directory) == 0) throw new IOException("设备工程备份路径不是目录。");
            }
            catch (FileNotFoundException) { return Array.Empty<string>(); }
            catch (DirectoryNotFoundException) { return Array.Empty<string>(); }
            RejectReparsePoint(root); RejectReparsePoint(directory);
            var result = new List<string>();
            foreach (string path in Directory.GetDirectories(directory, "EquipmentProject-*", SearchOption.TopDirectoryOnly)
                .OrderByDescending(value => value, StringComparer.OrdinalIgnoreCase))
            {
                try { VerifyListedBackup(path); result.Add(path); }
                catch (Exception error) { result.Add(path + "（备份不完整或损坏：" + error.Message + "）"); }
            }
            return result.AsReadOnly();
        }

        /// <summary>预检后备份并事务式应用；调用方须在整个操作期间独占设备命令并持续提供安全门禁。</summary>
        public string Apply(MachineProjectPreview preview, Func<bool> canApply)
        {
            if (preview == null || !ReferenceEquals(preview.Owner, this) || preview.Contents == null || preview.CurrentBytes == null)
                throw new ArgumentException("工程预览不属于当前设备会话，请重新预览。", nameof(preview));
            if (canApply == null) throw new ArgumentNullException(nameof(canApply));
            using (AcquireProjectLock())
            {
                RequireNoPending(); RequireCanApply(canApply);
                Dictionary<string, string> current = ReadCurrentFiles(out Dictionary<string, byte[]> currentBytes, true);
                if (!string.Equals(ComputeSignature(currentBytes), preview.TargetSignature, StringComparison.Ordinal))
                    throw new InvalidOperationException("预览后设备配置发生变化，请重新预览。 ");
                ValidateContents(preview.Contents, _machineId, out string definition, out string owner, out string backend, out string name);
                var merged = new Dictionary<string, string>(current, StringComparer.OrdinalIgnoreCase);
                foreach (var item in preview.Contents) merged[item.Key] = item.Value;
                ValidateContents(merged, _machineId, out string mergedDefinition, out string mergedOwner, out string mergedBackend, out string mergedName, true);
                ValidateTeachPointMigration(current, merged, preview.Contents, null);
                RequireCanApply(canApply);
                ProjectApplyJournal journal = CreateBackup(preview.CurrentBytes, preview.CurrentContents, preview.Contents);
                ReadCurrentFiles(out Dictionary<string, byte[]> afterBackupBytes, true);
                if (!string.Equals(preview.TargetSignature, ComputeSignature(afterBackupBytes), StringComparison.Ordinal))
                    throw new InvalidOperationException("备份期间设备配置发生变化，未应用工程；已保留本次备份。 ");
                string pending = PendingPath();
                Directory.CreateDirectory(Path.GetDirectoryName(pending)); RejectReparsePoint(Path.GetDirectoryName(pending));
                WriteNewMarker(pending, JsonConvert.SerializeObject(journal, Formatting.Indented));
                try
                {
                    foreach (ProjectApplyFile item in journal.Files.Where(value => value.WillWrite).OrderBy(value => value.RelativePath, StringComparer.OrdinalIgnoreCase))
                    {
                        RequireCanApply(canApply);
                        string destination = ProjectPath(item.RelativePath);
                        string actual = CurrentTargetHash(destination);
                        string expected = item.Existed ? item.Sha256 : null;
                        if (!string.Equals(actual, expected, StringComparison.Ordinal))
                            throw new InvalidOperationException("写入前配置已被其他操作改变：" + item.RelativePath);
                        if (!preview.Contents.TryGetValue(item.RelativePath, out string value))
                            throw new InvalidDataException("工程文件快照缺失：" + item.RelativePath);
                        WriteConfiguration(destination, value);
                    }
                    RequireCanApply(canApply);
                    foreach (ProjectApplyFile item in journal.Files.Where(value => value.WillWrite))
                        if (!string.Equals(CurrentTargetHash(ProjectPath(item.RelativePath)), item.AppliedSha256, StringComparison.Ordinal))
                            throw new InvalidOperationException("工程写入后配置被其他操作改变：" + item.RelativePath);
                    File.Delete(pending);
                    return BackupDirectory(journal.BackupId);
                }
                catch (Exception error)
                {
                    try { RestoreBackup(journal); }
                    catch (Exception restoreError) { throw new AggregateException("工程应用失败，备份恢复未完成；保留中断标记并禁止连接。", error, restoreError); }
                    throw new InvalidOperationException("工程应用失败，已恢复原配置并保留备份。", error);
                }
            }
        }

        /// <summary>在设备未连接且调用方持有命令门禁时恢复中断前配置；任何失败保留标记。</summary>
        public void RecoverPendingApply(Func<bool> canApply)
        {
            if (canApply == null) throw new ArgumentNullException(nameof(canApply));
            using (AcquireProjectLock())
            {
                RequireCanApply(canApply);
                string path = PendingPath();
                if (!HasPendingApply) throw new InvalidOperationException("没有待恢复的工程应用。 ");
                RejectReparsePoint(path);
                ProjectApplyJournal journal = ReadStrictObject(File.ReadAllText(path, new UTF8Encoding(false, true)), "工程恢复记录").ToObject<ProjectApplyJournal>();
                RequireCanApply(canApply);
                RestoreBackup(journal, canApply);
            }
        }

        /// <summary>确保门禁由调用方实际提供，拒绝读取失败或条件不满足。</summary>
        private static void RequireCanApply(Func<bool> canApply)
        { if (!canApply()) throw new InvalidOperationException("设备仍连接、正在收尾或现场待确认，不能应用工程。 "); }

        /// <summary>有未完成的事务时禁止继续读取应用或覆盖证据。</summary>
        private void RequireNoPending()
        { if (HasPendingApply) throw new InvalidOperationException("上次设备工程应用未完成，请先恢复原配置。 "); }

        /// <summary>获取设备目录内固定标记路径。</summary>
        private string PendingPath()
        {
            string root = _store.GetMachineDirectory(_machineId);
            if (Directory.Exists(root)) RejectReparsePoint(root);
            string runtime = Path.Combine(root, "Runtime");
            if (Directory.Exists(runtime)) RejectReparsePoint(runtime);
            return Path.Combine(root, PendingRelativePath.Replace('/', Path.DirectorySeparatorChar));
        }

        /// <summary>取得当前设备配置中的受限文件路径。</summary>
        private string ProjectPath(string relativePath)
        { return Path.Combine(_store.GetMachineDirectory(_machineId), RequireAllowedPath(relativePath).Replace('/', Path.DirectorySeparatorChar)); }

        /// <summary>只允许以固定前缀及 GUID 指定备份目录。</summary>
        private string BackupDirectory(string id)
        {
            if (!Guid.TryParseExact(id, "N", out Guid ignored)) throw new InvalidDataException("工程备份编号无效。");
            return Path.Combine(_store.GetMachineDirectory(_machineId), "Backups", "EquipmentProject-" + id);
        }

        /// <summary>先复制整个受影响文件集合并计算签名，不修改任何运行配置。</summary>
        private ProjectApplyJournal CreateBackup(Dictionary<string, byte[]> current, Dictionary<string, string> currentText, Dictionary<string, string> incoming)
        {
            string root = _store.GetMachineDirectory(_machineId);
            Directory.CreateDirectory(root); RejectReparsePoint(root);
            string backups = Path.Combine(root, "Backups"); Directory.CreateDirectory(backups); RejectReparsePoint(backups);
            var journal = new ProjectApplyJournal { MachineId = _machineId, BackupId = Guid.NewGuid().ToString("N") };
            string backup = BackupDirectory(journal.BackupId); Directory.CreateDirectory(backup);
            var fullBackup = new HashSet<string>(incoming.Keys, StringComparer.OrdinalIgnoreCase);
            var hardware = new HashSet<string>(new[] { "HardwareConfig/MotionCards.json", "HardwareConfig/AxisParameters.json",
                "HardwareConfig/IoMapping.json", "HardwareConfig/SafetyInterlock.json" }, StringComparer.OrdinalIgnoreCase);
            if (currentText.TryGetValue("HardwareConfig/MotionCards.json", out string cardText))
            {
                var document = ReadStrictObject(cardText, "运动卡配置");
                var cards = document["motionCards"] as Newtonsoft.Json.Linq.JArray ?? throw new InvalidDataException("运动卡配置缺少运动卡列表。");
                foreach (var card in cards)
                {
                    string connection = ((string)card["connectionConfigurationFile"])?.Replace('\\', '/');
                    hardware.Add(RequireAllowedPath(connection));
                }
            }
            foreach (string path in current.Keys)
                if (!path.StartsWith("HardwareConfig/", StringComparison.OrdinalIgnoreCase) || hardware.Contains(path)) fullBackup.Add(path);
            foreach (string relative in fullBackup.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            {
                bool existed = current.ContainsKey(relative);
                bool willWrite = incoming.TryGetValue(relative, out string incomingText);
                var item = new ProjectApplyFile { RelativePath = relative, Existed = existed, WillWrite = willWrite,
                    AppliedSha256 = willWrite ? HashBytes(new UTF8Encoding(false).GetBytes(incomingText)) : null };
                if (existed)
                {
                    string target = Path.Combine(backup, relative.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    WriteNewFileBytes(target, current[relative]); item.Sha256 = HashFile(target);
                    if (!string.Equals(item.Sha256, HashBytes(current[relative]), StringComparison.Ordinal))
                        throw new IOException("工程备份写入校验失败：" + relative);
                }
                journal.Files.Add(item);
            }
            WriteNewFile(Path.Combine(backup, "BackupManifest.json"), JsonConvert.SerializeObject(journal, Formatting.Indented));
            return journal;
        }

        /// <summary>验证所有恢复记录和备份完整后才写回；错误时保留中断标记。</summary>
        private void RestoreBackup(ProjectApplyJournal journal, Func<bool> canContinue = null)
        {
            if (journal == null || !string.Equals(journal.MachineId, _machineId, StringComparison.Ordinal)
                || journal.Files == null || journal.Files.Count == 0 || journal.Files.Count > 1024)
                throw new InvalidDataException("工程恢复记录无效，未恢复文件。 ");
            string backup = BackupDirectory(journal.BackupId);
            RejectReparsePoint(_store.GetMachineDirectory(_machineId)); RejectReparsePoint(Path.GetDirectoryName(backup)); RejectReparsePoint(backup);
            string manifestPath = Path.Combine(backup, "BackupManifest.json");
            RejectReparsePoint(manifestPath);
            var manifest = ReadStrictObject(File.ReadAllText(manifestPath, new UTF8Encoding(false, true)), "工程备份清单");
            var pending = Newtonsoft.Json.Linq.JObject.FromObject(journal);
            if (!Newtonsoft.Json.Linq.JToken.DeepEquals(manifest, pending))
                throw new InvalidDataException("工程恢复记录与备份清单不一致，未恢复文件。 ");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ProjectApplyFile item in journal.Files)
            {
                if (item == null || !names.Add(RequireAllowedPath(item.RelativePath))) throw new InvalidDataException("工程恢复记录含重复路径。 ");
                if (item.RelativePath.StartsWith("Parameters/", StringComparison.OrdinalIgnoreCase) && !_parameterPaths.Contains(item.RelativePath))
                    throw new InvalidDataException("工程恢复记录包含未登记的参数文件：" + item.RelativePath);
                if (item.WillWrite && string.IsNullOrWhiteSpace(item.AppliedSha256)) throw new InvalidDataException("工程恢复记录缺少写入内容校验：" + item.RelativePath);
                if (!item.WillWrite && (!item.Existed || item.AppliedSha256 != null)) throw new InvalidDataException("工程恢复记录包含无效的保留文件：" + item.RelativePath);
                if (!item.Existed) continue;
                string source = Path.Combine(backup, item.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                RejectReparsePoint(Path.GetDirectoryName(source)); RejectReparsePoint(source);
                if (string.IsNullOrEmpty(item.Sha256) || !string.Equals(item.Sha256, HashFile(source), StringComparison.Ordinal))
                    throw new InvalidDataException("工程备份缺失或内容变化：" + item.RelativePath);
            }
            foreach (ProjectApplyFile item in journal.Files.Where(value => value.WillWrite)) RequireRecoveryTargetUnchanged(item);
            foreach (ProjectApplyFile item in journal.Files.Where(value => value.WillWrite))
            {
                if (canContinue != null) RequireCanApply(canContinue);
                // 其他程序可能在第一轮核查之后修改文件，写回前再次逐项核对。
                RequireRecoveryTargetUnchanged(item);
                string destination = ProjectPath(item.RelativePath);
                if (!item.Existed) { if (File.Exists(destination)) { RejectReparsePoint(Path.GetDirectoryName(destination)); RejectReparsePoint(destination); File.Delete(destination); } continue; }
                string source = Path.Combine(backup, item.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                WriteConfigurationBytes(destination, File.ReadAllBytes(source));
            }
            File.Delete(PendingPath());
        }

        /// <summary>计算备份文件的 SHA256，恢复前验证原始字节。</summary>
        private static string HashFile(string path)
        { using (var hash = SHA256.Create()) using (var stream = File.OpenRead(path)) return Convert.ToBase64String(hash.ComputeHash(stream)); }

        /// <summary>计算预览时冻结的原始字节签名。</summary>
        private static string HashBytes(byte[] bytes)
        { using (var hash = SHA256.Create()) return Convert.ToBase64String(hash.ComputeHash(bytes)); }

        /// <summary>严格读取目标原始字节签名；只有明确不存在时返回 null。</summary>
        private static string CurrentTargetHash(string path)
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                    throw new InvalidDataException("配置目标不是普通文件：" + path);
                RejectReparsePoint(Path.GetDirectoryName(path));
                return HashFile(path);
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }

        /// <summary>仅允许目标仍为本次应用前或应用后的字节；第三方变化必须保留。</summary>
        private void RequireRecoveryTargetUnchanged(ProjectApplyFile item)
        {
            string actual = CurrentTargetHash(ProjectPath(item.RelativePath));
            bool original = item.Existed && string.Equals(actual, item.Sha256, StringComparison.Ordinal);
            bool incoming = string.Equals(actual, item.AppliedSha256, StringComparison.Ordinal);
            bool absent = !item.Existed && actual == null;
            if (!original && !incoming && !absent)
                throw new InvalidOperationException("设备配置已被其他操作改变，保留现场文件与恢复标记，请人工核查：" + item.RelativePath);
        }

        /// <summary>查看备份时核对清单和每份文件；损坏项不会被标成可用备份。</summary>
        private void VerifyListedBackup(string path)
        {
            RejectReparsePoint(path);
            string name = Path.GetFileName(path);
            if (!name.StartsWith("EquipmentProject-", StringComparison.Ordinal)) throw new InvalidDataException("备份目录名无效。");
            string id = name.Substring("EquipmentProject-".Length);
            if (!Guid.TryParseExact(id, "N", out Guid ignored)) throw new InvalidDataException("备份编号无效。");
            string manifestPath = Path.Combine(path, "BackupManifest.json"); RejectReparsePoint(manifestPath);
            ProjectApplyJournal journal = ReadStrictObject(File.ReadAllText(manifestPath, new UTF8Encoding(false, true)), "工程备份清单").ToObject<ProjectApplyJournal>();
            if (journal == null || journal.MachineId != _machineId || journal.BackupId != id || journal.Files == null || journal.Files.Count == 0 || journal.Files.Count > 1024)
                throw new InvalidDataException("备份清单无效。");
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ProjectApplyFile item in journal.Files)
            {
                if (item == null || !paths.Add(RequireAllowedPath(item.RelativePath))) throw new InvalidDataException("备份清单包含无效路径。");
                if (item.RelativePath.StartsWith("Parameters/", StringComparison.OrdinalIgnoreCase) && !_parameterPaths.Contains(item.RelativePath))
                    throw new InvalidDataException("备份包含未登记参数：" + item.RelativePath);
                if (item.WillWrite && string.IsNullOrWhiteSpace(item.AppliedSha256)) throw new InvalidDataException("备份清单缺少应用内容校验：" + item.RelativePath);
                if (!item.WillWrite && (!item.Existed || item.AppliedSha256 != null)) throw new InvalidDataException("备份清单包含无效的保留文件：" + item.RelativePath);
                if (!item.Existed) continue;
                string source = Path.Combine(path, item.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                RejectReparsePoint(Path.GetDirectoryName(source)); RejectReparsePoint(source);
                if (!string.Equals(item.Sha256, HashFile(source), StringComparison.Ordinal))
                    throw new InvalidDataException("备份文件内容变化：" + item.RelativePath);
            }
        }

        /// <summary>原子写入 JSON 配置文本。</summary>
        private static void WriteConfiguration(string path, string text)
        { WriteConfigurationBytes(path, new UTF8Encoding(false).GetBytes(text)); }

        /// <summary>在同目录写临时文件并原子替换，避免留下半个 JSON。</summary>
        private static void WriteConfigurationBytes(string path, byte[] bytes)
        {
            string parent = Path.GetDirectoryName(path); Directory.CreateDirectory(parent); RejectReparsePoint(parent);
            if (File.Exists(path)) RejectReparsePoint(path);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        /// <summary>独占创建事务证据并刷新至存储。</summary>
        private static void WriteNewFile(string path, string text)
        {
            byte[] bytes = new UTF8Encoding(false).GetBytes(text);
            WriteNewFileBytes(path, bytes);
        }

        /// <summary>独占创建并刷新备份原始字节。</summary>
        private static void WriteNewFileBytes(string path, byte[] bytes)
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
        }

        /// <summary>先持久化临时文件，再独占创建完整标记；失败时不能遗留半份恢复记录。</summary>
        private static void WriteNewMarker(string path, string text)
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { WriteNewFile(temporary, text); File.Move(temporary, path); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        /// <summary>按设备根路径跨线程及进程串行化工程包事务。</summary>
        private IDisposable AcquireProjectLock()
        {
            string key;
            using (var hash = SHA256.Create()) key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(_store.GetMachineDirectory(_machineId).ToUpperInvariant()))).Replace("-", "");
            var mutex = new Mutex(false, @"Local\MotionControl.Project." + key);
            bool acquired;
            try { acquired = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            catch { mutex.Dispose(); throw; }
            if (!acquired) { mutex.Dispose(); throw new InvalidOperationException("另一个工程操作正在进行，请稍后重试。 "); }
            return new ProjectLock(mutex);
        }

        /// <summary>拥有互斥量的线程结束后释放工程事务锁。</summary>
        private sealed class ProjectLock : IDisposable
        {
            /// <summary>本线程持有的命名互斥量。</summary>
            private Mutex _mutex;
            /// <summary>记录当前线程已取得的互斥量。</summary>
            internal ProjectLock(Mutex mutex) { _mutex = mutex; }
            /// <summary>仅释放一次。</summary>
            public void Dispose() { Mutex mutex = _mutex; if (mutex == null) return; _mutex = null; mutex.ReleaseMutex(); mutex.Dispose(); }
        }
    }

    /// <summary>可验证的工程应用回滚证据，仅包含受限相对路径。</summary>
    internal sealed class ProjectApplyJournal
    {
        /// <summary>目标设备编号。</summary>
        public string MachineId { get; set; }
        /// <summary>唯一备份目录后缀。</summary>
        public string BackupId { get; set; }
        /// <summary>写入前所有受影响文件的存在性和签名。</summary>
        public List<ProjectApplyFile> Files { get; set; } = new List<ProjectApplyFile>();
    }

    /// <summary>需要按原始字节恢复的单个设备配置文件。</summary>
    internal sealed class ProjectApplyFile
    {
        /// <summary>限定在设备配置目录中的相对路径。</summary>
        public string RelativePath { get; set; }
        /// <summary>应用前文件是否存在。</summary>
        public bool Existed { get; set; }
        /// <summary>本次工程会覆盖此文件；仅这些文件参与中断回滚。</summary>
        public bool WillWrite { get; set; }
        /// <summary>已存在文件的备份 SHA256。</summary>
        public string Sha256 { get; set; }
        /// <summary>该次工程应用写入目标内容的 SHA256，用于识别并保留外部修改。</summary>
        public string AppliedSha256 { get; set; }
    }
}
