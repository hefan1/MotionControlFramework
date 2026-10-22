using System;
using System.Collections.Generic;
using System.IO;
using MotionControl.TaskEngine.Definitions;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace MotionControl.TaskEngine.Configuration
{
    /// <summary>任务 JSON 的固定格式入口；拒绝未知字段、未知版本和重复字段。</summary>
    public static class TaskDefinitionJson
    {
        /// <summary>解析任务定义；这里只验证文件形状，实际配置引用由 TaskDefinitionValidator 校验。</summary>
        public static TaskDefinition Deserialize(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new InvalidDataException("任务 JSON 不能为空。");
            JObject document;
            using (var text = new StringReader(json))
            using (var reader = new JsonTextReader(text) { MaxDepth = 96, DateParseHandling = DateParseHandling.None })
            {
                document = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if (reader.Read()) throw new InvalidDataException("任务文件只能包含一个 JSON 根对象。");
            }
            CheckPropertyNames(document);
            if (document["schemaVersion"]?.Type != JTokenType.Integer || (int)document["schemaVersion"] != 1)
                throw new InvalidDataException("任务 schemaVersion 缺失或不是当前支持的版本 1。");
            if (document["taskId"]?.Type != JTokenType.String || document["name"]?.Type != JTokenType.String
                || document["revision"]?.Type != JTokenType.Integer || document["root"]?.Type != JTokenType.Object)
                throw new InvalidDataException("任务必须包含 taskId、name、整数 revision 和 root 对象。");
            return document.ToObject<TaskDefinition>(CreateSerializer());
        }

        /// <summary>生成不带 CLR 类型名的 JSON；循环对象会明确报错。</summary>
        public static string Serialize(TaskDefinition definition)
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            return JObject.FromObject(definition, CreateSerializer()).ToString(Formatting.Indented);
        }

        /// <summary>统一固定 JSON 规则，不启用任意类型还原。</summary>
        private static JsonSerializer CreateSerializer()
        {
            var serializer = new JsonSerializer
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver(),
                TypeNameHandling = TypeNameHandling.None,
                MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
                MissingMemberHandling = MissingMemberHandling.Error,
                NullValueHandling = NullValueHandling.Ignore,
                MaxDepth = 96
            };
            serializer.Converters.Add(new StringEnumConverter { AllowIntegerValues = false });
            serializer.Converters.Add(new TaskNodeJsonConverter());
            return serializer;
        }

        /// <summary>拒绝大小写重复字段和类型元数据，避免同一个字段存在两种解释。</summary>
        private static void CheckPropertyNames(JToken token)
        {
            if (token is JObject item)
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (JProperty property in item.Properties())
                {
                    if (property.Name.StartsWith("$", StringComparison.Ordinal) || !names.Add(property.Name))
                        throw new InvalidDataException("任务字段重复或含有不允许的类型元数据：" + property.Path);
                    CheckPropertyNames(property.Value);
                }
            }
            else if (token is JArray array)
            {
                foreach (JToken child in array) CheckPropertyNames(child);
            }
        }
    }
}
