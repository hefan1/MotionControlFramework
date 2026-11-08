using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MotionControl.Framework.Wpf.ManualDebug;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Configuration;
using Newtonsoft.Json.Linq;

namespace MotionControl.Metadata.Checks
{
    internal static partial class Program
    {
        /// <summary>创建完整且隔离的两轴映射，交换物理通道时逻辑键和各自单位保持。</summary>
        private static JObject Axes(bool swapped = false, string controller = "Card")
        {
            return JObject.FromObject(new { schemaVersion = 1, axes = new[] {
                new { controllerId = controller, axisNumber = swapped ? 1 : 0, name = "X", engineeringUnit = "mm", unitsPerEngineeringUnit = 1,
                    minimumPosition = -100, maximumPosition = 100, softLimitEnabled = true, manualSpeed = 1 },
                new { controllerId = controller, axisNumber = swapped ? 0 : 1, name = "R", engineeringUnit = "deg", unitsPerEngineeringUnit = 1,
                    minimumPosition = -180, maximumPosition = 180, softLimitEnabled = true, manualSpeed = 1 }
            } });
        }

        /// <summary>旧组只有物理键；显式迁移组同时保存逻辑归属和坐标单位。</summary>
        private static JObject Points(bool declared, bool swapped = false, string controller = "Card")
        {
            var groups = new JArray();
            foreach (string axis in new[] { "X", "R" })
            {
                int channel = axis == "X" ? (swapped ? 1 : 0) : (swapped ? 0 : 1);
                var group = new JObject { ["axisKey"] = controller + ".Axis" + channel, ["nextPositionNumber"] = 2,
                    ["positions"] = new JArray(new JObject { ["id"] = "P", ["name"] = "同名点位", ["position"] = axis == "X" ? 10 : 20 }) };
                if (declared)
                {
                    group["logicalAxisName"] = axis;
                    group["engineeringUnit"] = axis == "X" ? "mm" : "deg";
                }
                groups.Add(group);
            }
            return new JObject { ["schemaVersion"] = 1, ["axes"] = groups };
        }

        /// <summary>覆盖通道交换、控制器更名、错误声明、旧格式提示及原保存数据类的字段保留。</summary>
        private static void TeachPointIdentityAndMigration()
        {
            JObject oldAxes = Axes();
            JObject oldPoints = Points(false);
            var notices = new List<string>();
            TaskTeachPointOwnershipValidator.ValidateCurrent(oldAxes, oldPoints, notices);
            Check(notices.Any(message => message.Contains("归属未确认")), "旧点位没有明确提示未验证归属。");
            Reject(() => TaskTeachPointOwnershipValidator.ValidateMigration(oldAxes, oldPoints, Axes(true), oldPoints, false, null), "需显式迁移");
            Reject(() => TaskTeachPointOwnershipValidator.ValidateMigration(oldAxes, oldPoints, Axes(false, "NewCard"), Points(false, false, "NewCard"), true, null), "需显式迁移");
            TaskTeachPointOwnershipValidator.ValidateMigration(oldAxes, oldPoints, Axes(true), Points(true, true), true, notices);
            Check(notices.Any(message => message.Contains("迁移已按显式归属检查")), "明确点位迁移没有保留现场复核提示。");
            Reject(() => TaskTeachPointOwnershipValidator.ValidateCurrent(Axes(true), Points(true), null), "逻辑归属不一致");

            JObject wrongUnit = Points(true);
            wrongUnit["axes"][0]["engineeringUnit"] = "deg";
            Reject(() => TaskTeachPointOwnershipValidator.ValidateCurrent(Axes(), wrongUnit, null), "工程单位不一致");
            JObject displayOnly = Axes(); displayOnly["axes"][0]["displayName"] = "更好读的显示名";
            TaskTeachPointOwnershipValidator.ValidateMigration(oldAxes, oldPoints, displayOnly, oldPoints, false, notices);

            using (var files = new TestFiles())
            {
                WriteHardware(files, "M", Axes(true));
                files.Store.SaveJson("M", "Parameters/ManualAxisPositions.json", Points(true, true));
                var catalog = new TaskResourceCatalogLoader(files.Store).Load("M");
                Check(catalog.FindTeachPoint("X", "P").Position == 10 && catalog.FindTeachPoint("R", "P").Position == 20,
                    "通道交换后坐标没有随逻辑轴显式迁移。");
                Check(catalog.TeachPoints.All(point => point.OwnershipConfirmed), "显式归属没有进入目录。");
                var document = files.Store.LoadJson<ManualAxisPositionConfigurationDocument>("M", "Parameters/ManualAxisPositions.json");
                document.Axes[0].Positions[0].Name = "只改点位显示名";
                files.Store.SaveJson("M", "Parameters/ManualAxisPositions.json", document);
                JObject saved = files.Store.LoadJson<JObject>("M", "Parameters/ManualAxisPositions.json");
                Check((string)saved["axes"][0]["logicalAxisName"] == "X" && (string)saved["axes"][0]["engineeringUnit"] == "mm",
                    "原手动页使用的数据类保存时丢失归属字段。");
                files.Store.SaveJson("M", "Parameters/ManualAxisPositions.json", Points(false, true));
                catalog = new TaskResourceCatalogLoader(files.Store).Load("M");
                Check(catalog.TeachPoints.All(point => !point.OwnershipConfirmed), "旧文件被错误标为已确认归属。");
                files.Store.SaveJson("M", "Parameters/ManualAxisPositions.json", Points(true));
                Reject(() => new TaskResourceCatalogLoader(files.Store).Load("M"), "逻辑归属不一致");
            }
        }

