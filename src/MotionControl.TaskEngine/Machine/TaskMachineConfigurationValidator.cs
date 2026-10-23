using System;
using System.Collections.Generic;
using System.Linq;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Validation;

namespace MotionControl.TaskEngine.Machine
{
    /// <summary>设备清单和工位信号校验，不启动线程或动作。</summary>
    public static class TaskMachineConfigurationValidator
    {
        /// <summary>检查清单与已加载的任务库；错误可直接显示给后续配置页面。</summary>
        public static IReadOnlyList<string> Validate(TaskMachineConfiguration configuration,
            IEnumerable<TaskDefinition> definitions, TaskResourceCatalog catalog)
        {
            if (catalog == null) throw new ArgumentNullException(nameof(catalog));
            var errors = new List<string>();
            if (configuration == null) { errors.Add("设备任务清单不能为空。"); return errors.AsReadOnly(); }
            if (configuration.SchemaVersion != 1 || configuration.Revision < 1)
                errors.Add("设备清单只支持 schemaVersion=1，revision 必须大于零。");
            if (!ValidName(configuration.MachineId) || !ValidName(configuration.Name)) errors.Add("设备标识和名称不能为空或带首尾空格。");
            var library = new Dictionary<string, TaskDefinition>(StringComparer.OrdinalIgnoreCase);
            int count = 0;
            foreach (TaskDefinition task in definitions ?? new TaskDefinition[0])
            {
                if (++count > 128) { errors.Add("设备任务库最多 128 份定义。"); break; }
                if (task == null || !ValidName(task.TaskId) || library.ContainsKey(task.TaskId))
                    errors.Add("任务库包含空记录、空标识或重复标识。");
                else library.Add(task.TaskId, task);
            }
            if (library.Count == 0) errors.Add("设备任务库不能为空。");
            if (errors.Count == 0)
            {
                TaskDefinition main = library.Values.First();
                errors.AddRange(new TaskDefinitionValidator().Validate(main, catalog, library.Values.Skip(1))
                    .Where(issue => issue.Severity == TaskIssueSeverity.Error).Select(issue => issue.ToString()));
            }
            if (configuration.InitializationTaskId != null && !library.ContainsKey(configuration.InitializationTaskId))
                errors.Add("初始化任务未加载：" + configuration.InitializationTaskId);
            if (configuration.Stations == null || configuration.Stations.Count == 0 || configuration.Stations.Count > 128)
            {
                errors.Add("设备清单需要 1 到 128 个工位。");
                return errors.AsReadOnly();
            }
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var signalNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (StationTaskConfiguration station in configuration.Stations)
            {
                if (station == null) { errors.Add("工位记录不能为空。"); continue; }
                string prefix = "工位 " + station.StationId + "：";
                if (!ValidName(station.StationId) || station.StationId.StartsWith("__", StringComparison.Ordinal)
                    || !ids.Add(station.StationId) || !ValidName(station.Name)) errors.Add(prefix + "工位标识/名称无效或重复，__ 为保留前缀。");
                if (!ValidName(station.TaskId) || !library.ContainsKey(station.TaskId)) errors.Add(prefix + "任务未加载：" + station.TaskId);
                if (string.Equals(station.TaskId, configuration.InitializationTaskId, StringComparison.OrdinalIgnoreCase))
                    errors.Add(prefix + "不能把初始化任务同时作为自动工位任务。");
                if (!Enum.IsDefined(typeof(StationTaskMode), station.Mode)) errors.Add(prefix + "运行方式无效。");
                if (station.CycleIntervalMs < 1 || station.CycleIntervalMs > 60000) errors.Add(prefix + "周期间隔必须为 1–60000 ms。");
                if (station.Mode == StationTaskMode.OnRequest)
                {
                    foreach (string name in new[] { station.RequestVariable, station.CompletedVariable })
                    {
                        DeviceVariableType type;
                        if (!ValidName(name) || !catalog.Variables.TryGetValue(name, out type) || type != DeviceVariableType.Boolean)
                            errors.Add(prefix + "交接信号必须引用已有 bool 变量：" + name);
                        else if (!signalNames.Add(name)) errors.Add(prefix + "交接信号重复或被多个触发工位共用：" + name);
                    }
                }
                else if (station.RequestVariable != null || station.CompletedVariable != null)
                    errors.Add(prefix + "单次/循环工位不能填写请求交接变量。");
            }
            if (!configuration.Stations.Any(station => station != null && station.Enabled)) errors.Add("至少启用一个自动工位。");
            return errors.AsReadOnly();
        }

        /// <summary>引用名称不做静默修剪。</summary>
        private static bool ValidName(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && value == value.Trim();
        }
    }
}
