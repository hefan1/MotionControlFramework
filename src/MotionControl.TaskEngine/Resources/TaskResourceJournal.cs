using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MotionControl.TaskEngine.Resources
{
    /// <summary>保存单个任务会话曾取得的资源和退出条件；异常退出后由启动检查保守拦截。</summary>
    public sealed class TaskResourceJournal
    {
        /// <summary>设备专属记录目录。</summary>
        private readonly string _root;
        /// <summary>同一会话独立目录，防止安全收尾时删除其他会话记录。</summary>
        private readonly string _session;
        /// <summary>设备编号。</summary>
        private readonly string _machineId;
        /// <summary>序列化磁盘写入和安全清理，不持有资源队列锁。</summary>
        private readonly object _sync = new object();

        /// <summary>绑定设备配置目录，但不在构造时访问磁盘。</summary>
        public TaskResourceJournal(string machineDirectory, string machineId)
        {
            if (string.IsNullOrWhiteSpace(machineDirectory) || string.IsNullOrWhiteSpace(machineId))
                throw new ArgumentException("设备目录和编号不能为空。");
            _root = Path.Combine(machineDirectory, "Runtime", "ResourceGuards");
            _session = Path.Combine(_root, Guid.NewGuid().ToString("N"));
            _machineId = machineId;
        }

        /// <summary>先写入并刷新完整记录，返回后调用方才能授予资源。</summary>
        internal void Save(TaskResourceRequest request)
        {
            var record = new JObject {
                ["Version"] = 1, ["MachineId"] = _machineId,
                ["TaskId"] = request.TaskId, ["TaskName"] = request.TaskName,
                ["NodeId"] = request.NodeId, ["NodeName"] = request.NodeName,
                ["Resources"] = JArray.FromObject(request.Names),
                ["ExitInterlock"] = request.ExitInterlock == null ? JValue.CreateNull() : JToken.FromObject(request.ExitInterlock),
                ["ExitCondition"] = request.ExitCondition == null ? JValue.CreateNull() : JToken.FromObject(request.ExitCondition)
            };
            byte[] bytes = Encoding.UTF8.GetBytes(record.ToString(Formatting.None));
            string key;
            using (var hash = SHA256.Create()) key = BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "");
            lock (_sync)
            {
                Directory.CreateDirectory(_session);
                string path = Path.Combine(_session, key + ".guard");
                try
                {
                    using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                    {
                        stream.Write(bytes, 0, bytes.Length);
                        stream.Flush(true);
                    }
                }
                catch (IOException)
                {
                    // 只有已有文件与本次完整内容一致才能复用，半写文件一律报错。
                    if (!File.Exists(path) || !File.ReadAllBytes(path).SequenceEqual(bytes)) throw;
                }
            }
        }

        /// <summary>设备确认全部任务及动作退出后才清理当前会话记录；失败时保留控制权。</summary>
        internal void ClearSafeSession()
        {
            lock (_sync)
            {
                try { File.GetAttributes(_session); }
                catch (FileNotFoundException) { return; }
                catch (DirectoryNotFoundException) { return; }
                foreach (string path in Directory.GetFiles(_session)) File.Delete(path);
                Directory.Delete(_session, false);
            }
        }

        /// <summary>启动时仅将明确不存在或空目录视为无遗留记录；任何异常均由调用方阻止启动。</summary>
        public static string Inspect(string machineDirectory, string machineId)
        {
            string root = Path.Combine(machineDirectory, "Runtime", "ResourceGuards");
            try { File.GetAttributes(root); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            var entries = Directory.GetFileSystemEntries(root);
            foreach (string session in entries)
            {
                if ((File.GetAttributes(session) & FileAttributes.Directory) == 0)
                    throw new InvalidDataException("资源记录目录包含未知文件。");
                string[] files = Directory.GetFileSystemEntries(session);
                if (files.Length == 0) throw new InvalidDataException("资源记录会话为空，无法确认是否完整写入。");
                foreach (string path in files)
                {
                    if (!path.EndsWith(".guard", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("资源记录包含未知文件。");
                    var record = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
                    var names = record["Resources"] as JArray;
                    if ((int?)record["Version"] != 1 || (string)record["MachineId"] != machineId
                        || names == null || names.Count == 0 || names.Any(n => n.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)n))
                        || record.Property("ExitInterlock") == null || record.Property("ExitCondition") == null)
                        throw new InvalidDataException("资源记录内容无效或属于其他设备。");
                    return "上次任务会话的资源状态尚未确认：[" + string.Join("、", names.Select(n => (string)n))
                        + "]；关联步骤：" + ((string)record["NodeName"] ?? (string)record["NodeId"] ?? "未知")
                        + "。请联系维护人员核对现场和资源记录。";
                }
            }
            return null;
        }
    }
}