        /// <summary>使用正式工程导出/预览/应用入口证明换绑阻断发生在任何目标文件写入之前。</summary>
        private static void ProjectPreviewRejectsImplicitMigration()
        {
            using (var files = new TestFiles())
            {
                WriteHardware(files, "Target", Axes());
                WriteHardware(files, "Source", Axes(true));
                files.Store.SaveJson("Target", "Parameters/ManualAxisPositions.json", Points(false));
                files.Store.SaveJson("Source", "Parameters/ManualAxisPositions.json", Points(false, true));
                var source = ProjectService(files, "Source");
                var target = ProjectService(files, "Target");
                string package = Path.Combine(files.Root, "migration.mcpkg.json");
                string targetAxes = Path.Combine(files.Store.GetMachineDirectory("Target"), "HardwareConfig", "AxisParameters.json");
                string before = File.ReadAllText(targetAxes);
                source.Export(package);
                Reject(() => target.Preview(package), "需显式迁移");
                Check(File.ReadAllText(targetAxes) == before && !target.HasPendingApply, "预览拒绝却改写目标或生成恢复标记。");

                files.Store.SaveJson("Source", "Parameters/ManualAxisPositions.json", Points(true, true));
                source.Export(package);
                MachineProjectPreview preview = target.Preview(package);
                Check(preview.Warnings.Any(message => message.Contains("现场复核")), "迁移预览未保留实际机构复核要求。");
                target.Apply(preview, () => true);
                var catalog = new TaskResourceCatalogLoader(files.Store).Load("Target");
                Check(catalog.FindTeachPoint("X", "P").Position == 10 && catalog.FindTeachPoint("R", "P").Position == 20,
                    "正式工程应用后的逻辑归属与迁移方案不一致。");
            }
        }

        /// <summary>测试只声明隔离配置适配器，不连接它，也不证明任何实体控制卡能力。</summary>
        private static MachineProjectPackageService ProjectService(TestFiles files, string machineId)
        {
            return new MachineProjectPackageService(files.Store, machineId, new[] { "TestEquipment" }, new[] { "TestBackend" },
                validateCard: ValidateTestCard);
        }

        /// <summary>仅接受本检查明确写出的单卡配置。</summary>
        private static string ValidateTestCard(MachineProjectCardSelection card)
        {
            return card.Backend == "TestBackend" && card.AdapterType == "TestAdapter" && card.CardCount == 1 && card.AxisCount == 8
                ? null : "不是本检查登记的配置。";
        }

        /// <summary>建立供正式配置校验器读取的最小完整工程；不注册真实 SDK 或启动设备。</summary>
        private static void WriteHardware(TestFiles files, string id, JObject axes)
        {
            files.Store.SaveJson(id, "HardwareConfig/AxisParameters.json", axes);
            files.Store.SaveJson(id, "HardwareConfig/MotionCards.json", new { schemaVersion = 1, motionCardVendor = "TestVendor",
                motionCardType = "TestType", axisCount = 8, motionCards = new[] { new { controllerId = "Card", adapterType = "TestAdapter",
                    connectionConfigurationFile = "HardwareConfig/Connection.json" } } });
            files.Store.SaveJson(id, "HardwareConfig/Connection.json", new { schemaVersion = 1 });
            files.Store.SaveJson(id, "HardwareConfig/IoMapping.json", new { schemaVersion = 1,
                digitalInputs = new[] { new { name = "EStop", controllerId = "Card", channel = 0, activeHigh = true } }, digitalOutputs = new object[0] });
            files.Store.SaveJson(id, "HardwareConfig/SafetyInterlock.json", new { schemaVersion = 1, emergencyStopInputName = "EStop" });
            files.Store.SaveJson(id, "Parameters/Equipment.json", new { schemaVersion = 1, definition = "TestEquipment", executionOwner = "None" });
            files.Store.SaveJson(id, "Parameters/ControllerBackend.json", new { backend = "TestBackend" });
        }
    }
}
