using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.Framework.Wpf.Configuration;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Configuration;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Machine;
using MotionControl.TaskEngine.Device;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.TaskEngine.Device.InMemory;

namespace MotionControl.TaskEngine.Wpf.Simulation
{
    /// <summary>一个监控窗口独占一个内存会话；所有引用通过既有目录校验。</summary>
    public sealed class TaskMonitorSession
    {
        /// <summary>本会话冻结的任务定义副本，页面不修改它们。</summary>
        public IReadOnlyDictionary<string, TaskDefinition> Library { get; }
        /// <summary>仅供独立内存工作台修改输入和变量；设备会话中为空。</summary>
        public InMemoryTaskDevice Device { get; }
        /// <summary>本会话监控和执行使用的设备端口。</summary>
        public ITaskDevice TaskDevice { get; }
        /// <summary>整机启停入口，页面不直接控制单个 TaskRun。</summary>
        public TaskMachineController Machine { get; }
        /// <summary>配置来源文字，便于区分示例与本机。</summary>
        public string Source { get; }
        /// <summary>示例数据不允许跳转为真实配置中的同名对象。</summary>
        public bool IsExample { get; }
        /// <summary>本机会话加载的现场启动、停止、复位按钮映射；示例会话保持空映射。</summary>
        public SafetyInterlockConfigurationDocument ControlInputConfiguration { get; }

        /// <summary>冻结显示库并建立控制器；不会启动或连接任何运动卡。</summary>
        public TaskMonitorSession(TaskMachineConfiguration configuration, IEnumerable<TaskDefinition> definitions,
            InMemoryTaskDevice device, string source, bool isExample,
            SafetyInterlockConfigurationDocument controlInputConfiguration = null)
            : this(configuration, definitions, (ITaskDevice)device, source, isExample, null, controlInputConfiguration)
        {
        }

        /// <summary>冻结任务定义并连接指定设备与报警服务；构造不启动任务或运动。</summary>
        public TaskMonitorSession(TaskMachineConfiguration configuration, IEnumerable<TaskDefinition> definitions,
            ITaskDevice device, string source, bool isExample, IMachineAlarmService alarmService,
            SafetyInterlockConfigurationDocument controlInputConfiguration = null)
        {
            var frozen = definitions.Select(item => TaskDefinitionJson.Deserialize(TaskDefinitionJson.Serialize(item)))
                .ToDictionary(item => item.TaskId, StringComparer.OrdinalIgnoreCase);
            TaskDevice = device ?? throw new ArgumentNullException(nameof(device));
            Machine = new TaskMachineController(configuration, frozen.Values, device, alarmService);
            Library = new ReadOnlyDictionary<string, TaskDefinition>(frozen);
            Device = device as InMemoryTaskDevice; Source = source; IsExample = isExample;
            ControlInputConfiguration = controlInputConfiguration ?? new SafetyInterlockConfigurationDocument();
        }

        /// <summary>只读本机任务及已有轴、IO、变量、示教点文件，不创建另一份映射清单。</summary>
        public static TaskMonitorSession LoadMachine(MachineConfigurationStore store, string machineId)
        {
            return LoadMachine(store, machineId, null, null);
        }

        /// <summary>用正式设备和 Runtime 报警服务装载现有本机任务，不生成另一份配置。</summary>
        public static TaskMonitorSession LoadMachine(MachineConfigurationStore store, string machineId,
            ITaskDevice device, IMachineAlarmService alarmService)
        {
            new MotionControl.TaskEngine.Editing.TaskWorkspaceStore(store).RequireNoPendingApply(machineId);
            string taskFolder = Path.Combine(store.GetMachineDirectory(machineId), "Tasks");
            if (!Directory.Exists(taskFolder)) throw new InvalidOperationException("本机尚无 Tasks 任务目录。可先打开示例任务查看页面。");
            var definitionStore = new TaskDefinitionStore(store);
            var definitions = Directory.GetFiles(taskFolder, "*.json").OrderBy(path => path)
                .Select(path => definitionStore.LoadForEditing(machineId, Path.GetFileName(path))).ToList();
            var machineStore = new TaskMachineConfigurationStore(store);
            var namedResources = machineStore.LoadNamedResources(machineId);
            var catalog = new TaskResourceCatalogLoader(store).Load(machineId, namedResources);
            var configuration = machineStore.Load(machineId, definitions, catalog);
            string controlInputPath = Path.Combine(store.GetMachineDirectory(machineId), "HardwareConfig", "SafetyInterlock.json");
            var controlInputConfiguration = new SafetyInterlockConfigurationDocument();
            if (File.Exists(controlInputPath))
            {
                controlInputConfiguration = store.LoadJson<SafetyInterlockConfigurationDocument>(
                    machineId,
                    "HardwareConfig/SafetyInterlock.json");
                if (controlInputConfiguration == null || controlInputConfiguration.SchemaVersion != 1)
                {
                    throw new InvalidOperationException("SafetyInterlock.json 格式无效，只支持 schemaVersion 为 1。");
                }
            }

            ITaskDevice selectedDevice = device ?? new InMemoryTaskDevice(catalog);
            return new TaskMonitorSession(configuration, definitions, selectedDevice,
                store.GetMachineDirectory(machineId), false, alarmService, controlInputConfiguration);
        }
    }
}
