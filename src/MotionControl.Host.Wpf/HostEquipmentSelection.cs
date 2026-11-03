using System;
using System.IO;
using MotionControl.Equipment.Demo;
using MotionControl.Equipment.Demo.WeldingExample;
using MotionControl.Equipment.Demo.RotaryWelding;
using MotionControl.Framework.Contracts.Equipment;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Runtime.Configuration;

namespace MotionControl.Host.Wpf
{
    /// <summary>读取设备选择配置，显式选择设备项目与运行入口；不创建运行时或启动设备。</summary>
    internal static class HostEquipmentSelection
    {
        /// <summary>设备定义与执行入口在设备配置目录内使用的相对路径。</summary>
        private const string EquipmentSelectionPath = "Parameters/Equipment.json";

        /// <summary>按配置名称选择设备定义；新增设备项目时在这里显式登记。</summary>
        internal static IEquipmentDefinition ResolveEquipmentDefinition(MachineConfigurationStore store, string machineId)
        {
            EquipmentSelectionSettings settings = LoadEquipmentSelection(store, machineId);
            string name = settings == null || settings.Definition == null
                ? string.Empty
                : settings.Definition.Trim();
            if (name.Length == 0)
            {
                name = "Demo";
            }

            if (string.Equals(name, "Demo", StringComparison.OrdinalIgnoreCase))
            {
                return new DemoEquipmentDefinition();
            }

            if (string.Equals(name, "WeldingExample", StringComparison.OrdinalIgnoreCase))
            {
                return new WeldingExampleEquipmentDefinition();
            }

            if (string.Equals(name, "RotaryWelding", StringComparison.OrdinalIgnoreCase))
            {
                return new RotaryWeldingEquipmentDefinition();
            }

            throw new InvalidOperationException(
                "设备定义配置 '" + EquipmentSelectionPath + "' 指定了未登记的设备定义：" + name
                + "。当前版本支持：Demo、WeldingExample、RotaryWelding。");
        }

        /// <summary>解析 C# 工位、任务列表或手动入口；留空交由 Runtime 按工位配置判断。</summary>
        internal static MachineControlOwner? ResolveExecutionOwner(MachineConfigurationStore store, string machineId)
        {
            EquipmentSelectionSettings settings = LoadEquipmentSelection(store, machineId);
            string value = settings == null || settings.ExecutionOwner == null
                ? string.Empty
                : settings.ExecutionOwner.Trim();
            if (value.Length == 0)
            {
                return null;
            }

            if (string.Equals(value, "StationFlows", StringComparison.OrdinalIgnoreCase))
            {
                return MachineControlOwner.StationFlows;
            }

            if (string.Equals(value, "TaskEngine", StringComparison.OrdinalIgnoreCase))
            {
                return MachineControlOwner.TaskEngine;
            }

            if (string.Equals(value, "None", StringComparison.OrdinalIgnoreCase))
            {
                return MachineControlOwner.None;
            }

            throw new InvalidOperationException(
                "设备配置 '" + EquipmentSelectionPath + "' 的执行入口无法识别：" + value
                + "。可填写 StationFlows、TaskEngine 或 None，留空表示按工位流程自动判断。");
        }

        /// <summary>读取设备选择文件；缺少文件时写入与现有 Host 一致的默认配置。</summary>
        private static EquipmentSelectionSettings LoadEquipmentSelection(MachineConfigurationStore store, string machineId)
        {
            string fullPath = Path.Combine(
                store.GetMachineDirectory(machineId),
                EquipmentSelectionPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
            {
                store.SaveJson(machineId, EquipmentSelectionPath, new EquipmentSelectionSettings());
            }

            return store.LoadJson<EquipmentSelectionSettings>(machineId, EquipmentSelectionPath);
        }
    }
}
