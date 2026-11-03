using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using MotionControl.Framework.Runtime.Configuration;

namespace MotionControl.Host.Wpf
{
    /// <summary>让工程导入与控制卡连接使用同一排他门，避免连接读取到部分应用的配置。</summary>
    internal static class HostProjectOperationGate
    {
        /// <summary>进程内的单机工程操作门；一个 Host 只管理一台设备。</summary>
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
        /// <summary>成功更换工程后，旧 Host 内存不得再次连接控制器。</summary>
        private static int _restartRequired;
        /// <summary>多文件工程应用的持久收尾标记。</summary>
        private const string PendingFile = "Runtime/EquipmentProjectApplyPending.json";

        /// <summary>在后台应用工程前取得独占权；调用方必须在租约内再次检查设备状态。</summary>
        internal static async Task<IDisposable> EnterImportAsync(CancellationToken cancellationToken)
        {
            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new Lease();
        }

        /// <summary>连接前等待工程应用结束，并拒绝使用未恢复或已替换的工程。</summary>
        internal static async Task<IDisposable> EnterConnectAsync(MachineConfigurationStore store, string machineId, CancellationToken cancellationToken)
        {
            if (store == null) throw new ArgumentNullException(nameof(store));
            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _restartRequired) != 0)
                    throw new InvalidOperationException("设备工程已更新，请关闭并重新打开上位机后再连接。 ");
                string path = Path.Combine(store.GetMachineDirectory(machineId), PendingFile.Replace('/', Path.DirectorySeparatorChar));
                bool pending;
                try
                {
                    File.GetAttributes(path);
                    pending = true;
                }
                catch (FileNotFoundException) { pending = false; }
                catch (DirectoryNotFoundException) { pending = false; }
                if (pending) throw new InvalidOperationException("上次设备工程应用尚未完成，请先恢复工程配置再连接。");
                return new Lease();
            }
            catch
            {
                Gate.Release();
                throw;
            }
        }

        /// <summary>在线配置写入与工程导入共用排他门，并检查待恢复和重启状态。</summary>
        internal static Task<IDisposable> EnterConfigurationAsync(MachineConfigurationStore store, string machineId, CancellationToken cancellationToken)
        { return EnterConnectAsync(store, machineId, cancellationToken); }

        /// <summary>等页面 await 的正常优先级续体完成回滚和 finally 后再释放工程排他门。</summary>
        internal static void ReleaseAfterUiContinuation(IDisposable lease, Dispatcher dispatcher)
        {
            if (lease == null) return;
            if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            { lease.Dispose(); return; }
            try
            {
                DispatcherOperation operation = dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(lease.Dispose));
                operation.Aborted += (sender, args) => lease.Dispose();
                if (operation.Status == DispatcherOperationStatus.Aborted) lease.Dispose();
            }
            catch (InvalidOperationException) { lease.Dispose(); }
            catch (TaskCanceledException) { lease.Dispose(); }
        }

        /// <summary>必须在导入租约内调用；即使窗口返回也禁止旧内存配置重新连接。</summary>
        internal static void RequireRestart() { Interlocked.Exchange(ref _restartRequired, 1); }

        /// <summary>防止租约重复释放导致连接和导入并行。</summary>
        private sealed class Lease : IDisposable
        {
            /// <summary>为零表示尚未归还。</summary>
            private int _disposed;
            /// <summary>只释放一次独占门。</summary>
            public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) Gate.Release(); }
        }
    }
}
