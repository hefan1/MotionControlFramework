using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MotionControl.Equipment.Demo.RotaryWelding;
using MotionControl.Host.Wpf.Tasks;
using MotionControl.TaskEngine.Device;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace MotionControl.OperatorPages.Checks
{
    /// <summary>核对产品记录展示只读取真实文件并区分未知、损坏及不同阶段。</summary>
    internal static class ProductRecordChecks
    {
        /// <summary>使用隔离目录检查两种记录格式，不接入正式设备。</summary>
        internal static void Run()
        {
            string directory = Path.Combine(Path.GetTempPath(), "ProductRecordChecks-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var empty = ProductRecordReader.Read(directory, "Machine01");
                Require(!empty.AnyDirectory && empty.Rows.Count == 0 && empty.Errors.Count == 0, "缺失目录错误地当作已确认生产记录。");
                var sink = new TaskFileRecordSink(directory, "Machine01");
                var fields = new Dictionary<string, string> { { "产品编号", "P-001" }, { "夹位", "A" }, { "工位", "复检" },
                    { "阶段", "待出料" }, { "检测结果", "OK" }, { "出口", "合格出口" } };
                string taskDir = Path.Combine(directory, "Runtime", "TaskRecords"); Directory.CreateDirectory(taskDir);
                string oldRecord = JsonConvert.SerializeObject(new { version = 1, machineId = "Machine01", category = "产品出料", fields });
                File.WriteAllText(Path.Combine(taskDir, "old.json"), oldRecord);
                fields["阶段"] = "已出料";
                sink.CommitRecord(Guid.NewGuid().ToString("N"), "Inspect", "done", "产品出料", DateTimeOffset.UtcNow, fields);
                string equipmentDir = Path.Combine(directory, "Runtime", "EquipmentRecords"); Directory.CreateDirectory(equipmentDir);
                string equipmentPath = Path.Combine(equipmentDir, "RotaryWelding-record.json");
                var completed = new RotaryCompletedProduct { SessionId = "session", Id = "P-002", Nest = "B", Station = "复检分拣",
                    Stage = "已完成", Result = "NG", Outlet = "不良出口", CompletedUtc = new DateTime(2026, 11, 3, 1, 0, 0, DateTimeKind.Utc) };
                File.WriteAllText(equipmentPath, JsonConvert.SerializeObject(completed, new JsonSerializerSettings { ContractResolver = new CamelCasePropertyNamesContractResolver() }));
                string before = File.ReadAllText(equipmentPath);
                var result = ProductRecordReader.Read(directory, "Machine01");
                Require(result.AnyDirectory && result.Errors.Count == 0 && result.Rows.Count == 3, "两种来源或同一产品的阶段记录丢失。");
                Require(result.Rows.Count(row => row.Source == "任务列表" && row.Product == "P-001") == 2
                    && result.Rows.Single(row => row.Stage == "待出料").RecordedTime == "未知"
                    && result.Rows.Single(row => row.Stage == "已出料").RecordedTime.Contains("本地时间"),
                    "新旧任务记录时间或两个阶段显示错误。");
                Require(result.Rows.Any(row => row.Source == "C# 流程" && row.Product == "P-002" && row.Fixture == "B"
                    && row.Result == "NG" && row.Outlet == "不良出口" && row.RecordedTime.Contains("本地时间")), "设备流程记录字段或时间错误。");
                Require(File.ReadAllText(equipmentPath) == before, "读取记录时改变了原文件。");
                File.WriteAllText(Path.Combine(equipmentDir, "RotaryWelding-invalid.json"), "{bad");
                result = ProductRecordReader.Read(directory, "Machine01");
                Require(result.Rows.Count == 3 && result.Errors.Count == 1, "损坏记录没有提示，或隐藏了其他正常记录。");
                string repeated = JsonConvert.SerializeObject(new { version = 1, machineId = "Machine01", category = "产品出料",
                    fields = new Dictionary<string, string> { { "产品编号", "OTHER" } } });
                for (int i = 0; i < 1001; i++) File.WriteAllText(Path.Combine(taskDir, "bulk-" + i.ToString("D4") + ".json"), repeated);
                var filtered = ProductRecordReader.Read(directory, "Machine01", false, "P-001");
                Require(filtered.Rows.Count == 2 && !filtered.Truncated && filtered.Errors.Count == 1,
                    "筛选未先匹配产品编号，或丢失损坏记录提示。");
                var bounded = ProductRecordReader.Read(directory, "Machine01");
                Require(bounded.Truncated && bounded.Rows.Count <= 1001, "大量记录没有标记截断或突破每来源上限。");
            }
            finally { Directory.Delete(directory, true); }
        }
        /// <summary>在检查失败时给出具体原因。</summary>
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
