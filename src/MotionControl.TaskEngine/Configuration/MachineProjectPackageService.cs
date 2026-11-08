using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.TaskEngine.Device;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MotionControl.TaskEngine.Configuration
{
    /// <summary>导出和预检单机工程包；不会连接控制器或读取运行、恢复记录。</summary>
    public sealed partial class MachineProjectPackageService
    {
        /// <summary>工程包格式版本；与各配置文件自身的 schemaVersion 分开。</summary>
        private const int PackageVersion = 1;
        /// <summary>输入包及所有配置的总大小限制，防止异常文件耗尽内存。</summary>
        private const int MaximumBytes = 64 * 1024 * 1024;
        /// <summary>单机配置存储。</summary>
        private readonly MachineConfigurationStore _store;
        /// <summary>目标单机标识。</summary>
        private readonly string _machineId;
        /// <summary>Host 实际已登记的设备定义。</summary>
        private readonly HashSet<string> _definitions;
        /// <summary>Host 实际已安装的控制卡后端。</summary>
        private readonly HashSet<string> _backends;
        /// <summary>框架及设备明确登记的工程参数文件，不包含运行状态或凭据。</summary>
        private readonly HashSet<string> _parameterPaths;
        /// <summary>Host 对实际后端、卡型、轴数和卡数的安装能力检查。</summary>
        private readonly Func<MachineProjectCardSelection, string> _validateCard;
        /// <summary>当前 Host 正式任务设备的执行能力；工程文件不能自行声明获得这些能力。</summary>
        private readonly TaskDeviceCapabilities _taskCapabilities;
        /// <summary>本机代码实际声明的任务能力；为空表示本服务没有完成后端执行能力核对。</summary>
        public TaskDeviceCapabilities TaskCapabilities { get { return _taskCapabilities; } }

        /// <summary>绑定目标设备及当前 Host 明确支持的设备定义和控制器后端。</summary>
        public MachineProjectPackageService(MachineConfigurationStore store, string machineId,
            IEnumerable<string> supportedEquipmentDefinitions, IEnumerable<string> supportedBackends,
            IEnumerable<string> additionalConfigurationPaths = null, Func<MachineProjectCardSelection, string> validateCard = null,
            TaskDeviceCapabilities taskCapabilities = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _machineId = machineId ?? throw new ArgumentNullException(nameof(machineId));
            _store.GetMachineDirectory(machineId);
            _definitions = new HashSet<string>(supportedEquipmentDefinitions ?? throw new ArgumentNullException(nameof(supportedEquipmentDefinitions)), StringComparer.OrdinalIgnoreCase);
            _backends = new HashSet<string>(supportedBackends ?? throw new ArgumentNullException(nameof(supportedBackends)), StringComparer.OrdinalIgnoreCase);
            _validateCard = validateCard;
            _taskCapabilities = taskCapabilities;
            _parameterPaths = new HashSet<string>(new[] { "Parameters/Equipment.json", "Parameters/ControllerBackend.json", "Parameters/DemoStation.json",
                "Parameters/DemoStations.json", "Parameters/StationPreparation.json", "Parameters/TaskMachine.json", "Parameters/DeviceVariables.json",
                "Parameters/ManualAxisPositions.json", "Parameters/ManualAxisIoAssociations.json", "Parameters/AlarmCatalog.json", "Parameters/IoFeedbackAssociations.json" },
                StringComparer.OrdinalIgnoreCase);
            foreach (string path in additionalConfigurationPaths ?? Array.Empty<string>())
            {
                if (!IsAllowedPath(path) || !path.StartsWith("Parameters/", StringComparison.Ordinal))
                    throw new ArgumentException("设备扩展配置只能登记 Parameters 下的单个 JSON：" + path, nameof(additionalConfigurationPaths));
                _parameterPaths.Add(path);
            }
        }

        /// <summary>将当前运行配置写为单个工程包；不含 Runtime、草稿、备份或历史记录。</summary>
        public void Export(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("工程包路径不能为空。", nameof(filePath));
            string target = Path.GetFullPath(filePath);
            string root = _store.GetMachineDirectory(_machineId).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("工程包不能导出到当前设备配置目录内。 ");
            using (AcquireProjectLock())
            {
                RequireNoPending();
                var unregistered = ListUnregisteredParameterFiles();
                if (unregistered.Count != 0)
                    throw new InvalidOperationException("发现未登记的设备参数，不能导出完整工程。请由设备开发者先确认并登记：\n" + string.Join("\n", unregistered));
                Dictionary<string, string> contents = ReadCurrentFiles(out Dictionary<string, byte[]> sourceBytes);
                ReadCurrentFiles(out Dictionary<string, byte[]> latestBytes);
                if (!string.Equals(ComputeSignature(sourceBytes), ComputeSignature(latestBytes), StringComparison.Ordinal))
                    throw new InvalidOperationException("导出期间设备配置发生变化，请重新导出。 ");
                var files = new JObject();
                var hashes = new JObject();
                foreach (var item in contents.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
                { files.Add(item.Key, item.Value); hashes.Add(item.Key, HashText(item.Value)); }
                var document = new JObject { ["format"] = "MotionControlDeviceProject", ["version"] = PackageVersion,
                    ["sourceMachineId"] = _machineId, ["createdUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), ["files"] = files, ["hashes"] = hashes };
                string serialized = document.ToString(Formatting.Indented);
                if (Encoding.UTF8.GetByteCount(serialized) > MaximumBytes) throw new InvalidDataException("工程包超过大小限制，无法导出。 ");
                WriteAtomicExternal(filePath, serialized);
            }
        }

        /// <summary>严格读取并离线验证工程包，同时比较当前运行配置；不覆盖设备目录。</summary>
        public MachineProjectPreview Preview(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("工程包路径不能为空。", nameof(filePath));
            using (AcquireProjectLock())
            {
                RequireNoPending();
                if (!File.Exists(filePath)) throw new FileNotFoundException("找不到设备工程包。", filePath);
                if (new FileInfo(filePath).Length > MaximumBytes) throw new InvalidDataException("工程包超过大小限制。");
                JObject package = ReadStrictObject(File.ReadAllText(filePath, new UTF8Encoding(false, true)), "工程包");
                if ((string)package["format"] != "MotionControlDeviceProject" || package["version"]?.Type != JTokenType.Integer
                    || (int)package["version"] != PackageVersion || package["sourceMachineId"]?.Type != JTokenType.String)
                    throw new InvalidDataException("工程包格式或版本不受支持。");
                string sourceId = (string)package["sourceMachineId"];
                _store.GetMachineDirectory(sourceId);
                JObject files = package["files"] as JObject ?? throw new InvalidDataException("工程包缺少配置文件。");
                JObject hashes = package["hashes"] as JObject ?? throw new InvalidDataException("工程包缺少完整性校验。 ");
                var contents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (JProperty property in files.Properties())
                {
                    string path = RequireAllowedPath(property.Name);
                    if (property.Value.Type != JTokenType.String || contents.ContainsKey(path))
                        throw new InvalidDataException("工程配置路径重复或内容无效：" + path);
                    contents.Add(path, (string)property.Value);
                    if (hashes[path]?.Type != JTokenType.String || !string.Equals((string)hashes[path], HashText((string)property.Value), StringComparison.Ordinal))
                        throw new InvalidDataException("工程配置完整性检查失败：" + path);
                }
                if (hashes.Count != contents.Count || hashes.Properties().Any(item => !contents.ContainsKey(item.Name)))
                    throw new InvalidDataException("工程配置和校验清单不一致。 ");
                if (contents.Count == 0 || contents.Count > 512) throw new InvalidDataException("工程包文件数量无效。");
                RebindTaskMachine(contents, sourceId);
                ValidateContents(contents, _machineId, out string equipment, out string owner, out string backend, out string name);
                Dictionary<string, string> current = ReadCurrentFiles(out Dictionary<string, byte[]> currentBytes, true);
                var merged = new Dictionary<string, string>(current, StringComparer.OrdinalIgnoreCase);
                foreach (var item in contents) merged[item.Key] = item.Value;
                ValidateContents(merged, _machineId, out string mergedEquipment, out string mergedOwner, out string mergedBackend, out string mergedName, true);
                var changes = new List<string>();
                foreach (string path in current.Keys.Union(contents.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
                {
                    bool oldExists = current.TryGetValue(path, out string oldText), newExists = contents.TryGetValue(path, out string newText);
                    if (!oldExists) changes.Add("新增：" + path);
                    else if (!newExists) changes.Add("保留（工程包未包含）：" + path);
                    else if (!string.Equals(oldText, newText, StringComparison.Ordinal)) changes.Add("修改：" + path);
                }
                var warnings = new List<string> { "导入后必须核对现场轴、IO、行程及安全参数，并重新连接和准备设备。", "运行记录、恢复证据和任务草稿不会随工程包覆盖。" };
                ValidateTeachPointMigration(current, merged, contents, warnings);
                if (current.Keys.Any(path => !contents.ContainsKey(path))) warnings.Add("工程包未包含的现有配置文件将保留；请逐项核对设备代码是否读取这些文件。 ");
                foreach (string path in ListUnregisteredParameterFiles())
                { changes.Add("保留（未登记的配置文件）：" + path); warnings.Add("未登记的参数文件不会导入或覆盖，请核对设备是否依赖：" + path); }
                if (!string.Equals(sourceId, _machineId, StringComparison.Ordinal)) warnings.Add("任务清单的设备标识将绑定当前设备：" + _machineId);
                if (string.Equals(owner, "StationFlows", StringComparison.OrdinalIgnoreCase)) warnings.Add("C# 工位代码必须已由当前上位机提供；工程包不包含可执行代码。");
                return new MachineProjectPreview { Owner = this, Contents = contents, CurrentContents = current, CurrentBytes = currentBytes, TargetSignature = ComputeSignature(currentBytes),
                    SourceMachineId = sourceId, TargetMachineId = _machineId, EquipmentName = name, ExecutionOwner = owner, ControllerBackend = backend,
                    Files = contents.Keys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList().AsReadOnly(),
                    MergedFiles = merged.Keys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList().AsReadOnly(),
                    RetainedFiles = current.Keys.Where(value => !contents.ContainsKey(value)).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList().AsReadOnly(),
                    Changes = changes.AsReadOnly(), Warnings = warnings.AsReadOnly() };
            }
        }

        /// <summary>枚举设备配置目录的可迁移 JSON 文件，不遍历历史、恢复和草稿目录。</summary>
        private Dictionary<string, string> ReadCurrentFiles(out Dictionary<string, byte[]> originalBytes, bool includeRetainedHardware = false)
        {
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            originalBytes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            string root = _store.GetMachineDirectory(_machineId);
            if (!ExistsStrict(root, true)) return files;
            var hardwarePaths = new HashSet<string>(new[] { "HardwareConfig/MotionCards.json", "HardwareConfig/AxisParameters.json",
                "HardwareConfig/IoMapping.json", "HardwareConfig/SafetyInterlock.json" }, StringComparer.OrdinalIgnoreCase);
            string cardsPath = Path.Combine(root, "HardwareConfig", "MotionCards.json");
            if (ExistsStrict(cardsPath, false))
            {
                RejectReparsePoint(Path.GetDirectoryName(cardsPath)); RejectReparsePoint(cardsPath);
                JObject cardSettings = ReadStrictObject(File.ReadAllText(cardsPath, new UTF8Encoding(false, true)), "运动卡配置");
                JArray cards = cardSettings["motionCards"] as JArray ?? throw new InvalidDataException("运动卡配置缺少运动卡列表。 ");
                foreach (JToken card in cards)
                {
                    string connection = ((string)card["connectionConfigurationFile"])?.Replace('\\', '/');
                    if (!IsAllowedPath(connection) || !connection.StartsWith("HardwareConfig/", StringComparison.Ordinal))
                        throw new InvalidDataException("运动卡连接配置路径无效：" + connection);
                    hardwarePaths.Add(connection);
                }
            }
            long size = 0;
            foreach (string directory in new[] { "HardwareConfig", "Parameters", "Tasks" })
            {
                string path = Path.Combine(root, directory);
                if (!ExistsStrict(path, true)) continue;
                RejectReparsePoint(path);
                foreach (string file in Directory.GetFiles(path, "*.json", SearchOption.TopDirectoryOnly))
                {
                    string relative = directory + "/" + Path.GetFileName(file);
                    if (!IsAllowedPath(relative)) continue;
                    if (directory == "HardwareConfig" && !includeRetainedHardware && !hardwarePaths.Contains(relative)) continue;
                    if (directory == "Parameters" && !_parameterPaths.Contains(relative)) continue;
                    RejectReparsePoint(file);
                    long reportedSize = new FileInfo(file).Length;
                    if (reportedSize > MaximumBytes - size || files.Count >= 512) throw new InvalidDataException("设备配置超过工程包大小或数量限制。");
                    byte[] bytes = File.ReadAllBytes(file);
                    size += bytes.Length;
                    if (size > MaximumBytes) throw new InvalidDataException("读取期间设备配置超过工程包大小限制。");
                    originalBytes.Add(relative, bytes);
                    using (var stream = new MemoryStream(bytes))
                    using (var reader = new StreamReader(stream, new UTF8Encoding(false, true), true)) files.Add(relative, reader.ReadToEnd());
                }
            }
            return files;
        }

        /// <summary>只接收三个配置目录中的普通 JSON 文件，排除备份命名和任何路径跳转。</summary>
        private static bool IsAllowedPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.IndexOf('\\') >= 0 || path.IndexOf(':') >= 0) return false;
            string[] parts = path.Split('/');
            if (parts.Length != 2 || !(parts[0] == "HardwareConfig" || parts[0] == "Parameters" || parts[0] == "Tasks")) return false;
            string name = parts[1];
            string firstPart = name.Split('.')[0];
            bool reserved = new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
                "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(firstPart, StringComparer.OrdinalIgnoreCase);
            return !reserved && name.Length > 5 && name == name.Trim() && name == name.TrimEnd(' ', '.') && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                && name != "." && name != ".." && name == Path.GetFileName(name)
                && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
                && name.IndexOf(".before-", StringComparison.OrdinalIgnoreCase) < 0
                && name.IndexOf(".bak.", StringComparison.OrdinalIgnoreCase) < 0;
        }

        /// <summary>拒绝不属于设备配置范围的文件。</summary>
        private static string RequireAllowedPath(string path)
        { if (!IsAllowedPath(path)) throw new InvalidDataException("工程包包含不允许的路径：" + path); return path; }

        /// <summary>列出未登记的设备参数文件名称，保留原件并提醒核查是否需要迁移。</summary>
        private IReadOnlyList<string> ListUnregisteredParameterFiles()
        {
            string root = Path.Combine(_store.GetMachineDirectory(_machineId), "Parameters");
            if (!ExistsStrict(root, true)) return Array.Empty<string>();
            RejectReparsePoint(root);
            return Directory.GetFiles(root, "*.json", SearchOption.TopDirectoryOnly)
                .Select(path => "Parameters/" + Path.GetFileName(path))
                .Where(path => IsAllowedPath(path) && !_parameterPaths.Contains(path))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList().AsReadOnly();
        }

        /// <summary>拒绝配置目录或文件的链接，防止预览及备份越出设备目录。</summary>
        private static void RejectReparsePoint(string path)
        { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("工程配置不能使用目录联接或符号链接：" + path); }

        /// <summary>只有明确不存在才返回 false；访问失败、类型错误或链接均阻止工程操作。</summary>
        private static bool ExistsStrict(string path, bool directory)
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0 || ((attributes & FileAttributes.Directory) != 0) != directory)
                    throw new InvalidDataException("工程路径类型无效或为链接：" + path);
                return true;
            }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }

        /// <summary>严格读取单个 JSON 对象，拒绝重复字段及尾随内容。</summary>
        private static JObject ReadStrictObject(string json, string description)
        {
            using (var text = new StringReader(json))
            using (var reader = new JsonTextReader(text) { MaxDepth = 96, DateParseHandling = DateParseHandling.None })
            {
                JObject document = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if (reader.Read()) throw new InvalidDataException(description + "包含多余内容。");
                return document;
            }
        }

        /// <summary>迁移任务清单的设备目录标识，其他参数保持原样。</summary>
        private void RebindTaskMachine(Dictionary<string, string> contents, string sourceId)
        {
            if (!contents.TryGetValue("Parameters/TaskMachine.json", out string json)) return;
            JObject machine = ReadStrictObject(json, "任务清单");
            if (!string.Equals((string)machine["machineId"], sourceId, StringComparison.Ordinal))
                throw new InvalidDataException("任务清单设备标识与工程包来源不一致。");
            if (!string.Equals(sourceId, _machineId, StringComparison.Ordinal))
            { machine["machineId"] = _machineId; contents["Parameters/TaskMachine.json"] = machine.ToString(Formatting.Indented); }
        }

        /// <summary>按文件名与原始内容生成签名，预览之后发生变化时拒绝应用。</summary>
        private static string ComputeSignature(Dictionary<string, byte[]> files)
        {
            using (var hash = SHA256.Create())
            {
                string text = string.Join("\n", files.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(item => item.Key.ToLowerInvariant() + ":" + Convert.ToBase64String(hash.ComputeHash(item.Value))));
                return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(text)));
            }
        }

        /// <summary>计算配置文本的固定 SHA256，用于识别工程包损坏。</summary>
        private static string HashText(string text)
        { using (var hash = SHA256.Create()) return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(text))); }

        /// <summary>写入用户指定导出文件并原子替换；文件夹必须已经存在。</summary>
        private static void WriteAtomicExternal(string filePath, string text)
        {
            string full = Path.GetFullPath(filePath), directory = Path.GetDirectoryName(full);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) throw new DirectoryNotFoundException("工程包目录不存在。 ");
            string temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { byte[] bytes = new UTF8Encoding(false).GetBytes(text); stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                if (File.Exists(full)) File.Replace(temporary, full, null); else File.Move(temporary, full);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
