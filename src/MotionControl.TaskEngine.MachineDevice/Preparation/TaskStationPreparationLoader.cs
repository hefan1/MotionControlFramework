using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MotionControl.Framework.Contracts.Preparation;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Device;
using MotionControl.TaskEngine.Machine;
using MotionControl.TaskEngine.Preparation;
using MotionControl.TaskEngine.Validation;

namespace MotionControl.TaskEngine.MachineDevice.Preparation
{
    /// <summary>在现有任务会话内装配准备项目；配置纯校验由 TaskEngine 共享，现场条件仍由准备执行对象复核。</summary>
    public static class TaskStationPreparationLoader
    {
        /// <summary>沿用既有对外路径常量，实际文件格式和校验入口位于不依赖设备的 TaskEngine 层。</summary>
        public const string RelativePath = TaskStationPreparationStore.RelativePath;

        /// <summary>先复用编辑和导入使用的完整校验，再登记准备项目；不按准备名称绑定生产工位。</summary>
        public static IReadOnlyList<IStationPreparation> Load(MachineConfigurationStore store, string machineId,
            TaskMachineController machine, ITaskDevice device, IReadOnlyDictionary<string, TaskDefinition> library)
        {
            if (store == null || machine == null || device == null || library == null)
                throw new ArgumentNullException("准备装配依赖不能为空。");
            TaskStationPreparationConfiguration configuration = new TaskStationPreparationStore(store).Load(machineId);
            if (configuration == null) return new List<IStationPreparation>().AsReadOnly();

            var errors = TaskStationPreparationValidator.Validate(configuration, device.Catalog, library.Values)
                .Where(issue => issue.Severity == TaskIssueSeverity.Error).ToList();
            if (errors.Count > 0) throw new InvalidDataException("设备准备配置校验失败：\n" + string.Join("\n", errors));

            var preparations = new List<IStationPreparation>();
            foreach (TaskStationPreparationDefinition item in configuration.Stations)
                preparations.Add(new TaskStationPreparation(item, machine, device));
            return preparations.AsReadOnly();
        }
    }
}
