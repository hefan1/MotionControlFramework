using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
namespace MotionControl.TaskEngine.Device
{
    /// <summary>完整刷新后逐条发布文件，不覆盖旧记录。</summary>
    public sealed class TaskFileRecordSink : ITaskRecordSink
    {
        private readonly string _root;
        private readonly string _machineId;
        /// <summary>记录保存在设备运行记录目录，不修改配置或草稿。</summary>
        public TaskFileRecordSink(string machineDirectory, string machineId)
        {
            if (string.IsNullOrWhiteSpace(machineDirectory) || string.IsNullOrWhiteSpace(machineId)) throw new ArgumentException("设备目录和编号不能为空。");
            _root = Path.Combine(machineDirectory, "Runtime", "TaskRecords"); _machineId = machineId;
        }
        /// <summary>编号重复仅允许完全一致；写入失败抛出异常。</summary>
        public void CommitRecord(string executionId, string taskId, string nodeId, string category, DateTimeOffset recordedAtUtc, IReadOnlyDictionary<string, string> fields)
        {
            Guid id;
            if (!Guid.TryParseExact(executionId, "N", out id) || fields == null || fields.Count == 0 || recordedAtUtc.Offset != TimeSpan.Zero || recordedAtUtc == default(DateTimeOffset)) throw new ArgumentException("记录编号、UTC 时间或内容无效。");
            var obj = new JObject { ["version"] = 1, ["machineId"] = _machineId, ["executionId"] = executionId, ["taskId"] = taskId, ["nodeId"] = nodeId, ["category"] = category, ["recordedAtUtc"] = recordedAtUtc.ToString("O", CultureInfo.InvariantCulture) };
            var values = new JObject();
            foreach (var item in fields.OrderBy(x => x.Key, StringComparer.Ordinal)) values.Add(item.Key, item.Value);
            obj["fields"] = values;
            byte[] bytes = Encoding.UTF8.GetBytes(obj.ToString(Formatting.None));
            Directory.CreateDirectory(_root);
            string final = Path.Combine(_root, executionId + ".json");
            if (File.Exists(final)) { Verify(final, bytes); return; }
            string temp = Path.Combine(_root, executionId + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                try { File.Move(temp, final); }
                catch (IOException) { if (!File.Exists(final)) throw; Verify(final, bytes); }
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        private static void Verify(string path, byte[] bytes)
        {
            if (!File.ReadAllBytes(path).SequenceEqual(bytes)) throw new InvalidDataException("记录编号已存在但内容不一致：" + path);
        }
    }
}
