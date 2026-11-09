using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace MotionControl.Host.Wpf
{
    /// <summary>解析显式指定的仿真配置目录，避免将测试配置写入现场目录。</summary>
    internal static class SimulationConfigurationRoot
    {
        /// <summary>仿真配置目录启动参数；后面必须紧跟一个本机绝对路径。</summary>
        private const string Option = "--simulation-config-root";

        /// <summary>显式使用原设备配置，忽略本机保存的默认仿真项目。</summary>
        private const string ProductionOption = "--use-production-config";

        /// <summary>优先使用显式参数，再读取本机默认仿真项目；无记录时保持原设备配置。</summary>
        internal static string Resolve(string[] args, string productionRoot, string preferenceFile, out bool savedDefault)
        {
            savedDefault = false;
            if (args.Count(value => value == ProductionOption) > 1
                || (args.Contains(ProductionOption) && args.Contains(Option)))
                throw new ArgumentException("原设备配置和隔离测试配置不能同时或重复指定。");
            string specified = Parse(args, productionRoot);
            if (specified != null) return specified;
            if (args.Contains(ProductionOption) || args.Contains("--ui-workspace-preview")
                || args.Contains("--task-editing-preview") || args.Contains("--task-simulation")) return null;

            try { File.GetAttributes(preferenceFile); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            catch (Exception error) { throw new ArgumentException("无法读取本机默认项目设置。", error); }

            try
            {
                string path = File.ReadAllText(preferenceFile, System.Text.Encoding.UTF8).Trim();
                if (path.IndexOfAny(new[] { '\r', '\n' }) >= 0)
                    throw new ArgumentException("默认项目路径只能有一行。");
                string project = Parse(new[] { Option, path }, productionRoot);
                if (!Directory.Exists(project)) throw new ArgumentException("保存的默认项目目录不存在：" + project);
                ValidateProjectContainsNoLinks(project);
                ReadProjectSession(project);
                string backendPath = Path.Combine(project, "Machine01", "Parameters", "ControllerBackend.json");
                JObject backend = JObject.Parse(File.ReadAllText(backendPath, System.Text.Encoding.UTF8));
                int port = (int?)backend["port"] ?? 0;
                if (!string.Equals((string)backend["backend"], "Virtual", StringComparison.OrdinalIgnoreCase)
                    || port < 1 || port > 65535)
                    throw new ArgumentException("默认项目必须配置有效的本机软件控制器。");
                savedDefault = true;
                return project;
            }
            catch (Exception error)
            {
                throw new ArgumentException("本机默认项目无效，未切换到其他设备配置：" + error.Message, error);
            }
        }

        /// <summary>把新项目独有编号与其绝对路径绑定，避免复用其他项目的软件控制器状态。</summary>
        internal static string ReadProjectSession(string project)
        {
            string value = File.ReadAllText(Path.Combine(project, "ProjectSession.id"), Encoding.UTF8).Trim();
            if (!Guid.TryParseExact(value, "N", out Guid id) || id == Guid.Empty)
                throw new ArgumentException("默认项目缺少有效的控制器会话编号。");
            string path = Path.GetFullPath(project).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
            using (SHA256 hash = SHA256.Create())
            {
                byte[] bytes = hash.ComputeHash(Encoding.UTF8.GetBytes(path + "|" + id.ToString("N")));
                return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
            }
        }

        /// <summary>读取或写入默认项目之前拒绝内部链接，避免配置及恢复文件指向其他设备目录。</summary>
        private static void ValidateProjectContainsNoLinks(string project)
        {
            var pending = new Stack<DirectoryInfo>();
            pending.Push(new DirectoryInfo(project));
            while (pending.Count > 0)
            {
                foreach (FileSystemInfo item in pending.Pop().EnumerateFileSystemInfos())
                {
                    FileAttributes attributes = item.Attributes;
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new ArgumentException("默认项目内部不能包含目录联接或符号链接：" + item.FullName);
                    if ((attributes & FileAttributes.Directory) != 0)
                        pending.Push(new DirectoryInfo(item.FullName));
                }
            }
        }

        /// <summary>返回隔离目录；未指定时返回 null，格式错误时拒绝启动。</summary>
        internal static string Parse(string[] args, string productionRoot)
        {
            int index = Array.IndexOf(args, Option);
            if (index < 0) return null;
            if (args.Count(value => value == Option) != 1 || index + 1 >= args.Length
                || string.IsNullOrWhiteSpace(args[index + 1]) || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("测试配置目录参数必须且只能指定一次，并提供一个本机绝对路径。");
            if (args.Contains("--ui-workspace-preview") || args.Contains("--task-editing-preview") || args.Contains("--task-simulation"))
                throw new ArgumentException("测试配置目录不能与其他独立预览参数同时使用。");

            string path = args[index + 1];
            if (path.Length < 3 || !char.IsLetter(path[0]) || path[1] != ':'
                || (path[2] != '\\' && path[2] != '/'))
                throw new ArgumentException("测试配置目录必须是本机磁盘上的绝对路径。");
            string simulationRoot = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string liveRoot = Path.GetFullPath(productionRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (simulationRoot.Length <= 2)
                throw new ArgumentException("测试配置目录不能是磁盘根目录。");
            if (IsSameOrChild(simulationRoot, liveRoot) || IsSameOrChild(liveRoot, simulationRoot))
                throw new ArgumentException("测试配置目录不能与现场配置目录相同或互相包含。");

            // 不允许通过已有目录联接绕过目录隔离；不存在的子目录稍后由配置服务创建。
            var directory = new DirectoryInfo(simulationRoot);
            while (directory != null)
            {
                if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new ArgumentException("测试配置目录及其父目录不能是目录联接或符号链接：" + directory.FullName);
                directory = directory.Parent;
            }
            return simulationRoot;
        }

        /// <summary>判断目录是否相同，或第一个目录是否位于第二个目录之下。</summary>
        private static bool IsSameOrChild(string path, string root)
        {
            return string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
    }
}
