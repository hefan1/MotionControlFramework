using System;
using System.IO;
using System.Linq;

namespace MotionControl.Host.Wpf
{
    /// <summary>解析显式指定的仿真配置目录，避免将测试配置写入现场目录。</summary>
    internal static class SimulationConfigurationRoot
    {
        /// <summary>仿真配置目录启动参数；后面必须紧跟一个本机绝对路径。</summary>
        private const string Option = "--simulation-config-root";

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
