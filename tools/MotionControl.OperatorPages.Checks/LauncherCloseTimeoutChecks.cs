using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Threading;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.Host.Wpf;
using MotionControl.TaskEngine.Wpf.Simulation;

namespace MotionControl.OperatorPages.Checks
{
    /// <summary>检查真实任务入口的超时保留与重试清理；注入关闭结果，不覆盖底层设备关闭正确性。</summary>
    internal static class LauncherCloseTimeoutChecks
    {
        /// <summary>分别验证关闭迟到成功和迟到失败。</summary>
        internal static void Run()
        {
            foreach (bool failLate in new[] { false, true }) RunScenario(failLate);
        }

        /// <summary>驱动真实入口等待超时，检查迟到结果不会清除会话，并验证重试清理。</summary>
        private static void RunScenario(bool failLate)
        {
            var host = new MainWindow();
            var type = typeof(MainWindow).Assembly.GetType("MotionControl.Host.Wpf.Tasks.TaskMonitorLauncher", true);
            object page = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null,
                new object[] { new MachineConfigurationStore(Path.Combine(Path.GetTempPath(), "LauncherCloseChecks")), "MissingMachine", host, null }, null);
            var session = TaskMonitorExample.Create();
            var content = (ContentControl)Field(type, "_monitorContent").GetValue(page);
            var marker = new TextBlock { Text = "retained monitor" };
            var timer = (DispatcherTimer)Field(type, "_stateTimer").GetValue(page);
            var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var closeField = Field(session.Machine.GetType(), "_closeTask");
            closeField.SetValue(session.Machine, pending.Task);
            Field(type, "_session").SetValue(page, session);
            content.Content = marker;
            var close = type.GetMethod("CloseTaskSessionAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            try
            {
                Task attempt = (Task)close.Invoke(page, new object[] { false });
                PumpUntil(attempt);
                try { attempt.GetAwaiter().GetResult(); throw new InvalidOperationException("Launcher close did not time out."); }
                catch (TimeoutException) { }
                AssertRetained(type, page, session, content, marker, timer);
                if (failLate) pending.SetException(new IOException("late close failure"));
                else pending.SetResult(true);
                // Drain queued Dispatcher continuations, including any incorrect late cleanup.
                var drain = Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                PumpUntil(drain.Task);
                AssertRetained(type, page, session, content, marker, timer);
                // A successful retry result is injected for the failure case, so this
                // assertion remains about Launcher cleanup, not the device close body.
                if (failLate) closeField.SetValue(session.Machine, Task.FromResult(true));
                Task retry = (Task)close.Invoke(page, new object[] { false });
                PumpUntil(retry);
                retry.GetAwaiter().GetResult();
                if (Field(type, "_session").GetValue(page) != null || content.Content != null || timer.IsEnabled)
                    throw new InvalidOperationException("Successful retry did not clear Launcher session/UI and stop refresh.");
            }
            finally { pending.TrySetResult(true); timer.Stop(); host.Close(); }
        }

        /// <summary>读取入口或控制器私有字段，用于限定范围的状态检查和结果注入。</summary>
        private static FieldInfo Field(Type type, string name) => type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        /// <summary>断言会话、监控内容和恢复刷新仍被保留。</summary>
        private static void AssertRetained(Type type, object page, object session, ContentControl content, object marker, DispatcherTimer timer)
        {
            if (!ReferenceEquals(Field(type, "_session").GetValue(page), session) || !ReferenceEquals(content.Content, marker) || !timer.IsEnabled)
                throw new InvalidOperationException("Timed-out or late close cleared Launcher session/UI or disabled recovery refresh.");
            if (!(bool)Field(type, "_closingTaskSession").GetValue(page))
                throw new InvalidOperationException("Timed-out or late close made the unconfirmed session available for preparation.");
        }
        /// <summary>持续处理界面续体直到任务结束，并设置检查自身的等待上限。</summary>
        private static void PumpUntil(Task task)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(10) };
            DateTime deadline = DateTime.UtcNow.AddSeconds(25);
            timer.Tick += (s, e) => { if (task.IsCompleted || DateTime.UtcNow >= deadline) frame.Continue = false; };
            timer.Start();
            try { Dispatcher.PushFrame(frame); }
            finally { timer.Stop(); }
            if (!task.IsCompleted) throw new TimeoutException("Launcher check Dispatcher deadline exceeded.");
        }
    }
}
