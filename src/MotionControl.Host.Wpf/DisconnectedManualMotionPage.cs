using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MotionControl.Framework.Contracts.Configuration;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.IO;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Runtime.Alarms;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.Framework.Runtime.Machine;
using MotionControl.Framework.Runtime.Safety;
using MotionControl.Framework.Wpf.Configuration;
using MotionControl.Framework.Wpf.ManualDebug;

namespace MotionControl.Host.Wpf
{
    /// <summary>断卡时按已保存映射显示原轴手动页；不创建控制器连接，也不允许修改或动作。</summary>
    internal sealed class DisconnectedManualMotionPage : UserControl
    {
        /// <summary>原轴手动页面，用于按逻辑轴名称定位；配置读取失败时为空。</summary>
        private readonly ManualMotionDebugView _manualPage;

        /// <summary>读取设备配置并构造离线视图，读取失败时保留错误供操作员处理。</summary>
        internal DisconnectedManualMotionPage(MachineConfigurationStore store, string machineId, Action openAxisMapping)
        {
            var layout = new DockPanel();
            var banner = new DockPanel { Margin = new Thickness(12, 8, 12, 8) };
            var mappingButton = new Button { Margin = new Thickness(12, 0, 0, 0), Padding = new Thickness(10, 4, 10, 4), VerticalAlignment = VerticalAlignment.Center };
            mappingButton.SetResourceReference(ContentControl.ContentProperty, "轴映射配置");
            mappingButton.Click += (sender, args) => openAxisMapping?.Invoke();
            DockPanel.SetDock(mappingButton, Dock.Right);
            banner.Children.Add(mappingButton);
            var message = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkGoldenrod, VerticalAlignment = VerticalAlignment.Center };
            message.SetResourceReference(TextBlock.TextProperty, "轴手动离线提示");
            banner.Children.Add(message);
            var bannerBorder = new Border { Background = new SolidColorBrush(Color.FromRgb(255, 247, 231)), BorderBrush = new SolidColorBrush(Color.FromRgb(230, 208, 160)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Child = banner };
            DockPanel.SetDock(bannerBorder, Dock.Top);
            layout.Children.Add(bannerBorder);

            try
            {
                IReadOnlyList<AxisHardwareDefinition> axes = ReadAxes(store, machineId);
                IoMappingConfigurationDocument io = ReadIo(store, machineId);
                var card = new DisconnectedCard(axes);
                var cards = new MotionCardGroup(new IMotionCard[] { card });
                var runtime = new MachineRuntime(cards, new IStationFlow[0], new MachineAlarmService(),
                    new LogicalIoEmergencyStopStatus(card, string.Empty));
                var manualPage = new ManualMotionDebugView(runtime, cards, axes, card,
                    io.DigitalInputs, io.DigitalOutputs, store, machineId);
                manualPage.AxisMappingRequested += (sender, args) => openAxisMapping?.Invoke();
                // 卡片内含配置编辑与动作入口；离线时整体禁用，保留页面滚动和轴映射入口。
                var axisCards = manualPage.FindName("AxisCardsPanel") as Panel;
                if (axisCards == null) throw new InvalidOperationException("轴手动页缺少轴卡片容器，不能确认离线操作已禁用。");
                axisCards.IsEnabled = false;
                _manualPage = manualPage;
                layout.Children.Add(manualPage);
            }
            catch (Exception error)
            {
                var errors = new StackPanel { Margin = new Thickness(18) };
                var title = new TextBlock { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap };
                title.SetResourceReference(TextBlock.TextProperty, "轴手动离线读取失败");
                errors.Children.Add(title);
                errors.Children.Add(new TextBlock { Text = error.Message, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap });
                layout.Children.Add(errors);
            }
            Content = layout;
        }

        /// <summary>只定位离线卡片，不发出读取或运动命令。</summary>
        internal bool FocusAxisByLogicalName(string name)
        {
            return _manualPage != null && _manualPage.FocusAxisByLogicalName(name);
        }

