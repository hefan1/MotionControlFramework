using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MotionControl.TaskEngine.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MotionControl.TaskEngine.Editing
{
    public sealed partial class TaskWorkspaceStore
    {
        /// <summary>先保存所有原字节和原/应用/备份哈希，再写只读历史清单；此阶段不修改运行文件。</summary>
        private TaskApplyJournal CreateBackup(string machineId, Dictionary<string, byte[]> appliedContents)
        {
            var journal = new TaskApplyJournal { MachineId = machineId, BackupId = Guid.NewGuid().ToString("N") };
            string root = _store.GetMachineDirectory(machineId);
            RequirePlainTaskDirectory(root);
            string backups = Path.Combine(root, "Backups");
            Directory.CreateDirectory(backups);
            RequirePlainTaskDirectory(backups);
            string backupRoot = Path.Combine(backups, "TaskApply-" + journal.BackupId);
            Directory.CreateDirectory(backupRoot);
            foreach (var change in appliedContents.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
            {
                ValidateApplyRelativePath(change.Key);
                byte[] original = ReadTaskFileBytes(Path.Combine(root, change.Key));
                var file = new TaskApplyFile
                {
                    RelativePath = change.Key,
                    Existed = original != null,
                    AppliedExists = change.Value != null,
                    OriginalSha256 = original == null ? null : HashBytes(original),
                    AppliedSha256 = change.Value == null ? null : HashBytes(change.Value)
                };
                if (original != null)
                {
                    string backup = Path.Combine(backupRoot, change.Key);
                    WriteNewTaskFileBytes(backup, original);
                    file.BackupSha256 = CurrentTaskFileHash(backup);
                    if (file.BackupSha256 != file.OriginalSha256)
                        throw new IOException("任务备份写入校验失败：" + change.Key);
                }
                journal.Files.Add(file);
            }
            string manifest = JObject.FromObject(journal, TaskDefinitionJson.CreateSerializer()).ToString();
            WriteNewTaskFileBytes(Path.Combine(backupRoot, "BackupManifest.json"), new UTF8Encoding(false).GetBytes(manifest));
            return journal;
        }

        /// <summary>核对整个清单、全部备份和全部目标后才恢复；第三方改动或任何失败都保留 pending。</summary>
        private void RestoreBackup(string machineId, TaskApplyJournal journal)
        {
            Guid backupId;
            if (journal == null || journal.SchemaVersion != 2 || journal.MachineId != machineId
                || !Guid.TryParseExact(journal.BackupId, "N", out backupId)
                || journal.Files == null || journal.Files.Count == 0 || journal.Files.Count > 257)
                throw new InvalidDataException("任务恢复记录无效，未修改运行文件；恢复标记已保留。");
            string root = _store.GetMachineDirectory(machineId);
            string backupRoot = Path.Combine(root, "Backups", "TaskApply-" + journal.BackupId);
            RequirePlainTaskDirectory(root);
            RequirePlainTaskDirectory(Path.Combine(root, "Backups"));
            RequirePlainTaskDirectory(backupRoot);
            byte[] manifestBytes = ReadTaskFileBytes(Path.Combine(backupRoot, "BackupManifest.json"));
            if (manifestBytes == null) throw new InvalidDataException("任务备份清单缺失，未修改运行文件；恢复标记已保留。");
            TaskApplyJournal manifest = ReadApplyJournal(Encoding.UTF8.GetString(manifestBytes));
            if (!JToken.DeepEquals(JObject.FromObject(journal, TaskDefinitionJson.CreateSerializer()),
                JObject.FromObject(manifest, TaskDefinitionJson.CreateSerializer())))
                throw new InvalidDataException("任务恢复记录与备份清单不一致，未修改运行文件；恢复标记已保留。");

            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var originals = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (TaskApplyFile file in journal.Files)
            {
                if (file == null) throw new InvalidDataException("任务恢复清单包含空项，未修改运行文件。");
                ValidateApplyRelativePath(file.RelativePath);
                if (!paths.Add(file.RelativePath)) throw new InvalidDataException("任务恢复清单路径重复：" + file.RelativePath);
                ValidateApplyHashes(file);
                byte[] original = ReadTaskFileBytes(Path.Combine(backupRoot, file.RelativePath));
                if (file.Existed && (original == null || HashBytes(original) != file.BackupSha256))
                    throw new InvalidDataException("任务备份缺失或内容已改变，未修改运行文件：" + file.RelativePath);
                if (!file.Existed && original != null)
                    throw new InvalidDataException("任务备份出现清单之外的原内容，未修改运行文件：" + file.RelativePath);
                originals.Add(file.RelativePath, original);
            }

            // 必须先检查全部目标。不能恢复完前几个文件，才发现后一个已被别人改过。
            foreach (TaskApplyFile file in journal.Files) RequireRecoveryTargetUnchanged(root, file);

            int restored = 0;
            try
            {
                foreach (TaskApplyFile file in journal.Files)
                {
                    RequireRecoveryTargetUnchanged(root, file);
                    string destination = Path.Combine(root, file.RelativePath);
                    if (CurrentTaskFileHash(destination) == file.OriginalSha256) continue;
                    byte[] original = originals[file.RelativePath];
                    if (original == null) File.Delete(destination);
                    else WriteTaskFileBytes(destination, original, file.AppliedSha256);
                    restored++;
                }
                foreach (TaskApplyFile file in journal.Files)
                    if (CurrentTaskFileHash(Path.Combine(root, file.RelativePath)) != file.OriginalSha256)
                        throw new IOException("任务恢复后的内容核对失败：" + file.RelativePath);
                File.Delete(Path.Combine(root, PendingPath));
            }
            catch (Exception error)
            {
                string result = restored == 0 ? "任务恢复被拒绝，尚未修改运行文件。" : "任务恢复部分完成，已恢复 " + restored + " 个文件，其余恢复未完成。";
                throw new InvalidOperationException(result + "备份与恢复标记已保留，禁止加载任务；请核查具体文件后重试。", error);
            }
        }

        /// <summary>只接收当前带哈希版本；无哈希旧记录保留给人工核对，不能猜测目标是否仍属于旧应用。</summary>
        private static TaskApplyJournal ReadApplyJournal(string json)
        {
            using (var text = new StringReader(json))
            using (var reader = new JsonTextReader(text) { MaxDepth = 32, DateParseHandling = DateParseHandling.None })
            {
                JObject document = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if (reader.Read()) throw new InvalidDataException("任务恢复记录只能有一个根对象。");
                TaskDefinitionJson.CheckPropertyNames(document);
                if (document["schemaVersion"]?.Type != JTokenType.Integer || (int)document["schemaVersion"] != 2)
                    throw new InvalidDataException("任务恢复记录缺少版本 2 的内容校验，不能自动覆盖；请人工核对原文件和备份，保留恢复标记。");
                return document.ToObject<TaskApplyJournal>(TaskDefinitionJson.CreateSerializer());
            }
        }

        /// <summary>仅允许固定生产清单与 Tasks 目录的单个 JSON 文件，防止恢复到其他配置目录。</summary>
        private static void ValidateApplyRelativePath(string relativePath)
        {
            if (relativePath == "Parameters/TaskMachine.json") return;
            if (relativePath == null || !relativePath.StartsWith("Tasks/", StringComparison.Ordinal))
                throw new InvalidDataException("任务恢复路径超出允许目录：" + relativePath);
            ValidateTaskFileName(relativePath.Substring(6));
        }

        /// <summary>文件存在性与哈希必须完整一致；新文件、删除文件和空文件不能混为一种情况。</summary>
        private static void ValidateApplyHashes(TaskApplyFile file)
        {
            if (file.Existed)
            {
                if (!IsTaskHash(file.OriginalSha256) || !IsTaskHash(file.BackupSha256) || file.OriginalSha256 != file.BackupSha256)
                    throw new InvalidDataException("任务原内容或备份哈希无效：" + file.RelativePath);
            }
            else if (file.OriginalSha256 != null || file.BackupSha256 != null)
                throw new InvalidDataException("不存在的原任务文件不应有备份哈希：" + file.RelativePath);
            if (file.AppliedExists ? !IsTaskHash(file.AppliedSha256) : file.AppliedSha256 != null)
                throw new InvalidDataException("任务应用内容哈希与存在性不一致：" + file.RelativePath);
        }

        /// <summary>SHA256 使用当前存储统一的 64 位大写十六进制格式。</summary>
        private static bool IsTaskHash(string value)
        {
            return value != null && value.Length == 64 && value.All(character => character >= '0' && character <= '9' || character >= 'A' && character <= 'F');
        }

        /// <summary>目标只能仍为原内容或本次应用内容；冲突时同时显示三个版本的哈希供核查。</summary>
        private static void RequireRecoveryTargetUnchanged(string root, TaskApplyFile file)
        {
            string actual = CurrentTaskFileHash(Path.Combine(root, file.RelativePath));
            bool original = file.Existed ? actual == file.OriginalSha256 : actual == null;
            bool applied = file.AppliedExists ? actual == file.AppliedSha256 : actual == null;
            if (!original && !applied)
                throw new InvalidOperationException("任务文件已被其他操作改变，拒绝自动覆盖并保留恢复标记：" + file.RelativePath
                    + "；原内容=" + (file.OriginalSha256 ?? "不存在") + "；应用内容=" + (file.AppliedSha256 ?? "不存在")
                    + "；当前内容=" + (actual ?? "不存在") + "。请先核对文件与备份，不要清除恢复标记。");
        }

        /// <summary>严格读取普通文件；只有明确不存在才返回 null，目录、链接和权限错误都不能伪装成缺文件。</summary>
        private static byte[] ReadTaskFileBytes(string path)
        {
            RequirePlainTaskDirectory(Path.GetDirectoryName(path));
            try
            {
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                    throw new InvalidDataException("任务文件目标不是普通文件：" + path);
                return File.ReadAllBytes(path);
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }

        /// <summary>使用真实字节计算目标哈希；空文件有哈希，不存在的文件没有哈希。</summary>
        private static string CurrentTaskFileHash(string path)
        {
            byte[] bytes = ReadTaskFileBytes(path);
            return bytes == null ? null : HashBytes(bytes);
        }

        /// <summary>拒绝把任务或备份写入链接目录；尚不存在的目录由明确的写入步骤创建。</summary>
        private static void RequirePlainTaskDirectory(string path)
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("任务文件的父路径不是普通目录：" + path);
            }
            catch (FileNotFoundException) { return; }
            catch (DirectoryNotFoundException) { return; }
        }

        /// <summary>写入新备份或临时文件并刷新到磁盘；已有同名文件不会被覆盖。</summary>
        private static void WriteNewTaskFileBytes(string path, byte[] bytes)
        {
            string directory = Path.GetDirectoryName(path);
            Directory.CreateDirectory(directory);
            RequirePlainTaskDirectory(directory);
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
        }

        /// <summary>同目录临时文件完成后再次核对目标内容，再原子替换；发现外部修改即拒绝，未参与编辑锁的外部进程仍须停止并发写入。</summary>
        private static void WriteTaskFileBytes(string path, byte[] bytes, string expectedHash)
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".restore";
            try
            {
                WriteNewTaskFileBytes(temporary, bytes);
                if (CurrentTaskFileHash(path) != expectedHash)
                    throw new InvalidOperationException("任务文件在写回前再次变化，保留当前文件与恢复标记：" + path);
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }
}
