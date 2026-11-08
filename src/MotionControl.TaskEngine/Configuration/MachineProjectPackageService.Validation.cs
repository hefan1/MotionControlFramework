using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MotionControl.Framework.Contracts.Configuration;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.TaskEngine.Editing;
using MotionControl.TaskEngine.Preparation;
using MotionControl.TaskEngine.Validation;
using Newtonsoft.Json.Linq;

namespace MotionControl.TaskEngine.Configuration
{
    public sealed partial class MachineProjectPackageService
    {
        /// <summary>在独立临时目录核查完整设备配置及任务引用，不连接设备。</summary>
        private void ValidateContents(Dictionary<string, string> contents, string machineId,
            out string equipment, out string owner, out string backend, out string name, bool allowRetainedHardware = false)
        {
            foreach (var item in contents)
            {
                RequireAllowedPath(item.Key);
                if (item.Key.StartsWith("Parameters/", StringComparison.OrdinalIgnoreCase) && !_parameterPaths.Contains(item.Key))
                    throw new InvalidDataException("工程包包含未登记的设备参数文件：" + item.Key);
                if (item.Value == null || Encoding.UTF8.GetByteCount(item.Value) > MaximumBytes)
                    throw new InvalidDataException("配置文件内容为空或过大：" + item.Key);
                if (!allowRetainedHardware || !item.Key.StartsWith("HardwareConfig/", StringComparison.OrdinalIgnoreCase))
                    ReadStrictObject(item.Value, item.Key);
            }
            JObject card = RequireVersioned(contents, "HardwareConfig/MotionCards.json");
            JObject axes = RequireVersioned(contents, "HardwareConfig/AxisParameters.json");
            JObject io = RequireVersioned(contents, "HardwareConfig/IoMapping.json");
            JObject safety = RequireVersioned(contents, "HardwareConfig/SafetyInterlock.json");
            JObject selection = RequireVersioned(contents, "Parameters/Equipment.json");
            equipment = (string)selection["definition"];
            owner = (string)selection["executionOwner"];
            if (string.IsNullOrWhiteSpace(equipment) || !_definitions.Contains(equipment))
                throw new InvalidDataException("当前上位机未安装工程要求的设备定义：" + equipment);
            if (!string.Equals(owner, "StationFlows", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(owner, "TaskEngine", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(owner, "None", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("设备执行方式无效或未明确配置。 ");
            backend = "ZMotion";
            if (contents.TryGetValue("Parameters/ControllerBackend.json", out string backendText))
            {
                JObject backendDocument = ReadStrictObject(backendText, "控制器后端");
                backend = (string)backendDocument["backend"];
                if (string.Equals(backend, "Virtual", StringComparison.OrdinalIgnoreCase) && backendDocument["port"] != null
                    && (backendDocument["port"].Type != JTokenType.Integer || (int)backendDocument["port"] < 1 || (int)backendDocument["port"] > 65535))
                    throw new InvalidDataException("软件控制器端口无效。");
            }
            if (string.IsNullOrWhiteSpace(backend) || !_backends.Contains(backend))
                throw new InvalidDataException("当前上位机未安装工程要求的控制器后端：" + backend);
            JArray cardsArray = card["motionCards"] as JArray ?? throw new InvalidDataException("运动卡配置缺少运动卡列表。");
            JArray axesArray = axes["axes"] as JArray ?? throw new InvalidDataException("轴配置缺少轴列表。");
            JArray inputArray = io["digitalInputs"] as JArray ?? throw new InvalidDataException("IO 配置缺少数字输入列表。");
            JArray outputArray = io["digitalOutputs"] as JArray ?? throw new InvalidDataException("IO 配置缺少数字输出列表。");
            if (card["axisCount"]?.Type != JTokenType.Integer || (int)card["axisCount"] < 1)
                throw new InvalidDataException("控制卡物理轴数量无效。 ");
            int axisCount = (int)card["axisCount"];
            var cards = cardsArray.Select(item => item.ToObject<MotionCardHardwareDefinition>()).ToList();
            if (_validateCard == null) throw new InvalidOperationException("当前 Host 未提供控制卡类型和轴数的兼容性检查，不能导入工程。 ");
            string vendor = (string)card["motionCardVendor"], cardType = (string)card["motionCardType"];
            var hardwarePaths = new HashSet<string>(new[] { "HardwareConfig/MotionCards.json", "HardwareConfig/AxisParameters.json",
                "HardwareConfig/IoMapping.json", "HardwareConfig/SafetyInterlock.json" }, StringComparer.OrdinalIgnoreCase);
            foreach (MotionCardHardwareDefinition item in cards)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.AdapterType)) throw new InvalidDataException("运动卡适配器未配置。 ");
                string reason = _validateCard(new MachineProjectCardSelection { Backend = backend, Vendor = vendor, CardType = cardType,
                    AdapterType = item.AdapterType, AxisCount = axisCount, CardCount = cards.Count });
                if (!string.IsNullOrEmpty(reason)) throw new InvalidDataException("当前上位机不支持工程要求的运动卡：" + reason);
                string path = RequireAllowedPath(item.ConnectionConfigurationFile?.Replace('\\', '/'));
                if (!path.StartsWith("HardwareConfig/", StringComparison.Ordinal)) throw new InvalidDataException("控制卡连接配置必须在 HardwareConfig 目录。 ");
                hardwarePaths.Add(path);
                if (!contents.ContainsKey(path)) throw new InvalidDataException("工程包缺少运动卡连接配置：" + path);
                ReadStrictObject(contents[path], path);
            }
            foreach (string path in contents.Keys.Where(path => path.StartsWith("HardwareConfig/", StringComparison.OrdinalIgnoreCase)))
                if (!allowRetainedHardware && !hardwarePaths.Contains(path)) throw new InvalidDataException("工程包包含未引用的硬件配置：" + path);
            var axisDefinitions = new List<AxisHardwareDefinition>();
            foreach (JToken value in axesArray)
            {
                JObject item = value as JObject ?? throw new InvalidDataException("轴配置包含无效行。");
                if (string.IsNullOrWhiteSpace((string)item["name"])) continue;
                if (item["axisNumber"]?.Type != JTokenType.Integer || (int)item["axisNumber"] < 0 || (int)item["axisNumber"] >= axisCount
                    || item["minimumPosition"] == null || item["maximumPosition"] == null
                    || item["softLimitEnabled"]?.Type != JTokenType.Boolean || item["manualSpeed"] == null || item["unitsPerEngineeringUnit"] == null
                    || double.IsNaN((double)item["unitsPerEngineeringUnit"]) || double.IsInfinity((double)item["unitsPerEngineeringUnit"])
                    || (double)item["unitsPerEngineeringUnit"] <= 0)
                    throw new InvalidDataException("轴的通道、单位、行程、软限位或速度不完整：" + item["name"]);
                AxisHardwareDefinition axis = item.ToObject<AxisHardwareDefinition>();
                if (double.IsNaN(axis.MinimumPosition) || double.IsInfinity(axis.MinimumPosition) || double.IsNaN(axis.MaximumPosition)
                    || double.IsInfinity(axis.MaximumPosition) || double.IsNaN(axis.ManualSpeed) || double.IsInfinity(axis.ManualSpeed))
                    throw new InvalidDataException("轴参数必须是有限数：" + axis.Name);
                axisDefinitions.Add(axis);
            }
            var inputs = ReadIo(inputArray, "输入");
            var outputs = ReadIo(outputArray, "输出");
            MachineHardwareConfigurationValidator.Validate(cards, axisDefinitions, inputs, outputs, (string)safety["emergencyStopInputName"]);
            name = equipment;

            string temporary = Path.Combine(Path.GetTempPath(), "MotionControlProjectValidation", Guid.NewGuid().ToString("N"));
            try
            {
                var tempStore = new MachineConfigurationStore(temporary);
                string root = tempStore.GetMachineDirectory(machineId);
                foreach (var item in contents)
                {
                    string path = Path.Combine(root, item.Key.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(path, item.Value, new UTF8Encoding(false));
                }
                if (contents.ContainsKey(MachineAlarmCatalogStore.RelativeFilePath)) new MachineAlarmCatalogStore(tempStore).Load(machineId);
                if (contents.ContainsKey(IoFeedbackAssociationStore.RelativePath)) new IoFeedbackAssociationStore(tempStore, machineId).Load();
                bool hasManifest = contents.ContainsKey("Parameters/TaskMachine.json");
                bool hasTasks = contents.Keys.Any(path => path.StartsWith("Tasks/", StringComparison.OrdinalIgnoreCase));
                bool hasPreparation = contents.ContainsKey(TaskStationPreparationStore.RelativePath);
                if (string.Equals(owner, "TaskEngine", StringComparison.OrdinalIgnoreCase) && (!hasManifest || !hasTasks))
                    throw new InvalidDataException("任务列表方式需要任务清单和任务文件。 ");
                if (hasManifest || hasTasks || hasPreparation)
                {
                    if (!hasManifest || !hasTasks)
                        throw new InvalidDataException(hasPreparation ? "准备配置需要完整的任务清单和任务文件。" : "任务清单与任务文件必须同时提供。");
                    var workspace = new TaskWorkspaceStore(tempStore, _taskCapabilities);
                    TaskWorkspaceDocument draft = workspace.Load(machineId, false);
                    if (draft.Machine.MachineId != machineId) throw new InvalidDataException("任务清单设备标识不一致。");
                    // 工作区检查复用准备纯校验，缺准备任务、错误资源和依赖环必须在覆盖现场文件之前拒绝。
                    var errors = workspace.Check(draft).Where(issue => issue.Severity == TaskIssueSeverity.Error).Select(issue => issue.ToString()).ToList();
                    if (errors.Count != 0) throw new InvalidDataException("任务引用检查失败：\n" + string.Join("\n", errors));
                    if (!string.IsNullOrWhiteSpace(draft.Machine.Name)) name = draft.Machine.Name;
                }
            }
            finally
            {
                // 临时根路径由系统目录和新 GUID 组成，仅清理本次离线验证目录。
                if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
            }
        }

        /// <summary>读取必需且版本明确的设备配置。</summary>
        private static JObject RequireVersioned(Dictionary<string, string> files, string path)
        {
            if (!files.TryGetValue(path, out string value)) throw new InvalidDataException("工程包缺少必需配置：" + path);
            JObject document = ReadStrictObject(value, path);
            if (document["schemaVersion"]?.Type != JTokenType.Integer || (int)document["schemaVersion"] != 1)
                throw new InvalidDataException("配置版本不受支持：" + path);
            return document;
        }

        /// <summary>检查 IO 行的必需字段后转换为框架契约。</summary>
        private static List<DigitalIoHardwareDefinition> ReadIo(JArray source, string kind)
        {
            var result = new List<DigitalIoHardwareDefinition>();
            foreach (JToken value in source)
            {
                JObject item = value as JObject ?? throw new InvalidDataException(kind + " IO 配置包含无效行。");
                if (string.IsNullOrWhiteSpace((string)item["name"])) continue;
                if (item["channel"]?.Type != JTokenType.Integer || item["activeHigh"]?.Type != JTokenType.Boolean)
                    throw new InvalidDataException(kind + " IO 缺少通道或极性：" + item["name"]);
                result.Add(item.ToObject<DigitalIoHardwareDefinition>());
            }
            return result;
        }
    }
}
