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
using MotionControl.TaskEngine.Machine;
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
        /// <summary>一份草稿包保存完整编辑状态，避免多个草稿半保存。</summary>
        private const string DraftPath = "Drafts/TaskWorkspace.json";
        /// <summary>应用恢复标记；存在时禁止创建新的任务运行会话。</summary>
        private const string PendingPath = "Drafts/TaskApplyPending.json";

        /// <summary>绑定已有设备存储。</summary>
        public TaskWorkspaceStore(MachineConfigurationStore store)
        { _store = store ?? throw new ArgumentNullException(nameof(store)); }

        /// <summary>优先读取完整草稿；忽略草稿时只读运行文件，不自动删除草稿。</summary>
        public TaskWorkspaceDocument Load(string machineId, bool useDraft = true)
        {
            RequireNoPendingApply(machineId);
            string root = _store.GetMachineDirectory(machineId);
            string path = Path.Combine(root, DraftPath);
            if (useDraft && File.Exists(path))
            {
                var draft = ReadDraft(File.ReadAllText(path));
                RequireDraftShape(machineId, draft);
                return draft;
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

        /// <summary>允许语义未完成的草稿，但必须能严格往返读取，且文件名与设备边界有效。</summary>
        public void SaveDraft(TaskWorkspaceDocument draft)
        {
            RequireDraftShape(draft?.MachineId, draft);
            using (AcquireEditLock(draft.MachineId))
            {
                RequireNoPendingApply(draft.MachineId);
                draft.SavedAt = DateTime.Now;
                JObject document = JObject.FromObject(draft, TaskDefinitionJson.CreateSerializer());
                RequireDraftShape(draft.MachineId, ReadDraft(document.ToString()));
                _store.SaveJson(draft.MachineId, DraftPath, document);
            }
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

        /// <summary>供界面决定是否显示恢复入口，不读取或猜测设备状态。</summary>
        public bool HasPendingApply(string machineId)
        { return File.Exists(Path.Combine(_store.GetMachineDirectory(machineId), PendingPath)); }

        /// <summary>按当前草稿的命名资源和已保存硬件配置建立目录，不从任务文字猜资源。</summary>
        public TaskResourceCatalog LoadCatalog(TaskWorkspaceDocument draft)
        { return new TaskResourceCatalogLoader(_store).Load(draft.MachineId, draft.Machine.NamedResources); }

        /// <summary>检查全部任务和工位；只返回可定位问题，不修改运行文件。</summary>
        public IReadOnlyList<TaskValidationIssue> Check(TaskWorkspaceDocument draft)
        {
            RequireDraftShape(draft?.MachineId, draft);
            TaskResourceCatalog catalog = LoadCatalog(draft);
            var issues = new List<TaskValidationIssue>();
            var definitions = draft.Tasks.Select(item => item.Definition).ToList();
            if (definitions.Count > 0)
                issues.AddRange(new TaskDefinitionValidator().Validate(definitions[0], catalog, definitions.Skip(1)));
            foreach (string error in TaskMachineConfigurationValidator.Validate(draft.Machine, definitions, catalog))
                if (!issues.Any(issue => error.Contains(issue.ToString())))
                    issues.Add(new TaskValidationIssue(TaskIssueSeverity.Error, "设备清单", null, "stations", error));
            return issues.AsReadOnly();
        }

        /// <summary>只检查与任务有关的运行文件和引用目录，排除草稿、备份、变量恢复运行值。</summary>
        public string ComputeSignature(string machineId)
        {
            string root = _store.GetMachineDirectory(machineId);
            var paths = new List<string> { "Parameters/TaskMachine.json", "HardwareConfig/AxisParameters.json", "HardwareConfig/IoMapping.json",
                "Parameters/DeviceVariables.json", "Parameters/ManualAxisPositions.json" };
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
            string directory = Path.Combine(_store.GetMachineDirectory(machineId), "Drafts");
            Directory.CreateDirectory(directory);
            return new FileStream(Path.Combine(directory, "TaskWorkspace.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
    }
}
