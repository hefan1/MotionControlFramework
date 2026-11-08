using System;
using System.Collections.Generic;
using System.Linq;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Validation;

namespace MotionControl.TaskEngine.Preparation
{
    /// <summary>准备定义的共享纯校验：只使用配置、任务库与资源目录，不读取现场反馈或下发动作。</summary>
    public static class TaskStationPreparationValidator
    {
        /// <summary>返回带准备项和字段位置的问题；编辑检查、工程导入和最终装载必须复用此入口。</summary>
        public static IReadOnlyList<TaskValidationIssue> Validate(TaskStationPreparationConfiguration configuration,
            TaskResourceCatalog catalog, IEnumerable<TaskDefinition> definitions)
        {
            if (catalog == null) throw new ArgumentNullException(nameof(catalog));
            if (definitions == null) throw new ArgumentNullException(nameof(definitions));
            var issues = new List<TaskValidationIssue>();
            if (configuration == null || configuration.SchemaVersion != 1 || configuration.Stations == null
                || configuration.Stations.Count == 0 || configuration.Stations.Count > 64)
            {
                AddIssue(issues, null, "stations", "设备准备必须使用版本 1，准备项必须为 1–64 项。");
                return issues.AsReadOnly();
            }

            var library = definitions.ToList();
            var taskIds = new HashSet<string>(library.Where(task => task != null).Select(task => task.TaskId), StringComparer.OrdinalIgnoreCase);
            if (library.Count > 0)
                issues.AddRange(new TaskDefinitionValidator().Validate(library[0], catalog, library.Skip(1)));

            var preparations = new Dictionary<string, TaskStationPreparationDefinition>(StringComparer.OrdinalIgnoreCase);
            var taskOwners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < configuration.Stations.Count; index++)
            {
                TaskStationPreparationDefinition item = configuration.Stations[index];
                string path = "stations[" + index + "]";
                if (item == null)
                {
                    AddIssue(issues, null, path, "准备项不能为空。");
                    continue;
                }
                if (string.IsNullOrWhiteSpace(item.Id) || item.Id != item.Id.Trim() || preparations.ContainsKey(item.Id))
                    AddIssue(issues, item, path + ".id", "准备标识为空、带首尾空格或重复。");
                else preparations.Add(item.Id, item);
                if (string.IsNullOrWhiteSpace(item.Name)) AddIssue(issues, item, path + ".name", "准备名称不能为空。");
                if (string.IsNullOrWhiteSpace(item.TaskId) || !taskIds.Contains(item.TaskId))
                    AddIssue(issues, item, path + ".taskId", "准备任务不存在：" + (item.TaskId ?? "未填写"));
                else if (!taskOwners.Add(item.TaskId))
                    AddIssue(issues, item, path + ".taskId", "同一任务不能重复登记给多个准备项：" + item.TaskId);

                if (item.Dependencies == null || item.Axes == null || item.Inputs == null || item.Outputs == null || item.EmptyVariables == null)
                {
                    AddIssue(issues, item, path, "准备条件列表不能为 null。");
                    continue;
                }
                if (item.Axes.Count == 0 && item.Inputs.Count == 0)
                    AddIssue(issues, item, path, "必须配置至少一个轴或输入的准备完成条件。");

                ValidateAxes(item, path, catalog, issues);
                ValidateSignals(item, item.Inputs, catalog.Inputs, path + ".inputs", issues);
                ValidateSignals(item, item.Outputs, catalog.Outputs, path + ".outputs", issues);
                var variables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int variableIndex = 0; variableIndex < item.EmptyVariables.Count; variableIndex++)
                {
                    string variable = item.EmptyVariables[variableIndex];
                    DeviceVariableType type;
                    if (string.IsNullOrWhiteSpace(variable) || !variables.Add(variable)
                        || !catalog.Variables.TryGetValue(variable, out type) || type != DeviceVariableType.Boolean)
                        AddIssue(issues, item, path + ".emptyVariables[" + variableIndex + "]", "占料检查必须引用不重复的已有布尔变量：" + variable);
                }
            }

