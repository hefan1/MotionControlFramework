using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.Preparation;
using MotionControl.Framework.Runtime.Alarms;
using MotionControl.Framework.Runtime.Machine;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using MotionControl.Host.Wpf.Operation;

namespace MotionControl.OperatorPages.Checks
{
    /// <summary>验证部分启动摘要不会把 Host 空原因或未连接伪称为允许启动。</summary>
    internal static class StartConditionsObservationChecks
    {
        /// <summary>使用无设备页面检查双语、空上下文、缓存失效与只读刷新。</summary>
        internal static void Run()
        {
            int loads = 0, navigations = 0;
            var page = new EquipmentPreparationView(() => { loads++; return Task.CompletedTask; }, target => navigations++);
            page.SetLanguage("zh-CN");
            page.SetRuntime(null);
            ((Button)page.FindName("StartConditionsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            page.SetAutomaticStartReadiness(null, false);
            var message = (TextBlock)page.FindName("StartConditionsMessage");
            var list = (ListBox)page.FindName("StartConditionsList");
            Assert(message.Text.Contains("覆盖不完整") && message.Text.Contains("设备采样时间"), "缺少覆盖与时间边界。");
            Assert(list.Items.Count == 1 && list.Items[0].ToString().Contains("未知"), "空运行时应仅显示未知连接。");
            page.SetAutomaticStartReadiness("obsolete blocker", false);
            page.SetRuntime(null);
            ((Button)page.FindName("StartConditionsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(!list.Items.Cast<object>().Any(item => item.ToString().Contains("obsolete blocker")), "换上下文保留旧 Host 原因。");
            ((Button)page.FindName("RefreshStartConditionsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            object stableRows = list.ItemsSource;
            object stableRow = list.Items[0];
            list.SelectedItem = stableRow;
            ((Button)page.FindName("RefreshStartConditionsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(ReferenceEquals(stableRows, list.ItemsSource) && ReferenceEquals(stableRow, list.SelectedItem), "刷新重建集合或丢失选择。");
            Assert(!list.Items[0].ToString().Contains("runtime.connection"), "操作员看到内部条件键。");
            Assert(loads == 0 && navigations == 0, "摘要刷新装载配置或改变导航。");
            var card = new ObservedCard();
            var runtime = new MachineRuntime(new MotionCardGroup(new[] { card }), new IStationFlow[0], new MachineAlarmService(), new GuardSafety());
            runtime.ConfigurePreparations(new[] { new ObservedPreparation() });
            // 仅布置测试内存状态；绝不调用 Runtime.ConnectAsync 或设备生命周期。
            typeof(MachineRuntime).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(runtime, MachineRunState.Ready);
            page.SetRuntime(runtime);
            page.SetAutomaticStartReadiness(null, false);
            string all = string.Join("\n", list.Items.Cast<object>());
            Assert(all.Contains("当前读写未核实"), "连接会话被误当当前通信有效。");
            Assert(all.Contains("未报告恢复阻断（仅此属性）") && message.Text.Contains("不能判断整体可启动"), "恢复单项无阻断被解释为整机可启动。");
            Assert(all.Contains("Host 未提供已完成结论") && all.Contains("未知"), "Host 空原因被解释为已完成或通过。");
            var station = list.Items.Cast<object>().Single(item => (string)item.GetType().GetProperty("Key").GetValue(item) == "preparation/StableStation42");
            Assert((string)station.GetType().GetProperty("ObjectId").GetValue(station) == "StableStation42" && station.ToString().Contains("不满足"), "准备状态未保留真实对象ID或未准备结论。");
            card.Connected = false;
            ((Button)page.FindName("RefreshStartConditionsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(list.Items.Count == 1 && list.Items[0].ToString().Contains("未知"), "断开后保留旧准备或恢复结论。");
            page.SetRuntime(null);
            Assert(list.Items.Count == 1 && !list.Items[0].ToString().Contains("StableStation42"), "更换Runtime未清旧对象。");
            Assert(card.LifecycleCalls == 0 && loads == 0 && navigations == 0, "内存刷新触发动作或配置装载。");
            page.SetLanguage("en-US");
            Assert(message.Text.Contains("incomplete coverage") && message.Text.Contains("UI observation time"), "英文缺少准确边界。");
            Assert(list.Items[0].ToString().Contains("Unknown"), "未知态未切换英文。");
        }
        /// <summary>仅报告连接缓存；生命周期若被意外调用立即失败。</summary>
        private sealed class ObservedCard : IMotionCard
        {
            /// <summary>测试连接反馈，不创建连接。</summary>
            internal bool Connected = true;
            /// <summary>统计意外生命周期操作。</summary>
            internal int LifecycleCalls;

            /// <summary>稳定卡标识。</summary>
            public string ControllerId { get { return "MemoryObservation"; } }
            /// <summary>已有连接反馈。</summary>
            public bool IsConnected { get { return Connected; } }
            /// <summary>此摘要检查不提供运动轴。</summary>
            public IReadOnlyCollection<IAxis> Axes { get { return new IAxis[0]; } }
            /// <summary>禁止新连接。</summary>
            public Task ConnectAsync(CancellationToken token) { LifecycleCalls++; throw new InvalidOperationException("禁止连接"); }
            /// <summary>禁止断开操作。</summary>
            public Task DisconnectAsync(CancellationToken token) { LifecycleCalls++; throw new InvalidOperationException("禁止断开"); }
        }
        /// <summary>具备稳定对象标识的准备定义，不允许读取硬件或执行准备。</summary>
        private sealed class ObservedPreparation : IStationPreparation
        {
            /// <summary>稳定工位键，与显示名刻意不同。</summary>
            public string StationId { get { return "StableStation42"; } }
            /// <summary>显示名称。</summary>
            public string StationName { get { return "显示工位"; } }
            /// <summary>无测试依赖。</summary>
            public IReadOnlyCollection<string> Dependencies { get { return new string[0]; } }
            /// <summary>禁止触发真实前置读取。</summary>
            public Task ValidateStartAsync(CancellationToken token) { throw new InvalidOperationException("禁止读取准备条件"); }
            /// <summary>禁止动作。</summary>
            public Task ExecuteAsync(IProgress<StationPreparationProgress> progress, CancellationToken token) { throw new InvalidOperationException("禁止准备"); }
            /// <summary>禁止触发反馈读取。</summary>
            public Task ValidateReadyAsync(CancellationToken token) { throw new InvalidOperationException("禁止读取准备反馈"); }
        }
        /// <summary>失败时抛出明确原因。</summary>
        private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