        /// <summary>从已保存的轴映射读取完整轴参数；缺失或冲突时报告原因，不填造数值。</summary>
        private static IReadOnlyList<AxisHardwareDefinition> ReadAxes(MachineConfigurationStore store, string machineId)
        {
            string path = Path.Combine(store.GetMachineDirectory(machineId), "HardwareConfig", "AxisParameters.json");
            if (!File.Exists(path)) return Array.Empty<AxisHardwareDefinition>();
            var document = store.LoadJson<AxisMappingConfigurationDocument>(machineId, "HardwareConfig/AxisParameters.json");
            if (document == null || document.SchemaVersion != 1 || document.Axes == null || document.Axes.Any(axis => axis == null))
                throw new InvalidOperationException("AxisParameters.json 格式无效。");
            var configured = document.Axes.Where(axis => axis.IsConfigured).ToList();
            if (configured.Any(axis => string.IsNullOrWhiteSpace(axis.ControllerId) || axis.AxisNumber < 0
                || axis.Name != axis.Name.Trim() || !axis.ManualSpeed.HasValue || axis.ManualSpeed <= 0
                || !axis.MinimumPosition.HasValue || !axis.MaximumPosition.HasValue
                || axis.MinimumPosition >= axis.MaximumPosition || !axis.SoftLimitEnabled.HasValue
                || double.IsNaN(axis.ManualSpeed.Value) || double.IsInfinity(axis.ManualSpeed.Value)
                || double.IsNaN(axis.MinimumPosition.Value) || double.IsInfinity(axis.MinimumPosition.Value)
                || double.IsNaN(axis.MaximumPosition.Value) || double.IsInfinity(axis.MaximumPosition.Value)))
                throw new InvalidOperationException("已命名轴的控制卡、通道、软限位或手动速度配置不完整。");
            if (configured.Select(axis => axis.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != configured.Count
                || configured.Select(axis => axis.ControllerId + "\n" + axis.AxisNumber).Distinct(StringComparer.OrdinalIgnoreCase).Count() != configured.Count)
                throw new InvalidOperationException("轴映射包含重复的逻辑名称或物理通道。");
            return configured.Select(axis => new AxisHardwareDefinition
            {
                ControllerId = axis.ControllerId, Name = axis.Name, AxisNumber = axis.AxisNumber,
                EngineeringUnit = axis.EngineeringUnit, MinimumPosition = axis.MinimumPosition.Value,
                MaximumPosition = axis.MaximumPosition.Value, SoftLimitEnabled = axis.SoftLimitEnabled,
                ManualSpeed = axis.ManualSpeed.Value,
                PositionTolerance = axis.PositionTolerance ?? AxisHardwareDefinition.DefaultPositionTolerance,
                ManualAcceleration = axis.HomeAcceleration.GetValueOrDefault(),
                ManualDeceleration = axis.HomeDeceleration.GetValueOrDefault()
            }).ToList().AsReadOnly();
        }

        /// <summary>只读轴卡关联需要的已保存 IO 映射；缺少文件时使用空映射。</summary>
        private static IoMappingConfigurationDocument ReadIo(MachineConfigurationStore store, string machineId)
        {
            string path = Path.Combine(store.GetMachineDirectory(machineId), "HardwareConfig", "IoMapping.json");
            if (!File.Exists(path)) return new IoMappingConfigurationDocument();
            var document = store.LoadJson<IoMappingConfigurationDocument>(machineId, "HardwareConfig/IoMapping.json");
            if (document == null || document.SchemaVersion != 1 || document.DigitalInputs == null || document.DigitalOutputs == null)
                throw new InvalidOperationException("IoMapping.json 格式无效。");
            return document;
        }

        /// <summary>只供原页面显示配置的离线轴集合；所有连接、IO 及运动操作均明确拒绝。</summary>
        private sealed class DisconnectedCard : IMotionCard, ILogicalIo
        {
            /// <summary>从保存配置生成的只读逻辑轴列表。</summary>
            public IReadOnlyCollection<IAxis> Axes { get; }
            /// <summary>内部展示对象的标识，不代表真实控制卡。</summary>
            public string ControllerId => "DisconnectedAxisDisplay";
            /// <summary>此对象永远不建立控制器会话。</summary>
            public bool IsConnected => false;
            /// <summary>只保存轴名称，不使用任何驱动适配器。</summary>
            internal DisconnectedCard(IEnumerable<AxisHardwareDefinition> axes)
            { Axes = axes.Select(axis => (IAxis)new DisconnectedAxis(axis.Name)).ToList().AsReadOnly(); }
            /// <summary>离线视图不建立连接。</summary>
            public Task ConnectAsync(CancellationToken token) { throw Unavailable(); }
            /// <summary>离线视图没有连接可断开。</summary>
            public Task DisconnectAsync(CancellationToken token) { throw Unavailable(); }
            /// <summary>离线视图不能读取输入。</summary>
            public Task<bool> ReadInputAsync(string name, CancellationToken token) { throw Unavailable(); }
            /// <summary>离线视图不能读取输出。</summary>
            public Task<bool> ReadOutputAsync(string name, CancellationToken token) { throw Unavailable(); }
            /// <summary>离线视图不能写入输出。</summary>
            public Task WriteOutputAsync(string name, bool value, CancellationToken token) { throw Unavailable(); }
        }

        /// <summary>只提供轴名称供页面建立原卡片；不提供虚构状态或设备动作。</summary>
        private sealed class DisconnectedAxis : IAxis
        {
            /// <summary>已保存配置中的逻辑轴名称。</summary>
            public string Name { get; }
            /// <summary>保存逻辑轴名称。</summary>
            internal DisconnectedAxis(string name) { Name = name; }
            /// <summary>未连接时不能读取轴状态。</summary>
            public Task<AxisStatus> ReadStatusAsync(CancellationToken token) { throw Unavailable(); }
            /// <summary>未连接时不能使能。</summary>
            public Task EnableServoAsync(CancellationToken token) { throw Unavailable(); }
            /// <summary>未连接时不能关闭伺服。</summary>
            public Task DisableServoAsync(CancellationToken token) { throw Unavailable(); }
            /// <summary>未连接时不能回零。</summary>
            public Task HomeAsync(CancellationToken token) { throw Unavailable(); }
            /// <summary>未连接时不能绝对定位。</summary>
            public Task MoveAbsoluteAsync(double position, AxisMotionProfile profile, CancellationToken token) { throw Unavailable(); }
            /// <summary>未连接时不能相对移动。</summary>
            public Task MoveRelativeAsync(double distance, AxisMotionProfile profile, CancellationToken token) { throw Unavailable(); }
            /// <summary>未连接时不能点动。</summary>
            public Task StartJogAsync(MotionDirection direction, AxisMotionProfile profile, CancellationToken token) { throw Unavailable(); }
            /// <summary>未连接时不能向设备发送停止命令。</summary>
            public Task StopAsync(AxisStopMode mode, CancellationToken token) { throw Unavailable(); }
            /// <summary>未连接时不能复位轴报警。</summary>
            public Task ResetAlarmAsync(CancellationToken token) { throw Unavailable(); }
        }

        /// <summary>统一给出离线视图拒绝操作的明确原因。</summary>
        private static InvalidOperationException Unavailable()
        { return new InvalidOperationException("控制卡未连接，离线轴手动页面不能读写设备或执行动作。"); }
    }
}
