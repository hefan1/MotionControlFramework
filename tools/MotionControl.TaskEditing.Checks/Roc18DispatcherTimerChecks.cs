using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Threading;

namespace MotionControl.TaskEditing.Checks
{
    /// <summary>使用实际 WPF 到期调度核对 Tooltip 默认时长，不依赖鼠标位置，也不连接设备。</summary>
    internal static partial class Program
    {
        /// <summary>UI 短暂忙碌后新建 Tooltip 时长计时器，不能让已经到期的页面轮询永久等待。</summary>
        private static void Roc18ToolTipDurationKeepsDueTimerRunning()
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var frame = new DispatcherFrame();
            var elapsed = new Stopwatch();
            int duration = ToolTipService.GetShowDuration(new TextBlock { ToolTip = "计时器检查" });
            var polling = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
            var tooltipDuration = new DispatcherTimer(DispatcherPriority.Normal)
            {
                Interval = TimeSpan.FromMilliseconds(duration)
            };
            // 0=等待，1=按时触发，2=超时；守护已经记下的失败不能被后来到达的 Tick 覆盖。
            int result = 0;
            DispatcherOperation timeoutOperation = null;
            EventHandler tick = (sender, args) =>
            {
                // 同时核对单调时钟；线程池守护本身延迟不能放宽两秒的判定。
                Interlocked.CompareExchange(ref result, elapsed.Elapsed < TimeSpan.FromSeconds(2) ? 1 : 2, 0);
                polling.Stop();
                frame.Continue = false;
            };
            polling.Tick += tick;
            var deadline = new Timer(state =>
            {
                // 与 DispatcherTimer 分开计时；正常队列探针仍能使失败场景退出并保留失败。
                if (Interlocked.CompareExchange(ref result, 2, 0) != 0) return;
                timeoutOperation = dispatcher.BeginInvoke(DispatcherPriority.Send,
                    new Action(() => frame.Continue = false));
            }, null, Timeout.Infinite, Timeout.Infinite);
            try
            {
                elapsed.Start();
                deadline.Change(2000, Timeout.Infinite);
                polling.Start();
                int startedAt = Environment.TickCount;
                // 有意让短计时器先到期，期间不泵送 UI；模拟同步页面工作占用几十毫秒。
                Thread.Sleep(75);
                Assert(unchecked(Environment.TickCount - startedAt) >= 50,
                    "ROC-18：未建立短计时器已经到期的回归前提。");
                tooltipDuration.Start();
                int now = Environment.TickCount;
                Console.WriteLine("[到期调度回归] Tooltip时长=" + duration + "ms；TickCount=" + now
                    + "；缓存到期=" + ReadRoc18DiagnosticField(dispatcher, "_dueTimeInTicks"));
                WriteRoc18TimerDetails("已到期短轮询", polling, now, true);
                WriteRoc18TimerDetails("Tooltip有效时长", tooltipDuration, now, true);
                Dispatcher.PushFrame(frame);
                Assert(Volatile.Read(ref result) == 1,
                    "ROC18_TIMER_NOT_PROMOTED：新建 Tooltip 时长计时器后，已到期短轮询未在两秒内执行。");
            }
            finally
            {
                polling.Stop();
                polling.Tick -= tick;
                tooltipDuration.Stop();
                // 等待守护回调退出后再撤销它的排队操作，避免迟到探针影响下一项调度证据。
                var callbackFinished = new ManualResetEvent(false);
                bool disposed = deadline.Dispose(callbackFinished) && callbackFinished.WaitOne(1000);
                if (disposed) callbackFinished.Dispose();
                timeoutOperation?.Abort();
                Assert(disposed, "ROC-18：计时器检查的独立守护未结束，不能继续把后续结果当作有效证据。");
            }
        }
    }
}
