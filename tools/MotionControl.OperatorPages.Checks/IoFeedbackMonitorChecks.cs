using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MotionControl.Framework.Contracts.Configuration;
using MotionControl.Framework.Contracts.IO;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.Framework.Wpf.ManualDebug;

namespace MotionControl.OperatorPages.Checks
{
    /// <summary>验证输出关联输入的实际采样、单点失败和离页状态。</summary>
    internal static class IoFeedbackMonitorChecks
    {
        /// <summary>在隔离控件中检查多反馈采样，不连接控制器。</summary>
        internal static void Run()
        {
            var physical = new FakePhysicalIo();
            var logical = new FakeLogicalIo();
            var configuration = new IoFeedbackAssociationConfiguration
            {
                Associations = new List<IoFeedbackAssociation> {
                    new IoFeedbackAssociation { OutputName = "气缸打开", InputNames = new List<string> { "打开到位", "关闭到位" } },
                    new IoFeedbackAssociation { OutputName = "夹具夹紧", InputNames = new List<string> { "打开到位" } }
                }
            };
            var outputs = new[] {
                new DigitalIoHardwareDefinition { Name = "气缸打开", Channel = 0, ActiveHigh = true },
                new DigitalIoHardwareDefinition { Name = "夹具夹紧", Channel = 1, ActiveHigh = true }
            };
            var inputs = new[] {
                new DigitalIoHardwareDefinition { Name = "打开到位", ControllerId = "CardA", Channel = 1 },
                new DigitalIoHardwareDefinition { Name = "关闭到位", ControllerId = "CardB", Channel = 1 }
            };
            var view = new IoMonitorView(true, null, physical, physical, inputs, outputs, logical, configuration);
            var window = new Window { Content = view, Width = 1000, Height = 600, Left = -20000, Top = -20000,
                ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
            try
            {
                window.Show();
                ((TabControl)view.FindName("MonitorTabs")).SelectedIndex = 1;
                window.UpdateLayout();
                PumpUntil(() => Summary(view).Contains("打开到位：已触发") && Summary(view).Contains("关闭到位：读取失败"),
                    "未分别显示成功的输入和失败的输入。");
                window.UpdateLayout();
                Require(!FindButtons(view).Any(button => Equals(button.Content, "全部") && button.IsVisible), "输出行不应堆叠反馈按钮。");
                var feedbackList = (ItemsControl)view.FindName("FeedbackDetails");
                Require(feedbackList.Items.Count == 2, "右侧列表未显示全部反馈输入。");
                var firstFeedback = feedbackList.Items[0];
                var secondFeedback = feedbackList.Items[1];
                Require((string)firstFeedback.GetType().GetProperty("Address").GetValue(firstFeedback) == "DI1 / CardA"
                    && (string)secondFeedback.GetType().GetProperty("Address").GetValue(secondFeedback) == "DI1 / CardB",
                    "同号跨卡输入没有显示各自的控制卡与 DI 通道。");
                lock (logical.ReadNames)
                    Require(logical.ReadNames.Contains("打开到位") && logical.ReadNames.Contains("关闭到位"), "没有按逻辑名称读取两个反馈。");
                Require(physical.OutputWrites == 0, "反馈监控不应写入输出。");
                logical.FailSecond = false;
                PumpUntil(() => Summary(view).Contains("关闭到位：未触发"), "输入恢复后未刷新状态。");
                logical.BlockFirst = true;
                PumpUntil(() => logical.FirstBlocked, "没有开始模拟慢速反馈读取。");
                int readsBeforeTimeout = physical.OutputReads;
                PumpUntil(() => Summary(view).Contains("打开到位：读取超时") && physical.OutputReads > readsBeforeTimeout,
                    "慢反馈没有显示超时，或阻塞了输出状态刷新。");
                logical.ReleaseFirst();
                PumpUntil(() => Summary(view).Contains("打开到位：已触发"), "慢反馈恢复后未重新显示实际状态。");
                var outputList = (ListBox)view.FindName("DigitalOutputList");
                var column = outputList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
                Require(column != null && Math.Abs(column.ActualWidth - 260) < 2, "输出行应保持原有紧凑宽度。");
                ((TabControl)view.FindName("MonitorTabs")).SelectedIndex = 0;
                Require(Summary(view).Contains("等待读取"), "切离输出页后仍显示旧输入状态。");
            }
            finally { logical.ReleaseFirst(); window.Close(); }
        }

        /// <summary>查找已生成的按钮，确保真实输出行数据模板被加载。</summary>
        private static IEnumerable<Button> FindButtons(DependencyObject parent)
        {
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, index);
                if (child is Button button) yield return button;
                foreach (Button nested in FindButtons(child)) yield return nested;
            }
        }

