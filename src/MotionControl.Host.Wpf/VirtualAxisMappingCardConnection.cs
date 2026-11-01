using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Adapter.MotionCard.Virtual;
using MotionControl.Framework.Contracts.Configuration;
using MotionControl.Framework.Contracts.IO;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.Framework.Wpf.Configuration;

namespace MotionControl.Host.Wpf
{
    /// <summary>把现有设备逻辑映射接入独立控制器，不更改厂商和控制卡配置文件。</summary>
    public sealed class VirtualAxisMappingCardConnection : IMotionCardHostConnection, IMotionCardIoCapabilityProvider
    {
        /// <summary>选择设备已有的正运动卡型，轴与 IO 映射保持原有设备标识。</summary>
        private static readonly IReadOnlyList<MotionCardSelectionOption> CardOptions =
            new List<MotionCardSelectionOption>
            {
                new MotionCardSelectionOption
                {
                    VendorCode = "正运动", VendorDisplayResourceKey = "厂商正运动",
                    CardTypeCode = "总线型", CardTypeDisplayResourceKey = "总线型", AdapterType = "ZMotion",
                    SupportedAxisCounts = Array.AsReadOnly(new[] { 8, 16, 32, 64 }), DefaultAxisCount = 64
                }
            }.AsReadOnly();

        /// <summary>用于只读加载设备映射的配置存储。</summary>
        private readonly MachineConfigurationStore _store;
        /// <summary>设备配置编号。</summary>
        private readonly string _machineId;
        /// <summary>独立控制器的本机连接端口。</summary>
        private readonly int _port;
        /// <summary>已成功连接的控制器适配器。</summary>
        private VirtualMotionCard _card;
        /// <summary>当前连接使用的逻辑轴定义。</summary>
        private IReadOnlyList<AxisHardwareDefinition> _axes = Array.Empty<AxisHardwareDefinition>();
        /// <summary>当前连接使用的逻辑输入映射。</summary>
        private IReadOnlyList<DigitalIoHardwareDefinition> _inputs = Array.Empty<DigitalIoHardwareDefinition>();
        /// <summary>当前连接使用的逻辑输出映射。</summary>
        private IReadOnlyList<DigitalIoHardwareDefinition> _outputs = Array.Empty<DigitalIoHardwareDefinition>();

        /// <summary>创建一个只能访问本机独立控制器的 Host 连接。</summary>
        public VirtualAxisMappingCardConnection(MachineConfigurationStore store, string machineId, int port)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _machineId = string.IsNullOrWhiteSpace(machineId) ? throw new ArgumentException("设备编号不能为空。", nameof(machineId)) : machineId;
            if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
            _port = port;
        }

        /// <summary>返回设备已保存的卡型选项；实际后端由独立工程配置指定。</summary>
        public IReadOnlyList<MotionCardSelectionOption> SupportedMotionCards { get { return CardOptions; } }
        /// <summary>返回独立控制器的连接状态。</summary>
        public bool IsConnected { get { return _card != null && _card.IsConnected; } }
        /// <summary>明确标识本次控制器身份，不冒充厂商实体卡。</summary>
        public string ActualControllerType { get { return IsConnected ? "VirtualController" : string.Empty; } }
        /// <summary>当前连接协议的版本。</summary>
        public string ActualControllerVersion { get { return IsConnected ? "1" : string.Empty; } }
        /// <summary>返回已连接控制器供 Runtime 使用。</summary>
        public IMotionCard ConnectedMotionCard { get { return _card; } }
        /// <summary>返回已连接控制器供逻辑 IO 使用。</summary>
        public ILogicalIo ConnectedLogicalIo { get { return _card; } }
        /// <summary>当前已映射轴，供 Runtime 和手动页使用。</summary>
        public IReadOnlyList<AxisHardwareDefinition> AxisHardwareDefinitions { get { return _axes; } }
        /// <summary>当前已映射数字输入。</summary>
        public IReadOnlyList<DigitalIoHardwareDefinition> DigitalInputs { get { return _inputs; } }
        /// <summary>当前已映射数字输出。</summary>
        public IReadOnlyList<DigitalIoHardwareDefinition> DigitalOutputs { get { return _outputs; } }

