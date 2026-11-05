using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Runtime.Machine;
using MotionControl.Framework.Wpf.Monitoring;

namespace MotionControl.OperatorPages.Checks
{
    /// <summary>通过可控异步轴验证只读监控的迟到响应和逐轴失败隔离。</summary>
    internal static class AxisMonitorAsyncChecks
    {
        /// <summary>在已有应用与中文资源的 STA 调度线程执行；不接触设备。</summary>
        internal static async Task RunAsync()
        {
            var first = new SampleAxis("First");
            var second = new SampleAxis("Second");
            var card = new SampleCard(first, second);
            Guid? session = Guid.NewGuid();
            var view = new AxisMonitorView(true, new MotionCardGroup(new[] { card }), null, () => session);
            var window = new Window { Content = view, Width = 1000, Height = 600, ShowActivated = false,
                ShowInTaskbar = false, Opacity = 0, WindowStartupLocation = WindowStartupLocation.Manual, Left = -32000, Top = -32000 };
            try
            {
                window.Show();
                StopTimer(view);
                first.Pending.SetResult(Status(12));
                await Task.Yield();
                await Task.Yield();
                second.Pending.SetException(new InvalidOperationException("isolated failure"));
                await DrainAsync(view);
                IList rows = (IList)typeof(AxisMonitorView).GetField("_axisRows", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(view);
                Assert(Get(rows[0], "LastStatus") != null, "单轴失败清除了其他轴结果。");
                Assert((bool)Get(rows[1], "HasReadError"), "单轴失败没有标为未知。");
                Assert(((TextBlock)view.FindName("MonitorStatusText")).Text.Contains("设备采样时间"), "时间说明缺少设备采样能力边界。");
                var grid = (DataGrid)view.FindName("AxisStatusGrid");
                var search = (TextBox)view.FindName("AxisSearchBox");
                var attention = (CheckBox)view.FindName("AttentionOnlyBox");
                grid.SelectedItem = rows[0];
                search.Text = "SECOND"; attention.IsChecked = true;
                Assert(grid.ItemsSource is System.ComponentModel.ICollectionView && grid.Items.Count == 1 && ReferenceEquals(grid.Items[0], rows[1]),
                    "失败轴筛选不一致：可见=" + grid.Items.Count + "，查询=" + search.Text + "，失败轴=" + Get(rows[1], "LogicalAxisName")
                    + "，读取失败=" + Get(rows[1], "HasReadError") + "，匹配提示=" + ((TextBlock)view.FindName("AxisMatchText")).Text);
                int hiddenReads = first.ReadCount;
                await Refresh(view);
                Assert(first.ReadCount > hiddenReads && rows.Count == 2, "只读筛选改变全量采集集合。");
                search.Text = "no-matching-axis";
                Assert(grid.Items.Count == 0 && ((TextBlock)view.FindName("AxisMatchText")).Text.Contains("匹配 0 / 已映射 2"), "无匹配被误报为无映射。");
                ((Button)view.FindName("ClearAxisFilterButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert(grid.Items.Count == 2 && ReferenceEquals(grid.SelectedItem, rows[0]) && (bool)Get(rows[1], "HasReadError"), "清除筛选丢选择或清除了轴错误。");
                await CheckStopFeedbackAsync(view, first, rows[0]);
                await CheckLimitFilterAsync(view, first, rows[0]);

                first.Pending = new TaskCompletionSource<AxisStatus>();
                Task refresh = Refresh(view);
                card.Connected = false;
                int stalledReads = first.ReadCount;
                await Refresh(view);
                Assert(!refresh.IsCompleted && first.ReadCount == stalledReads && Get(rows[0], "LastStatus") == null,
                    "驱动仍悬起时页面没有清除已断线的轴反馈，或重复读取。");
                first.Pending.SetResult(Status(99));
                await refresh;
                Assert(Get(rows[0], "LastStatus") == null && Get(rows[1], "LastStatus") == null, "断连迟到结果覆盖了未知。");

                card.Connected = true;
                first.Pending = new TaskCompletionSource<AxisStatus>();
                int secondReads = second.ReadCount;
                refresh = Refresh(view);
                card.Connected = false;
                first.Pending.SetException(new InvalidOperationException("disconnected during read"));
                await refresh;
                Assert(!(bool)Get(rows[0], "IsConnected") && !(bool)Get(rows[1], "IsConnected")
                    && second.ReadCount == secondReads, "断连异常后仍显示已连接或继续读取后续轴。");

                card.Connected = true;
                first.Pending = new TaskCompletionSource<AxisStatus>();
                refresh = Refresh(view);
                secondReads = second.ReadCount;
                card.Connected = false;
                session = null;
                card.Connected = true;
                session = Guid.NewGuid();
                await Refresh(view);
                Assert(!refresh.IsCompleted && Get(rows[0], "LastStatus") == null && second.ReadCount == secondReads,
                    "新会话没有在旧驱动返回前使显示缓存失效。");
                first.Pending.SetResult(Status(77));
                await refresh;
                Assert(Get(rows[0], "LastStatus") == null && Get(rows[1], "LastStatus") == null
                    && second.ReadCount == secondReads, "同一轴重连后旧位置污染新连接或继续读取后续轴。");

                first.Pending = new TaskCompletionSource<AxisStatus>();
                refresh = Refresh(view);
                window.Content = null;
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Assert(!view.IsLoaded, "测试尚未收到 WPF 页面卸载事件。");
                first.Pending.SetResult(Status(88));
                await refresh;
                Assert(Get(rows[0], "LastStatus") == null, "离页迟到结果覆盖了未知。");
            }
            finally { window.Close(); }
        }
        /// <summary>经正常监控读取核对速度容差、冲突反馈及非有限数值的显示。</summary>
        private static async Task CheckStopFeedbackAsync(AxisMonitorView view, SampleAxis axis, object row)
        {
            async Task Check(double velocity, bool moving, string expected)
            {
                axis.Pending = new TaskCompletionSource<AxisStatus>();
                axis.Pending.SetResult(new AxisStatus(12, velocity, true, moving, true, false, false, null, null, false, null, false));
                await Refresh(view);
                Assert((string)Get(row, "MotionStateText") == expected, "停稳判定错误，速度：" + velocity + "，运动标志：" + moving);
                if (double.IsNaN(velocity) || double.IsInfinity(velocity))
                    Assert((string)Get(row, "VelocityText") == "未知" && (string)Get(row, "ReadStateText") == "部分反馈待确认",
                        "非有限速度仍显示为有效读数。");
            }
            await Check(0, false, "已确认停止");
            await Check(0.001, false, "已确认停止");
            await Check(-0.001, false, "已确认停止");
            await Check(0.00101, false, "停稳未知");
            await Check(-0.00101, false, "停稳未知");
            await Check(double.NaN, false, "停稳未知");
            await Check(double.PositiveInfinity, false, "停稳未知");
            await Check(double.NegativeInfinity, false, "停稳未知");
            await Check(0, true, "运动中");

            axis.Pending = new TaskCompletionSource<AxisStatus>();
            axis.Pending.SetResult(new AxisStatus(double.NaN, 0, true, false, true, false, false, null, null, false,
                null, false, double.PositiveInfinity));
            await Refresh(view);
            string position = (string)Get(row, "PositionText");
            Assert(position.Split(new[] { "未知" }, StringSplitOptions.None).Length == 3
                && (string)Get(row, "ReadStateText") == "部分反馈待确认", "无效反馈位置或指令位置仍显示为有效。");
        }

        /// <summary>四种限位触发或无法确认都保留；单纯原点未配置不冒充轴故障。</summary>
        private static async Task CheckLimitFilterAsync(AxisMonitorView view, SampleAxis axis, object row)
        {
            var attention = (CheckBox)view.FindName("AttentionOnlyBox");
            var grid = (DataGrid)view.FindName("AxisStatusGrid");
            attention.IsChecked = true;
            foreach (int index in new[] { 0, 1, 2, 3 })
                foreach (bool? state in new bool?[] { true, null })
                {
                    bool?[] limits = { false, false, false, false }; limits[index] = state;
                    axis.Pending = new TaskCompletionSource<AxisStatus>();
                    axis.Pending.SetResult(new AxisStatus(0, 0, true, false, true, limits[0], limits[1], limits[2], limits[3], false, null, false, 0, null));
                    await Refresh(view);
                    Assert(grid.Items.Contains(row), "异常/未知筛选隐藏触发或未确认限位：" + index);
                }
            axis.Pending = new TaskCompletionSource<AxisStatus>();
            axis.Pending.SetResult(new AxisStatus(0, 0, true, false, true, false, false, false, false, false, null, false, 0, null));
            await Refresh(view);
            Assert(!grid.Items.Contains(row) && ((string)Get(row, "OriginInputStateText")).Contains("未配置"), "仅原点未配置被误当成轴故障。");
            axis.Pending = new TaskCompletionSource<AxisStatus>();
            axis.Pending.SetResult(new AxisStatus(0, 0, true, false, true, false, false, false, false, true, "真实轴报警", false, 0, null));
            await Refresh(view);
            Assert(grid.Items.Contains(row), "真实轴报警被异常筛选隐藏。");
            ((Button)view.FindName("ClearAxisFilterButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }

        /// <summary>等待当前测试读取轮次结束，避免依赖固定延时。</summary>
        private static async Task DrainAsync(AxisMonitorView view)
        {
            for (int i = 0; i < 100; i++)
            {
                if (!(bool)typeof(AxisMonitorView).GetField("_refreshInProgress", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(view)) return;
                await Task.Yield();
            }
            throw new InvalidOperationException("测试读取未结束。");
        }
        /// <summary>停止真实页面定时器，仅由检查驱动已有读取方法。</summary>
        private static void StopTimer(AxisMonitorView view)
        { ((System.Windows.Threading.DispatcherTimer)typeof(AxisMonitorView).GetField("_refreshTimer", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(view)).Stop(); }
        /// <summary>调用页面原有读取路径，不引入额外采集实现。</summary>
        private static Task Refresh(AxisMonitorView view)
        { return (Task)typeof(AxisMonitorView).GetMethod("RefreshAxisStatusesAsync", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(view, null); }
        /// <summary>读取内部行的可观察状态。</summary>
        private static object Get(object row, string name) { return row.GetType().GetProperty(name).GetValue(row); }
        /// <summary>生成明确非零的测试反馈，帮助识别迟到覆盖。</summary>
        private static AxisStatus Status(double position) { return new AxisStatus(position, 0, true, false, true, false, false, null, null, false, null, false); }
        /// <summary>检查失败时抛出可定位异常。</summary>
        private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

        /// <summary>仅提供已有连接状态的卡替身，生命周期调用一律失败。</summary>
        private sealed class SampleCard : IMotionCard
        {
            /// <summary>由检查直接切换的连接反馈。</summary>
            internal bool Connected = true;
            /// <summary>创建纯内存卡。</summary>
            internal SampleCard(params IAxis[] axes) { Axes = axes; }
            /// <summary>稳定卡标识。</summary>
            public string ControllerId { get { return "AsyncCheck"; } }
            /// <summary>返回测试连接反馈。</summary>
            public bool IsConnected { get { return Connected; } }
            /// <summary>测试逻辑轴集合。</summary>
            public IReadOnlyCollection<IAxis> Axes { get; private set; }
            /// <summary>禁止页面连接。</summary>
            public Task ConnectAsync(CancellationToken token) { throw new InvalidOperationException("禁止连接"); }
            /// <summary>禁止页面断开。</summary>
            public Task DisconnectAsync(CancellationToken token) { throw new InvalidOperationException("禁止断开"); }
        }
        /// <summary>只允许读取的可控异步轴；全部动作均立即失败。</summary>
        internal sealed class SampleAxis : IAxis
        {
            /// <summary>由检查完成或失败的读取结果。</summary>
            internal TaskCompletionSource<AxisStatus> Pending = new TaskCompletionSource<AxisStatus>();
            /// <summary>记录只读请求次数，检查断连后不再访问后续轴。</summary>
            internal int ReadCount;
            /// <summary>指定稳定逻辑名称。</summary>
            internal SampleAxis(string name) { Name = name; }
            /// <summary>逻辑名称。</summary>
            public string Name { get; private set; }
            /// <summary>返回可控异步反馈。</summary>
            public Task<AxisStatus> ReadStatusAsync(CancellationToken token) { ReadCount++; return Pending.Task; }
            /// <summary>禁止使能。</summary>
            public Task EnableServoAsync(CancellationToken token) { throw new InvalidOperationException("禁止动作"); }
            /// <summary>禁止关伺服。</summary>
            public Task DisableServoAsync(CancellationToken token) { throw new InvalidOperationException("禁止动作"); }
            /// <summary>禁止回零。</summary>
            public Task HomeAsync(CancellationToken token) { throw new InvalidOperationException("禁止动作"); }
            /// <summary>禁止绝对运动。</summary>
            public Task MoveAbsoluteAsync(double value, AxisMotionProfile profile, CancellationToken token) { throw new InvalidOperationException("禁止动作"); }
            /// <summary>禁止相对运动。</summary>
            public Task MoveRelativeAsync(double value, AxisMotionProfile profile, CancellationToken token) { throw new InvalidOperationException("禁止动作"); }
            /// <summary>禁止点动。</summary>
            public Task StartJogAsync(MotionDirection direction, AxisMotionProfile profile, CancellationToken token) { throw new InvalidOperationException("禁止动作"); }
            /// <summary>禁止停止动作。</summary>
            public Task StopAsync(AxisStopMode mode, CancellationToken token) { throw new InvalidOperationException("禁止动作"); }
            /// <summary>禁止复位。</summary>
            public Task ResetAlarmAsync(CancellationToken token) { throw new InvalidOperationException("禁止动作"); }
        }
    }
}
