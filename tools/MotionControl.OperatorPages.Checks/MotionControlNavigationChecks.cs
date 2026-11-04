using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using MotionControl.Host.Wpf;
using MotionControl.Host.Wpf.Operation;

/// <summary>检查集中入口复用已有页面并保持关联返回。</summary>
internal static class MotionControlNavigationChecks
{
    /// <summary>逐项确认核心页面入口，检查跨页跳转不离开集中分区。</summary>
    internal static void Run()
    {
        var window = new MainWindow();
        try
        {
            var nav = (StackPanel)window.FindName("PrimaryNavigation");
            var sidebar = (StackPanel)window.FindName("SidebarNavigation");
            var content = (ContentControl)window.FindName("ManualDebugContent");
            var axisMapping = new UserControl();
            var ioMapping = new UserControl();
            var manualAxis = new UserControl();
            var ioMonitor = new UserControl();
            var taskConfig = new UserControl();
            var taskMonitor = new UserControl();
            window.SetAxisMappingConfigurationPage(axisMapping);
            window.SetIoMappingConfigurationPage(ioMapping);
            window.SetManualMotionDebugPage(manualAxis);
            window.SetIoMonitorPage(ioMonitor);
            window.SetTaskConfigurationPage(taskConfig);
            window.SetTaskMonitorPage(taskMonitor);

            nav.Children.OfType<ToggleButton>().Single(b => Equals(b.Tag, "MotionControl"))
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var required = new[] { "设备总览", "设备准备", "工位运行", "轴监控", "IO监控", "轴手动", "IO手动", "轴映射配置", "IO映射", "安全联锁", "任务配置", "设备变量", "当前报警", "历史报警", "报警库", "设备工程" };
            foreach (string page in required)
                if (sidebar.Children.OfType<ToggleButton>().Count(b => Equals(b.Tag, page)) != 1)
                    throw new InvalidOperationException("集中入口缺少或重复：" + page);
            if (sidebar.Children.OfType<ToggleButton>().Any(b => Equals(b.Tag, "流程状态") || Equals(b.Tag, "任务接入状态")))
                throw new InvalidOperationException("仍向操作员暴露按执行器划分的重复入口。");

            foreach (var section in nav.Children.OfType<ToggleButton>().Where(b => !Equals(b.Tag, "MotionControl")).ToArray())
            {
                section.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                foreach (string page in required)
                    if (sidebar.Children.OfType<ToggleButton>().Any(b => Equals(b.Tag, page)))
                        throw new InvalidOperationException("原分类仍保留已迁移页面：" + page);
            }
            nav.Children.OfType<ToggleButton>().Single(b => Equals(b.Tag, "MotionControl"))
                .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            Select(sidebar, "设备总览");
            if (!(content.Content is EquipmentOverviewView)) throw new InvalidOperationException("设备总览未显示。");
            Select(sidebar, "轴映射配置");
            if (!ReferenceEquals(content.Content, axisMapping)) throw new InvalidOperationException("轴映射未复用原页面。");
            Select(sidebar, "IO映射");
            if (!ReferenceEquals(content.Content, ioMapping)) throw new InvalidOperationException("IO 映射未复用原页面。");
            Select(sidebar, "轴手动");
            if (!ReferenceEquals(content.Content, manualAxis)) throw new InvalidOperationException("轴手动未复用原页面。");
            Select(sidebar, "IO监控");
            if (!ReferenceEquals(content.Content, ioMonitor)) throw new InvalidOperationException("IO 监控未复用原页面。");
            Select(sidebar, "任务配置");
            if (!ReferenceEquals(content.Content, taskConfig)) throw new InvalidOperationException("任务配置未复用原页面。");
            window.ShowTaskRun();
            if (!ReferenceEquals(content.Content, taskMonitor) || !Equals(((TextBlock)window.FindName("SidebarTitle")).Text, "运动控制"))
                throw new InvalidOperationException("任务跳转未留在集中入口。");
            ((Button)window.FindName("OperationReturnButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (!ReferenceEquals(content.Content, taskConfig)) throw new InvalidOperationException("关联返回未回到任务配置。");

            var card = new MotionControl.OperatorPages.Checks.GuardCard();
            var cards = new MotionControl.Framework.Runtime.Machine.MotionCardGroup(
                new MotionControl.Framework.Contracts.Motion.IMotionCard[] { card });
            var taskRuntime = new MotionControl.Framework.Runtime.Machine.MachineRuntime(cards,
                new MotionControl.Framework.Contracts.Flow.IStationFlow[0],
                new MotionControl.Framework.Runtime.Alarms.MachineAlarmService(),
                new MotionControl.OperatorPages.Checks.GuardSafety(),
                controlOwner: MotionControl.Framework.Contracts.Flow.MachineControlOwner.TaskEngine);
            window.SetMachineRuntime(taskRuntime);
            window.ShowFlowStatus();
            if (!ReferenceEquals(content.Content, taskMonitor)) throw new InvalidOperationException("任务模式的统一工位页选择错误。");
            var flowPage = new UserControl();
            window.SetFlowStatusPage(flowPage);
            var codeRuntime = new MotionControl.Framework.Runtime.Machine.MachineRuntime(cards,
                new MotionControl.Framework.Contracts.Flow.IStationFlow[0],
                new MotionControl.Framework.Runtime.Alarms.MachineAlarmService(),
                new MotionControl.OperatorPages.Checks.GuardSafety());
            window.SetMachineRuntime(codeRuntime);
            if (!ReferenceEquals(content.Content, flowPage) || card.Calls != 0)
                throw new InvalidOperationException("切换运行来源未刷新统一工位页，或查看页面触发硬件操作。");
            codeRuntime.AcquireTaskEngineControlAsync(System.Threading.CancellationToken.None).GetAwaiter().GetResult();
            window.SetMachineRuntime(codeRuntime);
            if (!ReferenceEquals(content.Content, taskMonitor)) throw new InvalidOperationException("同一运行时接管任务后仍显示旧视图。");
            codeRuntime.ReleaseTaskEngineControl();
            window.SetMachineRuntime(codeRuntime);
            if (!ReferenceEquals(content.Content, flowPage) || card.Calls != 0)
                throw new InvalidOperationException("同一运行时归还入口后没有恢复工位视图。");
        }
        finally { window.Close(); }
    }

    /// <summary>通过当前左侧导航选择指定页面。</summary>
    private static void Select(StackPanel sidebar, string page)
    {
        sidebar.Children.OfType<ToggleButton>().Single(b => Equals(b.Tag, page))
            .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }
}
