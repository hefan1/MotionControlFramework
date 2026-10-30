using System;
using System.IO;
using System.Linq;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Simulation;
using Newtonsoft.Json.Linq;

namespace MotionControl.TaskEngine.Checks
{
    /// <summary>使用现有配置文件格式验证离线共享目录。</summary>
    internal static class CatalogChecks
    {
        /// <summary>验证目录保留设备变量初值，并由内存设备装入布尔和数值初值。</summary>
        internal static void ExistingConfiguration()
        {
            using (var workspace = new TestWorkspace())
            {
                WriteHardware(workspace);
                workspace.Store.SaveJson("Test01", "Parameters/DeviceVariables.json", new {
                    schemaVersion = 1,
                    variables = new[] {
                        new { name = "Counter", dataType = "UInt32", initialValue = "5", allowOperatorWrite = true },
                        new { name = "Vision.Request", dataType = "Boolean", initialValue = "false", allowOperatorWrite = false }
                    }
                });
                workspace.Store.SaveJson("Test01", "Parameters/ManualAxisPositions.json", new {
                    schemaVersion = 1,
                    axes = new[] {
                        new { axisKey = "Card01.Axis0", nextPositionNumber = 2, positions = new[] { new { id = "P001", name = "取料位", position = 12.5 } } },
                        new { axisKey = "OldCard.Axis0", nextPositionNumber = 2, positions = new[] { new { id = "P001", name = "旧点位", position = 8.0 } } }
                    }
                });
                string[] before = Directory.GetFiles(workspace.Root, "*", SearchOption.AllDirectories);
                var catalog = new TaskResourceCatalogLoader(workspace.Store).Load("Test01", new[] { "Zone:Camera" });
                CheckAssert.True(catalog.Axes.Count == 1 && catalog.Axes.ContainsKey("x"), "逻辑轴没有正确读取。");
                CheckAssert.True(catalog.FindTeachPoint("X", "P001").Position == 12.5, "旧物理键没有对应到逻辑轴。");
                CheckAssert.True(catalog.Variables.ContainsKey("Counter") && catalog.Inputs.Contains("PartReady"), "变量或 IO 丢失。");
                CheckAssert.True(catalog.VariableInitialValues["Counter"] == "5"
                    && catalog.VariableInitialValues["Vision.Request"] == "false", "变量配置初值没有进入只读目录。");
                var device = new SimulatedTaskDevice(catalog);
                var counter = device.ReadVariables().Single(value => value.Name == "Counter");
                var request = device.ReadVariables().Single(value => value.Name == "Vision.Request");
                CheckAssert.True(counter.HasValue && counter.CurrentValue == "5", "内存仿真没有装入 UInt32 变量初值。");
                CheckAssert.True(request.HasValue && request.CurrentValue == "False", "内存仿真没有装入 Boolean 握手初值。");
                CheckAssert.True(catalog.Notices.Any(item => item.Contains("OldCard")), "旧卡点位被静默丢弃。");
                CheckAssert.True(before.SequenceEqual(Directory.GetFiles(workspace.Root, "*", SearchOption.AllDirectories)), "目录读取创建了额外文件。");
            }
        }

        /// <summary>可选文件缺失只产生提示；存在但损坏的配置必须报错。</summary>
        internal static void MissingAndBrokenFiles()
        {
            using (var workspace = new TestWorkspace())
            {
                WriteHardware(workspace);
                var loader = new TaskResourceCatalogLoader(workspace.Store);
                var catalog = loader.Load("Test01");
                CheckAssert.True(catalog.Variables.Count == 0 && catalog.TeachPoints.Count == 0 && catalog.Notices.Count == 3
                    && catalog.Notices.Any(item => item.Contains("AlarmCatalog.json")), "可选文件或报警库缺失提示处理错误。");
                workspace.Store.SaveJson("Test01", "Parameters/DeviceVariables.json", new { schemaVersion = 2, variables = new object[0] });
                CheckAssert.Throws(() => loader.Load("Test01"), "未来变量配置版本没有被拒绝。");
                workspace.Store.SaveJson("Test01", "Parameters/DeviceVariables.json", new { schemaVersion = 1, variables = new object[0] });
                workspace.Store.SaveJson("Test01", "Parameters/ManualAxisPositions.json", new { schemaVersion = 1, axes = "bad" });
                CheckAssert.Throws(() => loader.Load("Test01"), "损坏示教文件被忽略。");
            }
        }

        /// <summary>重复名称和无效范围不能进入只读目录。</summary>
        internal static void InvalidCatalog()
        {
            using (var workspace = new TestWorkspace())
            {
                WriteHardware(workspace);
                JObject axes = workspace.Store.LoadJson<JObject>("Test01", "HardwareConfig/AxisParameters.json");
                ((JArray)axes["axes"]).Add(axes["axes"][0].DeepClone());
                workspace.Store.SaveJson("Test01", "HardwareConfig/AxisParameters.json", axes);
                CheckAssert.Throws(() => new TaskResourceCatalogLoader(workspace.Store).Load("Test01"), "重复物理映射被接受。");
            }
            CheckAssert.Throws(() => new TaskResourceCatalog(
                new[] { new TaskAxisInfo("X", "Card", "mm", 10, -10) }, new string[0], new string[0],
                new System.Collections.Generic.Dictionary<string, MotionControl.Framework.Contracts.Variables.DeviceVariableType>(),
                new TaskTeachPoint[0]), "反向行程被接受。");
        }

        /// <summary>只在临时目录写入与现有硬件配置一致的最小样例。</summary>
        private static void WriteHardware(TestWorkspace workspace)
        {
            workspace.Store.SaveJson("Test01", "HardwareConfig/AxisParameters.json", new {
                schemaVersion = 1,
                axes = new[] { new { name = "X", controllerId = "Card01", axisNumber = 0, engineeringUnit = "mm", minimumPosition = -100.0, maximumPosition = 100.0 } }
            });
            workspace.Store.SaveJson("Test01", "HardwareConfig/IoMapping.json", new {
                schemaVersion = 1,
                digitalInputs = new[] { new { name = "PartReady", controllerId = "Card01", channel = 0, activeHigh = true } },
                digitalOutputs = new[] { new { name = "Vacuum", controllerId = "Card01", channel = 1, activeHigh = false } }
            });
        }
    }
}
