using System;
using System.IO;
using System.Linq;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.TaskEngine.Preparation;
using Newtonsoft.Json.Linq;

namespace MotionControl.MachineProject.Checks
{
    internal static partial class Program
    {
        /// <summary>工程预览复用准备纯校验，错误准备任务、依赖环和资源引用在写入目标目录前被拒绝。</summary>
        private static void CheckPreparationImport(string temporary)
        {
            string folder = Path.Combine(temporary, "V18PreparationImport");
            var store = new MachineConfigurationStore(Path.Combine(folder, "config"));
            string sourceRoot = store.GetMachineDirectory("PreparationSource");
            string targetRoot = store.GetMachineDirectory("PreparationTarget");
            CreateFixture(sourceRoot, "PreparationSource", "PreparationAxis", "TaskEngine");
            CreateFixture(targetRoot, "PreparationTarget", "TargetAxis", "StationFlows");
            JObject preparationTask = JObject.Parse(File.ReadAllText(Path.Combine(sourceRoot, "Tasks/T1.json")));
            preparationTask["taskId"] = "PrepareOne";
            Write(sourceRoot, "Tasks/PrepareOne.json", preparationTask.ToString());
            preparationTask["taskId"] = "PrepareTwo";
            Write(sourceRoot, "Tasks/PrepareTwo.json", preparationTask.ToString());
            JObject preparation = JObject.Parse(@"{
                'schemaVersion': 1,
                'stations': [
                    { 'id': 'SetupOne', 'name': '准备一', 'taskId': 'PrepareOne', 'dependencies': [],
                      'axes': [{ 'name': 'PreparationAxis', 'position': 0, 'tolerance': 0.01 }], 'inputs': [], 'outputs': [], 'emptyVariables': [] },
                    { 'id': 'SetupTwo', 'name': '准备二', 'taskId': 'PrepareTwo', 'dependencies': ['SetupOne'],
                      'axes': [{ 'name': 'PreparationAxis', 'position': 0, 'tolerance': 0.01 }], 'inputs': [], 'outputs': [], 'emptyVariables': [] }
                ]
            }");
            Write(sourceRoot, TaskStationPreparationStore.RelativePath, preparation.ToString());
            string packagePath = Path.Combine(folder, "valid.json");
            NewService(store, "PreparationSource").Export(packagePath);
            var target = NewService(store, "PreparationTarget");
            Require(target.Preview(packagePath).Files.Contains(TaskStationPreparationStore.RelativePath),
                "独立准备配置未进入工程预检，或被错误要求与生产工位同名。");
            string originalPackage = File.ReadAllText(packagePath);
            string before = ReadPreparationImportTargets(targetRoot);

            foreach (string scenario in new[] { "缺失任务", "未填写任务", "依赖环", "错误轴", "错误输出", "缺少任务清单" })
            {
                JObject package = JObject.Parse(originalPackage);
                JObject candidate = (JObject)preparation.DeepClone();
                string expected = TaskStationPreparationStore.RelativePath;
                switch (scenario)
                {
                    case "缺失任务": candidate["stations"][0]["taskId"] = "MissingPreparationTask"; break;
                    case "未填写任务": candidate["stations"][0]["taskId"] = JValue.CreateNull(); break;
                    case "依赖环": candidate["stations"][0]["dependencies"] = new JArray("SetupTwo"); expected = "依赖存在循环"; break;
                    case "错误轴": candidate["stations"][0]["axes"][0]["name"] = "MissingAxis"; break;
                    case "错误输出": candidate["stations"][0]["outputs"] = new JArray(JObject.Parse("{'name':'MissingOutput','expected':false}")); break;
                    default:
                        foreach (string path in ((JObject)package["files"]).Properties().Select(property => property.Name)
                            .Where(path => path.StartsWith("Tasks/", StringComparison.Ordinal) || path == "Parameters/TaskMachine.json").ToArray())
                        {
                            ((JObject)package["files"]).Remove(path);
                            ((JObject)package["hashes"]).Remove(path);
                        }
                        JObject selection = JObject.Parse((string)package["files"]["Parameters/Equipment.json"]);
                        selection["executionOwner"] = "StationFlows";
                        SetPackageFile(package, "Parameters/Equipment.json", selection.ToString());
                        expected = "准备配置需要完整的任务清单";
                        break;
                }
                SetPackageFile(package, TaskStationPreparationStore.RelativePath, candidate.ToString());
                string invalidPath = Path.Combine(folder, scenario + ".json");
                File.WriteAllText(invalidPath, package.ToString());
                ExpectFailure(() => target.Preview(invalidPath), expected);
                Require(before == ReadPreparationImportTargets(targetRoot) && !target.HasPendingApply,
                    "准备预检失败仍改写了当前工程或创建应用标记：" + scenario);
            }
        }

        /// <summary>按文件路径和原字节比较目标目录，确认预览没有部分覆盖配置。</summary>
        private static string ReadPreparationImportTargets(string root)
        {
            return string.Join("\n", Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(path => path.Substring(root.Length) + ":" + Convert.ToBase64String(File.ReadAllBytes(path))));
        }
    }
}
