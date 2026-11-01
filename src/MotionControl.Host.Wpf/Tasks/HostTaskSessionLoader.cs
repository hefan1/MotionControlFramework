using MotionControl.Framework.Contracts.IO;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.Framework.Runtime.Machine;
using MotionControl.Framework.Runtime.Variables;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Configuration;
using MotionControl.TaskEngine.MachineDevice;
using MotionControl.TaskEngine.Wpf.Simulation;

namespace MotionControl.Host.Wpf.Tasks
{
    /// <summary>为正式 Host 装载已应用任务并创建设备任务会话；不启动任务或发送运动命令。</summary>
    internal static class HostTaskSessionLoader
    {
        /// <summary>使用当前运行时的卡组、逻辑 IO、变量和报警服务建立任务设备；调用方应在后台线程执行。</summary>
        internal static TaskMonitorSession Load(MachineConfigurationStore store, string machineId,
            MotionCardGroup motionCards, ILogicalIo logicalIo, DeviceVariableStore variables, MachineRuntime runtime)
        {
            var namedResources = new TaskMachineConfigurationStore(store).LoadNamedResources(machineId);
            var catalog = new TaskResourceCatalogLoader(store).Load(machineId, namedResources);
            var device = new MachineTaskDevice(catalog, motionCards, logicalIo, variables, runtime);
            return TaskMonitorSession.LoadMachine(store, machineId, device, runtime.AlarmService);
        }
    }
}
