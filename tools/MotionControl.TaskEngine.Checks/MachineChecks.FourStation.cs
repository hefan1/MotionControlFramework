using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Configuration;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Device.InMemory;
using MotionControl.TaskEngine.Machine;
using Newtonsoft.Json.Linq;

namespace MotionControl.TaskEngine.Checks
{
    /// <summary>验证四工位样例与给定设备目录兼容，并仅在内存中检查握手及故障恢复。</summary>
    internal static partial class MachineChecks
    {
        /// <summary>不改指定配置；读取现有设备目录并加上样例新增项进行检查。</summary>
        internal static int RunFourStationDemo(string configurationRoot, string machineId)
        {
            try { RunFourStationDemoAsync(configurationRoot, machineId).GetAwaiter().GetResult(); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
            finally
            {
                foreach (TaskMachineController machine in Machines) Wait(machine.CloseAsync()).GetAwaiter().GetResult();
                Machines.Clear();
            }
        }

        /// <summary>只读校验指定目录中实际的全部任务和设备清单，不创建设备会话。</summary>
        internal static int ValidateTaskMachine(string configurationRoot, string machineId)
        {
            try
            {
                var store = new MachineConfigurationStore(configurationRoot);
                var definitionStore = new TaskDefinitionStore(store);
                string folder = Path.Combine(store.GetMachineDirectory(machineId), "Tasks");
                var definitions = Directory.GetFiles(folder, "*.json").OrderBy(path => path)
                    .Select(path => definitionStore.LoadForEditing(machineId, Path.GetFileName(path))).ToArray();
                var machineStore = new TaskMachineConfigurationStore(store);
                var catalog = new TaskResourceCatalogLoader(store).Load(machineId, machineStore.LoadNamedResources(machineId));
                var machine = machineStore.Load(machineId, definitions, catalog);
                Console.WriteLine("[通过] 已安装清单及 " + definitions.Length + " 份任务通过设备配置校验，工位=" + machine.Stations.Count + "。未连接设备。");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }

        /// <summary>通过正式配置解析及校验器检查清单，并验证初始化、超时和原步恢复。</summary>
        private static async Task RunFourStationDemoAsync(string configurationRoot, string machineId)
        {
            string folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Samples", "FourStation");
            var original = new TaskResourceCatalogLoader(new MachineConfigurationStore(configurationRoot)).Load(machineId);
            var variables = original.Variables.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            var initialValues = original.VariableInitialValues.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            foreach (JToken row in (JArray)JObject.Parse(File.ReadAllText(Path.Combine(folder, "Variables.additions.json")))["variables"])
            {
                string name = (string)row["name"];
                DeviceVariableType type = (DeviceVariableType)Enum.Parse(typeof(DeviceVariableType), (string)row["dataType"]);
                if (variables.ContainsKey(name) && variables[name] != type) throw new InvalidOperationException("现有变量类型冲突：" + name);
                variables[name] = type;
                initialValues[name] = (string)row["initialValue"];
            }
            var alarms = original.AlarmDefinitions.ToList();
            foreach (JToken row in (JArray)JObject.Parse(File.ReadAllText(Path.Combine(folder, "Alarms.additions.json")))["alarms"])
            {
                string code = (string)row["code"];
                MachineAlarmKind kind = (MachineAlarmKind)Enum.Parse(typeof(MachineAlarmKind), (string)row["kind"]);
                var existing = alarms.FirstOrDefault(item => string.Equals(item.Code, code, StringComparison.OrdinalIgnoreCase));
                if (existing != null && existing.Kind != kind) throw new InvalidOperationException("现有报警类型冲突：" + code);
                if (existing == null) alarms.Add(new TaskAlarmInfo(code, (string)row["message"], kind));
            }
            var namedResources = original.Resources.Where(name => !name.StartsWith("Axis:", StringComparison.OrdinalIgnoreCase)
                && !name.StartsWith("Output:", StringComparison.OrdinalIgnoreCase));
            var catalog = new TaskResourceCatalog(original.Axes.Values, original.Inputs, original.Outputs, variables,
                original.TeachPoints, namedResources, variableInitialValues: initialValues, alarmDefinitions: alarms);
            var definitions = new[] { "FourInit", "FourLoad", "FourPress", "FourInspect", "FourUnload" }
                .Select(name => TaskDefinitionJson.Deserialize(File.ReadAllText(Path.Combine(folder, name + ".json")))).ToArray();
            TaskMachineConfiguration configuration;
            using (var workspace = new TestWorkspace())
            {
                string target = Path.Combine(workspace.Store.GetMachineDirectory(machineId), "Parameters");
                Directory.CreateDirectory(target);
                File.Copy(Path.Combine(folder, "TaskMachine.json"), Path.Combine(target, "TaskMachine.json"));
                configuration = new TaskMachineConfigurationStore(workspace.Store).Load(machineId, definitions, catalog);
            }
            Console.WriteLine("[通过] 四工位清单及任务已按现有设备轴、IO、变量和报警目录校验；未写入该设备配置。");

            // 缩短本次内存测试的故障等待；仓库样例和设备配置仍使用 30 秒。
            var press = (SequenceNode)definitions.Single(item => item.TaskId == "FourPress").Root;
            ((WaitInputNode)press.Children.Single(item => item.Id == "pressDone")).TimeoutMs = 800;
            var device = new InMemoryTaskDevice(catalog);
            foreach (string axis in new[] { "Load_X", "Press_Z", "Camera_X", "Unload_X" }) device.ConfigureAxis(axis, 0, true, true);
            foreach (string name in new[] { "Press.Request", "Press.Complete", "Inspect.Request", "Inspect.Complete", "Unload.Request", "Unload.Complete" })
                device.SetVariable(name, "true");
            foreach (string name in new[] { "上料到位", "压装完成", "检测完成", "下料到位" }) device.SetInput(name, name == "上料到位");
            var machine = new TaskMachineController(configuration, definitions, device); Machines.Add(machine);
            int pressStuck = 1;
            using (var cancel = new CancellationTokenSource())
            {
                Task signals = Task.Run(async () =>
                {
                    bool clampSeen = false;
                    while (!cancel.IsCancellationRequested)
                    {
                        if (device.ReadOutput("夹具夹紧") == true) clampSeen = true;
                        if (clampSeen && device.ReadOutput("夹具夹紧") == false) device.SetInput("上料到位", false);
                        if (Volatile.Read(ref pressStuck) == 0) device.SetInput("压装完成", device.ReadOutput("压头动作") == true);
                        device.SetInput("检测完成", device.ReadOutput("相机触发") == true);
                        device.SetInput("下料到位", device.ReadOutput("下料电磁阀") == true);
                        await Task.Delay(20).ConfigureAwait(false);
                    }
                });
                try
                {
                    machine.Reset(); machine.Start();
                    await Until(() => machine.GetSnapshot().State == TaskMachineState.Faulted, "压装反馈异常未触发停机。");
                    var failed = machine.GetSnapshot();
                    CheckAssert.True(failed.Stations.Single(item => item.StationId == "Press").Run.Nodes.Any(node => node.NodeId == "pressDone"), "故障未定位到压装完成等待。");
                    CheckAssert.True(BooleanValue(device, "Inspect.Request") == false && BooleanValue(device, "Unload.Request") == false,
                        "初始化未清除残留请求，下游出现旧请求。");
                    Volatile.Write(ref pressStuck, 0);
                    machine.Reset(); machine.Start();
                    await Until(() => machine.GetSnapshot().Stations.All(item => item.CompletedCycles >= 1), "故障恢复后四个工位未完成一件产品。");
                    await Until(() => BooleanValue(device, "Press.Complete") == false && BooleanValue(device, "Inspect.Complete") == false
                        && BooleanValue(device, "Unload.Complete") == false, "交接完成信号未复归。");
                    machine.Pause();
                    CheckAssert.True(machine.GetSnapshot().Stations.All(item => item.CompletedCycles == 1), "出现重复工位周期。");
                    Console.WriteLine("[通过] 内存检查：残留交接初始化、压装反馈超时、复位续跑、四工位各完成一次并复归。");
                }
                finally { cancel.Cancel(); await signals.ConfigureAwait(false); }
            }
        }
    }
}