        /// <summary>加载现有轴和 IO 映射后连接独立控制器；失败时不保存或修改设备映射。</summary>
        public async Task ConnectAsync(MotionCardSettingsDocument settings, CancellationToken cancellationToken)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (IsConnected) return;
            if (_card != null)
                throw new InvalidOperationException("软件控制器的原会话已中断，轴和输出状态尚未确认。请先检查设备并结束当前运行，重新启动上位机后再连接。");
            if (settings.MotionCards == null || settings.MotionCards.Count != 1 || settings.MotionCards[0] == null)
                throw new NotSupportedException("当前独立控制器仅支持一张已配置运动卡，不能忽略额外卡配置。");
            var selected = settings.MotionCards[0];
            if (string.IsNullOrWhiteSpace(selected.ControllerId))
                throw new InvalidOperationException("运动卡配置缺少控制器编号。");
            if (!string.Equals(settings.MotionCardVendor, "正运动", StringComparison.Ordinal)
                || !string.Equals(settings.MotionCardType, "总线型", StringComparison.Ordinal)
                || !string.Equals(selected.AdapterType, "ZMotion", StringComparison.Ordinal))
                throw new NotSupportedException("当前工程配置只支持复用已保存的正运动总线卡映射；没有修改卡型配置。");
            if (settings.AxisCount <= 0 || settings.AxisCount > 64)
                throw new NotSupportedException("当前独立控制器支持 1 至 64 个物理轴。");

