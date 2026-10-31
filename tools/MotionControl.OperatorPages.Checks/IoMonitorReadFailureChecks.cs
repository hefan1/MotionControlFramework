using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MotionControl.Framework.Contracts.IO;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Wpf.ManualDebug;
using MotionControl.Host.Wpf;

namespace MotionControl.OperatorPages.Checks
{
    /// <summary>验证 IO 监控的通道范围与实时读取故障不会混淆。</summary>
    internal static class IoMonitorReadFailureChecks
    {
        /// <summary>验证批量失败后的逐路回退、部分故障、恢复和切换标签。</summary>
        internal static void Run()
        {
            var fake = new ReadOnlyIo();
            var view = new IoMonitorView(true, null, fake, fake, null, null);
            int outputReads = 0;
            view.VisibleReadStatusChanged += (input, failed) => { if (!input && !failed) outputReads++; };
            var host = new MainWindow();
            host.SetMotionCardConnectionStatus(true);
            host.SetIoMonitorPage(view);
            var footer = (TextBlock)host.FindName("MotionCardConnectionStatus");
            Require(footer.Text.Contains("会话已建立"), "未采样时没有说明连接只是会话状态。");
            var window = new Window { Content = view, Width = 900, Height = 600,
                Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual, WindowStyle = WindowStyle.None };
            try
            {
                window.Show();
                var status = (TextBlock)view.FindName("MonitorStatusText");
                PumpUntil(() => status.Text.Contains("读取失败 4/4"), "全部 DI 读取失败没有汇总。");
                Require(footer.Text.Contains("DI最近可见通道读取异常"), "DI 读取失败时底栏未提示最近采样异常。");
                Require(status.Text.Contains("已探测通道范围") && status.Text.Contains("20009"), "没有显示范围与具体失败原因。");
                fake.FailureMode = 1;
                PumpUntil(() => status.Text.Contains("读取失败 1/4"), "部分 DI 读取失败没有更新汇总。");
                fake.FailureMode = 0;
                PumpUntil(() => status.Text.Contains("已探测通道范围") && !status.Text.Contains("读取失败"), "读取恢复后故障汇总没有清除。");
                Require(!footer.Text.Contains("读取异常"), "相同范围后续读取成功后底栏未更新。");
                fake.FailureMode = 1;
                PumpUntil(() => status.Text.Contains("读取失败 1/4"), "再次出现读取失败时未更新。");
                ((TabControl)view.FindName("MonitorTabs")).SelectedIndex = 1;
                PumpUntil(() => outputReads > 0 && !status.Text.Contains("读取失败"), "切换 DO 后未完成独立读取。");
                Require(footer.Text.Contains("DI最近可见通道读取异常"), "DO 成功时不应掩盖上次 DI 读取失败。");
                fake.FailureMode = 0;
                ((TabControl)view.FindName("MonitorTabs")).SelectedIndex = 0;
                PumpUntil(() => !footer.Text.Contains("读取异常"), "DI 再次成功采样后底栏没有清除 DI 提示。");
                host.SetIoMonitorPage(null);
                Require(!footer.Text.Contains("读取异常"), "更换监控页后仍引用旧页采样结果。");
                Require(fake.BatchReads > 0 && fake.SingleReads > 0, "没有覆盖批量失败后的逐路读取。");
            }
            finally { window.Close(); host.Close(); }
        }

        /// <summary>让 WPF 调度轮询推进，超过限定时间则报告具体失败。</summary>
        private static void PumpUntil(Func<bool> condition, string message)
        {
            var watch = Stopwatch.StartNew();
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (sender, args) => { if (condition() || watch.ElapsedMilliseconds > 4000) frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
            timer.Stop();
            Require(condition(), message);
        }

        /// <summary>失败时保留测试原因，避免把空结果当作通过。</summary>
        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        /// <summary>只读测试卡；可构造批量错误、逐路错误和恢复，不连接设备。</summary>
        private sealed class ReadOnlyIo : IMotionCardIoCapabilityProvider, IPhysicalDigitalIoMonitor, IPhysicalDigitalIoBatchMonitor
        {
            /// <summary>2 表示全部 DI 失败，1 表示 DI1 失败，0 表示全部成功。</summary>
            public int FailureMode { get; set; } = 2;
            /// <summary>累计批量读取次数。</summary>
            public int BatchReads { get; private set; }
            /// <summary>累计逐路读取次数。</summary>
            public int SingleReads { get; private set; }
            /// <summary>返回四路输入和输出的模拟能力。</summary>
            public Task<MotionCardIoCapabilities> ReadIoCapabilitiesAsync(CancellationToken token)
            {
                return Task.FromResult(new MotionCardIoCapabilities {
                    DigitalInputChannelRange = new MotionCardIoChannelRange { MappableChannelCount = 4 },
                    DigitalOutputChannelRange = new MotionCardIoChannelRange { MappableChannelCount = 4 } });
            }
            /// <summary>刷新能力与首次读取使用相同的固定范围。</summary>
            public Task<MotionCardIoCapabilities> RefreshIoCapabilitiesAsync(CancellationToken token) { return ReadIoCapabilitiesAsync(token); }
            /// <summary>逐路返回输入状态或可定位的错误。</summary>
            public Task<bool> ReadDigitalInputChannelAsync(int channel, CancellationToken token)
            {
                SingleReads++;
                return FailureMode == 2 || (FailureMode == 1 && channel == 1)
                    ? Task.FromException<bool>(new InvalidOperationException("SDK 错误码：20009")) : Task.FromResult(false);
            }
            /// <summary>输出始终只读成功。</summary>
            public Task<bool> ReadDigitalOutputChannelAsync(int channel, CancellationToken token) { return Task.FromResult(false); }
            /// <summary>测试页不允许执行输出写入。</summary>
            public Task WriteDigitalOutputChannelAsync(int channel, bool high, CancellationToken token) { throw new InvalidOperationException("禁止写入输出。"); }
            /// <summary>模拟批量输入失败，供页面回退到逐路读取。</summary>
            public Task<IReadOnlyDictionary<int, bool>> ReadDigitalInputChannelsAsync(IReadOnlyCollection<int> channels, CancellationToken token)
            {
                BatchReads++;
                if (FailureMode != 0) return Task.FromException<IReadOnlyDictionary<int, bool>>(new InvalidOperationException("批量读取失败"));
                return Task.FromResult<IReadOnlyDictionary<int, bool>>(channels.ToDictionary(channel => channel, channel => false));
            }
            /// <summary>批量输出只读成功。</summary>
            public Task<IReadOnlyDictionary<int, bool>> ReadDigitalOutputChannelsAsync(IReadOnlyCollection<int> channels, CancellationToken token)
            {
                return Task.FromResult<IReadOnlyDictionary<int, bool>>(channels.ToDictionary(channel => channel, channel => false));
            }
        }
    }
}