            // 准备依赖只与准备标识比较，不按名称猜测生产工位关系。
            for (int index = 0; index < configuration.Stations.Count; index++)
            {
                TaskStationPreparationDefinition item = configuration.Stations[index];
                if (item?.Dependencies == null) continue;
                var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int dependencyIndex = 0; dependencyIndex < item.Dependencies.Count; dependencyIndex++)
                {
                    string dependency = item.Dependencies[dependencyIndex];
                    if (string.IsNullOrWhiteSpace(dependency) || !preparations.ContainsKey(dependency) || !unique.Add(dependency)
                        || string.Equals(dependency, item.Id, StringComparison.OrdinalIgnoreCase))
                        AddIssue(issues, item, "stations[" + index + "].dependencies[" + dependencyIndex + "]", "准备依赖缺失、重复或指向自身：" + dependency);
                }
            }
            var completed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var currentPath = new List<string>();
            foreach (string id in preparations.Keys) CheckDependencyCycles(id, preparations, completed, currentPath, issues);
            return issues.AsReadOnly();
        }

        /// <summary>逐轴检查名称、工程位置、容差及行程，错误直接定位到准备配置字段。</summary>
        private static void ValidateAxes(TaskStationPreparationDefinition item, string path, TaskResourceCatalog catalog,
            List<TaskValidationIssue> issues)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < item.Axes.Count; index++)
            {
                TaskPreparationAxis axis = item.Axes[index];
                string field = path + ".axes[" + index + "]";
                if (axis == null || string.IsNullOrWhiteSpace(axis.Name) || !names.Add(axis.Name) || !catalog.Axes.ContainsKey(axis.Name))
                {
                    AddIssue(issues, item, field + ".name", "准备轴不存在、为空或重复：" + axis?.Name);
                    continue;
                }
                if (!Finite(axis.Position)) AddIssue(issues, item, field + ".position", "准备待机位置必须明确填写有限数值。");
                if (!Finite(axis.Tolerance) || axis.Tolerance.Value <= 0)
                    AddIssue(issues, item, field + ".tolerance", "准备位置容差必须明确填写且大于零。");
                TaskAxisInfo known = catalog.Axes[axis.Name];
                if (Finite(axis.Position) && (!known.MinimumPosition.HasValue || !known.MaximumPosition.HasValue
                    || axis.Position.Value < known.MinimumPosition.Value || axis.Position.Value > known.MaximumPosition.Value))
                    AddIssue(issues, item, field + ".position", "准备待机位置超出轴行程或轴行程尚未配置：" + axis.Name);
            }
        }

        /// <summary>分别使用输入和输出目录核对方向，不把输出命令当作输入反馈。</summary>
        private static void ValidateSignals(TaskStationPreparationDefinition item, IList<TaskPreparationSignal> signals,
            IReadOnlyCollection<string> known, string path, List<TaskValidationIssue> issues)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < signals.Count; index++)
            {
                TaskPreparationSignal signal = signals[index];
                string field = path + "[" + index + "]";
                if (signal == null || string.IsNullOrWhiteSpace(signal.Name) || !names.Add(signal.Name)
                    || !known.Contains(signal.Name, StringComparer.OrdinalIgnoreCase))
                    AddIssue(issues, item, field + ".name", "准备信号不存在、方向错误、为空或重复：" + signal?.Name);
                if (signal != null && !signal.Expected.HasValue)
                    AddIssue(issues, item, field + ".expected", "准备信号必须明确填写期望逻辑状态。");
            }
        }

        /// <summary>每个准备项只完整检查一次；当前调用链中再次出现同一标识即为依赖环。</summary>
        private static void CheckDependencyCycles(string id, Dictionary<string, TaskStationPreparationDefinition> preparations,
            HashSet<string> completed, List<string> path, List<TaskValidationIssue> issues)
        {
            if (completed.Contains(id)) return;
            if (path.Contains(id, StringComparer.OrdinalIgnoreCase))
            {
                AddIssue(issues, preparations[id], "stations[" + id + "].dependencies", "设备准备依赖存在循环：" + string.Join(" → ", path) + " → " + id);
                return;
            }
            path.Add(id);
            TaskStationPreparationDefinition item = preparations[id];
            if (item.Dependencies != null)
            {
                foreach (string dependency in item.Dependencies)
                    if (dependency != null && preparations.ContainsKey(dependency)) CheckDependencyCycles(dependency, preparations, completed, path, issues);
            }
            path.RemoveAt(path.Count - 1);
            completed.Add(id);
        }

        /// <summary>问题同时保留准备所有者和文件字段路径，不能只报一个无法定位的任务错误。</summary>
        private static void AddIssue(List<TaskValidationIssue> issues, TaskStationPreparationDefinition item, string field, string message)
        {
            string owner = item == null ? "设备准备" : "准备项[" + item.Id + " / " + item.Name + "]";
            issues.Add(new TaskValidationIssue(TaskIssueSeverity.Error, item?.TaskId, null,
                TaskStationPreparationStore.RelativePath + "/" + field, owner + "：" + message));
        }

        /// <summary>位置与容差必须明确且为有限值。</summary>
        private static bool Finite(double? value)
        {
            return value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value);
        }
    }
}