        /// <summary>通过内部行绑定检查操作员可见的完整反馈文本。</summary>
        private static string Summary(IoMonitorView view)
        {
            var field = typeof(IoMonitorView).GetField("_outputChannels", BindingFlags.Instance | BindingFlags.NonPublic);
            var rows = (IEnumerable)field.GetValue(view);
            object first = rows.Cast<object>().FirstOrDefault();
            return first == null ? string.Empty : (string)first.GetType().GetProperty("FeedbackSummary").GetValue(first);
        }

        /// <summary>推动 WPF 调度，限制最长等待时间。</summary>
        private static void PumpUntil(Func<bool> condition, string message)
        {
            var watch = Stopwatch.StartNew();
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (sender, args) => { if (condition() || watch.ElapsedMilliseconds > 5000) frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
            timer.Stop();
            Require(condition(), message);
        }

        /// <summary>检查失败时保留具体原因。</summary>
        private static void Require(bool result, string message) { if (!result) throw new InvalidOperationException(message); }

        /// <summary>四路只读输出能力替身，任何写入都会被记录。</summary>
        private sealed class FakePhysicalIo : IMotionCardIoCapabilityProvider, IPhysicalDigitalIoMonitor
        {
            /// <summary>累计写入次数。</summary>
            public int OutputWrites { get; private set; }
            /// <summary>累计物理输出读取次数，用于确认慢反馈不阻塞刷新。</summary>
            public int OutputReads;
            /// <summary>报告四路输入和输出。</summary>
            public Task<MotionCardIoCapabilities> ReadIoCapabilitiesAsync(CancellationToken token) { return Task.FromResult(new MotionCardIoCapabilities {
                DigitalInputChannelRange = new MotionCardIoChannelRange { MappableChannelCount = 4 },
                DigitalOutputChannelRange = new MotionCardIoChannelRange { MappableChannelCount = 4 } }); }
            /// <summary>刷新时返回相同的能力。</summary>
            public Task<MotionCardIoCapabilities> RefreshIoCapabilitiesAsync(CancellationToken token) { return ReadIoCapabilitiesAsync(token); }
            /// <summary>返回测试输入电平。</summary>
            public Task<bool> ReadDigitalInputChannelAsync(int channel, CancellationToken token) { return Task.FromResult(false); }
            /// <summary>返回测试输出电平。</summary>
            public Task<bool> ReadDigitalOutputChannelAsync(int channel, CancellationToken token) { OutputReads++; return Task.FromResult(false); }
            /// <summary>记录任何意外写入。</summary>
            public Task WriteDigitalOutputChannelAsync(int channel, bool high, CancellationToken token) { OutputWrites++; return Task.CompletedTask; }
        }

        /// <summary>按逻辑名称返回不同控制卡上同号输入的独立状态。</summary>
        private sealed class FakeLogicalIo : ILogicalIo
        {
            /// <summary>第二个输入是否返回错误。</summary>
            public bool FailSecond { get; set; } = true;
            /// <summary>保持第一个输入的底层读取未完成。</summary>
            public volatile bool BlockFirst;
            /// <summary>表明第一个输入已进入阻塞。</summary>
            public volatile bool FirstBlocked;
            /// <summary>等待测试释放的反馈读取。</summary>
            private readonly TaskCompletionSource<bool> _firstRead = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            /// <summary>实际读取过的逻辑名称。</summary>
            public HashSet<string> ReadNames { get; } = new HashSet<string>();
            /// <summary>第一个输入有效；第二个可失败后恢复为无效。</summary>
            public Task<bool> ReadInputAsync(string name, CancellationToken token)
            {
                lock (ReadNames) ReadNames.Add(name);
                if (name == "打开到位" && BlockFirst) { FirstBlocked = true; return _firstRead.Task; }
                if (name == "关闭到位" && FailSecond) return Task.FromException<bool>(new InvalidOperationException("连接失败"));
                return Task.FromResult(name == "打开到位");
            }
            /// <summary>释放阻塞的模拟读取，使后续轮询可以恢复。</summary>
            public void ReleaseFirst() { BlockFirst = false; _firstRead.TrySetResult(true); }
            /// <summary>反馈读取不应调用输出读取。</summary>
            public Task<bool> ReadOutputAsync(string name, CancellationToken token) { throw new InvalidOperationException("不应读取逻辑输出。"); }
            /// <summary>反馈读取不应写入输出。</summary>
            public Task WriteOutputAsync(string name, bool value, CancellationToken token) { throw new InvalidOperationException("不应写入逻辑输出。"); }
        }
    }
}
