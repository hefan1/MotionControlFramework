using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MotionControl.Framework.Contracts.Preparation;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Device;
using MotionControl.TaskEngine.Machine;
using MotionControl.TaskEngine.Validation;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MotionControl.TaskEngine.MachineDevice.Preparation
{
    /// <summary>从独立文件加载任务准备工位，在现有任务会话内装配，不创建或启动生产会话。</summary>
    public static class TaskStationPreparationLoader
    {
        /// <summary>与生产任务清单分开保存，避免旧任务草稿应用覆盖准备配置。</summary>
        public const string RelativePath = "Parameters/StationPreparation.json";

        /// <summary>读取配置并校验全部引用后，登记每个工位的准备任务；没有配置时返回空列表。</summary>
        public static IReadOnlyList<IStationPreparation> Load(MachineConfigurationStore store, string machineId,
            TaskMachineController machine, ITaskDevice device, IReadOnlyDictionary<string, TaskDefinition> library)
        {
            if (store == null || machine == null || device == null || library == null) throw new ArgumentNullException("准备装配依赖不能为空。");
            string path = Path.Combine(store.GetMachineDirectory(machineId), RelativePath);
            if (!File.Exists(path)) return new List<IStationPreparation>().AsReadOnly();
            TaskStationPreparationConfiguration configuration = Read(path);
            Validate(configuration, device, library);
            return configuration.Stations.Select(item => (IStationPreparation)new TaskStationPreparation(item, machine, device)).ToList().AsReadOnly();
        }

        /// <summary>严格读取版本、重复字段与未知字段；文件修改只在重建会话后生效。</summary>
        private static TaskStationPreparationConfiguration Read(string path)
        {
            JObject document;
            using (var text = File.OpenText(path))
            using (var reader = new JsonTextReader(text) { MaxDepth = 32 })
            {
                document = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if (reader.Read()) throw new InvalidDataException("设备准备文件只能有一个根对象。");
            }
            if (document["schemaVersion"]?.Type != JTokenType.Integer || (int)document["schemaVersion"] != 1
                || document["stations"]?.Type != JTokenType.Array)
                throw new InvalidDataException("设备准备文件需要 schemaVersion=1 和 stations 列表。");
            return document.ToObject<TaskStationPreparationConfiguration>(JsonSerializer.Create(new JsonSerializerSettings
            { MissingMemberHandling = MissingMemberHandling.Error, TypeNameHandling = TypeNameHandling.None, MetadataPropertyHandling = MetadataPropertyHandling.Ignore }));
        }

        /// <summary>一次检查全部准备定义，避免执行到中途才发现缺轴、错任务或依赖环。</summary>
        private static void Validate(TaskStationPreparationConfiguration config, ITaskDevice device, IReadOnlyDictionary<string, TaskDefinition> library)
        {
            if (config.Stations == null || config.Stations.Count == 0 || config.Stations.Count > 64)
                throw new InvalidDataException("设备准备工位必须为 1–64 项。");
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var tasks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (TaskStationPreparationDefinition item in config.Stations)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Name)
                    || string.IsNullOrWhiteSpace(item.TaskId) || !ids.Add(item.Id) || !tasks.Add(item.TaskId))
                    throw new InvalidDataException("设备准备的工位标识、名称或任务编号为空或重复。");
                if (item.Dependencies == null || item.Axes == null || item.Inputs == null || item.Outputs == null || item.EmptyVariables == null)
                    throw new InvalidDataException("准备条件列表不能为 null：" + item.Name);
                if (item.Axes.Count == 0 && item.Inputs.Count == 0)
                    throw new InvalidDataException("必须为工位配置至少一个轴或输入的完成条件：" + item.Name);
                TaskDefinition task;
                if (!library.TryGetValue(item.TaskId, out task)) throw new InvalidDataException("准备任务不存在：" + item.TaskId);
                var errors = new TaskDefinitionValidator().Validate(task, device.Catalog,
                    library.Values.Where(value => !string.Equals(value.TaskId, task.TaskId, StringComparison.OrdinalIgnoreCase)))
                    .Where(issue => issue.Severity == TaskIssueSeverity.Error).ToArray();
                if (errors.Length > 0) throw new InvalidDataException("准备任务校验失败：" + string.Join("；", errors.Select(error => error.ToString())));
                var axisNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (TaskPreparationAxis axis in item.Axes)
                {
                    if (axis == null || string.IsNullOrWhiteSpace(axis.Name) || !axisNames.Add(axis.Name) || !device.Catalog.Axes.ContainsKey(axis.Name)
                        || !Finite(axis.Position) || !Finite(axis.Tolerance) || axis.Tolerance.Value <= 0)
                        throw new InvalidDataException("准备轴名称、待机位或容差无效：" + item.Name);
                    var known = device.Catalog.Axes[axis.Name];
                    if (!known.MinimumPosition.HasValue || !known.MaximumPosition.HasValue
                        || axis.Position.Value < known.MinimumPosition.Value || axis.Position.Value > known.MaximumPosition.Value)
                        throw new InvalidDataException("准备待机位置超出轴行程：" + axis.Name);
                }
                ValidateSignals(item.Inputs, device.Catalog.Inputs, item.Name);
                ValidateSignals(item.Outputs, device.Catalog.Outputs, item.Name);
                foreach (string variable in item.EmptyVariables)
                {
                    DeviceVariableType type;
                    if (string.IsNullOrWhiteSpace(variable) || !device.Catalog.Variables.TryGetValue(variable, out type) || type != DeviceVariableType.Boolean)
                        throw new InvalidDataException("占料检查必须引用已有布尔变量：" + variable);
                }
            }
            foreach (var item in config.Stations)
            {
                var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string dependency in item.Dependencies)
                    if (string.IsNullOrWhiteSpace(dependency) || !ids.Contains(dependency) || !unique.Add(dependency)
                        || string.Equals(dependency, item.Id, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("准备工位依赖缺失、重复或指向自身：" + item.Name);
                Visit(item.Id, config.Stations, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            }
        }

        /// <summary>检查信号方向、名称和期望值；未知或重复引用不能带入设备执行。</summary>
        private static void ValidateSignals(IEnumerable<TaskPreparationSignal> values, IReadOnlyCollection<string> known, string station)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (TaskPreparationSignal signal in values)
                if (signal == null || string.IsNullOrWhiteSpace(signal.Name) || !signal.Expected.HasValue || !names.Add(signal.Name)
                    || !known.Contains(signal.Name, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidDataException("准备信号名称或期望状态无效：" + station);
        }

        /// <summary>沿当前依赖链检查循环；准备数量有上限，不做隐式调度。</summary>
        private static void Visit(string id, IList<TaskStationPreparationDefinition> stations, HashSet<string> path)
        {
            if (!path.Add(id)) throw new InvalidDataException("设备准备依赖存在循环：" + id);
            foreach (string dependency in stations.Single(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)).Dependencies)
                Visit(dependency, stations, path);
            path.Remove(id);
        }

        /// <summary>位置与容差必须明确且为有限值。</summary>
        private static bool Finite(double? value) => value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value);
    }
}
