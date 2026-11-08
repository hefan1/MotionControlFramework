using System;
using System.IO;
using MotionControl.Framework.Runtime.Configuration;

namespace MotionControl.Metadata.Checks
{
    /// <summary>检查数值元数据和点位归属的软件合同，不连接设备、不操作生产目录。</summary>
    internal static partial class Program
    {
        /// <summary>记录实际运行的断言数量。</summary>
        private static int _checks;

        /// <summary>逐组执行检查，任何未预期结果均以非零退出码报告。</summary>
        private static int Main()
        {
            try
            {
                LegacyAndMetadataRoundTrip();
                NumericRangeAndValueIdentity();
                DefinitionEditingRequiresClosedTaskSession();
                NumericBindingCompatibility();
                ProjectionUsesExactAxis();
                TeachPointIdentityAndMigration();
                ProjectPreviewRejectsImplicitMigration();
                Console.WriteLine("变量元数据、数值引用、示教归属检查通过：" + _checks + " 项断言。未验证 WPF 屏幕或实物硬件。");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                return 1;
            }
        }

        /// <summary>报告具体不符合的合同。</summary>
        private static void Check(bool condition, string reason)
        {
            if (!condition) throw new InvalidOperationException(reason);
            _checks++;
        }

        /// <summary>拒绝场景必须真正抛出带定位原因的错误，不能以默认值通过。</summary>
        private static void Reject(Action action, string reason)
        {
            try { action(); }
            catch (Exception error)
            {
                Check(error.ToString().Contains(reason), "拒绝原因不符合预期：" + error);
                return;
            }
            throw new InvalidOperationException("应拒绝但意外通过：" + reason);
        }

        /// <summary>只创建本次检查自己的配置根目录，结束时删除本次临时数据。</summary>
        private sealed class TestFiles : IDisposable
        {
            /// <summary>带随机标识的临时目录，不复用用户设备配置。</summary>
            internal readonly string Root = Path.Combine(Path.GetTempPath(), "MotionControlMetadataChecks-" + Guid.NewGuid().ToString("N"));
            /// <summary>正式配置读写服务，用来检查实际序列化路径。</summary>
            internal readonly MachineConfigurationStore Store;

            /// <summary>把正式存储服务限制到本测试目录。</summary>
            internal TestFiles() { Store = new MachineConfigurationStore(Root); }

            /// <summary>仅清理随机建立的测试目录。</summary>
            public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
        }
    }
}
