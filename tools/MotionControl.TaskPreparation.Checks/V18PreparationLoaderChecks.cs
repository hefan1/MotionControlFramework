using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Device.InMemory;
using MotionControl.TaskEngine.Machine;
using MotionControl.TaskEngine.MachineDevice.Preparation;
using Newtonsoft.Json.Linq;

namespace MotionControl.TaskPreparation.Checks
{
    /// <summary>使用真实准备样例核对最终装载仍执行共享校验，不依赖编辑器曾经检查通过。</summary>
    internal static class V18PreparationLoaderChecks
    {
        /// <summary>在正式准备对象登记前注入缺任务、循环和资源错误，核对拒绝且没有发送任何轴动作。</summary>
        internal static void Run(MachineConfigurationStore store, TaskMachineController machine, InMemoryTaskDevice device,
            IReadOnlyDictionary<string, TaskDefinition> library)
        {
            string path = Path.Combine(store.GetMachineDirectory("Machine01"), TaskStationPreparationLoader.RelativePath);
            byte[] original = File.ReadAllBytes(path);
            try
            {
                foreach (string scenario in new[] { "task", "cycle", "axis", "output" })
                {
                    JObject configuration = JObject.Parse(System.Text.Encoding.UTF8.GetString(original));
                    switch (scenario)
                    {
                        case "task": configuration["stations"][0]["taskId"] = "MissingTask"; break;
                        case "cycle": configuration["stations"][0]["dependencies"] = new JArray("ClipPress"); break;
                        case "axis": configuration["stations"][0]["axes"][0]["name"] = "MissingAxis"; break;
                        default: configuration["stations"][0]["outputs"][0]["name"] = "MissingOutput"; break;
                    }
                    File.WriteAllText(path, configuration.ToString());
                    bool rejected = false;
                    try { TaskStationPreparationLoader.Load(store, "Machine01", machine, device, library); }
                    catch (InvalidDataException error)
                    {
                        rejected = error.Message.Contains(TaskStationPreparationLoader.RelativePath);
                    }
                    if (!rejected) throw new InvalidOperationException("最终准备装载未用共享校验拒绝并定位错误：" + scenario);
                    if (device.Catalog.Axes.Keys.Any(axis => device.ReadAxis(axis).AcceptedCommands != 0))
                        throw new InvalidOperationException("准备配置校验失败时仍发送了轴动作。");
                }
            }
            finally
            {
                // 文件位于本检查建立的唯一临时目录，仅恢复该样例的原字节。
                File.WriteAllBytes(path, original);
            }
        }
    }
}
