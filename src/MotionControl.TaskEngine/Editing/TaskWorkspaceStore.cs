using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Configuration;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Device;
using MotionControl.TaskEngine.Machine;
using MotionControl.TaskEngine.Preparation;
using MotionControl.TaskEngine.Validation;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MotionControl.TaskEngine.Editing
{
    /// <summary>具体负责任务草稿、引用检查和应用文件，不连接运动卡。</summary>
    public sealed partial class TaskWorkspaceStore
    {
        /// <summary>沿用现有设备目录与原子 JSON 保存。</summary>
        private readonly MachineConfigurationStore _store;
        /// <summary>Host 声明的实际任务能力；为空时只能作定义检查，不代表可执行。</summary>
        private readonly TaskDeviceCapabilities _executionCapabilities;
        /// <summary>一份草稿包保存完整编辑状态，避免多个草稿半保存。</summary>
        private const string DraftPath = "Drafts/TaskWorkspace.json";
        /// <summary>应用恢复标记；存在时禁止创建新的任务运行会话。</summary>
        private const string PendingPath = "Drafts/TaskApplyPending.json";

        /// <summary>绑定已有设备存储；正式 Host 同时提供实际能力，使检查和应用提前拒绝不支持的动作。</summary>
        public TaskWorkspaceStore(MachineConfigurationStore store, TaskDeviceCapabilities executionCapabilities = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _executionCapabilities = executionCapabilities;
        }

        /// <summary>优先读取完整草稿；忽略草稿时只读运行文件，不自动删除草稿。</summary>
        public TaskWorkspaceDocument Load(string machineId, bool useDraft = true)
        {
            RequireNoPendingApply(machineId);
            string root = _store.GetMachineDirectory(machineId);
            string path = Path.Combine(root, DraftPath);
            if (useDraft && File.Exists(path))
            {
                byte[] bytes = File.ReadAllBytes(path);
                using (var stream = new MemoryStream(bytes))
                using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                {
                    var draft = ReadDraft(reader.ReadToEnd());
                    RequireDraftShape(machineId, draft);
                    draft.DraftSignature = HashBytes(bytes);
                    return draft;
                }
            }
            var result = new TaskWorkspaceDocument { MachineId = machineId, BaseSignature = ComputeSignature(machineId) };
            var taskStore = new TaskDefinitionStore(_store);
            string tasks = Path.Combine(root, "Tasks");
            if (Directory.Exists(tasks))
                foreach (string file in Directory.GetFiles(tasks, "*.json").OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
                    result.Tasks.Add(new TaskWorkspaceTask { FileName = Path.GetFileName(file), Definition = taskStore.LoadForEditing(machineId, Path.GetFileName(file)) });
            result.Machine = File.Exists(Path.Combine(root, "Parameters/TaskMachine.json"))
                ? new TaskMachineConfigurationStore(_store).LoadForEditing(machineId)
                : new TaskMachineConfiguration { MachineId = machineId, Name = machineId };
            return result;
        }

        /// <summary>允许业务未完成的草稿；锁内核对原草稿签名，拒绝覆盖别的窗口或外部更新。</summary>
        public void SaveDraft(TaskWorkspaceDocument draft)
        {
            RequireDraftShape(draft?.MachineId, draft);
            using (AcquireEditLock(draft.MachineId))
            {
                RequireNoPendingApply(draft.MachineId);
                RequireCurrentDraft(draft);
                DateTime savedAt = DateTime.Now;
                JObject document = JObject.FromObject(draft, TaskDefinitionJson.CreateSerializer());
                document["savedAt"] = JToken.FromObject(savedAt, TaskDefinitionJson.CreateSerializer());
                RequireDraftShape(draft.MachineId, ReadDraft(document.ToString()));
                _store.SaveJson(draft.MachineId, DraftPath, document);
                draft.SavedAt = savedAt;
                draft.DraftSignature = CurrentDraftSignature(draft.MachineId);
            }
        }

        /// <summary>备份当前草稿后只同步其来源签名，不修改硬件配置或运行任务文件。</summary>
        public string RebaseDraftToCurrentSources(TaskWorkspaceDocument draft)
        {
            RequireDraftShape(draft?.MachineId, draft);
            using (AcquireEditLock(draft.MachineId))
            {
                RequireNoPendingApply(draft.MachineId);
                RequireCurrentDraft(draft);

                string sourceSignature = ComputeSignature(draft.MachineId);
                if (string.Equals(sourceSignature, draft.BaseSignature, StringComparison.Ordinal))
                    throw new InvalidOperationException("任务来源已经与草稿一致，无需同步。");

                string backupPath = SaveLocalCopy(draft);
                if (!string.Equals(sourceSignature, ComputeSignature(draft.MachineId), StringComparison.Ordinal))
                    throw new InvalidOperationException("保留草稿期间来源文件再次变化，草稿未同步；已保留本次草稿副本：" + backupPath);

                string previousSignature = draft.BaseSignature;
                DateTime previousSavedAt = draft.SavedAt;
                string previousDraftSignature = draft.DraftSignature;
                DateTime savedAt = DateTime.Now;
                try
                {
                    draft.BaseSignature = sourceSignature;
                    JObject document = JObject.FromObject(draft, TaskDefinitionJson.CreateSerializer());
                    document["savedAt"] = JToken.FromObject(savedAt, TaskDefinitionJson.CreateSerializer());
                    RequireDraftShape(draft.MachineId, ReadDraft(document.ToString()));
                    if (!string.Equals(sourceSignature, ComputeSignature(draft.MachineId), StringComparison.Ordinal))
                        throw new InvalidOperationException("保存草稿前来源文件再次变化，草稿未同步；已保留本次草稿副本：" + backupPath);

                    _store.SaveJson(draft.MachineId, DraftPath, document);
                }
                catch
                {
                    draft.BaseSignature = previousSignature;
                    draft.SavedAt = previousSavedAt;
                    draft.DraftSignature = previousDraftSignature;
                    throw;
                }

                draft.SavedAt = savedAt;
                draft.DraftSignature = CurrentDraftSignature(draft.MachineId);
                return backupPath;
            }
        }

        /// <summary>把本窗口完整内容保留到唯一备份文件；可带入尚未保存的单个任务，不修改原工作区。</summary>
        public string SaveLocalCopy(TaskWorkspaceDocument draft, TaskWorkspaceTask editedTask = null)
        {
            RequireDraftShape(draft?.MachineId, draft);
            var copy = ReadDraft(JObject.FromObject(draft, TaskDefinitionJson.CreateSerializer()).ToString());
            if (editedTask != null)
            {
                var target = copy.Tasks.Single(item => string.Equals(item.FileName, editedTask.FileName, StringComparison.OrdinalIgnoreCase));
                target.Definition = TaskDefinitionJson.Deserialize(TaskDefinitionJson.Serialize(editedTask.Definition));
            }
            RequireDraftShape(draft.MachineId, copy);
            JObject document = JObject.FromObject(copy, TaskDefinitionJson.CreateSerializer());
            string machineDirectory = _store.GetMachineDirectory(draft.MachineId);
            string relativePath = "Backups/TaskLocal-" + Guid.NewGuid().ToString("N") + ".json";
            _store.SaveJson(draft.MachineId, relativePath, document);
            return Path.GetFullPath(Path.Combine(machineDirectory, relativePath));
        }

        /// <summary>把当前工作区导出为单个可拷走的 JSON 备份，不修改草稿或运行文件。</summary>
        public void ExportBackup(TaskWorkspaceDocument draft, string filePath, TaskWorkspaceTask editedTask = null)
        {
            if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("备份文件路径不能为空。", nameof(filePath));
            RequireDraftShape(draft?.MachineId, draft);

            var copy = ReadDraft(JObject.FromObject(draft, TaskDefinitionJson.CreateSerializer()).ToString());
            if (editedTask != null)
            {
                TaskWorkspaceTask target = copy.Tasks.SingleOrDefault(item => string.Equals(item.FileName, editedTask.FileName, StringComparison.OrdinalIgnoreCase));
                if (target == null) throw new InvalidDataException("当前编辑任务不属于这份设备工作区。");
                target.Definition = TaskDefinitionJson.Deserialize(TaskDefinitionJson.Serialize(editedTask.Definition));
            }

            RequireDraftShape(copy.MachineId, copy);
            string fullPath = Path.GetFullPath(filePath);
            string folder = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) throw new DirectoryNotFoundException("备份文件夹不存在：" + folder);

            JObject document = JObject.FromObject(copy, TaskDefinitionJson.CreateSerializer());
            string temporaryPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporaryPath, document.ToString(Formatting.Indented), new UTF8Encoding(false));
                if (File.Exists(fullPath)) File.Replace(temporaryPath, fullPath, null);
                else File.Move(temporaryPath, fullPath);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }

        /// <summary>严格读取备份并存为当前设备草稿；不直接改写运行任务文件。</summary>
        public TaskWorkspaceDocument ImportBackupAsDraft(string machineId, string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("备份文件路径不能为空。", nameof(filePath));
            if (!File.Exists(filePath)) throw new FileNotFoundException("找不到任务配置备份文件。", filePath);

            var backupFile = new FileInfo(filePath);
            if (backupFile.Length > 64L * 1024 * 1024) throw new InvalidDataException("任务配置备份超过 64 MB，已拒绝读取。");

            string json = File.ReadAllText(filePath, new UTF8Encoding(false, true));
            TaskWorkspaceDocument draft = ReadDraft(json);
            RequireDraftShape(machineId, draft);

            // 恢复是人员明确选择的操作；用当前硬件配置重新检查资源引用，而不是沿用备份时的旧签名。
            draft.BaseSignature = ComputeSignature(machineId);
            draft.DraftSignature = CurrentDraftSignature(machineId);
            SaveDraft(draft);
            return draft;
        }

        /// <summary>同一份草稿从读取到写入期间发生变化时，保留两边内容并要求重新核对。</summary>
        private void RequireCurrentDraft(TaskWorkspaceDocument draft)
        {
            if (!string.Equals(draft.DraftSignature, CurrentDraftSignature(draft.MachineId), StringComparison.Ordinal))
                throw new InvalidOperationException("任务草稿已被其他窗口或外部程序保存、删除或替换，本次未覆盖任何文件。请先保留当前编辑内容，再重新打开任务配置核对最新草稿。");
        }

        /// <summary>只读取草稿文件的实际字节签名；不存在与空文件明确区分。</summary>
        private string CurrentDraftSignature(string machineId)
        {
            string path = Path.Combine(_store.GetMachineDirectory(machineId), DraftPath);
            return File.Exists(path) ? HashBytes(File.ReadAllBytes(path)) : null;
        }

        /// <summary>使用固定 SHA256 表示内容，不受保存时间或文件时间精度影响。</summary>
        private static string HashBytes(byte[] bytes)
        {
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", string.Empty);
        }

        /// <summary>人员确认放弃后删除当前草稿，先保留一份可找回的副本。</summary>
        public void DiscardDraft(string machineId)
        {
            using (AcquireEditLock(machineId))
            {
                RequireNoPendingApply(machineId);
                string root = _store.GetMachineDirectory(machineId), path = Path.Combine(root, DraftPath);
                if (!File.Exists(path)) return;
                string backup = Path.Combine(root, "Backups", "TaskDraft-" + Guid.NewGuid().ToString("N") + ".json");
                Directory.CreateDirectory(Path.GetDirectoryName(backup)); File.Copy(path, backup, false); File.Delete(path);
            }
        }

        /// <summary>严格检查恢复路径；已存在的目录或链接也视为阻断，权限和读取错误不能当作没有标记。</summary>
        public bool HasPendingApply(string machineId)
        {
            string root = _store.GetMachineDirectory(machineId);
            RequirePlainTaskDirectory(root);
            RequirePlainTaskDirectory(Path.Combine(root, "Drafts"));
            try
            {
                File.GetAttributes(Path.Combine(root, PendingPath));
                return true;
            }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }

        /// <summary>按当前草稿的命名资源和已保存硬件配置建立目录，不从任务文字猜资源。</summary>
        public TaskResourceCatalog LoadCatalog(TaskWorkspaceDocument draft)
        { return new TaskResourceCatalogLoader(_store).Load(draft.MachineId, draft.Machine.NamedResources); }

        /// <summary>检查全部任务、生产清单和独立准备配置；只返回可定位问题，不修改运行文件。</summary>
        public IReadOnlyList<TaskValidationIssue> Check(TaskWorkspaceDocument draft)
        {
            RequireDraftShape(draft?.MachineId, draft);
            TaskResourceCatalog catalog = LoadCatalog(draft);
            var issues = new List<TaskValidationIssue>();
            var definitions = draft.Tasks.Select(item => item.Definition).ToList();
            if (definitions.Count > 0)
                issues.AddRange(new TaskDefinitionValidator().Validate(definitions[0], catalog, definitions.Skip(1)));
            issues.AddRange(new TaskDeviceCapabilityValidator().Validate(definitions, _executionCapabilities));
            foreach (string error in TaskMachineConfigurationValidator.Validate(draft.Machine, definitions, catalog))
                if (!issues.Any(issue => error.Contains(issue.ToString())))
                    issues.Add(new TaskValidationIssue(TaskIssueSeverity.Error, "设备清单", null, "stations", error));
            foreach (TaskValidationIssue issue in CheckPreparation(draft, catalog, definitions))
                if (!issues.Any(existing => existing.ToString() == issue.ToString())) issues.Add(issue);
            return issues.AsReadOnly();
        }

        /// <summary>只检查与任务有关的运行文件和引用目录，排除草稿、备份、变量恢复运行值。</summary>
        public string ComputeSignature(string machineId)
        {
            string root = _store.GetMachineDirectory(machineId);
            var paths = new List<string> { "Parameters/TaskMachine.json", TaskStationPreparationStore.RelativePath, "HardwareConfig/AxisParameters.json", "HardwareConfig/IoMapping.json",
                "Parameters/DeviceVariables.json", "Parameters/ManualAxisPositions.json", MachineAlarmCatalogStore.RelativeFilePath };
            string tasks = Path.Combine(root, "Tasks");
            if (Directory.Exists(tasks)) paths.AddRange(Directory.GetFiles(tasks, "*.json").Select(path => "Tasks/" + Path.GetFileName(path)));
            using (var bytes = new MemoryStream())
            using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
            using (var hash = SHA256.Create())
            {
                foreach (string path in paths.OrderBy(value => value, StringComparer.Ordinal))
                {
                    writer.Write(path);
                    bool exists = File.Exists(Path.Combine(root, path)); writer.Write(exists);
                    if (exists) { byte[] data = File.ReadAllBytes(Path.Combine(root, path)); writer.Write(data.Length); writer.Write(data); }
                }
                writer.Flush();
                return BitConverter.ToString(hash.ComputeHash(bytes.ToArray())).Replace("-", string.Empty);
            }
        }

        /// <summary>严格解析整个草稿包，不接受重复字段、未知属性或任意 CLR 类型。</summary>
        private static TaskWorkspaceDocument ReadDraft(string json)
        {
            using (var text = new StringReader(json))
            using (var reader = new JsonTextReader(text) { MaxDepth = 96, DateParseHandling = DateParseHandling.None })
            {
                var document = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if (reader.Read()) throw new InvalidDataException("草稿文件只能包含一个根对象。");
                TaskDefinitionJson.CheckPropertyNames(document);
                if (document["schemaVersion"]?.Type != JTokenType.Integer || (int)document["schemaVersion"] != 1
                    || document["tasks"]?.Type != JTokenType.Array || document["machine"]?.Type != JTokenType.Object)
                    throw new InvalidDataException("草稿缺少版本 1、tasks 数组或 machine 对象。");
                return document.ToObject<TaskWorkspaceDocument>(TaskDefinitionJson.CreateSerializer());
            }
        }

        /// <summary>草稿可以有业务错误，但不能含空任务、跨路径文件名或不一致设备标识。</summary>
        private static void RequireDraftShape(string machineId, TaskWorkspaceDocument draft)
        {
            if (draft == null || draft.SchemaVersion != 1 || string.IsNullOrWhiteSpace(machineId)
                || draft.MachineId != machineId || draft.Machine == null || draft.Machine.MachineId != machineId
                || draft.Tasks == null || draft.Tasks.Count > 128 || string.IsNullOrEmpty(draft.BaseSignature))
                throw new InvalidDataException("任务草稿版本、设备标识、任务集合或来源签名无效。");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (TaskWorkspaceTask item in draft.Tasks)
            {
                if (item == null || item.Definition == null) throw new InvalidDataException("草稿包含空任务。");
                ValidateTaskFileName(item.FileName);
                if (!names.Add(item.FileName)) throw new InvalidDataException("任务文件名重复：" + item.FileName);
                TaskDefinitionJson.Deserialize(TaskDefinitionJson.Serialize(item.Definition));
            }
        }

        /// <summary>任务文件只能是当前 Tasks 下的单个 JSON 文件。</summary>
        private static void ValidateTaskFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || Path.GetFileName(value) != value
                || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || value.Contains("/") || value.Contains("\\")
                || !value.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || value.Length <= 5)
                throw new InvalidDataException("任务文件名不是安全的单个 JSON 文件名：" + value);
        }

        /// <summary>不同编辑窗口不能同时写同一设备的草稿或运行定义。</summary>
        private FileStream AcquireEditLock(string machineId)
        {
            string root = _store.GetMachineDirectory(machineId);
            RequirePlainTaskDirectory(root);
            string directory = Path.Combine(root, "Drafts");
            RequirePlainTaskDirectory(directory);
            Directory.CreateDirectory(directory);
            return new FileStream(Path.Combine(directory, "TaskWorkspace.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
    }
}
