using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.Host.Wpf;

namespace MotionControl.ProjectGate.Checks
{
    /// <summary>验证工程应用与卡连接之间的独占和重启保护。</summary>
    internal static class Program
    {
        /// <summary>运行独立进程检查，不连接控制卡或改动设备配置。</summary>
        private static int Main()
        {
            try { RunAsync().GetAwaiter().GetResult(); Console.WriteLine("工程应用与连接门禁检查通过。"); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }

        /// <summary>验证导入期间禁止抢连、遗留标记阻止连接及更新后必须重启。</summary>
        private static async Task RunAsync()
        {
            string root = Path.Combine(Path.GetTempPath(), "MotionControlProjectGate-" + Guid.NewGuid().ToString("N"));
            var store = new MachineConfigurationStore(root);
            try
            {
                IDisposable import = await HostProjectOperationGate.EnterImportAsync(CancellationToken.None);
                Task<IDisposable> connect = HostProjectOperationGate.EnterConnectAsync(store, "Machine01", CancellationToken.None);
                if (await Task.WhenAny(connect, Task.Delay(100)) == connect)
                    throw new InvalidOperationException("工程应用期间连接未等待独占门。");
                import.Dispose();
                using (await connect) { }

                string runtime = Path.Combine(store.GetMachineDirectory("Machine01"), "Runtime");
                Directory.CreateDirectory(runtime);
                string marker = Path.Combine(runtime, "EquipmentProjectApplyPending.json");
                File.WriteAllText(marker, "pending");
                await ExpectRejectedAsync(store, "中断标记未阻止连接。");
                File.Delete(marker);

                import = await HostProjectOperationGate.EnterImportAsync(CancellationToken.None);
                connect = HostProjectOperationGate.EnterConnectAsync(store, "Machine01", CancellationToken.None);
                HostProjectOperationGate.RequireRestart();
                import.Dispose();
                try { using (await connect) { } throw new InvalidOperationException("工程更新后未阻止连接。"); }
                catch (InvalidOperationException error)
                { if (!error.Message.Contains("重新打开")) throw; }
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        /// <summary>检查遗留事务标记必须拒绝连接，且释放门以允许后续检查。</summary>
        private static async Task ExpectRejectedAsync(MachineConfigurationStore store, string message)
        {
            try { using (await HostProjectOperationGate.EnterConnectAsync(store, "Machine01", CancellationToken.None)) { } throw new InvalidOperationException(message); }
            catch (InvalidOperationException error)
            { if (!error.Message.Contains("尚未完成")) throw; }
        }
    }
}
