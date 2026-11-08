using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Editing;
using MotionControl.TaskEngine.Validation;
using Newtonsoft.Json.Linq;

namespace MotionControl.TaskEngine.Configuration
{
    /// <summary>按设备独立保存任务，复用现有配置服务的原子替换和备份。</summary>
    public sealed class TaskDefinitionStore
    {
        /// <summary>现有的设备配置根目录服务。</summary>
        private readonly MachineConfigurationStore _store;

        /// <summary>创建任务定义存储，不自动创建目录或写入示例。</summary>
        public TaskDefinitionStore(MachineConfigurationStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        /// <summary>读取用于编辑或组装任务库的定义；调用者还须用最新共享目录检查引用。</summary>
        public TaskDefinition LoadForEditing(string machineId, string fileName)
        {
            string path = GetPath(machineId, fileName);
            try
            {
                // 直接读取文本是为了在转成 JObject 之前拒绝重复字段；原子保存仍复用现有服务。
                return TaskDefinitionJson.Deserialize(File.ReadAllText(path, Encoding.UTF8));
            }
            catch (Exception error)
            {
                throw new InvalidOperationException("读取任务失败：设备=" + machineId + "，文件=" + fileName + "。", error);
            }
        }

        /// <summary>读取并使用当前目录校验定义和引用；仍不代表实时设备状态满足运行条件。</summary>
        public TaskDefinition Load(string machineId, string fileName, TaskResourceCatalog catalog,
            IEnumerable<TaskDefinition> dependencies = null)
        {
            // 应用中断时仅允许编辑恢复入口读取原始形状，正常加载不能接受半套运行文件。
            new TaskWorkspaceStore(_store).RequireNoPendingApply(machineId);
            TaskDefinition definition = LoadForEditing(machineId, fileName);
            EnsureDefinitionValid(definition, catalog, dependencies);
            return definition;
        }

        /// <summary>先检查全部定义和引用再保存；任何错误均保留原文件。修改已有定义必须递增修订号。</summary>
        public string Save(string machineId, string fileName, TaskDefinition definition,
            TaskResourceCatalog catalog, IEnumerable<TaskDefinition> dependencies = null)
        {
            string path = GetPath(machineId, fileName);
            EnsureDefinitionValid(definition, catalog, dependencies);

            string json = TaskDefinitionJson.Serialize(definition);
            JObject document = JObject.Parse(json);
            // 序列化后再通过严格读取入口，确保本程序写出的文件以后也可以读取。
            TaskDefinitionJson.Deserialize(json);
            if (File.Exists(path))
            {
                TaskDefinition previous = LoadForEditing(machineId, fileName);
                if (!string.Equals(previous.TaskId, definition.TaskId, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("不能用另一任务 ID 覆盖已有文件，请使用新文件名。");
                JObject oldDocument = JObject.Parse(TaskDefinitionJson.Serialize(previous));
                if (JToken.DeepEquals(oldDocument, document)) return null;
                if (definition.Revision <= previous.Revision)
                    throw new InvalidOperationException("任务内容已改变，revision 必须大于旧修订号 " + previous.Revision + "。");
            }
            return _store.SaveJson(machineId, "Tasks/" + fileName, document);
        }

        /// <summary>读取和保存采用同一个校验入口，并汇总所有带节点位置的错误。</summary>
        private static void EnsureDefinitionValid(TaskDefinition definition, TaskResourceCatalog catalog,
            IEnumerable<TaskDefinition> dependencies)
        {
            var issues = new TaskDefinitionValidator().Validate(definition, catalog, dependencies);
            var errors = issues.Where(issue => issue.Severity == TaskIssueSeverity.Error).ToList();
            if (errors.Count > 0)
                throw new InvalidOperationException("任务定义校验失败：\n"
                    + string.Join("\n", errors.Select(issue => issue.ToString())));
        }

        /// <summary>只允许 Tasks 目录中的单个 JSON 文件名，禁止跨设备或跨目录覆盖。</summary>
        private string GetPath(string machineId, string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName) || fileName != fileName.Trim()
                || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || fileName.Contains("/") || fileName.Contains("\\")
                || !string.Equals(Path.GetExtension(fileName), ".json", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(fileName)))
                throw new ArgumentException("任务文件名必须是单个 .json 文件名，不能包含路径。", nameof(fileName));
            return Path.Combine(_store.GetMachineDirectory(machineId), "Tasks", fileName);
        }
    }
}