            IReadOnlyList<AxisHardwareDefinition> axes = LoadAxes(selected.ControllerId, settings.AxisCount);
            IoMappingConfigurationDocument io = _store.LoadJson<IoMappingConfigurationDocument>(_machineId, "HardwareConfig/IoMapping.json");
            if (io == null || io.SchemaVersion != 1 || io.DigitalInputs == null || io.DigitalOutputs == null)
                throw new InvalidOperationException("IoMapping.json 格式无效，无法连接控制器。");
            ValidateIoMappings(io.DigitalInputs, selected.ControllerId);
            ValidateIoMappings(io.DigitalOutputs, selected.ControllerId);
            var inputs = io.DigitalInputs.ToList().AsReadOnly();
            var outputs = io.DigitalOutputs.ToList().AsReadOnly();
            var card = new VirtualMotionCard(selected.ControllerId, axes, inputs, outputs, _port);
            await card.ConnectAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                foreach (IAxis axis in card.Axes)
                {
                    AxisStatus status = await axis.ReadStatusAsync(cancellationToken).ConfigureAwait(false);
                    if (status.Moving)
                        throw new InvalidOperationException("逻辑轴 '" + axis.Name + "' 仍在运动，不能接管控制器。");
                }
            }
            catch
            {
                // 无法核实停稳时让租约自然过期，由控制器看门狗停车，不能伪造断连成功。
                card.AbandonConnection();
                throw;
            }
            _card = card;
            _axes = axes;
            _inputs = inputs;
            _outputs = outputs;
        }

        /// <summary>读取并验证已有逻辑轴定义；不把厂商回零模式编号当成独立控制器模式。</summary>
        private IReadOnlyList<AxisHardwareDefinition> LoadAxes(string controllerId, int axisCount)
        {
            AxisMappingConfigurationDocument document = _store.LoadJson<AxisMappingConfigurationDocument>(_machineId, "HardwareConfig/AxisParameters.json");
            if (document == null || document.SchemaVersion != 1 || document.Axes == null)
                throw new InvalidOperationException("AxisParameters.json 格式无效，无法连接控制器。");
            var axes = new List<AxisHardwareDefinition>();
            foreach (AxisMappingEntry axis in document.Axes)
            {
                if (axis == null) throw new InvalidOperationException("轴映射中存在空项目。");
                if (!axis.IsConfigured) continue;
                if (!string.Equals(axis.ControllerId, controllerId, StringComparison.OrdinalIgnoreCase))
                    throw new NotSupportedException("轴 '" + axis.Name + "' 属于另一张卡；不能忽略其他卡的轴。");
                if (axis.AxisNumber < 0 || axis.AxisNumber >= axisCount || !axis.UnitsPerEngineeringUnit.HasValue
                    || axis.UnitsPerEngineeringUnit <= 0 || !axis.MinimumPosition.HasValue || !axis.MaximumPosition.HasValue
                    || axis.MinimumPosition >= axis.MaximumPosition || !axis.SoftLimitEnabled.HasValue || !axis.ManualSpeed.HasValue
                    || axis.ManualSpeed <= 0)
                    throw new InvalidOperationException("轴 '" + axis.Name + "' 的通道、单位、限位或手动速度配置不完整。");
                axes.Add(new AxisHardwareDefinition
                {
                    ControllerId = axis.ControllerId, Name = axis.Name, AxisNumber = axis.AxisNumber,
                    EngineeringUnit = axis.EngineeringUnit, MinimumPosition = axis.MinimumPosition.Value,
                    MaximumPosition = axis.MaximumPosition.Value, SoftLimitEnabled = axis.SoftLimitEnabled.Value,
                    ManualSpeed = axis.ManualSpeed.Value,
                    PositionTolerance = axis.PositionTolerance ?? AxisHardwareDefinition.DefaultPositionTolerance,
                    ManualAcceleration = axis.HomeAcceleration.GetValueOrDefault(),
                    ManualDeceleration = axis.HomeDeceleration.GetValueOrDefault()
                });
            }
            return axes.AsReadOnly();
        }

        /// <summary>拒绝未知控制器或超出范围的 IO 映射，避免显示没有连接的通道。</summary>
        private static void ValidateIoMappings(IEnumerable<DigitalIoHardwareDefinition> mappings, string controllerId)
        {
            foreach (DigitalIoHardwareDefinition mapping in mappings)
            {
                if (mapping == null || string.IsNullOrWhiteSpace(mapping.Name)
                    || !string.Equals(mapping.ControllerId, controllerId, StringComparison.OrdinalIgnoreCase)
                    || mapping.Channel < 0 || mapping.Channel >= 4096)
                    throw new InvalidOperationException("IO 映射包含空名称、其他控制器或超出范围的通道。");
            }
        }

        /// <summary>只有全部已映射轴确认停稳后才断开；读取失败时保留连接并报告错误。</summary>
        public async Task DisconnectAsync(CancellationToken cancellationToken)
        {
            if (_card == null) return;
            foreach (IAxis axis in _card.Axes)
            {
                AxisStatus status = await axis.ReadStatusAsync(cancellationToken).ConfigureAwait(false);
                if (status.Moving) throw new InvalidOperationException("逻辑轴 '" + axis.Name + "' 仍在运动，不能断开连接。");
            }
            await _card.DisconnectAsync(cancellationToken).ConfigureAwait(false);
            _card = null;
            _axes = Array.Empty<AxisHardwareDefinition>();
            _inputs = Array.Empty<DigitalIoHardwareDefinition>();
            _outputs = Array.Empty<DigitalIoHardwareDefinition>();
        }

        /// <summary>确认停稳后重新读取设备映射并连接控制器。</summary>
        public async Task ReconnectAsync(MotionCardSettingsDocument settings, CancellationToken cancellationToken)
        {
            await DisconnectAsync(cancellationToken).ConfigureAwait(false);
            await ConnectAsync(settings, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>控制器尚未实现物理轴参数读回，不使用设备配置伪装读卡结果。</summary>
        public Task<AxisMappingCardParameters> ReadAxisParametersAsync(int axisNumber, CancellationToken cancellationToken)
        {
            throw new NotSupportedException("当前控制器不支持读取物理轴参数。");
        }

        /// <summary>独立控制器不复用任何厂商回零模式编号。</summary>
        public IReadOnlyList<AxisHomeModeOption> GetSupportedHomeModes(MotionCardSelectionOption motionCard, int axisNumber)
        {
            return Array.Empty<AxisHomeModeOption>();
        }

        /// <summary>从当前连接读取数字 IO 能力。</summary>
        public Task<MotionCardIoCapabilities> ReadIoCapabilitiesAsync(CancellationToken cancellationToken)
        {
            if (!IsConnected) throw new InvalidOperationException("运动控制器未连接。");
            return _card.ReadIoCapabilitiesAsync(cancellationToken);
        }

        /// <summary>重新读取当前连接的数字 IO 能力。</summary>
        public Task<MotionCardIoCapabilities> RefreshIoCapabilitiesAsync(CancellationToken cancellationToken)
        {
            if (!IsConnected) throw new InvalidOperationException("运动控制器未连接。");
            return _card.RefreshIoCapabilitiesAsync(cancellationToken);
        }
    }
}
