using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.IO;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Contracts.Safety;
using MotionControl.Framework.Runtime.Alarms;
using MotionControl.Framework.Runtime.Machine;
using MotionControl.Framework.Wpf.ManualDebug;

namespace MotionControl.OperatorPages.Checks
{
    /// <summary>检查手动输出切换始终依据最新物理电平，不连接控制器。</summary>
    internal static class IoMonitorToggleChecks
    {
        /// <summary>覆盖显示过期、高低有效与读取失败，确认写入和读回顺序。</summary>
        internal static void Run()
        {
            var card = new FakeIoCard();
            var runtime = new MachineRuntime(new MotionCardGroup(new IMotionCard[] { card }),
                new IStationFlow[0], new MachineAlarmService(), new SafeInput());
            runtime.ConnectAsync(CancellationToken.None).GetAwaiter().GetResult();
            try
            {
                var view = new IoMonitorView(true, runtime, null, card, null, null);
                object highActive = CreateRow(true, false);
                card.PhysicalHigh = true;
                Click(view, highActive);
                Require(card.WriteCount == 1 && !card.PhysicalHigh, "输出已被外部触发时，没有依据真实状态解除。");
                Require(card.ReadCount == 2, "切换必须先读取物理输出，再对写入结果回读。");

                Click(view, highActive);
                Require(card.WriteCount == 2 && card.PhysicalHigh, "第二次点击没有重新触发输出。");

                object lowActive = CreateRow(false, true);
                card.PhysicalHigh = false;
                Click(view, lowActive);
                Require(card.WriteCount == 3 && card.PhysicalHigh, "低电平有效的输出没有按实际状态解除。");

                card.FailRead = true;
                Click(view, lowActive);
                Require(card.WriteCount == 3, "读取当前输出失败时不允许写入。");
                Require(lowActive.GetType().GetProperty("PhysicalHigh").GetValue(lowActive) == null,
                    "读取失败后不应保留旧电平作为可操作状态。");

                // 即使输出读取失败，点击的灯也要选择对应反馈，不能继续显示其他输出的输入。
                highActive.GetType().GetMethod("SetFeedbackNames").Invoke(highActive,
                    new object[] { new[] { "夹具到位" }, new Func<string, string>(name => "DI4"), "等待读取" });
                Click(view, highActive);
                var details = (ItemsControl)view.FindName("FeedbackDetails");
                var empty = (TextBlock)view.FindName("FeedbackEmptyText");
                Require(details.Items.Count == 1 && empty.Visibility == Visibility.Collapsed,
                    "点击有关联的输出灯后，未显示该输出的反馈输入。");
                Click(view, lowActive);
                Require(details.Items.Count == 0 && empty.Visibility == Visibility.Visible,
                    "点击无关联的输出灯后，仍显示上一个输出的反馈。");
                Require(card.WriteCount == 3, "反馈选择不应在输出读取失败时触发写入。");
            }
            finally { runtime.DisconnectAsync(CancellationToken.None).GetAwaiter().GetResult(); }
        }

        /// <summary>创建一个已显示指定旧电平的 DO 行，以重现外部变化后页面尚未刷新的情况。</summary>
        private static object CreateRow(bool activeHigh, bool displayedPhysicalHigh)
        {
            Type type = typeof(IoMonitorView).Assembly.GetType("MotionControl.Framework.Wpf.ManualDebug.IoMonitorChannel", true);
            object row = Activator.CreateInstance(type, 0, "测试输出", activeHigh, true, true);
            type.GetMethod("SetReadSuccess").Invoke(row,
                new object[] { displayedPhysicalHigh, "已触发", "未触发", "高电平", "低电平" });
            return row;
        }

        /// <summary>触发监控页的真实点击处理；替身方法均同步完成，不操作桌面窗口。</summary>
        private static void Click(IoMonitorView view, object row)
        {
            MethodInfo handler = typeof(IoMonitorView).GetMethod("OutputIndicator_Click", BindingFlags.Instance | BindingFlags.NonPublic);
            Require(handler != null, "未找到 DO 点击处理。");
            handler.Invoke(view, new object[] { new Button { Tag = row }, new RoutedEventArgs() });
        }

        /// <summary>将检查失败转成带上下文的异常。</summary>
        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        /// <summary>只在内存保存电平和调用计数的运动卡替身。</summary>
        private sealed class FakeIoCard : IMotionCard, IPhysicalDigitalIoMonitor
        {
            /// <summary>替身卡名称。</summary>
            public string ControllerId { get { return "ToggleCheck"; } }
            /// <summary>测试连接标记。</summary>
            public bool IsConnected { get; private set; }
            /// <summary>本检查不使用轴。</summary>
            public IReadOnlyCollection<IAxis> Axes { get { return new IAxis[0]; } }
            /// <summary>替身中的物理输出电平，可用于模拟外部改变。</summary>
            public bool PhysicalHigh { get; set; }
            /// <summary>是否模拟物理输出读取失败。</summary>
            public bool FailRead { get; set; }
            /// <summary>累计物理输出读取次数。</summary>
            public int ReadCount { get; private set; }
            /// <summary>累计物理输出写入次数。</summary>
            public int WriteCount { get; private set; }
            /// <summary>建立内存连接。</summary>
            public Task ConnectAsync(CancellationToken token) { IsConnected = true; return Task.CompletedTask; }
            /// <summary>关闭内存连接。</summary>
            public Task DisconnectAsync(CancellationToken token) { IsConnected = false; return Task.CompletedTask; }
            /// <summary>本检查不读取输入。</summary>
            public Task<bool> ReadDigitalInputChannelAsync(int channel, CancellationToken token) { throw new InvalidOperationException("不应读取输入。"); }
            /// <summary>返回当前物理输出或明确报错。</summary>
            public Task<bool> ReadDigitalOutputChannelAsync(int channel, CancellationToken token)
            {
                ReadCount++;
                return FailRead ? Task.FromException<bool>(new InvalidOperationException("输出读取失败")) : Task.FromResult(PhysicalHigh);
            }
            /// <summary>记录目标电平，供随后读回。</summary>
            public Task WriteDigitalOutputChannelAsync(int channel, bool physicalHigh, CancellationToken token)
            {
                WriteCount++;
                PhysicalHigh = physicalHigh;
                return Task.CompletedTask;
            }
        }

        /// <summary>提供正常的测试急停状态，不读取实际硬件。</summary>
        private sealed class SafeInput : IMachineSafetyStatus
        {
            /// <summary>测试急停始终未触发。</summary>
            public Task<bool> ReadEmergencyStopActiveAsync(CancellationToken token) { return Task.FromResult(false); }
        }
    }
}
