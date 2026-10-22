using System;
using System.IO;
using MotionControl.Framework.Runtime.Configuration;

namespace MotionControl.TaskEngine.Checks
{
    /// <summary>每项文件检查使用独立临时目录，不触碰用户的 Machine01 配置。</summary>
    internal sealed class TestWorkspace : IDisposable
    {
        /// <summary>本次专用临时目录。</summary>
        internal string Root { get; private set; }
        /// <summary>临时目录中的配置服务。</summary>
        internal MachineConfigurationStore Store { get; private set; }

        /// <summary>创建随机且独立的检查目录。</summary>
        internal TestWorkspace()
        {
            Root = Path.Combine(Path.GetTempPath(), "MotionControl.TaskEngine.Checks", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Store = new MachineConfigurationStore(Root);
        }

        /// <summary>只删除已确认位于专用测试根目录下的随机子目录。</summary>
        public void Dispose()
        {
            string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MotionControl.TaskEngine.Checks"))
                + Path.DirectorySeparatorChar;
            string fullPath = Path.GetFullPath(Root);
            if (!fullPath.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(fullPath).Length != 32)
                throw new InvalidOperationException("拒绝清理非测试目录：" + fullPath);
            if (Directory.Exists(fullPath)) Directory.Delete(fullPath, true);
        }
    }
}
