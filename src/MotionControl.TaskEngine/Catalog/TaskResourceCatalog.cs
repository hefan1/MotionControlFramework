using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using MotionControl.Framework.Contracts.Variables;

namespace MotionControl.TaskEngine.Catalog
{
    /// <summary>编辑和校验共用的只读数据目录；只保存配置引用，不持有硬件服务。</summary>
    public sealed class TaskResourceCatalog
    {
        /// <summary>逻辑轴目录，名称不区分大小写。</summary>
        public IReadOnlyDictionary<string, TaskAxisInfo> Axes { get; private set; }
        /// <summary>已配置的逻辑 DI 名称。</summary>
        public IReadOnlyCollection<string> Inputs { get; private set; }
        /// <summary>已配置的逻辑 DO 名称。</summary>
        public IReadOnlyCollection<string> Outputs { get; private set; }
        /// <summary>设备变量及现有数据类型。</summary>
        public IReadOnlyDictionary<string, DeviceVariableType> Variables { get; private set; }
        /// <summary>同一批变量定义的只读元数据；旧目录未提供时保留未定义字段。</summary>
        public IReadOnlyDictionary<string, TaskVariableInfo> VariableDefinitions { get; private set; }
        /// <summary>设备变量初始值；未设置初值的变量对应 null。</summary>
        public IReadOnlyDictionary<string, string> VariableInitialValues { get; private set; }
        /// <summary>现有示教点；用逻辑轴和点位 ID 联合定位。</summary>
        public IReadOnlyList<TaskTeachPoint> TeachPoints { get; private set; }
        /// <summary>轴、输出资源及 Host 明确提供的区域、夹具等资源名称。</summary>
        public IReadOnlyCollection<string> Resources { get; private set; }
        /// <summary>当前设备报警库中的稳定报警代码。</summary>
        public IReadOnlyCollection<string> AlarmCodes { get; private set; }
        /// <summary>当前设备报警库中供任务编辑器解释代码的类别与说明。</summary>
        public IReadOnlyList<TaskAlarmInfo> AlarmDefinitions { get; private set; }
        /// <summary>读取时发现的可选配置缺失或旧点位无法映射等信息。</summary>
        public IReadOnlyList<string> Notices { get; private set; }

