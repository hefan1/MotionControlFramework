using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
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
        /// <summary>本实例成功写入或核对一致的完整记录路径，清理时不接受外部放入的同后缀文件。</summary>
        private readonly HashSet<string> _savedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
            byte[] bytes = CreateRecordBytes(request);
            string key = RecordKey(bytes);
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
                _savedPaths.Add(Path.GetFullPath(path));
            }
        }

        /// <summary>按原始写入格式生成一条资源记录，供退出交接逐项复核。</summary>
        private byte[] CreateRecordBytes(TaskResourceRequest request)
        {
            var record = new JObject {
                ["Version"] = 1, ["MachineId"] = _machineId,
                ["TaskId"] = request.TaskId, ["TaskName"] = request.TaskName,
                ["NodeId"] = request.NodeId, ["NodeName"] = request.NodeName,
                ["Resources"] = JArray.FromObject(request.Names),
                ["ExitInterlock"] = request.ExitInterlock == null ? JValue.CreateNull() : JToken.FromObject(request.ExitInterlock),
                ["ExitCondition"] = request.ExitCondition == null ? JValue.CreateNull() : JToken.FromObject(request.ExitCondition)
            };
            return Encoding.UTF8.GetBytes(record.ToString(Formatting.None));
        }

        /// <summary>按内容生成记录文件名，便于检测缺失或不完整的原始记录。</summary>
        private static string RecordKey(byte[] bytes)
        {
            using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "");
        }

        /// <summary>退出交接前逐项复读当前会话的完整记录；不删除或修改任何证据。</summary>
        internal void VerifyRetained(IEnumerable<TaskResourceRequest> requests)
        {
            lock (_sync)
            {
                foreach (TaskResourceRequest request in requests)
                {
                    byte[] expected = CreateRecordBytes(request);
                    string path = Path.Combine(_session, RecordKey(expected) + ".guard");
                    if (!File.Exists(path) || !File.ReadAllBytes(path).SequenceEqual(expected))
                        throw new InvalidDataException("任务待确认资源记录缺失或损坏，不能结束会话：" + string.Join("、", request.Names));
                }
            }
        }

        /// <summary>设备确认全部任务及动作退出后，先核对会话目录再清理已知记录；失败时由调用方保留控制权。</summary>
        internal void ClearSafeSession()
        {
            lock (_sync)
            {
                FileAttributes sessionAttributes;
                try { sessionAttributes = File.GetAttributes(_session); }
                catch (FileNotFoundException) { return; }
                catch (DirectoryNotFoundException) { return; }
                if ((sessionAttributes & FileAttributes.Directory) == 0
                    || (sessionAttributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("资源记录会话不是普通目录或已被重定向，保留当前记录，不能安全清理。");
                // 在删除任何记录前先核对整个目录，未知内容可能是恢复证据，不能顺手清理。
                string[] entries = Directory.GetFileSystemEntries(_session);
                foreach (string path in entries)
                {
                    FileAttributes attributes = File.GetAttributes(path);
                    if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0
                        || !_savedPaths.Contains(Path.GetFullPath(path)))
                        throw new IOException("资源记录会话包含未知文件、子目录或重定向，保留当前记录，不能安全清理：" + Path.GetFileName(path));
                }
                // 目录预检不锁定外部文件系统；后续真实磁盘错误继续上抛，不能伪装为清理成功。
                foreach (string path in entries) File.Delete(path);
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
            var pendingResources = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var pendingSteps = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
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
                    foreach (JToken name in names) pendingResources.Add((string)name);
                    pendingSteps.Add((string)record["NodeName"] ?? (string)record["NodeId"] ?? "未知");
                }
            }
            return pendingResources.Count == 0 ? null
                : "上次任务会话的资源状态尚未确认：[" + string.Join("、", pendingResources)
                    + "]；关联步骤：" + string.Join("、", pendingSteps) + "。请联系维护人员核对现场和资源记录。";
        }
    }
}
