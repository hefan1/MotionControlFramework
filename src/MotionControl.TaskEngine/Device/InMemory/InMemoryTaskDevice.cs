using System;
using System.Collections.Generic;
using System.Linq;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.Framework.Runtime.Variables;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Device;
using MotionControl.TaskEngine.Execution;
using MotionControl.TaskEngine.Resources;

namespace MotionControl.TaskEngine.Device.InMemory
{
    /// <summary>任务引擎专用的独立内存设备；没有控制卡、网络或原生 DLL 入口。</summary>
    public sealed partial class InMemoryTaskDevice : ITaskDevice, ITaskInterlockSupport, ITaskRecordSink
    {
        private readonly ITaskRecordSink _recordOverride;
        private readonly Dictionary<string, Dictionary<string, string>> _records = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        /// <summary>按执行编号保存首次记录时间，重试不得更改。</summary>
        private readonly Dictionary<string, DateTimeOffset> _recordTimes = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        /// <summary>读取隔离检查使用的记录快照。</summary>
        public IReadOnlyList<IReadOnlyDictionary<string, string>> ReadRecords()
        {
            lock (_sync) return _records.Values.Select(item => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(item)).ToArray();
        }
        /// <summary>内存记录只允许幂等重试。</summary>
        public void CommitRecord(string executionId, string taskId, string nodeId, string category, DateTimeOffset recordedAtUtc, IReadOnlyDictionary<string, string> fields)
        {
            if (_recordOverride != null) { _recordOverride.CommitRecord(executionId, taskId, nodeId, category, recordedAtUtc, fields); return; }
            lock (_sync)
            {
                Dictionary<string, string> existing;
                if (_records.TryGetValue(executionId, out existing))
                {
                    if (_recordTimes[executionId] != recordedAtUtc || existing.Count != fields.Count || fields.Any(item => !existing.ContainsKey(item.Key) || existing[item.Key] != item.Value)) throw new InvalidOperationException("记录重试时间或内容不一致。");
                }
                else { _records.Add(executionId, fields.ToDictionary(item => item.Key, item => item.Value)); _recordTimes.Add(executionId, recordedAtUtc); }
            }
        }
        /// <summary>保护内存设备状态；只在短小的读写期间持有。</summary>
        private readonly object _sync = new object();
        /// <summary>每根轴的独立运行状态。</summary>
        private readonly Dictionary<string, InMemoryAxis> _axes = new Dictionary<string, InMemoryAxis>(StringComparer.OrdinalIgnoreCase);
        /// <summary>输入默认未知；测试场景必须明确注入信号。</summary>
        private readonly Dictionary<string, bool?> _inputs = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
        /// <summary>仿真输出默认未知，只有执行写入后才有值。</summary>
        private readonly Dictionary<string, bool?> _outputs = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
        /// <summary>复用已有变量实现，但本实例不连接现场变量或恢复文件。</summary>
        private readonly DeviceVariableStore _variables;
        /// <summary>与本内存设备对应的只读配置目录。</summary>
        public TaskResourceCatalog Catalog { get; }
        /// <summary>本内存设备共用的协调器；不影响其他设备实例或旧工位运行器。</summary>
        public TaskResourceCoordinator Resources { get; }

        /// <summary>从目录建立隔离状态；轴默认未使能、未回零，变量使用配置初值。</summary>
        public InMemoryTaskDevice(TaskResourceCatalog catalog, ITaskRecordSink recordSink = null)
        {
            Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _recordOverride = recordSink;
            Resources = new TaskResourceCoordinator(catalog.Resources);
            foreach (TaskAxisInfo axis in catalog.Axes.Values) _axes.Add(axis.Name, new InMemoryAxis());
            foreach (string input in catalog.Inputs) _inputs.Add(input, null);
            foreach (string output in catalog.Outputs) _outputs.Add(output, null);
            _variables = new DeviceVariableStore(catalog.Variables.Select(variable => new DeviceVariableDefinition
            {
                Name = variable.Key,
                DataType = variable.Value.ToString(),
                InitialValue = catalog.VariableInitialValues[variable.Key],
                AllowOperatorWrite = false
            }));
        }

        /// <summary>注入逻辑 DI；null 表示当前没有有效输入数据。</summary>
        public void SetInput(string name, bool? value)
        {
            lock (_sync) { RequireIo(_inputs, name, "DI"); _inputs[name] = value; }
        }

        /// <summary>读取逻辑 DI，未知状态不会被当作 OFF。</summary>
        public bool? ReadInput(string name)
        {
            lock (_sync) { RequireIo(_inputs, name, "DI"); return _inputs[name]; }
        }

        /// <summary>读取内存 DO；不会访问真实输出。</summary>
        public bool? ReadOutput(string name)
        {
            lock (_sync) { RequireIo(_outputs, name, "DO"); return _outputs[name]; }
        }

        /// <summary>注入带类型的变量文本；null 将该变量标记为无效。</summary>
        public void SetVariable(string name, string value)
        {
            lock (_sync)
            {
                DeviceVariableType type;
                if (!Catalog.Variables.TryGetValue(name, out type)) throw new ArgumentException("设备变量未配置：" + name);
                if (value == null)
                {
                    _variables.RestoreCurrentValues(new[] { new DeviceVariableSnapshot(name, type.ToString(),
                        string.Empty, false, DateTime.Now, string.Empty, false) });
                    return;
                }
                TaskRuntimeValues.Write(_variables, name, type, TaskRuntimeValues.Parse(type, value));
            }
        }

        /// <summary>复制全部当前变量值，供一次条件判断使用一致的数据。</summary>
        public IReadOnlyList<DeviceVariableSnapshot> ReadVariables()
        {
            lock (_sync) return _variables.GetSnapshot();
        }

        /// <summary>只写本内存设备的输出；由执行器在运行许可内调用。</summary>
        public void WriteOutput(string name, bool value)
        {
            lock (_sync) { RequireIo(_outputs, name, "DO"); _outputs[name] = value; }
        }

        /// <summary>按现有类型规则写入流程值；现场手动权限不等于流程赋值权限。</summary>
        public void WriteVariable(string name, object value)
        {
            lock (_sync) TaskRuntimeValues.Write(_variables, name, Catalog.Variables[name], value);
        }

        /// <summary>把两个布尔采样原子写入同一个内存变量表。</summary>
        public void WriteBooleanPair(string firstName, bool firstValue, string secondName, bool secondValue)
        {
            lock (_sync) _variables.SetBooleanPair(firstName, firstValue, secondName, secondValue);
        }

        /// <summary>拒绝错误的 IO 名称，不自动创建或猜测通道。</summary>
        private static void RequireIo(Dictionary<string, bool?> values, string name, string direction)
        {
            if (name == null || !values.ContainsKey(name)) throw new ArgumentException(direction + " 未配置：" + name);
        }
    }
}