        /// <summary>复制全部输入，防止校验期间目录列表被外部修改；重复名称立即报错。</summary>
        public TaskResourceCatalog(IEnumerable<TaskAxisInfo> axes, IEnumerable<string> inputs,
            IEnumerable<string> outputs, IDictionary<string, DeviceVariableType> variables,
            IEnumerable<TaskTeachPoint> teachPoints, IEnumerable<string> namedResources = null,
            IEnumerable<string> notices = null, IDictionary<string, string> variableInitialValues = null,
            IEnumerable<string> alarmCodes = null, IEnumerable<TaskAlarmInfo> alarmDefinitions = null,
            IEnumerable<DeviceVariableDefinition> variableDefinitions = null)
        {
            if (axes == null || inputs == null || outputs == null || variables == null || teachPoints == null)
                throw new ArgumentNullException("目录中的轴、IO、变量和示教点集合均不能为空。");

            var axisMap = new Dictionary<string, TaskAxisInfo>(StringComparer.OrdinalIgnoreCase);
            var resourceNames = new List<string>();
            foreach (TaskAxisInfo axis in axes)
            {
                if (axis == null) throw new ArgumentException("轴目录包含空记录。");
                CheckName(axis.Name);
                CheckName(axis.ControllerId);
                if ((axis.MinimumPosition.HasValue && (double.IsNaN(axis.MinimumPosition.Value) || double.IsInfinity(axis.MinimumPosition.Value)))
                    || (axis.MaximumPosition.HasValue && (double.IsNaN(axis.MaximumPosition.Value) || double.IsInfinity(axis.MaximumPosition.Value)))
                    || (axis.MinimumPosition.HasValue && axis.MaximumPosition.HasValue && axis.MinimumPosition > axis.MaximumPosition))
                    throw new ArgumentException("轴目录行程无效：" + axis.Name);
                if (axisMap.ContainsKey(axis.Name)) throw new ArgumentException("逻辑轴名称重复：" + axis.Name);
                axisMap.Add(axis.Name, axis);
                resourceNames.Add("Axis:" + axis.Name);
            }
            Axes = new ReadOnlyDictionary<string, TaskAxisInfo>(axisMap);
            Inputs = CopyNames(inputs);
            Outputs = CopyNames(outputs);
            foreach (string output in Outputs) resourceNames.Add("Output:" + output);

            var variableMap = new Dictionary<string, DeviceVariableType>(StringComparer.OrdinalIgnoreCase);
            foreach (var variable in variables)
            {
                CheckName(variable.Key);
                if (!Enum.IsDefined(typeof(DeviceVariableType), variable.Value))
                    throw new ArgumentException("变量类型无效：" + variable.Key);
                if (variableMap.ContainsKey(variable.Key)) throw new ArgumentException("变量名称重复：" + variable.Key);
                variableMap.Add(variable.Key, variable.Value);
            }
            Variables = new ReadOnlyDictionary<string, DeviceVariableType>(variableMap);

            var variableDefinitionsByName = new Dictionary<string, TaskVariableInfo>(StringComparer.OrdinalIgnoreCase);
            if (variableDefinitions != null)
            {
                // 复用运行时定义检查，避免目录接受了运行时会拒绝的单位、范围或初值。
                var definitions = MotionControl.Framework.Runtime.Variables.DeviceVariableStore.ValidateDefinitions(variableDefinitions);
                foreach (DeviceVariableDefinition definition in definitions)
                {
                    var info = new TaskVariableInfo(definition);
                    DeviceVariableType expectedType;
                    if (!variableMap.TryGetValue(info.Name, out expectedType) || info.DataType != expectedType)
                        throw new ArgumentException("变量元数据与目录名称或类型不一致：" + info.Name);
                    variableDefinitionsByName.Add(info.Name, info);
                }
            }
            foreach (var variable in variableMap)
                if (!variableDefinitionsByName.ContainsKey(variable.Key))
                    variableDefinitionsByName.Add(variable.Key, new TaskVariableInfo(new DeviceVariableDefinition
                    { Name = variable.Key, DataType = variable.Value.ToString() }));
            VariableDefinitions = new ReadOnlyDictionary<string, TaskVariableInfo>(variableDefinitionsByName);

            var initialValueMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (variableInitialValues != null)
            {
                foreach (var initialValue in variableInitialValues)
                {
                    CheckName(initialValue.Key);
                    if (!variableMap.ContainsKey(initialValue.Key))
                        throw new ArgumentException("变量初值对应的变量不存在：" + initialValue.Key);
                    if (initialValueMap.ContainsKey(initialValue.Key))
                        throw new ArgumentException("变量初值名称重复：" + initialValue.Key);
                    initialValueMap.Add(initialValue.Key, initialValue.Value);
                }
            }
            foreach (string variableName in variableMap.Keys)
                if (!initialValueMap.ContainsKey(variableName)) initialValueMap.Add(variableName, null);
            VariableInitialValues = new ReadOnlyDictionary<string, string>(initialValueMap);

            var points = new List<TaskTeachPoint>();
            var pointKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (TaskTeachPoint point in teachPoints)
            {
                if (point == null) throw new ArgumentException("示教点目录包含空记录。");
                CheckName(point.AxisName);
                CheckName(point.PointId);
                if (!axisMap.ContainsKey(point.AxisName))
                    throw new ArgumentException("示教点所属轴不存在：" + point.AxisName);
                // 用长度前缀避免两个名称中含有分隔符时产生相同键。
                string key = point.AxisName.Length + ":" + point.AxisName + point.PointId;
                if (!pointKeys.Add(key))
                    throw new ArgumentException("示教点编号重复：" + point.AxisName + "/" + point.PointId);
                points.Add(point);
            }
            TeachPoints = points.AsReadOnly();
            if (namedResources != null)
            {
                foreach (string name in namedResources)
                {
                    CheckName(name);
                    if (name.StartsWith("Axis:", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith("Output:", StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException("自定义资源不能使用保留前缀 Axis: 或 Output:：" + name);
                    resourceNames.Add(name);
                }
            }
            Resources = CopyNames(resourceNames);
            var alarms = new List<TaskAlarmInfo>();
            var alarmCodesFromDefinitions = new List<string>();
            if (alarmDefinitions != null)
            {
                var alarmNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (TaskAlarmInfo alarm in alarmDefinitions)
                {
                    if (alarm == null) throw new ArgumentException("报警目录包含空记录。");
                    CheckName(alarm.Code);
                    if (string.IsNullOrWhiteSpace(alarm.Message))
                        throw new ArgumentException("报警说明不能为空：" + alarm.Code);
                    if (!Enum.IsDefined(typeof(MotionControl.Framework.Contracts.Alarms.MachineAlarmKind), alarm.Kind))
                        throw new ArgumentException("报警类别无效：" + alarm.Code);
                    if (!alarmNames.Add(alarm.Code)) throw new ArgumentException("报警代码重复：" + alarm.Code);
                    alarms.Add(new TaskAlarmInfo(alarm.Code, alarm.Message, alarm.Kind,
                        alarm.CheckGuidance, alarm.RecoveryGuidance));
                    alarmCodesFromDefinitions.Add(alarm.Code);
                }
            }
            AlarmDefinitions = alarms.AsReadOnly();
            AlarmCodes = CopyNames(alarmCodesFromDefinitions.Count == 0
                ? alarmCodes ?? new string[0]
                : alarmCodesFromDefinitions);
            Notices = new List<string>(notices ?? new string[0]).AsReadOnly();
        }

        /// <summary>按逻辑轴和稳定点位编号查找，不跨轴使用同名或同编号点位。</summary>
        public TaskTeachPoint FindTeachPoint(string axisName, string pointId)
        {
            foreach (TaskTeachPoint point in TeachPoints)
                if (string.Equals(point.AxisName, axisName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(point.PointId, pointId, StringComparison.OrdinalIgnoreCase))
                    return point;
            return null;
        }

        /// <summary>复制名称列表，并拒绝空名称和重复名称。</summary>
        private static IReadOnlyCollection<string> CopyNames(IEnumerable<string> names)
        {
            var result = new List<string>();
            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in names)
            {
                CheckName(name);
                if (!unique.Add(name)) throw new ArgumentException("目录名称重复：" + name);
                result.Add(name);
            }
            return result.AsReadOnly();
        }

        /// <summary>名称必须明确且没有首尾空格，避免引用静默匹配到不同对象。</summary>
        private static void CheckName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name != name.Trim())
                throw new ArgumentException("目录名称不能为空或包含首尾空格。");
        }
    }
}
