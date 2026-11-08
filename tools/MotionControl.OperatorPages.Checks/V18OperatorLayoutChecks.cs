using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.Preparation;
using MotionControl.Framework.Runtime.Alarms;
using MotionControl.Framework.Runtime.Machine;
using MotionControl.Host.Wpf;
using MotionControl.Host.Wpf.Operation;

namespace MotionControl.OperatorPages.Checks
{
    /// <summary>核对现场宽屏并排、窄屏上下排列，并避免详情和表格互相遮挡。</summary>
    internal static class V18OperatorLayoutChecks
    {
        /// <summary>使用真实操作页检查两个视口下的主要内容位置。</summary>
        internal static void CheckPreparation()
        {
            var preparation = new EquipmentPreparationView(null, null);
            Require(((Expander)preparation.FindName("ChecksExpander")).IsExpanded,
                "设备准备未在首屏展开当前缺项与后续检查。");
            CheckPage(preparation, (FrameworkElement)preparation.FindName("StationsGrid"),
                (FrameworkElement)preparation.FindName("PreparationDetailsCard"), "设备准备");
        }

        /// <summary>检查设备总览的工位列表与选中详情位置。</summary>
        internal static void CheckOverview()
        {
            var overview = new EquipmentOverviewView();
            overview.SetPreview(FourWorkspacesPreviewWindow.CreateExampleSnapshot());
            CheckPage(overview, (FrameworkElement)overview.FindName("StationTable"),
                (FrameworkElement)overview.FindName("OverviewDetailsCard"), "设备总览");

            var runtime = new MachineRuntime(new MotionCardGroup(new[] { new GuardCard { IsConnected = true } }),
                new IStationFlow[] { new ReadOnlyFlow() }, new MachineAlarmService(), new GuardSafety());
            runtime.ConfigurePreparations(new[] { new UnpreparedStation() });
            runtime.ConnectAsync(CancellationToken.None).GetAwaiter().GetResult();
            var liveOverview = new EquipmentOverviewView();
            liveOverview.SetConnectionStatus(true, null);
            liveOverview.SetRuntime(runtime);
            string action = ((TextBlock)liveOverview.FindName("NextActionText")).Text;
            Require(action.Contains("设备准备") && action.Contains("启动条件") && !action.Contains("复位后启动"),
                "未完成设备准备时，总览没有提示先完成准备并核对启动条件。实际提示：" + action);
            var reasonMethod = typeof(EquipmentOverviewView).GetMethod("BuildStationReason", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var readySnapshot = new OperationSnapshot { State = MachineRunState.Ready, ControlOwner = MachineControlOwner.StationFlows };
            string Reason() => (string)reasonMethod.Invoke(liveOverview, new object[] { readySnapshot });
            Require(Reason().Contains("设备准备未完成") && !Reason().Contains("已满足启动检查"),
                "查看原因声称已满足启动检查，覆盖了尚未完成的设备准备。");
            liveOverview.SetAutomaticStartReadiness("轴未使能", false);
            Require(Reason().Contains("轴未使能"), "查看原因覆盖了具体启动拦截。");
            liveOverview.SetAutomaticStartReadiness(null, true);
            Require(Reason().Contains("正在检查"), "查看原因覆盖了正在检查的状态。");
            liveOverview.SetAutomaticStartReadiness(null, false);
            liveOverview.SetAxisMappingStatus(0, 13);
            Require(((TextBlock)liveOverview.FindName("NextActionText")).Text.Contains("轴映射"),
                "准备提示覆盖了缺少轴映射的当前处理指引。");
            Require(((Button)liveOverview.FindName("AutomaticStartActionButton")).Visibility == Visibility.Collapsed,
                "缺少轴映射时仍显示设备准备快捷入口。");
            liveOverview.SetAxisMappingStatus(13, 13);
            var clearing = typeof(MachineRuntime).GetField("_clearing", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            clearing.SetValue(runtime, true);
            liveOverview.RefreshDisplay();
            Require(((TextBlock)liveOverview.FindName("NextActionText")).Text.Contains("停止清料"),
                "准备提示覆盖了清料进行中的操作指引。");
            Require(((Button)liveOverview.FindName("AutomaticStartActionButton")).Visibility == Visibility.Collapsed,
                "清料进行中仍显示准备或轴手动快捷入口。");
            clearing.SetValue(runtime, false);
            liveOverview.SetConnectionStatus(false, "断连");
            Require(!((TextBlock)liveOverview.FindName("NextActionText")).Text.Contains("完成设备准备"),
                "连接失效时仍把准备状态显示为已确认。");

            var taskRuntime = new MachineRuntime(new MotionCardGroup(new[] { new GuardCard { IsConnected = true } }),
                Array.Empty<IStationFlow>(), new MachineAlarmService(), new GuardSafety(), controlOwner: MachineControlOwner.TaskEngine);
            taskRuntime.ConfigurePreparations(new[] { new UnpreparedStation() });
            taskRuntime.ConnectAsync(CancellationToken.None).GetAwaiter().GetResult();
            var taskOverview = new EquipmentOverviewView();
            taskOverview.SetConnectionStatus(true, null);
            taskOverview.SetRuntime(taskRuntime);
            Require(!((TextBlock)taskOverview.FindName("NextActionText")).Text.Contains("完成设备准备"),
                "任务会话身份未确认时，仍把旧配置准备状态显示为当前结论。");
            Require(((Button)taskOverview.FindName("AutomaticStartActionButton")).Visibility == Visibility.Collapsed,
                "任务会话身份未确认时仍按旧准备配置显示快捷入口。");
            object validTaskSession = new object();
            taskOverview.SetTaskSessionIdentityProvider(() => validTaskSession);
            Require(((Button)taskOverview.FindName("AutomaticStartActionButton")).Visibility == Visibility.Collapsed,
                "任务准备范围尚未确认发布时仍显示快捷入口。");
            bool published = true;
            taskOverview.SetTaskPreparationScopeProvider((current, session) => published);
            Require(((Button)taskOverview.FindName("AutomaticStartActionButton")).Visibility == Visibility.Visible,
                "确认当前任务会话后没有恢复设备准备快捷入口。");
            published = false;
            taskOverview.RefreshDisplay();
            Require(((Button)taskOverview.FindName("AutomaticStartActionButton")).Visibility == Visibility.Collapsed
                && ((TextBlock)taskOverview.FindName("NextActionText")).Text.Contains("准备范围待确认"),
                "任务仍有身份但准备会话失效时沿用了旧范围或快捷入口。");
            var taskReadySnapshot = new OperationSnapshot { State = MachineRunState.Ready, ControlOwner = MachineControlOwner.TaskEngine };
            var taskReason = (string)reasonMethod.Invoke(taskOverview, new object[] { taskReadySnapshot });
            Require(taskReason.Contains("准备范围待确认") && !taskReason.Contains("已满足启动检查"),
                "任务准备范围失效后，查看原因仍声称启动检查通过。");
            int scopeReads = 0;
            taskOverview.SetTaskPreparationScopeProvider((current, session) => ++scopeReads == 1);
            Require(((Button)taskOverview.FindName("AutomaticStartActionButton")).Visibility == Visibility.Collapsed
                && ((TextBlock)taskOverview.FindName("NextActionText")).Text.Contains("准备范围待确认"),
                "采样过程中发布范围失效后仍显示旧准备结论。");
            taskOverview.SetTaskPreparationScopeProvider((current, session) => throw new InvalidOperationException("范围读取失败"));
            Require(((Button)taskOverview.FindName("AutomaticStartActionButton")).Visibility == Visibility.Collapsed,
                "准备范围校验抛错时仍显示设备准备快捷入口。");
            taskOverview.SetTaskSessionIdentityProvider(() => throw new InvalidOperationException(new string('错', 1200) + "错误末尾"));
            Layout(taskOverview, 700, 760);
            var statusScroll = (ScrollViewer)taskOverview.FindName("TaskRunStatusScroll");
            Require(statusScroll != null && statusScroll.ScrollableHeight > 0
                && ((FrameworkElement)taskOverview.FindName("StationTable")).ActualHeight > 50
                && ((FrameworkElement)taskOverview.FindName("OverviewDetailsCard")).ActualHeight > 100,
                "长任务读取异常遮挡了工位列表或详情，或者完整错误无法滚动查看。");
            statusScroll.ScrollToEnd();
            Layout(taskOverview, 700, 760);
            Require(statusScroll.VerticalOffset >= statusScroll.ScrollableHeight - 1
                && ((TextBlock)taskOverview.FindName("TaskRunStatusText")).Text.EndsWith("错误末尾"),
                "长任务读取异常的末尾不可达。");
        }

        /// <summary>宽屏应同时看到列表与详情，窄屏应按阅读顺序改为上下布局。</summary>
        private static void CheckPage(FrameworkElement view, FrameworkElement list, FrameworkElement detail, string page)
        {
            Layout(view, 1200, 760);
            Rect wideList = Bounds(list, view);
            Rect wideDetail = Bounds(detail, view);
            Require(wideList.Width > 300 && wideDetail.Width > 150 && wideDetail.Left >= wideList.Right,
                page + "宽屏没有把详情放在列表右侧。");
            Require(wideDetail.Top < wideList.Bottom && wideDetail.Bottom <= 760,
                page + "宽屏详情不可见或超出页面。");

            Layout(view, 700, 760);
            Rect narrowList = Bounds(list, view);
            Rect narrowDetail = Bounds(detail, view);
            Require(narrowList.Width > 300 && narrowDetail.Width > 150 && narrowDetail.Top >= narrowList.Bottom,
                page + "窄屏没有按列表在上、详情在下排列。");
            Require(narrowDetail.Height >= narrowList.Height,
                page + "窄屏详情区过矮，首屏难以查看选中对象的状态。");
        }

        /// <summary>执行 WPF 测量与排列，不触发设备读取或动作。</summary>
        private static void Layout(FrameworkElement view, double width, double height)
        {
            view.Measure(new Size(width, height));
            view.Arrange(new Rect(0, 0, width, height));
            view.UpdateLayout();
        }

        /// <summary>取得控件相对页面的实际位置与尺寸。</summary>
        private static Rect Bounds(FrameworkElement element, FrameworkElement page)
        {
            Point position = element.TransformToAncestor(page).Transform(new Point(0, 0));
            return new Rect(position, element.RenderSize);
        }

        /// <summary>用清晰场景描述输出布局回归失败。</summary>
        private static void Require(bool value, string message)
        { if (!value) throw new InvalidOperationException(message); }

        /// <summary>只安装未准备项，不允许测试触发设备动作。</summary>
        private sealed class UnpreparedStation : IStationPreparation
        {
            public string StationId => "inspection";
            public string StationName => "测试工位";
            public IReadOnlyCollection<string> Dependencies => Array.Empty<string>();
            public Task ValidateStartAsync(CancellationToken token) => throw new InvalidOperationException("禁止动作检查。");
            public Task ExecuteAsync(IProgress<StationPreparationProgress> progress, CancellationToken token) => throw new InvalidOperationException("禁止回零。");
            public Task ValidateReadyAsync(CancellationToken token) => throw new InvalidOperationException("禁止验收读取。");
        }
    }
}
