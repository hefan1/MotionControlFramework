using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using MotionControl.Framework.Contracts.IO;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.Framework.Runtime.Machine;
using MotionControl.Framework.Wpf.Configuration;
using MotionControl.Framework.Wpf.ManualDebug;
using MotionControl.Host.Wpf;

/// <summary>检查轴手动离线视图复用原页面，并且不提供虚构状态或设备动作。</summary>
internal static class DisconnectedManualMotionChecks
{
    /// <summary>覆盖离线查看、真实页面替换、重新断开及无效映射提示。</summary>
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "MCF-ManualOffline-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new MachineConfigurationStore(directory);
            var configuration = new AxisMappingConfigurationDocument();
            configuration.Axes.Add(new AxisMappingEntry
            {
                ControllerId = "Card1", AxisNumber = 0, Name = "Load_X", EngineeringUnit = "mm",
                MinimumPosition = -100, MaximumPosition = 100, SoftLimitEnabled = true, ManualSpeed = 5
            });
            store.SaveJson("Machine01", "HardwareConfig/AxisParameters.json", configuration);
            string path = Path.Combine(store.GetMachineDirectory("Machine01"), "HardwareConfig", "AxisParameters.json");
            string before = File.ReadAllText(path);
            Type wrapperType = typeof(MainWindow).Assembly.GetType("MotionControl.Host.Wpf.DisconnectedManualMotionPage", true);
            Func<UserControl> create = () => (UserControl)Activator.CreateInstance(wrapperType,
                BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { store, "Machine01", (Action)(() => { }) }, null);
            var offline = create();
            var manual = (ManualMotionDebugView)wrapperType.GetField("_manualPage", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(offline);
            if (manual == null) throw new InvalidOperationException("离线没有创建原轴手动页面。");
            var cardsPanel = (Panel)manual.FindName("AxisCardsPanel");
            if (cardsPanel.IsEnabled || cardsPanel.Children.Count != 1)
                throw new InvalidOperationException("离线轴卡数量不符或卡片仍可操作。");
            var axisControl = (ManualAxisControl)cardsPanel.Children[0];
            axisControl.RefreshStatusAsync().GetAwaiter().GetResult();
            if (!((TextBlock)axisControl.FindName("CommandPositionText")).Text.Contains("未连接")
                || ((TextBlock)axisControl.FindName("FeedbackPositionText")).Text != "—"
                || axisControl.CanRunManualOperations || axisControl.CanRunManualRecoveryOperations)
                throw new InvalidOperationException("离线显示了有效位置或放行手动动作。");
            var cards = (MotionCardGroup)typeof(ManualMotionDebugView)
                .GetField("_motionCards", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manual);
            if (cards.IsConnected) throw new InvalidOperationException("离线视图错误显示卡已连接。");
            RequireUnavailable(() => cards.Axes.Single().ReadStatusAsync(CancellationToken.None));
            RequireUnavailable(() => cards.Axes.Single().EnableServoAsync(CancellationToken.None));
            RequireUnavailable(() => cards.Axes.Single().HomeAsync(CancellationToken.None));
            RequireUnavailable(() => cards.Axes.Single().StartJogAsync(MotionDirection.Positive, null, CancellationToken.None));
            RequireUnavailable(() => ((ILogicalIo)cards.Cards.Single()).WriteOutputAsync("夹具", true, CancellationToken.None));
            if (File.ReadAllText(path) != before) throw new InvalidOperationException("离线查看改写了轴配置。");

            var window = new MainWindow();
            try
            {
                typeof(MainWindow).GetMethod("SetDisconnectedManualMotionPageFactory", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(window, new object[] { create });
                var nav = (StackPanel)window.FindName("PrimaryNavigation");
                nav.Children.OfType<ToggleButton>().Single(button => Equals(button.Tag, "MotionControl"))
                    .RaiseEvent(new System.Windows.RoutedEventArgs(ButtonBase.ClickEvent));
                var sidebar = (StackPanel)window.FindName("SidebarNavigation");
                sidebar.Children.OfType<ToggleButton>().Single(button => Equals(button.Tag, "轴手动"))
                    .RaiseEvent(new System.Windows.RoutedEventArgs(ButtonBase.ClickEvent));
                var content = (ContentControl)window.FindName("ManualDebugContent");
                if (content.Content == null || content.Content.GetType() != wrapperType)
                    throw new InvalidOperationException("离线导航没有显示原轴页面。");
                var connectedPage = new UserControl();
                window.SetManualMotionDebugPage(connectedPage);
                if (!ReferenceEquals(content.Content, connectedPage)) throw new InvalidOperationException("连接后没有切换到真实运行时页面。");
                window.SetManualMotionDebugPage(null);
                if (content.Content == null || content.Content.GetType() != wrapperType)
                    throw new InvalidOperationException("断开后没有恢复离线轴页面。");
                configuration.Axes[0].Name = "Load_Y";
                store.SaveJson("Machine01", "HardwareConfig/AxisParameters.json", configuration);
                sidebar.Children.OfType<ToggleButton>().Single(button => Equals(button.Tag, "轴手动"))
                    .RaiseEvent(new System.Windows.RoutedEventArgs(ButtonBase.ClickEvent));
                var refreshed = (ManualMotionDebugView)wrapperType.GetField("_manualPage", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(content.Content);
                if (((ManualAxisControl)((Panel)refreshed.FindName("AxisCardsPanel")).Children[0]).AxisName != "Load_Y")
                    throw new InvalidOperationException("重新进入时没有读取已保存的最新轴映射。");
            }
            finally { window.Close(); }

            configuration.Axes[0].ManualSpeed = null;
            store.SaveJson("Machine01", "HardwareConfig/AxisParameters.json", configuration);
            var invalid = create();
            if (wrapperType.GetField("_manualPage", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(invalid) != null
                || !((DockPanel)invalid.Content).Children.OfType<StackPanel>()
                    .SelectMany(panel => panel.Children.OfType<TextBlock>()).Any(text => text.Text.Contains("配置不完整")))
                throw new InvalidOperationException("轴参数不完整时没有显示错误，或构造了虚假的参数。");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    /// <summary>断卡对象的每项设备动作均必须明确拒绝。</summary>
    private static void RequireUnavailable(Func<System.Threading.Tasks.Task> action)
    {
        try { action().GetAwaiter().GetResult(); }
        catch (InvalidOperationException error) when (error.Message.Contains("未连接")) { return; }
        throw new InvalidOperationException("离线对象未拒绝设备动作。");
    }
}
