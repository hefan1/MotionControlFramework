using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace MotionControl.Host.Wpf.Tasks
{
    /// <summary>设备已写入的产品记录显示行，未记录的字段显示未知。</summary>
    internal sealed class ProductRecordRow
    {
        /// <summary>记录来源是任务列表还是 C# 工位流程。</summary>
        public string Source { get; set; }
        /// <summary>设备返回的产品编号；不是跨会话唯一性证明。</summary>
        public string Product { get; set; }
        /// <summary>记录中的夹位。</summary>
        public string Fixture { get; set; }
        /// <summary>记录时的工位。</summary>
        public string Station { get; set; }
        /// <summary>记录时的阶段，待出料和已出料分别保留。</summary>
        public string Stage { get; set; }
        /// <summary>检测结果，不根据出口自行推断。</summary>
        public string Result { get; set; }
        /// <summary>记录中的出料口。</summary>
        public string Outlet { get; set; }
        /// <summary>业务记录时间；记录没有时间时为未知。</summary>
        public string RecordedTime { get; set; }
        /// <summary>记录文件名，用于区分同产品的阶段记录和排查错误。</summary>
        public string RecordId { get; set; }
    }

    /// <summary>一次只读加载结果，不对缺失或损坏记录推断产量。</summary>
    internal sealed class ProductRecordReadResult
    {
        /// <summary>成功解析的独立记录。</summary>
        public List<ProductRecordRow> Rows { get; } = new List<ProductRecordRow>();
        /// <summary>读取失败的记录及目录。</summary>
        public List<string> Errors { get; } = new List<string>();
        /// <summary>是否存在至少一个记录目录。</summary>
        public bool AnyDirectory { get; set; }
        /// <summary>是否因数量上限未加载全部文件。</summary>
        public bool Truncated { get; set; }
    }

    /// <summary>只读解析当前设备两种产品记录；不创建、删除或覆盖任何文件。</summary>
    internal static class ProductRecordReader
    {
        /// <summary>每种来源单次最多读取的文件数；页面明确提示截断，不按文件名推断生产时间。</summary>
        private const int MaxFilesPerSource = 1000;
        /// <summary>限制单份记录大小，避免损坏文件占满界面内存。</summary>
        private const long MaxFileBytes = 1024 * 1024;

        /// <summary>分别读取任务记录和设备流程记录；一个来源失败不隐藏另一个来源。</summary>
        internal static ProductRecordReadResult Read(string machineDirectory, string machineId, bool english = false, string productFilter = null)
        {
            if (string.IsNullOrWhiteSpace(machineDirectory) || string.IsNullOrWhiteSpace(machineId))
                throw new ArgumentException("设备目录或编号为空。");
            var result = new ProductRecordReadResult();
            string filter = productFilter?.Trim();
            ReadDirectory(Path.Combine(machineDirectory, "Runtime", "TaskRecords"), "*.json", true, machineId, english, filter, result);
            ReadDirectory(Path.Combine(machineDirectory, "Runtime", "EquipmentRecords"), "RotaryWelding-*.json", false, machineId, english, filter, result);
            return result;
        }

        /// <summary>单条损坏或无法读取时记录错误并继续；读取时不访问临时文件。</summary>
        private static void ReadDirectory(string directory, string pattern, bool task, string machineId, bool english, string filter, ProductRecordReadResult result)
        {
            try
            {
                var files = Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly);
                // 先匹配产品编号再限制结果数量，避免目标记录因目录枚举顺序而不可查。
                int matched = 0;
                result.AnyDirectory = true;
                foreach (string file in files)
                {
                    try
                    {
                        var info = new FileInfo(file);
                        if (info.Length > MaxFileBytes) throw new InvalidDataException("记录文件过大");
                        JObject document;
                        using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                            document = JObject.Parse(reader.ReadToEnd());
                        ProductRecordRow row = task ? ReadTask(document, machineId, english) : ReadEquipment(document, english);
                        if (row == null) continue;
                        if (!string.IsNullOrWhiteSpace(filter) && row.Product.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (++matched > MaxFilesPerSource) { result.Truncated = true; break; }
                        row.RecordId = Path.GetFileName(file);
                        result.Rows.Add(row);
                    }
                    catch (Exception error) { result.Errors.Add(Path.GetFileName(file) + "：" + error.Message); }
                }
            }
            catch (DirectoryNotFoundException) { /* 未建立目录与读取失败不同，交由页面明确展示。 */ }
            catch (Exception error) { result.Errors.Add(directory + "：" + error.Message); }
        }

        /// <summary>任务记录按真实 fields 展示，非产品出料类别不混入本表。</summary>
        private static ProductRecordRow ReadTask(JObject document, string machineId, bool english)
        {
            if ((int?)document["version"] != 1 || (string)document["machineId"] != machineId)
                throw new InvalidDataException("任务记录版本或设备编号不符");
            if ((string)document["category"] != "产品出料") return null;
            var fields = document["fields"] as JObject;
            if (fields == null) throw new InvalidDataException("产品记录缺少字段");
            return new ProductRecordRow { Source = english ? "Task list" : "任务列表", Product = Field(fields, "产品编号", english), Fixture = Field(fields, "夹位", english),
                Station = Field(fields, "工位", english), Stage = Field(fields, "阶段", english), Result = Field(fields, "检测结果", english),
                Outlet = Field(fields, "出口", english), RecordedTime = Time(document["recordedAtUtc"], english) };
        }

        /// <summary>设备流程记录使用各自已保存字段；不会根据结果猜测出口。</summary>
        private static ProductRecordRow ReadEquipment(JObject document, bool english)
        {
            if (document["id"] == null || document["sessionId"] == null)
                throw new InvalidDataException("设备流程记录缺少产品或会话标识");
            return new ProductRecordRow { Source = english ? "C# flow" : "C# 流程", Product = Field(document, "id", english), Fixture = Field(document, "nest", english),
                Station = Field(document, "station", english), Stage = Field(document, "stage", english), Result = Field(document, "result", english),
                Outlet = Field(document, "outlet", english), RecordedTime = Time(document["completedUtc"], english) };
        }

        /// <summary>未记录或空字段显示未知，不用默认值伪造记录。</summary>
        private static string Field(JObject obj, string name, bool english)
        {
            var value = obj[name];
            if (value == null || value.Type == JTokenType.Null) return english ? "Unknown" : "未知";
            if (value.Type != JTokenType.String) throw new InvalidDataException("字段类型无效：" + name);
            string text = (string)value;
            return string.IsNullOrWhiteSpace(text) ? (english ? "Unknown" : "未知") : text;
        }

        /// <summary>只使用记录自身的 UTC 时间，不使用文件时间或文件名推断。</summary>
        private static string Time(JToken value, bool english)
        {
            string unknown = english ? "Unknown" : "未知";
            if (value == null || value.Type == JTokenType.Null) return unknown;
            DateTimeOffset time;
            if (value.Type == JTokenType.Date)
            {
                DateTime date = value.Value<DateTime>();
                if (date.Kind == DateTimeKind.Unspecified) return unknown;
                time = new DateTimeOffset(date);
            }
            else if (value.Type == JTokenType.String)
            {
                string text = (string)value;
                // 无时区的时间不能假定为 UTC 或本地时间。
                if (string.IsNullOrWhiteSpace(text) || !text.EndsWith("Z", StringComparison.OrdinalIgnoreCase)
                    && !(text.Length >= 6 && (text[text.Length - 6] == '+' || text[text.Length - 6] == '-') && text[text.Length - 3] == ':')) return unknown;
                if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out time)) return unknown;
            }
            else return unknown;
            return time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + (english ? " (local time)" : "（本地时间）");
        }
    }
}
