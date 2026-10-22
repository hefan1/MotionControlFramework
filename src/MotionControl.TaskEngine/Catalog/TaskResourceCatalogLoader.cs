using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.Framework.Runtime.Variables;
using Newtonsoft.Json.Linq;

namespace MotionControl.TaskEngine.Catalog
{
    /// <summary>只读现有设备配置；不创建配置、不加载 WPF、不连接任何运动卡。</summary>
    public sealed class TaskResourceCatalogLoader
    {
        /// <summary>复用已有的设备隔离和 JSON 读取。</summary>
        private readonly MachineConfigurationStore _store;

        /// <summary>接收配置根目录服务。</summary>
        public TaskResourceCatalogLoader(MachineConfigurationStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        /// <summary>加载一台设备的已有配置；可选文件缺失时返回空目录并记录提示。</summary>
        public TaskResourceCatalog Load(string machineId, IEnumerable<string> namedResources = null)
        {
            var notices = new List<string>();
            var axes = new List<TaskAxisInfo>();
            var axisByPhysicalKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            JObject axisDocument = ReadDocument(machineId, "HardwareConfig/AxisParameters.json", false, notices);
            foreach (JObject axis in ReadObjects(axisDocument, "axes"))
            {
                string name = (string)axis["name"];
                if (string.IsNullOrWhiteSpace(name)) continue; // 未映射的物理通道不提供给任务选择。
                string controllerId = (string)axis["controllerId"];
                if (axis["axisNumber"]?.Type != JTokenType.Integer)
                    throw new InvalidDataException("轴号必须是整数：" + name);
                int? channel = (int?)axis["axisNumber"];
                if (string.IsNullOrWhiteSpace(controllerId) || !channel.HasValue || channel < 0)
                    throw new InvalidDataException("轴映射缺少有效卡号或轴号：" + name);
                string key = controllerId + ".Axis" + channel.Value.ToString(CultureInfo.InvariantCulture);
                if (axisByPhysicalKey.ContainsKey(key))
                    throw new InvalidDataException("物理轴被重复映射：" + key + "，逻辑轴=" + name);
                axisByPhysicalKey.Add(key, name);
                double? minimum = (double?)axis["minimumPosition"];
                double? maximum = (double?)axis["maximumPosition"];
                if ((minimum.HasValue && !IsFinite(minimum.Value))
                    || (maximum.HasValue && !IsFinite(maximum.Value))
                    || (minimum.HasValue && maximum.HasValue && minimum > maximum))
                    throw new InvalidDataException("轴行程范围无效：" + name);
                axes.Add(new TaskAxisInfo(name, controllerId, (string)axis["engineeringUnit"], minimum, maximum));
            }

            JObject ioDocument = ReadDocument(machineId, "HardwareConfig/IoMapping.json", false, notices);
            List<string> inputs = ReadIoNames(ioDocument, "digitalInputs");
            List<string> outputs = ReadIoNames(ioDocument, "digitalOutputs");
            var variables = new Dictionary<string, DeviceVariableType>(StringComparer.OrdinalIgnoreCase);
            JObject variableDocument = ReadDocument(machineId, "Parameters/DeviceVariables.json", true, notices);
            if (variableDocument != null)
            {
                var definitions = variableDocument.ToObject<DeviceVariableConfigurationDocument>();
                definitions.Validate();
                foreach (DeviceVariableDefinition definition in definitions.Variables)
                {
                    DeviceVariableType type;
                    if (!Enum.TryParse(definition.DataType, false, out type)
                        || !Enum.IsDefined(typeof(DeviceVariableType), type))
                        throw new InvalidDataException("变量类型不受支持：" + definition.Name);
                    variables.Add(definition.Name, type);
                }
            }

            var points = new List<TaskTeachPoint>();
            JObject pointDocument = ReadDocument(machineId, "Parameters/ManualAxisPositions.json", true, notices);
            if (pointDocument != null)
            {
                foreach (JObject group in ReadObjects(pointDocument, "axes"))
                {
                    string axisName;
                    string key = (string)group["axisKey"];
                    if (string.IsNullOrWhiteSpace(key))
                        throw new InvalidDataException("示教位置组缺少 axisKey。");
                    if (!axisByPhysicalKey.TryGetValue(key, out axisName))
                    {
                        notices.Add("旧示教位置组没有对应逻辑轴，未加入目录：" + key);
                        continue;
                    }
                    foreach (JObject point in ReadObjects(group, "positions"))
                    {
                        double? position = (double?)point["position"];
                        if (position.HasValue && !IsFinite(position.Value))
                            throw new InvalidDataException("示教值不是有限数：" + key + "/" + point["id"]);
                        points.Add(new TaskTeachPoint(axisName, (string)point["id"], (string)point["name"], position));
                    }
                }
            }
            return new TaskResourceCatalog(axes, inputs, outputs, variables, points, namedResources, notices);
        }

        /// <summary>读取已知格式文件；只允许可选文件不存在，不忽略损坏文件。</summary>
        private JObject ReadDocument(string machineId, string path, bool optional, List<string> notices)
        {
            string fullPath = Path.Combine(_store.GetMachineDirectory(machineId), path);
            if (optional && !File.Exists(fullPath))
            {
                notices.Add("可选配置尚未建立：" + path);
                return null;
            }
            JObject document = _store.LoadJson<JObject>(machineId, path);
            if (document["schemaVersion"]?.Type != JTokenType.Integer
                || (int)document["schemaVersion"] != 1)
                throw new InvalidDataException("配置版本不受支持：" + path + "；仅支持 schemaVersion 1。");
            return document;
        }

        /// <summary>读取明确的对象数组；格式错误时保留字段名供现场排查。</summary>
        private static IEnumerable<JObject> ReadObjects(JObject document, string property)
        {
            var array = document[property] as JArray;
            if (array == null) throw new InvalidDataException("配置缺少数组：" + property);
            foreach (JToken item in array)
            {
                var value = item as JObject;
                if (value == null) throw new InvalidDataException("配置数组中含有非对象项：" + property);
                yield return value;
            }
        }

        /// <summary>读取有名称的现有 IO 映射；空白行不提供给任务使用。</summary>
        private static List<string> ReadIoNames(JObject document, string property)
        {
            var names = new List<string>();
            foreach (JObject point in ReadObjects(document, property))
            {
                string name = (string)point["name"];
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (string.IsNullOrWhiteSpace((string)point["controllerId"])
                    || point["channel"]?.Type != JTokenType.Integer || (int)point["channel"] < 0
                    || point["activeHigh"]?.Type != JTokenType.Boolean)
                    throw new InvalidDataException("IO 映射的卡号、通道或极性不完整：" + name);
                names.Add(name);
            }
            return names;
        }

        /// <summary>拒绝不能用于运动参数的非有限数。</summary>
        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
