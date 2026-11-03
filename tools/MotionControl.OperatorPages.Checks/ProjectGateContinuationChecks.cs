using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using MotionControl.Host.Wpf;

namespace MotionControl.OperatorPages.Checks
{
    /// <summary>验证工程导入门等待映射页面的 UI 回滚续体后才释放。</summary>
    internal static class ProjectGateContinuationChecks
    {
        /// <summary>分别检查同步及异步失败、页面回滚和已关闭的调度器。</summary>
        internal static void Run()
        {
            var previous = SynchronizationContext.Current;
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            try
            {
                CheckRollbackBeforeImport(dispatcher, false);
                CheckRollbackBeforeImport(dispatcher, true);
                CheckShutdownWaitsForRollback(dispatcher);
                CheckStoppedDispatcherReleasesGate();
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        }

        /// <summary>模拟映射写入失败后的外层 catch；导入取得门时必须看到已恢复的文件。</summary>
        private static void CheckRollbackBeforeImport(Dispatcher dispatcher, bool yieldBeforeFailure)
        {
            string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "mcf-ui-gate-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(root);
            string file = Path.Combine(root, "IoMapping.json");
            File.WriteAllText(file, "原配置");
            var frame = new DispatcherFrame();
            Exception failure = null;
            var timeout = new DispatcherTimer(DispatcherPriority.Send, dispatcher) { Interval = TimeSpan.FromSeconds(5) };
            timeout.Tick += (sender, args) => { timeout.Stop(); failure = new TimeoutException("工程排他门未及时释放。"); frame.Continue = false; };
            timeout.Start();
            async void Start()
            {
                try
                {
                    IDisposable lease = await EnterAsync();
                    try { await SimulateApplyFailure(lease, dispatcher, file, yieldBeforeFailure); }
                    catch (InvalidOperationException) { File.WriteAllText(file, "原配置"); }
                    finally
                    {
                        Task<IDisposable> waiting = EnterAsync();
                        if (waiting.IsCompleted) throw new InvalidOperationException("页面回滚和 finally 尚未结束，工程导入已取得排他门。");
                        IDisposable next = await waiting;
                        try
                        {
                            if (File.ReadAllText(file) != "原配置")
                                throw new InvalidOperationException("工程导入读取到了映射回滚前的文件。");
                        }
                        finally { next.Dispose(); }
                    }
                }
                catch (Exception error) { failure = error; }
                finally { timeout.Stop(); frame.Continue = false; }
            }
            try
            {
                Start();
                Dispatcher.PushFrame(frame);
                if (failure != null) throw new InvalidOperationException("映射回滚与工程导入次序错误。", failure);
            }
            finally
            {
                timeout.Stop();
                if (root.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) && Directory.Exists(root))
                    Directory.Delete(root, true);
            }
        }

        /// <summary>正式 Host 关闭屏障必须等待页面失败回滚完成。</summary>
        private static void CheckShutdownWaitsForRollback(Dispatcher dispatcher)
        {
            var app = (App)FormatterServices.GetUninitializedObject(typeof(App));
            var frame = new DispatcherFrame();
            Exception failure = null;
            bool rolledBack = false;
            var timeout = new DispatcherTimer(DispatcherPriority.Send, dispatcher) { Interval = TimeSpan.FromSeconds(5) };
            timeout.Tick += (sender, args) => { timeout.Stop(); failure = new TimeoutException("关闭等待映射回滚超时。"); frame.Continue = false; };
            timeout.Start();
            async void Start()
            {
                try
                {
                    IDisposable mapping = await EnterAsync();
                    MethodInfo acquire = typeof(App).GetMethod("AcquireShutdownProjectGateAsync", BindingFlags.Instance | BindingFlags.NonPublic);
                    var closeWait = (Task)acquire.Invoke(app, null);
                    if (closeWait.IsCompleted) throw new InvalidOperationException("关闭越过了在途映射操作。");
                    ReleaseAfterUiContinuation(mapping, dispatcher);
                    await Task.Yield();
                    rolledBack = true;
                    await closeWait;
                    if (!rolledBack) throw new InvalidOperationException("关闭早于页面回滚。");
                    typeof(App).GetMethod("ReleaseShutdownProjectGate", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(app, null);
                }
                catch (Exception error) { failure = error; }
                finally { timeout.Stop(); frame.Continue = false; }
            }
            Start();
            Dispatcher.PushFrame(frame);
            timeout.Stop();
            if (failure != null) throw new InvalidOperationException("关闭与映射回滚的屏障错误。", failure);
        }

        /// <summary>模拟 Host 在线配置回调在同步或异步失败时延后归还租约。</summary>
        private static async Task SimulateApplyFailure(IDisposable lease, Dispatcher dispatcher, string file, bool yieldBeforeFailure)
        {
            try
            {
                File.WriteAllText(file, "未完成配置");
                if (yieldBeforeFailure) await Task.Yield();
                throw new InvalidOperationException("模拟下发失败");
            }
            finally { ReleaseAfterUiContinuation(lease, dispatcher); }
        }

        /// <summary>调度器已经停止时必须直接归还租约，不能使后续工程操作永远阻塞。</summary>
        private static void CheckStoppedDispatcherReleasesGate()
        {
            Dispatcher stopped = null;
            var thread = new Thread(() => { stopped = Dispatcher.CurrentDispatcher; stopped.InvokeShutdown(); });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if (!thread.Join(5000)) throw new TimeoutException("测试调度器无法结束。");
            IDisposable lease = EnterAsync().GetAwaiter().GetResult();
            ReleaseAfterUiContinuation(lease, stopped);
            Task<IDisposable> next = EnterAsync();
            if (!next.Wait(TimeSpan.FromSeconds(2))) throw new TimeoutException("调度器关闭时工程门没有释放。");
            next.Result.Dispose();
        }

        /// <summary>通过正式 Host 内部工程门申请排他租约。</summary>
        private static Task<IDisposable> EnterAsync()
        {
            Type gate = typeof(App).Assembly.GetType("MotionControl.Host.Wpf.HostProjectOperationGate", true);
            return (Task<IDisposable>)gate.GetMethod("EnterImportAsync", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { CancellationToken.None });
        }

        /// <summary>通过正式 Host 的 UI 续体安全释放方法归还租约。</summary>
        private static void ReleaseAfterUiContinuation(IDisposable lease, Dispatcher dispatcher)
        {
            Type gate = typeof(App).Assembly.GetType("MotionControl.Host.Wpf.HostProjectOperationGate", true);
            gate.GetMethod("ReleaseAfterUiContinuation", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { lease, dispatcher });
        }
    }
}
