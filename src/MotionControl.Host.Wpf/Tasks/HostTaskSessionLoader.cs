using System;
using System.Collections.Generic;
using MotionControl.Framework.Contracts.Preparation;
using MotionControl.TaskEngine.Device;
using MotionControl.Framework.Contracts.IO;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.Framework.Runtime.Machine;
using MotionControl.Framework.Runtime.Variables;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Configuration;
using MotionControl.TaskEngine.MachineDevice;
using MotionControl.TaskEngine.MachineDevice.Preparation;
using MotionControl.TaskEngine.Resources;
using MotionControl.TaskEngine.Wpf.Simulation;

namespace MotionControl.Host.Wpf.Tasks
{
    /// <summary>为正式 Host 装载已应用任务并创建设备任务会话；不启动任务或发送运动命令。</summary>
    internal static class HostTaskSessionLoader
    {
        /// <summary>使用当前运行时的卡组、逻辑 IO、变量和报警服务建立任务设备；调用方应在后台线程执行。</summary>
        internal static TaskMonitorSession Load(MachineConfigurationStore store, string machineId,
            MotionCardGroup motionCards, ILogicalIo logicalIo, DeviceVariableStore variables, MachineRuntime runtime,
            Guid sessionId)
        {
            var namedResources = new TaskMachineConfigurationStore(store).LoadNamedResources(machineId);
            var catalog = new TaskResourceCatalogLoader(store).Load(machineId, namedResources);
            var journal = new TaskResourceJournal(store.GetMachineDirectory(machineId), machineId);
            var device = new MachineTaskDevice(catalog, motionCards, logicalIo, variables, runtime, sessionId, journal,
                new TaskFileRecordSink(store.GetMachineDirectory(machineId), machineId));
            // 任务控制器一旦接管就交给调用方；后续准备解析失败也能持有并重试清理同一会话。
            return TaskMonitorSession.LoadMachine(store, machineId, device, runtime.AlarmService);
        }

        /// <summary>只在调用方已持有会话引用后装配其工位准备定义，不修改 Runtime。</summary>
        internal static IReadOnlyList<IStationPreparation> LoadPreparations(MachineConfigurationStore store, string machineId,
            TaskMonitorSession session)
        {
            return TaskStationPreparationLoader.Load(store, machineId, session.Machine, session.TaskDevice, session.Library);
        }
    }
}
