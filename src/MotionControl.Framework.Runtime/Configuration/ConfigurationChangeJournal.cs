using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MotionControl.Framework.Runtime.Configuration
{
    /// <summary>每条配置变更为独立不可覆盖文件。只接受实际阶段回执，不执行或重试配置动作。</summary>
    public sealed class ConfigurationChangeJournal
    {
        /// <summary>目录最多容纳的记录和残留临时文件数，满时独立报告审计失败。</summary>
        public const int MaximumRecords = 1024;
        /// <summary>单条 UTF-8 记录最大字节数。</summary>
        private const int MaximumBytes = 16384;
        /// <summary>进程内串行分配有限目录空间；进程间另用独占文件。</summary>
        private static readonly object WriterGate = new object();
        /// <summary>确定编码和严格的无效字节处理。</summary>
        private static readonly UTF8Encoding Encoding = new UTF8Encoding(false, true);
        /// <summary>设备工程标识，不接受其他工程记录。</summary>
        private readonly string machineId;
        /// <summary>当前记录目录绑定的实际设备工程标识。</summary>
        public string MachineId => machineId;
        /// <summary>工程包排除的 Runtime 子目录。</summary>
        private readonly string directory;

        /// <summary>只解析路径，不创建目录、不挂接任何保存或控制动作。</summary>
        public ConfigurationChangeJournal(MachineConfigurationStore store, string machineId)
        {
            if (store == null) throw new ArgumentNullException(nameof(store));
            directory = Path.Combine(store.GetMachineDirectory(machineId), "Runtime", "ConfigurationChanges");
            this.machineId = machineId;
        }

        /// <summary>单次写入，成功返回 null，失败返回独立审计错误；调用者保留原业务回执。</summary>
        public string TryAppend(ConfigurationChangeRecord record)
        {
            try
            {
                Validate(record);
                byte[] bytes = Encoding.GetBytes(JsonConvert.SerializeObject(record));
                if (bytes.Length > MaximumBytes) throw new InvalidDataException("记录超过 16 KiB。");
                // 后续定位键取自本次序列化的快照，避免可编辑输入在写入期间发生变化。
                record = JsonConvert.DeserializeObject<ConfigurationChangeRecord>(Encoding.GetString(bytes));
                Validate(record);
                if (!Monitor.TryEnter(WriterGate, TimeSpan.FromSeconds(2)))
                    throw new IOException("其他记录写入中，本次未追加。");
                try
                {
                    Directory.CreateDirectory(directory);
                    using (new FileStream(Path.Combine(directory, "writer.lock"), FileMode.OpenOrCreate,
                        FileAccess.ReadWrite, FileShare.None))
                    {
                        var files = ListFiles();
                        if (files.Count >= MaximumRecords) throw new IOException("记录目录已满，未覆盖旧记录。");
                        string idSuffix = "-" + record.RecordId.ToString("N") + ".json";
                        if (files.Any(file => file.EndsWith(idSuffix, StringComparison.OrdinalIgnoreCase)))
                            throw new IOException("记录标识已存在，未覆盖。");
                        string final = Path.Combine(directory, FileName(record));
                        string pending = final + ".pending";
                        using (var output = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            output.Write(bytes, 0, bytes.Length);
                            output.Flush(true);
                        }
                        // 同目录原子改名发布完整文件；失败时保留临时文件作为未提交证据。
                        File.Move(pending, final);
                    }
                }
                finally { Monitor.Exit(WriterGate); }
                return null;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException ||
                error is InvalidDataException || error is JsonException || error is ArgumentException ||
                error is System.Security.SecurityException || error is NotSupportedException)
            {
                return "配置变更记录写入失败 [" + machineId + "]：" + error.Message;
            }
        }

        /// <summary>最多查看最近 100 条候选记录；损坏和未提交文件另行返回，目录访问失败抛出。</summary>
        public IReadOnlyList<ConfigurationChangeRecord> ReadRecent(int maximum, out IReadOnlyList<string> diagnostics)
        {
            if (maximum < 1 || maximum > 100) throw new ArgumentOutOfRangeException(nameof(maximum));
            var errors = new List<string>();
            var rows = new List<ConfigurationChangeRecord>();
            diagnostics = errors.AsReadOnly();
            List<string> files;
            try { files = ListFiles(); }
            catch (DirectoryNotFoundException)
            {
                // 区分尚未创建的记录目录和被普通文件阻塞的路径，后者不能显示为无历史。
                for (string path = directory; path != null; path = Path.GetDirectoryName(path))
                {
                    FileAttributes attributes;
                    try { attributes = File.GetAttributes(path); }
                    catch (FileNotFoundException) { continue; }
                    catch (DirectoryNotFoundException) { continue; }
                    if ((attributes & FileAttributes.Directory) == 0) throw new IOException("记录目录路径被文件占用：" + path);
                    return rows.AsReadOnly();
                }
                throw;
            }
            int pending = files.Count(file => file.EndsWith(".pending", StringComparison.OrdinalIgnoreCase));
            if (pending > 0) errors.Add("有 " + pending + " 个尚未确认提交的临时文件。");
            if (files.Any(file => !file.EndsWith(".pending", StringComparison.OrdinalIgnoreCase) &&
                !file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))) errors.Add("记录目录中存在未识别的文件。");
            foreach (string file in files.Where(path => path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(path => path, StringComparer.Ordinal).Take(maximum))
            {
                try
                {
                    using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        if (input.Length < 1 || input.Length > MaximumBytes) throw new InvalidDataException("记录大小无效。");
                        byte[] bytes = new byte[(int)input.Length];
                        int offset = 0;
                        while (offset < bytes.Length)
                        {
                            int count = input.Read(bytes, offset, bytes.Length - offset);
                            if (count == 0) throw new EndOfStreamException("记录被截断。");
                            offset += count;
                        }
                        var item = ReadRecord(bytes);
                        Validate(item);
                        if (!string.Equals(Path.GetFileName(file), FileName(item), StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("记录定位键与文件名不符。");
                        rows.Add(item);
                    }
                }
                catch (Exception error) when (error is IOException || error is InvalidDataException ||
                    error is JsonException || error is UnauthorizedAccessException || error is ArgumentException ||
                    error is System.Security.SecurityException)
                {
                    errors.Add(Path.GetFileName(file) + "：" + error.Message);
                }
            }
            return rows.AsReadOnly();
        }

        /// <summary>拒绝重复结果字段、未知字段或尾随内容，禁止损坏的事实被悄悄覆盖。</summary>
        private static ConfigurationChangeRecord ReadRecord(byte[] bytes)
        {
            using (var reader = new JsonTextReader(new StringReader(Encoding.GetString(bytes))))
            {
                reader.DateParseHandling = DateParseHandling.None;
                reader.MaxDepth = 8;
                var json = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if (reader.Read()) throw new InvalidDataException("记录含尾随内容。");
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in json.Properties())
                    if (!names.Add(property.Name)) throw new InvalidDataException("记录字段重复：" + property.Name);
                return json.ToObject<ConfigurationChangeRecord>(JsonSerializer.Create(new JsonSerializerSettings {
                    MissingMemberHandling = MissingMemberHandling.Error, MetadataPropertyHandling = MetadataPropertyHandling.Ignore }));
            }
        }

        /// <summary>限制目录枚举数量，避免损坏或失控的目录造成无界查询。</summary>
        private List<string> ListFiles()
        {
            var files = Directory.EnumerateFileSystemEntries(directory)
                .Where(file => !string.Equals(Path.GetFileName(file), "writer.lock", StringComparison.OrdinalIgnoreCase))
                .Take(MaximumRecords + 1).ToList();
            if (files.Count > MaximumRecords) throw new InvalidDataException("记录目录超过 1024 项，无法完整查询。");
            return files;
        }

        /// <summary>按源动作 UTC 时间和记录标识定位；排序不依赖文件时间。</summary>
        private static string FileName(ConfigurationChangeRecord record)
        {
            return record.TimeUtc.ToString("yyyyMMddHHmmssfffffff", CultureInfo.InvariantCulture)
                + "-" + record.RecordId.ToString("N") + ".json";
        }

        /// <summary>确认定位、阶段和结果有效，禁止用默认枚举或无来源时间补造事实。</summary>
        private void Validate(ConfigurationChangeRecord record)
        {
            if (record == null || record.FormatVersion != 1 || record.RecordId == Guid.Empty ||
                record.TimeUtc == default(DateTimeOffset) || record.TimeUtc.Offset != TimeSpan.Zero ||
                !string.Equals(record.MachineId, machineId, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(record.Source) || string.IsNullOrWhiteSpace(record.ObjectId) ||
                string.IsNullOrWhiteSpace(record.ObjectType) ||
                !Enum.IsDefined(typeof(ConfigurationChangeStage), record.Stage) ||
                !Enum.IsDefined(typeof(ConfigurationChangeOutcome), record.Outcome))
                throw new InvalidDataException("记录的格式、标识、设备、UTC 时间、对象、来源、阶段或结果不完整。");
            if (record.Outcome != ConfigurationChangeOutcome.Succeeded && string.IsNullOrWhiteSpace(record.Detail))
                throw new InvalidDataException("失败或拒绝记录缺少来源原因。");
            foreach (string field in new[] { record.Source, record.ObjectType, record.ObjectId, record.OperationId,
                record.BeforeVersion, record.AfterVersion, record.Actor, record.Field, record.BeforeValue, record.AfterValue,
                record.Unit, record.Detail })
                if (field != null && field.Length > MaximumBytes) throw new InvalidDataException("记录字段过长。");
        }
    }
}
