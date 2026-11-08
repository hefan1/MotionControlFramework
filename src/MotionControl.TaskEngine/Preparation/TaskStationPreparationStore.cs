using System;
using System.IO;
using System.Text;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.TaskEngine.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MotionControl.TaskEngine.Preparation
{
    /// <summary>读取独立的准备配置；编辑检查、工程预检和最终装载使用同一文件格式，不访问设备。</summary>
    public sealed class TaskStationPreparationStore
    {
        /// <summary>准备配置与生产任务清单分开保存，任务草稿应用不会覆盖本文件。</summary>
        public const string RelativePath = "Parameters/StationPreparation.json";

        /// <summary>提供当前设备的配置目录。</summary>
        private readonly MachineConfigurationStore _store;

        /// <summary>保留配置存储入口，不创建或改写任何准备文件。</summary>
        public TaskStationPreparationStore(MachineConfigurationStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        /// <summary>文件明确不存在时返回 null；读取失败或文件损坏会报错，不能当作没有准备配置。</summary>
        public TaskStationPreparationConfiguration Load(string machineId)
        {
            string path = Path.Combine(_store.GetMachineDirectory(machineId), RelativePath);
            string json;
            try { json = File.ReadAllText(path, new UTF8Encoding(false, true)); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            return Deserialize(json);
        }

        /// <summary>严格检查版本、重复字段、未知字段和类型元数据；这里只读定义，不判断实时准备状态。</summary>
        public static TaskStationPreparationConfiguration Deserialize(string json)
        {
            JObject document;
            using (var text = new StringReader(json))
            using (var reader = new JsonTextReader(text) { MaxDepth = 32, DateParseHandling = DateParseHandling.None })
            {
                document = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if (reader.Read()) throw new InvalidDataException("设备准备文件只能有一个根对象。");
            }
            TaskDefinitionJson.CheckPropertyNames(document);
            if (document["schemaVersion"]?.Type != JTokenType.Integer || (int)document["schemaVersion"] != 1
                || document["stations"]?.Type != JTokenType.Array)
                throw new InvalidDataException("设备准备文件需要 schemaVersion=1 和 stations 列表。");
            JsonSerializer serializer = TaskDefinitionJson.CreateSerializer();
            // 显式 null 必须交给纯校验拒绝，不能被反序列化器替换成属性的默认空列表。
            serializer.NullValueHandling = NullValueHandling.Include;
            return document.ToObject<TaskStationPreparationConfiguration>(serializer);
        }
    }
}
