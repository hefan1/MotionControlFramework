using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MotionControl.TaskEngine.Configuration;
using MotionControl.TaskEngine.Validation;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MotionControl.TaskEngine.Editing
{
    /// <summary>应用任务文件的明确备份、写入与恢复顺序。</summary>
    public sealed partial class TaskWorkspaceStore
    {
        /// <summary>调用方先确认运行会话已结束；此处再锁定文件、检查签名和引用后应用。</summary>
        public TaskWorkspaceDocument Apply(TaskWorkspaceDocument draft, out string warning)
        {
            warning = null;
            RequireDraftShape(draft?.MachineId, draft);
            using (AcquireEditLock(draft.MachineId))
            {
                RequireNoPendingApply(draft.MachineId);
                if (ComputeSignature(draft.MachineId) != draft.BaseSignature)
                    throw new InvalidOperationException("运行任务或关联配置已经变化，请重新载入后检查；当前草稿仍保留。");
                var errors = Check(draft).Where(issue => issue.Severity == TaskIssueSeverity.Error).ToList();
                if (errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
                TaskWorkspaceDocument active = Load(draft.MachineId, false);
                TaskWorkspaceDocument candidate = ReadDraft(JObject.FromObject(draft, TaskDefinitionJson.CreateSerializer()).ToString());
                PrepareRevisions(active, candidate);
                TaskApplyJournal journal = CreateBackup(active, candidate);
                if (ComputeSignature(draft.MachineId) != draft.BaseSignature)
                    throw new InvalidOperationException("备份期间来源文件发生变化，未写入运行文件，请重新载入。");
                _store.SaveJson(draft.MachineId, PendingPath, JObject.FromObject(journal, TaskDefinitionJson.CreateSerializer()));
                try
                {
                    var catalog = LoadCatalog(candidate);
                    var definitions = candidate.Tasks.Select(item => item.Definition).ToList();
                    var taskStore = new TaskDefinitionStore(_store);
                    foreach (TaskWorkspaceTask item in candidate.Tasks)
                        taskStore.Save(candidate.MachineId, item.FileName, item.Definition, catalog,
                            definitions.Where(task => !ReferenceEquals(task, item.Definition)));
                    new TaskMachineConfigurationStore(_store).Save(candidate.MachineId, candidate.Machine, definitions, catalog);
                    string root = _store.GetMachineDirectory(candidate.MachineId);
                    foreach (TaskWorkspaceTask removed in active.Tasks.Where(old => !candidate.Tasks.Any(item => string.Equals(item.FileName, old.FileName, StringComparison.OrdinalIgnoreCase))))
                        File.Delete(Path.Combine(root, "Tasks", removed.FileName));
                    File.Delete(Path.Combine(root, PendingPath));
                }
                catch (Exception applyError)
                {
                    try { RestoreBackup(draft.MachineId, journal); }
                    catch (Exception restoreError) { throw new AggregateException("应用失败且自动恢复未完成，保留恢复标记并禁止加载运行任务。", applyError, restoreError); }
                    throw new InvalidOperationException("任务应用失败，运行文件已恢复，草稿仍保留。", applyError);
                }
                candidate.BaseSignature = ComputeSignature(candidate.MachineId);
                try { File.Delete(Path.Combine(_store.GetMachineDirectory(candidate.MachineId), DraftPath)); }
                catch (Exception error) { warning = "运行文件已应用，但旧草稿未能清理：" + error.Message; }
                return candidate;
            }
        }

        /// <summary>意外退出后的半应用文件不得进入新的任务会话。</summary>
        public void RequireNoPendingApply(string machineId)
        {
            if (File.Exists(Path.Combine(_store.GetMachineDirectory(machineId), PendingPath)))
                throw new InvalidOperationException("上次任务应用未完成，请在任务配置页恢复上次应用，再载入任务。");
        }

        /// <summary>运行会话结束后由工程入口调用，恢复标记中的原始文件。</summary>
        public void RecoverPendingApply(string machineId)
        {
            using (AcquireEditLock(machineId))
            {
                string path = Path.Combine(_store.GetMachineDirectory(machineId), PendingPath);
                if (!File.Exists(path)) return;
                using (var text = new StringReader(File.ReadAllText(path)))
                using (var reader = new JsonTextReader(text) { MaxDepth = 96, DateParseHandling = DateParseHandling.None })
                {
                    JObject document = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                    if (reader.Read()) throw new InvalidDataException("恢复文件只能有一个根对象。");
                    TaskDefinitionJson.CheckPropertyNames(document);
                    var journal = document.ToObject<TaskApplyJournal>(TaskDefinitionJson.CreateSerializer());
                    RestoreBackup(machineId, journal);
                }
            }
        }

        /// <summary>只有内容变化才递增修订号；界面不让人员猜下一个编号。</summary>
        private static void PrepareRevisions(TaskWorkspaceDocument active, TaskWorkspaceDocument candidate)
        {
            foreach (TaskWorkspaceTask item in candidate.Tasks)
            {
                var old = active.Tasks.FirstOrDefault(value => string.Equals(value.FileName, item.FileName, StringComparison.OrdinalIgnoreCase));
                if (old == null) { item.Definition.Revision = 1; continue; }
                item.Definition.Revision = old.Definition.Revision;
                if (TaskDefinitionJson.Serialize(item.Definition) != TaskDefinitionJson.Serialize(old.Definition))
                    item.Definition.Revision = checked(old.Definition.Revision + 1);
            }
            candidate.Machine.Revision = active.Machine.Revision;
            if (!JToken.DeepEquals(JObject.FromObject(candidate.Machine, TaskDefinitionJson.CreateSerializer()), JObject.FromObject(active.Machine, TaskDefinitionJson.CreateSerializer())))
                candidate.Machine.Revision = checked(active.Machine.Revision + 1);
        }

        /// <summary>写运行文件前复制原字节；备份作为现场历史保留，不递归清理目录。</summary>
        private TaskApplyJournal CreateBackup(TaskWorkspaceDocument active, TaskWorkspaceDocument candidate)
        {
            var journal = new TaskApplyJournal { MachineId = candidate.MachineId, BackupId = Guid.NewGuid().ToString("N") };
            string root = _store.GetMachineDirectory(candidate.MachineId);
            var paths = active.Tasks.Select(item => "Tasks/" + item.FileName).Concat(candidate.Tasks.Select(item => "Tasks/" + item.FileName))
                .Concat(new[] { "Parameters/TaskMachine.json" }).Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths)
            {
                string original = Path.Combine(root, path);
                bool existed = File.Exists(original);
                journal.Files.Add(new TaskApplyFile { RelativePath = path, Existed = existed });
                if (!existed) continue;
                string backup = Path.Combine(root, "Backups", "TaskApply-" + journal.BackupId, path);
                Directory.CreateDirectory(Path.GetDirectoryName(backup));
                File.Copy(original, backup, false);
            }
            return journal;
        }

        /// <summary>只恢复本设备 Tasks 与固定清单；任何恢复失败都保留标记。</summary>
        private void RestoreBackup(string machineId, TaskApplyJournal journal)
        {
            Guid backupId;
            if (journal == null || journal.MachineId != machineId || !Guid.TryParseExact(journal.BackupId, "N", out backupId)
                || journal.Files == null || journal.Files.Count == 0 || journal.Files.Count > 257)
                throw new InvalidDataException("任务恢复记录无效，未触碰运行文件。");
            string root = _store.GetMachineDirectory(machineId);
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (TaskApplyFile file in journal.Files)
            {
                if (file == null || file.RelativePath == null) throw new InvalidDataException("恢复记录包含空路径。");
                if (!paths.Add(file.RelativePath)) throw new InvalidDataException("恢复记录路径重复。");
                if (file.RelativePath != "Parameters/TaskMachine.json")
                {
                    if (!file.RelativePath.StartsWith("Tasks/", StringComparison.Ordinal)) throw new InvalidDataException("恢复路径超出任务目录。");
                    ValidateTaskFileName(file.RelativePath.Substring(6));
                }
                if (file.Existed && !File.Exists(Path.Combine(root, "Backups", "TaskApply-" + journal.BackupId, file.RelativePath)))
                    throw new InvalidDataException("恢复备份缺失，未触碰运行文件：" + file.RelativePath);
            }
            foreach (TaskApplyFile file in journal.Files)
            {
                string destination = Path.Combine(root, file.RelativePath);
                if (!file.Existed) { if (File.Exists(destination)) File.Delete(destination); continue; }
                string source = Path.Combine(root, "Backups", "TaskApply-" + journal.BackupId, file.RelativePath);
                string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".restore";
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                File.Copy(source, temporary, false);
                if (File.Exists(destination)) File.Replace(temporary, destination, null);
                else File.Move(temporary, destination);
            }
            File.Delete(Path.Combine(root, PendingPath));
        }
    }

    /// <summary>只描述这次任务应用需要恢复的固定目录文件。</summary>
    internal sealed class TaskApplyJournal
    {
        /// <summary>所属设备，恢复时必须一致。</summary>
        [JsonProperty(Required = Required.Always)]
        public string MachineId { get; set; }
        /// <summary>备份目录的固定 GUID 后缀。</summary>
        [JsonProperty(Required = Required.Always)]
        public string BackupId { get; set; }
        /// <summary>写入前的文件存在性清单。</summary>
        [JsonProperty(Required = Required.Always)]
        public List<TaskApplyFile> Files { get; set; } = new List<TaskApplyFile>();
    }
    /// <summary>单个运行文件的恢复记录。</summary>
    internal sealed class TaskApplyFile
    {
        /// <summary>受限的 Tasks 文件或固定设备清单路径。</summary>
        [JsonProperty(Required = Required.Always)]
        public string RelativePath { get; set; }
        /// <summary>原来不存在的文件回滚时删除，原来存在的按原字节恢复。</summary>
        [JsonProperty(Required = Required.Always)]
        public bool Existed { get; set; }
    }
}
