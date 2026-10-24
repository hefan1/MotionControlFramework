using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Machine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MotionControl.TaskEngine.Configuration
{
    /// <summary>设备任务清单固定保存于 Parameters/TaskMachine.json，复用设备隔离、备份与原子替换。</summary>
    public sealed class TaskMachineConfigurationStore
    {
        /// <summary>已有设备配置目录服务。</summary>
        private readonly MachineConfigurationStore _store;
        /// <summary>与单份任务文件分目录，避免枚举 Tasks 时把清单误当作任务。</summary>
        private const string RelativePath = "Parameters/TaskMachine.json";

        /// <summary>保存配置目录入口，不自动创建示例。</summary>
        public TaskMachineConfigurationStore(MachineConfigurationStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        /// <summary>读取并校验全部引用；清单设备标识必须与目录一致。</summary>
        public TaskMachineConfiguration Load(string machineId, IEnumerable<TaskDefinition> definitions, TaskResourceCatalog catalog)
        {
            // 正常运行入口不读取半应用清单；编辑器恢复使用明确的 LoadForEditing 入口。
            if (File.Exists(Path.Combine(_store.GetMachineDirectory(machineId), "Drafts/TaskApplyPending.json")))
                throw new InvalidOperationException("任务应用尚未完成，请先在任务配置页恢复上次应用。");
            var result = Deserialize(File.ReadAllText(Path.Combine(_store.GetMachineDirectory(machineId), RelativePath)));
            RequireValid(machineId, result, definitions, catalog);
            return result;
        }

        /// <summary>供编辑器读取清单形状；引用校验在检查和应用时执行。</summary>
        public TaskMachineConfiguration LoadForEditing(string machineId)
        {
            return Deserialize(File.ReadAllText(Path.Combine(_store.GetMachineDirectory(machineId), RelativePath)));
        }

        /// <summary>读取供资源目录加载使用的命名资源；调用方随后仍须执行完整 Load 校验。</summary>
        public IReadOnlyList<string> LoadNamedResources(string machineId)
        {
            string path = Path.Combine(_store.GetMachineDirectory(machineId), RelativePath);
            if (!File.Exists(path)) return new ReadOnlyCollection<string>(new List<string>());
            TaskMachineConfiguration configuration = Deserialize(File.ReadAllText(path));
            return new ReadOnlyCollection<string>(configuration.NamedResources ?? new List<string>());
        }

        /// <summary>校验成功才保存，内容变化必须递增修订号，覆盖返回原文件备份路径。</summary>
        public string Save(string machineId, TaskMachineConfiguration configuration,
            IEnumerable<TaskDefinition> definitions, TaskResourceCatalog catalog)
        {
            RequireValid(machineId, configuration, definitions, catalog);
            JObject document = JObject.FromObject(configuration, TaskDefinitionJson.CreateSerializer());
            Deserialize(document.ToString());
            string path = Path.Combine(_store.GetMachineDirectory(machineId), RelativePath);
            if (File.Exists(path))
            {
                TaskMachineConfiguration previous = Deserialize(File.ReadAllText(path));
                if (!string.Equals(previous.MachineId, machineId, StringComparison.Ordinal))
                    throw new InvalidOperationException("已有清单的设备标识与目录不一致，拒绝覆盖。");
                if (JToken.DeepEquals(document, JObject.FromObject(previous, TaskDefinitionJson.CreateSerializer()))) return null;
                if (configuration.Revision <= previous.Revision) throw new InvalidOperationException("设备清单内容变化后必须递增 revision。");
            }
            return _store.SaveJson(machineId, RelativePath, document);
        }

        /// <summary>严格读取版本、重复字段和枚举，不允许任意 CLR 类型信息。</summary>
        internal static TaskMachineConfiguration Deserialize(string json)
        {
            JObject document;
            using (var text = new StringReader(json))
            using (var reader = new JsonTextReader(text) { MaxDepth = 96, DateParseHandling = DateParseHandling.None })
            {
                document = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if (reader.Read()) throw new InvalidDataException("设备清单只能有一个 JSON 根对象。");
            }
            TaskDefinitionJson.CheckPropertyNames(document);
            if (document["schemaVersion"]?.Type != JTokenType.Integer || (int)document["schemaVersion"] != 1
                || document["machineId"]?.Type != JTokenType.String || document["name"]?.Type != JTokenType.String
                || document["revision"]?.Type != JTokenType.Integer || document["stations"]?.Type != JTokenType.Array)
                throw new InvalidDataException("设备清单必须有版本 1、machineId、name、整数 revision 和 stations 列表。");
            return document.ToObject<TaskMachineConfiguration>(TaskDefinitionJson.CreateSerializer());
        }

        /// <summary>冻结一份清单，外部界面修改不会影响运行中的设备。</summary>
        internal static TaskMachineConfiguration Copy(TaskMachineConfiguration configuration)
        {
            return Deserialize(JObject.FromObject(configuration, TaskDefinitionJson.CreateSerializer()).ToString());
        }

        /// <summary>汇总可定位错误，保存失败前不修改原文件。</summary>
        private static void RequireValid(string machineId, TaskMachineConfiguration configuration,
            IEnumerable<TaskDefinition> definitions, TaskResourceCatalog catalog)
        {
            var errors = TaskMachineConfigurationValidator.Validate(configuration, definitions, catalog).ToList();
            if (configuration == null || configuration.MachineId != machineId) errors.Add("设备清单标识与目录不一致。");
            if (errors.Count != 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        }
    }
}
