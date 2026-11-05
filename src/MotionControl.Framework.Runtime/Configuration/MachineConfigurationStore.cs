using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace MotionControl.Framework.Runtime.Configuration
{
    /// <summary>
    /// 为每台物理设备建立独立的 JSON 配置目录，首次使用时从设备项目模板复制默认文件。
    /// Host 应把配置根目录设在软件安装目录之外。
    /// </summary>
    public sealed class MachineConfigurationStore
    {
        /// <summary>所有设备独立配置目录的根路径，构造时已转换为绝对路径。</summary>
        private readonly string _configurationRootDirectory;

        /// <summary>创建按 MachineId 隔离的 JSON 配置存储。</summary>
        public MachineConfigurationStore(string configurationRootDirectory)
        {
            if (string.IsNullOrWhiteSpace(configurationRootDirectory))
            {
                throw new ArgumentException("配置根目录不能为空。", nameof(configurationRootDirectory));
            }

            _configurationRootDirectory = Path.GetFullPath(configurationRootDirectory);
        }

        /// <summary>返回某台设备的配置目录，不会自动创建目录。</summary>
        public string GetMachineDirectory(string machineId)
        {
            ValidateMachineId(machineId);

            var machineDirectory = Path.GetFullPath(
                Path.Combine(_configurationRootDirectory, machineId));
            EnsurePathIsBelowDirectory(_configurationRootDirectory, machineDirectory, nameof(machineId));
            return machineDirectory;
        }

        /// <summary>
        /// 首次部署时复制模板目录下的 JSON 文件。已存在的现场文件不会被覆盖。
        /// 返回本次新复制的相对文件路径。
        /// </summary>
        public IReadOnlyList<string> CopyMissingJsonFilesFromTemplate(
            string machineId,
            string templateDirectory)
        {
            if (string.IsNullOrWhiteSpace(templateDirectory))
            {
                throw new ArgumentException("设备默认模板目录不能为空。", nameof(templateDirectory));
            }

            var templateRoot = Path.GetFullPath(templateDirectory);
            if (!Directory.Exists(templateRoot))
            {
                throw new DirectoryNotFoundException("设备默认模板目录不存在：" + templateRoot);
            }

            var templateFiles = Directory.GetFiles(templateRoot, "*.json", SearchOption.AllDirectories);
            if (templateFiles.Length == 0)
            {
                throw new InvalidOperationException("设备默认模板目录中没有 JSON 配置文件：" + templateRoot);
            }

            var machineDirectory = GetMachineDirectory(machineId);
            Directory.CreateDirectory(machineDirectory);

            var templatePrefix = EnsureTrailingSeparator(templateRoot);
            var copiedFiles = new List<string>();

            foreach (var templateFile in templateFiles.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var fullTemplateFile = Path.GetFullPath(templateFile);
                EnsurePathIsBelowDirectory(templateRoot, fullTemplateFile, nameof(templateDirectory));
                var relativePath = fullTemplateFile.Substring(templatePrefix.Length);
                var machineFile = ResolveConfigurationFilePath(machineDirectory, relativePath);

                if (File.Exists(machineFile))
                {
                    continue;
                }

                var destinationDirectory = Path.GetDirectoryName(machineFile);
                Directory.CreateDirectory(destinationDirectory);
                File.Copy(fullTemplateFile, machineFile, false);
                copiedFiles.Add(relativePath.Replace(Path.DirectorySeparatorChar, '/'));
            }

            return copiedFiles.AsReadOnly();
        }

        /// <summary>读取并反序列化一台设备的 JSON 配置。</summary>
        public T LoadJson<T>(string machineId, string relativeFilePath)
        {
            var filePath = ResolveConfigurationFilePath(GetMachineDirectory(machineId), relativeFilePath);
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException(
                    "设备配置文件不存在：MachineId='" + machineId + "'，文件='" + relativeFilePath + "'。",
                    filePath);
            }

            string json;
            try
            {
                json = File.ReadAllText(filePath, Encoding.UTF8);
            }
            catch (Exception error)
            {
                throw new InvalidOperationException(
                    "读取设备配置失败：MachineId='" + machineId + "'，文件='" + relativeFilePath + "'。",
                    error);
            }

            if (string.IsNullOrWhiteSpace(json))
            {
                throw new InvalidOperationException(
                    "设备配置文件内容为空：MachineId='" + machineId + "'，文件='" + relativeFilePath + "'。");
            }

            try
            {
                var configuration = JsonConvert.DeserializeObject<T>(json);
                if (ReferenceEquals(configuration, null))
                {
                    throw new InvalidOperationException("配置文件没有包含可读取的 JSON 对象。");
                }

                return configuration;
            }
            catch (Exception error)
            {
                throw new InvalidOperationException(
                    "解析设备配置失败：MachineId='" + machineId + "'，文件='" + relativeFilePath + "'。",
                    error);
            }
        }

        /// <summary>读取 JSON 根节点的 schemaVersion，供设备项目执行明确的版本升级。</summary>
        public int ReadSchemaVersion(string machineId, string relativeFilePath)
        {
            var document = LoadJson<JObject>(machineId, relativeFilePath);
            var versionToken = document["schemaVersion"];
            if (versionToken == null || versionToken.Type != JTokenType.Integer)
            {
                throw new InvalidOperationException(
                    "配置文件缺少整数 schemaVersion：MachineId='" + machineId
                    + "'，文件='" + relativeFilePath + "'。");
            }

            var schemaVersion = versionToken.Value<int>();
            if (schemaVersion < 1)
            {
                throw new InvalidOperationException(
                    "配置文件 schemaVersion 必须大于 0：MachineId='" + machineId
                    + "'，文件='" + relativeFilePath + "'。");
            }

            return schemaVersion;
        }

        /// <summary>
        /// 以 UTF-8 缩进 JSON 保存配置。覆盖已有文件时先原子替换，并保留带时间戳的旧文件备份。
        /// 返回备份文件路径；首次写入时返回 null。
        /// </summary>
        public string SaveJson<T>(string machineId, string relativeFilePath, T configuration)
        {
            if (ReferenceEquals(configuration, null))
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            var machineDirectory = GetMachineDirectory(machineId);
            var filePath = ResolveConfigurationFilePath(machineDirectory, relativeFilePath);
            var destinationDirectory = Path.GetDirectoryName(filePath);
            Directory.CreateDirectory(destinationDirectory);

            var temporaryPath = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            string backupPath = null;

            try
            {
                var json = JsonConvert.SerializeObject(
                    configuration,
                    Formatting.Indented,
                    new JsonSerializerSettings
                    {
                        ContractResolver = new CamelCasePropertyNamesContractResolver()
                    });
                File.WriteAllText(temporaryPath, json + Environment.NewLine, new UTF8Encoding(false));

                if (File.Exists(filePath))
                {
                    backupPath = CreateBackupPath(filePath);
                    File.Replace(temporaryPath, filePath, backupPath, true);
                }
                else
                {
                    File.Move(temporaryPath, filePath);
                }
                // 原子安装成功后临时文件已被移动；此后不再执行可能失败的文件操作。
                return backupPath;
            }
            catch (Exception saveError)
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                catch (Exception cleanupError)
                {
                    throw new AggregateException("保存设备配置失败，且临时文件清理也失败。", saveError, cleanupError);
                }
                throw new InvalidOperationException(
                    "保存设备配置失败：MachineId='" + machineId + "'，文件='" + relativeFilePath + "'。",
                    saveError);
            }
        }

        /// <summary>验证相对 JSON 路径，并确保目标文件位于设备配置目录内。</summary>
        private static string ResolveConfigurationFilePath(string machineDirectory, string relativeFilePath)
        {
            if (string.IsNullOrWhiteSpace(relativeFilePath))
            {
                throw new ArgumentException("配置文件相对路径不能为空。", nameof(relativeFilePath));
            }

            if (Path.IsPathRooted(relativeFilePath))
            {
                throw new ArgumentException("配置文件路径必须是设备目录内的相对路径。", nameof(relativeFilePath));
            }

            var normalizedPath = relativeFilePath
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar);
            var pathSegments = normalizedPath.Split(Path.DirectorySeparatorChar);
            if (pathSegments.Any(segment => string.IsNullOrWhiteSpace(segment)
                || segment == "."
                || segment == ".."
                || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            {
                throw new ArgumentException("配置文件路径包含无效目录或文件名。", nameof(relativeFilePath));
            }

            if (!string.Equals(Path.GetExtension(normalizedPath), ".json", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("只允许读取或写入 .json 配置文件。", nameof(relativeFilePath));
            }

            var filePath = Path.GetFullPath(Path.Combine(machineDirectory, normalizedPath));
            EnsurePathIsBelowDirectory(machineDirectory, filePath, nameof(relativeFilePath));
            return filePath;
        }

        /// <summary>验证 MachineId 是安全的单层目录名称。</summary>
        private static void ValidateMachineId(string machineId)
        {
            if (string.IsNullOrWhiteSpace(machineId)
                || !string.Equals(machineId, machineId.Trim(), StringComparison.Ordinal)
                || machineId == "."
                || machineId == ".."
                || machineId.EndsWith(".", StringComparison.Ordinal)
                || machineId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new ArgumentException(
                    "MachineId 必须是有效的单层目录名称，不能包含路径分隔符或首尾空格。",
                    nameof(machineId));
            }
        }

        /// <summary>确认规范化后的目标路径仍位于指定父目录内。</summary>
        private static void EnsurePathIsBelowDirectory(string parentDirectory, string childPath, string parameterName)
        {
            var parentPrefix = EnsureTrailingSeparator(Path.GetFullPath(parentDirectory));
            var fullChildPath = Path.GetFullPath(childPath);
            if (!fullChildPath.StartsWith(parentPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("配置路径必须位于指定目录内。", parameterName);
            }
        }

        /// <summary>为目录路径补齐结尾分隔符，便于进行目录前缀校验。</summary>
        private static string EnsureTrailingSeparator(string directory)
        {
            var separator = Path.DirectorySeparatorChar.ToString();
            if (directory.EndsWith(separator, StringComparison.Ordinal)
                || directory.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal))
            {
                return directory;
            }

            return directory + separator;
        }

        /// <summary>为已有配置生成唯一的时间戳备份文件路径。</summary>
        private static string CreateBackupPath(string filePath)
        {
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
            var backupPath = filePath + ".bak." + timestamp;
            if (File.Exists(backupPath))
            {
                backupPath += "." + Guid.NewGuid().ToString("N");
            }

            return backupPath;
        }
    }
}
