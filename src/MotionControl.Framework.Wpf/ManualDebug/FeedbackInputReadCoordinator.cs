using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.IO;

namespace MotionControl.Framework.Wpf.ManualDebug
{
    /// <summary>关联反馈单次读取的结果，未知状态不能被当作有效或无效。</summary>
    internal enum FeedbackInputReadStatus
    {
        /// <summary>设备在规定时间内返回了有效状态。</summary>
        Success,
        /// <summary>设备调用未在规定时间内返回，底层调用可能仍在运行。</summary>
        Timeout,
        /// <summary>其他未返回的设备调用占满了进程的读取名额。</summary>
        Busy,
        /// <summary>设备调用返回了错误。</summary>
        Error
    }

    /// <summary>保存一项关联输入的读取结果和可用于诊断的异常。</summary>
    internal sealed class FeedbackInputReadResult
    {
        /// <summary>标识采样是否成功、超时、繁忙或失败。</summary>
        internal FeedbackInputReadStatus Status { get; set; }
        /// <summary>设备按逻辑有效电平转换的结果，仅成功时有值。</summary>
        internal bool? Value { get; set; }
        /// <summary>真实读取错误；超时和繁忙没有底层异常。</summary>
        internal Exception Error { get; set; }
    }

    /// <summary>限制监控页对不可取消设备读取的并发，避免超时后重复调用堆积。</summary>
    internal sealed class FeedbackInputReadCoordinator
    {
        /// <summary>全进程同时在途的真实读取最多四个；只有调用真正结束才释放。</summary>
        private static readonly SemaphoreSlim GlobalSlots = new SemaphoreSlim(4, 4);
        /// <summary>同一设备连接共享在途集合，监控页面重建也不能重复启动同一个输入。</summary>
        private static readonly ConditionalWeakTable<ILogicalIo, FeedbackInputReadCoordinator> Instances =
            new ConditionalWeakTable<ILogicalIo, FeedbackInputReadCoordinator>();
        /// <summary>设备未返回时，本轮在 500 毫秒后显示未知。</summary>
        private const int ReadTimeoutMilliseconds = 500;
        /// <summary>当前设备连接的逻辑输入接口。</summary>
        private readonly ILogicalIo _logicalIo;
        /// <summary>保护在途读取清单。</summary>
        private readonly object _sync = new object();
        /// <summary>每个逻辑输入在同一设备上至多存在一个实际调用。</summary>
        private readonly Dictionary<string, PendingRead> _pending =
            new Dictionary<string, PendingRead>(StringComparer.OrdinalIgnoreCase);

        /// <summary>记录一个底层调用及其是否已经超时。</summary>
        private sealed class PendingRead
        {
            /// <summary>设备尚在进行的真实读取任务。</summary>
            internal Task<bool> Task;
            /// <summary>本次读取已经超时，后续轮询不能再把迟到值当作新采样。</summary>
            internal volatile bool TimedOut;
        }

        /// <summary>创建和一个逻辑 IO 连接关联的采样协调器。</summary>
        private FeedbackInputReadCoordinator(ILogicalIo logicalIo)
        {
            _logicalIo = logicalIo;
        }

        /// <summary>按同一逻辑 IO 实例复用协调器；断开后新连接有独立名称空间但共享全局上限。</summary>
        internal static FeedbackInputReadCoordinator For(ILogicalIo logicalIo)
        {
            if (logicalIo == null) throw new ArgumentNullException(nameof(logicalIo));
            return Instances.GetValue(logicalIo, value => new FeedbackInputReadCoordinator(value));
        }

        /// <summary>在 500 毫秒内等待读取；设备调用无法取消时保留槽位直至实际结束。</summary>
        internal async Task<FeedbackInputReadResult> ReadAsync(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return new FeedbackInputReadResult { Status = FeedbackInputReadStatus.Error,
                    Error = new ArgumentException("反馈输入名称不能为空。", nameof(name)) };

            PendingRead pending;
            lock (_sync)
            {
                if (!_pending.TryGetValue(name, out pending))
                {
                    if (!GlobalSlots.Wait(0)) return new FeedbackInputReadResult { Status = FeedbackInputReadStatus.Busy };
                    pending = new PendingRead();
                    try
                    {
                        // 某些适配器会在返回 Task 前同步阻塞；不能让它卡住 WPF 线程。
                        pending.Task = Task.Run(() => _logicalIo.ReadInputAsync(name, CancellationToken.None));
                        _pending.Add(name, pending);
                        PendingRead started = pending;
                        pending.Task.ContinueWith(task => ReleaseCompleted(name, started, task),
                            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    }
                    catch
                    {
                        GlobalSlots.Release();
                        throw;
                    }
                }
                if (pending.TimedOut) return new FeedbackInputReadResult { Status = FeedbackInputReadStatus.Timeout };
            }

            Task winner = await Task.WhenAny(pending.Task, Task.Delay(ReadTimeoutMilliseconds)).ConfigureAwait(false);
            if (!ReferenceEquals(winner, pending.Task))
            {
                pending.TimedOut = true;
                return new FeedbackInputReadResult { Status = FeedbackInputReadStatus.Timeout };
            }
            try
            {
                bool active = await pending.Task.ConfigureAwait(false);
                if (pending.TimedOut) return new FeedbackInputReadResult { Status = FeedbackInputReadStatus.Timeout };
                return new FeedbackInputReadResult { Status = FeedbackInputReadStatus.Success, Value = active };
            }
            catch (Exception error)
            {
                return new FeedbackInputReadResult { Status = FeedbackInputReadStatus.Error, Error = error };
            }
        }

        /// <summary>真实调用完成后释放占位并观察异常；超时并不意味着底层任务已停止。</summary>
        private void ReleaseCompleted(string name, PendingRead pending, Task<bool> task)
        {
            if (task.IsFaulted) { var ignored = task.Exception; }
            lock (_sync)
            {
                if (_pending.TryGetValue(name, out PendingRead current) && ReferenceEquals(current, pending))
                    _pending.Remove(name);
            }
            GlobalSlots.Release();
        }
    }
}
