using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MotionControl.TaskEngine.Configuration;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Machine;
using Newtonsoft.Json.Linq;

namespace MotionControl.TaskEngine.Checks
{
    /// <summary>设备任务清单复用配置保存规则，并在启动前检查全部引用。</summary>
    internal static partial class MachineChecks
    {
        /// <summary>清单字段、任务/信号引用、设备隔离、修订备份与未知 JSON 均实际检查。</summary>
        private static Task Configuration()
        {
            var device = Device();
            var definitions = new[] { TaskDefinition("Work", TaskFixtures.Delay("work")) };
            var configuration = new TaskMachineConfiguration { MachineId = "Test01", Name = "清单检查",
                Stations = new List<StationTaskConfiguration> { Station("work", "Work", StationTaskMode.Once) } };
            CheckAssert.True(TaskMachineConfigurationValidator.Validate(configuration, definitions, device.Catalog).Count == 0, "合法清单被拒绝。");
            configuration.Stations.Add(Station("WORK", "Work", StationTaskMode.Once));
            CheckAssert.True(TaskMachineConfigurationValidator.Validate(configuration, definitions, device.Catalog).Any(error => error.Contains("重复")), "工位重复未拒绝。");
            configuration.Stations.RemoveAt(1);
            configuration.Stations[0].TaskId = "Missing";
            CheckAssert.True(TaskMachineConfigurationValidator.Validate(configuration, definitions, device.Catalog).Any(error => error.Contains("任务未加载")), "缺失任务未拒绝。");
            configuration.Stations[0].TaskId = "Work";
            configuration.Stations[0].Mode = StationTaskMode.OnRequest;
            configuration.Stations[0].RequestVariable = "Source.Id"; configuration.Stations[0].CompletedVariable = "Feed.Complete";
            CheckAssert.True(TaskMachineConfigurationValidator.Validate(configuration, definitions, device.Catalog).Any(error => error.Contains("bool")), "非 bool 交接信号被接受。");
            configuration.Stations[0].RequestVariable = "Feed.Request";
            using (var workspace = new TestWorkspace())
            {
                var store = new TaskMachineConfigurationStore(workspace.Store);
                CheckAssert.True(store.Save("Test01", configuration, definitions, device.Catalog) == null, "首次保存有备份。");
                string path = Path.Combine(workspace.Root, "Test01", "Parameters", "TaskMachine.json");
                string original = File.ReadAllText(path);
                configuration.Name = "内容已修改";
                CheckAssert.Throws(() => store.Save("Test01", configuration, definitions, device.Catalog), "未递增版本覆盖清单。");
                CheckAssert.True(File.ReadAllText(path) == original, "保存失败修改了清单。");
                configuration.Revision++;
                string backup = store.Save("Test01", configuration, definitions, device.Catalog);
                CheckAssert.True(File.Exists(backup) && store.Load("Test01", definitions, device.Catalog).Revision == 2, "清单修订备份失败。");
                CheckAssert.Throws(() => store.Save("Other", configuration, definitions, device.Catalog), "清单跨设备保存成功。");
                var unknown = JObject.Parse(File.ReadAllText(path)); unknown["unknown"] = true;
                File.WriteAllText(path, unknown.ToString());
                CheckAssert.Throws(() => store.Load("Test01", definitions, device.Catalog), "未知清单字段被忽略。");
                CheckAssert.Throws(() => store.Save("Test01", configuration, definitions, device.Catalog), "损坏已有清单被覆盖。");
                foreach (string field in new[] { "mode", "enabled" })
                {
                    var document = JObject.Parse(original); ((JObject)document["stations"][0]).Remove(field);
                    File.WriteAllText(path, document.ToString());
                    CheckAssert.Throws(() => store.Load("Test01", definitions, device.Catalog), "缺少工位关键字段被默认接受：" + field);
                }
            }
            return Task.CompletedTask;
        }
    }
}
