using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.TaskEngine.Configuration;
using MotionControl.TaskEngine.Device;
using Newtonsoft.Json.Linq;

namespace MotionControl.MachineProject.Checks
{
    internal static partial class Program
    {
        /// <summary>真实工程包预检区分定义和执行能力，并分别保留导出来源、导入目标及包外配置身份。</summary>
        private static void CheckProjectCapabilityAndSources(string temporary)
        {
            string folder = Path.Combine(temporary, "V18ProjectCapabilities");
            var store = new MachineConfigurationStore(Path.Combine(folder, "config"));
            string sourceRoot = store.GetMachineDirectory("CapabilitySource");
            string targetRoot = store.GetMachineDirectory("CapabilityTarget");
            CreateFixture(sourceRoot, "CapabilitySource", "GroupX", "TaskEngine");
            CreateFixture(targetRoot, "CapabilityTarget", "OldAxis", "StationFlows");
            JObject axes = JObject.Parse(File.ReadAllText(Path.Combine(sourceRoot, "HardwareConfig/AxisParameters.json")));
            JObject secondAxis = (JObject)axes["axes"][0].DeepClone();
            secondAxis["axisNumber"] = 1;
            secondAxis["name"] = "GroupY";
            ((JArray)axes["axes"]).Add(secondAxis);
            Write(sourceRoot, "HardwareConfig/AxisParameters.json", axes.ToString());
            JObject task = JObject.Parse(File.ReadAllText(Path.Combine(sourceRoot, "Tasks/T1.json")));
            ((JArray)task["root"]["children"]).Add(JObject.Parse(@"{
                'kind':'MoveAxisGroup','id':'project-group','name':'工程组定位','timeoutMs':30000,
                'targets':[
                    { 'axisName':'GroupX','mode':'Absolute','target':{'source':'Constant','dataType':'Number','constant':'0'},
                      'profile':{'speed':1,'acceleration':10,'deceleration':10} },
                    { 'axisName':'GroupY','mode':'Absolute','target':{'source':'Constant','dataType':'Number','constant':'0'},
                      'profile':{'speed':1,'acceleration':10,'deceleration':10} }
                ]
            }"));
            Write(sourceRoot, "Tasks/T1.json", task.ToString());
            const string retainedPath = "Parameters/DemoStations.json";
            const string retainedContent = "{\"schemaVersion\":1,\"note\":\"仅目标设备保留的配置\"}";
            Write(targetRoot, retainedPath, retainedContent);
            string package = Path.Combine(folder, "group-project.json");
            NewService(store, "CapabilitySource").Export(package);
            string before = ReadPreparationImportTargets(targetRoot);

            var definitionsOnly = NewService(store, "CapabilityTarget");
            MachineProjectPreview preview = definitionsOnly.Preview(package);
            Require(definitionsOnly.TaskCapabilities == null, "没有传入能力时却声称已核对后端能力。");
            Require(preview.SourceMachineId == "CapabilitySource" && preview.TargetMachineId == "CapabilityTarget",
                "工程预览混淆了导出来源和实际导入目标。");
            Require((string)JObject.Parse(preview.GetFileContent("Parameters/TaskMachine.json"))["machineId"] == "CapabilityTarget",
                "预览中的任务清单没有绑定实际目标设备。");
            Require(!preview.Files.Contains(retainedPath) && preview.MergedFiles.Contains(retainedPath)
                && preview.RetainedFiles.Contains(retainedPath) && preview.GetCurrentFileContent(retainedPath) == retainedContent,
                "目标保留文件被当成本包内容，或预览丢失了原值。");
            Require(new HashSet<string>(preview.MergedFiles, StringComparer.OrdinalIgnoreCase)
                .SetEquals(preview.Files.Concat(preview.RetainedFiles)), "合并预览集合不是包内和保留文件的明确并集。");
            ExpectFailure(() => preview.GetFileContent(retainedPath), "不包含");

            var capabilities = new TaskDeviceCapabilities("MachineTaskDevice", false, false);
            var restricted = new MachineProjectPackageService(store, "CapabilityTarget", new[] { "Demo" },
                new[] { "ZMotion", "Virtual" }, null, CheckCard, capabilities);
            Require(ReferenceEquals(restricted.TaskCapabilities, capabilities), "工程服务没有保留 Host 提供的只读能力来源。");
            ExpectFailure(() => restricted.Preview(package), "Capability.MoveAxisGroup");
            Require(before == ReadPreparationImportTargets(targetRoot) && !restricted.HasPendingApply,
                "工程能力预检拒绝时修改了目标配置或进入应用事务。");
        }
    }
}
