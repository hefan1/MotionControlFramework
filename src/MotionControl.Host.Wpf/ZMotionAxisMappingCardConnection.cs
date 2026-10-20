using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Adapter.MotionCard.ZMotion;
using MotionControl.Framework.Contracts.Configuration;
using MotionControl.Framework.Contracts.IO;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.Framework.Wpf.Configuration;

namespace MotionControl.Host.Wpf
{
    /// <summary>由 Host 选择正运动适配器，并为轴映射页面提供连接和参数读写。</summary>
    public sealed class ZMotionAxisMappingCardConnection :
        IMotionCardHostConnection,
        IMotionCardIoCapabilityProvider,
        IIoMappingCardConnection
    {
        /// <summary>运动卡选择配置在单机配置目录中的相对路径。</summary>
        private const string MotionCardsFilePath = "HardwareConfig/MotionCards.json";

        /// <summary>首次连接正运动仿真器时使用的连接参数文件。</summary>
        private const string DefaultConnectionConfigurationFile = "HardwareConfig/ZDevelopSimulation.json";

        /// <summary>设备逻辑轴名称、物理轴号和运动参数配置文件。</summary>
        private const string AxisParametersFilePath = "HardwareConfig/AxisParameters.json";

        /// <summary>设备逻辑 DI/DO 名称和物理通道映射配置文件。</summary>
        private const string IoMappingFilePath = "HardwareConfig/IoMapping.json";

        /// <summary>正运动适配器在设备硬件配置中使用的稳定标识。</summary>
        private const string ZMotionAdapterType = "ZMotion";

        /// <summary>早期轴参数 JSON 保存的正运动厂商文字，用于识别旧版回零配置。</summary>
        private const string ZMotionVendorCode = "正运动";

        /// <summary>当前正运动连接支持的控制卡选项，由通用页面读取显示。</summary>
        private static readonly IReadOnlyList<MotionCardSelectionOption> SupportedCardOptions =
            new List<MotionCardSelectionOption>
            {
                new MotionCardSelectionOption
                {
                    VendorCode = ZMotionVendorCode,
                    VendorDisplayResourceKey = "厂商正运动",
                    CardTypeCode = "总线型",
                    CardTypeDisplayResourceKey = "总线型",
                    AdapterType = ZMotionAdapterType,
                    SupportedAxisCounts = Array.AsReadOnly(new[] { 8, 16, 32, 64 }),
                    DefaultAxisCount = 64
                }
            }.AsReadOnly();

        /// <summary>当前 Host 所属设备的 JSON 配置存储。</summary>
        private readonly MachineConfigurationStore _configurationStore;

        /// <summary>当前 Host 所属设备的唯一编号。</summary>
        private readonly string _machineId;

        /// <summary>当前由本页面连接并维护的正运动仿真器实例。</summary>
        private ZMotionCard _card;

        /// <summary>当前连接的控制卡标识，用于把新 IO 映射应用到对应卡实例。</summary>
        private string _connectedControllerId;

        /// <summary>控制卡连接成功后预扫描并缓存的 I/O 能力。</summary>
        private MotionCardIoCapabilities _cachedIoCapabilities;

        /// <summary>当前已连接卡上实际创建的逻辑轴硬件定义。</summary>
        private IReadOnlyList<AxisHardwareDefinition> _axisHardwareDefinitions =
            new List<AxisHardwareDefinition>().AsReadOnly();

        /// <summary>当前卡上配置的逻辑 DI 点位。</summary>
        private IReadOnlyList<DigitalIoHardwareDefinition> _digitalInputs =
            new List<DigitalIoHardwareDefinition>().AsReadOnly();

        /// <summary>当前卡上配置的逻辑 DO 点位。</summary>
        private IReadOnlyList<DigitalIoHardwareDefinition> _digitalOutputs =
            new List<DigitalIoHardwareDefinition>().AsReadOnly();

        /// <summary>创建使用指定 MachineId 保存连接资料的正运动连接服务。</summary>
        public ZMotionAxisMappingCardConnection(MachineConfigurationStore configurationStore, string machineId)
        {
            if (configurationStore == null)
            {
                throw new ArgumentNullException(nameof(configurationStore));
            }

            if (string.IsNullOrWhiteSpace(machineId))
            {
                throw new ArgumentException("设备编号不能为空。", nameof(machineId));
            }

            _configurationStore = configurationStore;
            _machineId = machineId;
        }

        /// <summary>当前正运动仿真器是否保持连接。</summary>
        public bool IsConnected
        {
            get { return _card != null && _card.IsConnected; }
        }

        /// <summary>返回当前 Host 选择的正运动卡型及其轴数量选项。</summary>
        public IReadOnlyList<MotionCardSelectionOption> SupportedMotionCards
        {
            get { return SupportedCardOptions; }
        }

        /// <summary>提供给 Host 运行时和手动调试页共用的当前控制卡实例。</summary>
        public IMotionCard ConnectedMotionCard
        {
            get { return _card; }
        }

        /// <summary>提供当前正运动卡上的逻辑 DI/DO 读写入口。</summary>
        public ILogicalIo ConnectedLogicalIo
        {
            get { return _card; }
        }

        /// <summary>返回创建在当前卡实例上的已映射逻辑轴定义。</summary>
        public IReadOnlyList<AxisHardwareDefinition> AxisHardwareDefinitions
        {
            get { return _axisHardwareDefinitions; }
        }

        /// <summary>返回创建在当前卡实例上的逻辑 DI 映射。</summary>
        public IReadOnlyList<DigitalIoHardwareDefinition> DigitalInputs
        {
            get { return _digitalInputs; }
        }

        /// <summary>返回创建在当前卡实例上的逻辑 DO 映射。</summary>
        public IReadOnlyList<DigitalIoHardwareDefinition> DigitalOutputs
        {
            get { return _digitalOutputs; }
        }

        /// <summary>连接后从仿真控制器读取的型号标识。</summary>
        public string ActualControllerType
        {
            get { return _card == null ? string.Empty : _card.ActualControllerType; }
        }

        /// <summary>连接后从仿真控制器读取的版本信息。</summary>
        public string ActualControllerVersion
        {
            get { return _card == null ? string.Empty : _card.ActualControllerVersion; }
        }

        /// <summary>校验单卡配置后连接正运动仿真器；发现多卡配置时拒绝只连接其中一张。</summary>
        public async Task ConnectAsync(MotionCardSettingsDocument settings, CancellationToken cancellationToken)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            if (settings.MotionCards != null && settings.MotionCards.Count > 1)
            {
                throw new NotSupportedException(
                    "当前正运动 Host 暂不支持同时连接多张运动控制卡；配置中检测到 "
                    + settings.MotionCards.Count
                    + " 张。为避免只连接第一张卡而遗漏其他卡，已拒绝连接。");
            }

            MotionCardSelectionOption selectedCard = SupportedCardOptions.FirstOrDefault(option =>
                string.Equals(option.VendorCode, settings.MotionCardVendor, StringComparison.Ordinal)
                && string.Equals(option.CardTypeCode, settings.MotionCardType, StringComparison.Ordinal));
            if (selectedCard == null)
            {
                throw new NotSupportedException(
                    "所选控制卡不在当前 Host 已接入的卡型清单中。请检查厂商、卡类型及 Host 适配器配置。");
            }

            if (IsConnected)
            {
                return;
            }

            MotionCardConnectionDefinition connectionDefinition = GetConnectionDefinition(settings);
            if (!string.IsNullOrWhiteSpace(connectionDefinition.AdapterType)
                && !string.Equals(connectionDefinition.AdapterType, selectedCard.AdapterType, StringComparison.Ordinal))
            {
                throw new NotSupportedException(
                    "设备配置要求适配器 '" + connectionDefinition.AdapterType
                    + "'，当前 Host 选择的是 '" + selectedCard.AdapterType + "'。");
            }

            connectionDefinition.AdapterType = selectedCard.AdapterType;
            if (string.IsNullOrWhiteSpace(connectionDefinition.ConnectionConfigurationFile))
            {
                connectionDefinition.ConnectionConfigurationFile = DefaultConnectionConfigurationFile;
            }

            ZMotionCardConfiguration connectionConfiguration = LoadConnectionConfiguration(
                connectionDefinition.ConnectionConfigurationFile);
            connectionConfiguration.Validate();

            LoadedMotionMappings mappings = LoadMotionMappings(
                connectionDefinition.ControllerId,
                connectionDefinition.AdapterType);

            var newCard = new ZMotionCard(
                connectionDefinition.ControllerId,
                connectionConfiguration,
                mappings.AxisConfigurations,
                mappings.DigitalInputs,
                mappings.DigitalOutputs);

            try
            {
                await newCard.ConnectAsync(cancellationToken).ConfigureAwait(false);
                await newCard.ApplyAxisParametersAsync(cancellationToken).ConfigureAwait(false);
                SaveConnectionConfiguration(
                    connectionDefinition.ConnectionConfigurationFile,
                    connectionConfiguration);
                _configurationStore.SaveJson(_machineId, MotionCardsFilePath, settings);
                _card = newCard;
                _connectedControllerId = connectionDefinition.ControllerId;
                _axisHardwareDefinitions = mappings.AxisHardwareDefinitions.AsReadOnly();
                _digitalInputs = mappings.AxisDigitalInputs.AsReadOnly();
                _digitalOutputs = mappings.AxisDigitalOutputs.AsReadOnly();

                try
                {
                    _cachedIoCapabilities = await newCard.ReadIoCapabilitiesAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception capabilityError)
                {
                    System.Diagnostics.Trace.TraceError(
                        "正运动控制卡已连接，但启动时 I/O 能力扫描失败：" + capabilityError);
                }
            }
            catch (Exception connectionError)
            {
                if (newCard.IsConnected)
                {
                    try
                    {
                        await newCard.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception disconnectError)
                    {
                        throw new AggregateException(
                            "正运动连接失败，且回收连接句柄时也发生错误。",
                            connectionError,
                            disconnectError);
                    }
                }

                throw;
            }
        }

        /// <summary>断开当前仿真器连接，并释放控制器句柄。</summary>
        public async Task DisconnectAsync(CancellationToken cancellationToken)
        {
            if (_card == null)
            {
                return;
            }

            ZMotionCard currentCard = _card;
            await currentCard.DisconnectAsync(cancellationToken).ConfigureAwait(false);
            _card = null;
            _connectedControllerId = null;
            _cachedIoCapabilities = null;
            _axisHardwareDefinitions = new List<AxisHardwareDefinition>().AsReadOnly();
            _digitalInputs = new List<DigitalIoHardwareDefinition>().AsReadOnly();
            _digitalOutputs = new List<DigitalIoHardwareDefinition>().AsReadOnly();
        }

        /// <summary>确认所有已映射轴停止后重连，重新加载轴、DI/DO 映射并应用轴参数。</summary>
        public async Task ReconnectAsync(
            MotionCardSettingsDocument settings,
            CancellationToken cancellationToken)
        {
            if (_card != null && _card.IsConnected)
            {
                foreach (IAxis axis in _card.Axes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AxisStatus status = await axis.ReadStatusAsync(cancellationToken).ConfigureAwait(false);
                    if (status.Moving)
                    {
                        throw new InvalidOperationException(
                            "逻辑轴 '" + axis.Name + "' 仍在运动，停止所有轴后再保存并应用轴映射。");
                    }
                }

                await DisconnectAsync(cancellationToken).ConfigureAwait(false);
            }

            await ConnectAsync(settings, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>确认已映射轴全部停止后，在当前正运动卡实例内替换 DI/DO 逻辑名称映射。</summary>
        public async Task ApplyIoMappingsAsync(
            IReadOnlyList<DigitalIoHardwareDefinition> digitalInputs,
            IReadOnlyList<DigitalIoHardwareDefinition> digitalOutputs,
            CancellationToken cancellationToken)
        {
            if (digitalInputs == null)
            {
                throw new ArgumentNullException(nameof(digitalInputs));
            }

            if (digitalOutputs == null)
            {
                throw new ArgumentNullException(nameof(digitalOutputs));
            }

            EnsureConnected();

            var mappings = new LoadedMotionMappings();
            AddIoMappings(mappings, digitalInputs, _connectedControllerId, true);
            AddIoMappings(mappings, digitalOutputs, _connectedControllerId, false);

            foreach (IAxis axis in _card.Axes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AxisStatus status = await axis.ReadStatusAsync(cancellationToken).ConfigureAwait(false);
                if (status.Moving)
                {
                    throw new InvalidOperationException(
                        "逻辑轴 '" + axis.Name + "' 仍在运动，停止所有轴后再保存 IO 映射。");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            _card.UpdateIoMappings(mappings.DigitalInputs, mappings.DigitalOutputs);
            _digitalInputs = mappings.AxisDigitalInputs.AsReadOnly();
            _digitalOutputs = mappings.AxisDigitalOutputs.AsReadOnly();
        }

        /// <summary>把设备已保存的轴和 DI/DO 映射转换成当前正运动卡实例所需的对象。</summary>
        private LoadedMotionMappings LoadMotionMappings(string controllerId, string adapterType)
        {
            var result = new LoadedMotionMappings();
            string axisPath = GetConfigurationPath(AxisParametersFilePath);
            if (File.Exists(axisPath))
            {
                AxisMappingConfigurationDocument axisDocument = _configurationStore
                    .LoadJson<AxisMappingConfigurationDocument>(_machineId, AxisParametersFilePath);
                if (axisDocument == null || axisDocument.SchemaVersion != 1 || axisDocument.Axes == null)
                {
                    throw new InvalidOperationException("AxisParameters.json 格式无效，不能创建手动调试轴对象。");
                }

                foreach (AxisMappingEntry axis in axisDocument.Axes)
                {
                    if (axis == null)
                    {
                        throw new InvalidOperationException("AxisParameters.json 中包含空轴配置。");
                    }

                    if (!axis.IsConfigured
                        || !string.Equals(axis.ControllerId, controllerId, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!axis.UnitsPerEngineeringUnit.HasValue
                        || !axis.MinimumPosition.HasValue
                        || !axis.MaximumPosition.HasValue
                        || !axis.SoftLimitEnabled.HasValue
                        || !axis.ManualSpeed.HasValue)
                    {
                        throw new InvalidOperationException(
                            "逻辑轴 '" + axis.Name + "' 的单位换算、软限位或手动速度尚未配置完整。");
                    }

                    string homeModeAdapterType = GetEffectiveHomeModeAdapterType(axis, adapterType);
                    if (!axis.HomeMode.HasValue
                        || !string.Equals(homeModeAdapterType, adapterType, StringComparison.Ordinal)
                        || !ZMotionHomeModeCatalog.IsSupported(axis.HomeMode.Value))
                    {
                        throw new InvalidOperationException(
                            "逻辑轴 '" + axis.Name
                            + "' 的回零模式未配置，或其模式编号不属于当前运动卡适配器。");
                    }

                    result.AxisConfigurations.Add(new ZMotionAxisConfiguration
                    {
                        ControllerId = axis.ControllerId,
                        Name = axis.Name,
                        AxisNumber = axis.AxisNumber,
                        UnitsPerEngineeringUnit = axis.UnitsPerEngineeringUnit.Value,
                        MinimumPosition = axis.MinimumPosition.Value,
                        MaximumPosition = axis.MaximumPosition.Value,
                        SoftLimitEnabled = axis.SoftLimitEnabled.Value,
                        ManualSpeed = axis.ManualSpeed.Value,
                        HomeMode = axis.HomeMode.GetValueOrDefault(),
                        HomeSearchSpeed = axis.HomeSearchSpeed.GetValueOrDefault(),
                        HomeCreepSpeed = axis.HomeCreepSpeed.GetValueOrDefault(),
                        HomeAcceleration = axis.HomeAcceleration.GetValueOrDefault(),
                        HomeDeceleration = axis.HomeDeceleration.GetValueOrDefault()
                    });
                    result.AxisHardwareDefinitions.Add(new AxisHardwareDefinition
                    {
                        Name = axis.Name,
                        ControllerId = axis.ControllerId,
                        AxisNumber = axis.AxisNumber,
                        EngineeringUnit = axis.EngineeringUnit,
                        MinimumPosition = axis.MinimumPosition.Value,
                        MaximumPosition = axis.MaximumPosition.Value,
                        SoftLimitEnabled = axis.SoftLimitEnabled.Value,
                        ManualSpeed = axis.ManualSpeed.Value,
                        ManualAcceleration = axis.HomeAcceleration.GetValueOrDefault(),
                        ManualDeceleration = axis.HomeDeceleration.GetValueOrDefault()
                    });
                }
            }

            string ioPath = GetConfigurationPath(IoMappingFilePath);
            if (!File.Exists(ioPath))
            {
                return result;
            }

            IoMappingConfigurationDocument ioDocument = _configurationStore
                .LoadJson<IoMappingConfigurationDocument>(_machineId, IoMappingFilePath);
            if (ioDocument == null || ioDocument.SchemaVersion != 1
                || ioDocument.DigitalInputs == null || ioDocument.DigitalOutputs == null)
            {
                throw new InvalidOperationException("IoMapping.json 格式无效，不能创建手动调试 IO 对象。");
            }

            AddIoMappings(result, ioDocument.DigitalInputs, controllerId, true);
            AddIoMappings(result, ioDocument.DigitalOutputs, controllerId, false);
            return result;
        }

        /// <summary>为旧版 ZMotion 轴配置补全适配器标识，其他情况不跨适配器推断。</summary>
        private static string GetEffectiveHomeModeAdapterType(AxisMappingEntry axis, string adapterType)
        {
            if (axis == null)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(axis.HomeModeAdapterType))
            {
                return axis.HomeModeAdapterType;
            }

            if (string.Equals(adapterType, ZMotionAdapterType, StringComparison.Ordinal)
                && string.Equals(axis.HomeModeVendor, ZMotionVendorCode, StringComparison.Ordinal))
            {
                return ZMotionAdapterType;
            }

            return null;
        }

        /// <summary>把同一张卡的逻辑 DI 或 DO 映射复制到适配器和手动页配置中。</summary>
        private static void AddIoMappings(
            LoadedMotionMappings mappings,
            IEnumerable<DigitalIoHardwareDefinition> points,
            string controllerId,
            bool isInput)
        {
            foreach (DigitalIoHardwareDefinition point in points)
            {
                if (point == null)
                {
                    throw new InvalidOperationException("IoMapping.json 中包含空 IO 配置。");
                }

                if (!string.Equals(point.ControllerId, controllerId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var adapterPoint = new ZMotionIoPointConfiguration
                {
                    ControllerId = point.ControllerId,
                    Name = point.Name,
                    Channel = point.Channel,
                    ActiveHigh = point.ActiveHigh
                };
                if (isInput)
                {
                    mappings.DigitalInputs.Add(adapterPoint);
                    mappings.AxisDigitalInputs.Add(point);
                }
                else
                {
                    mappings.DigitalOutputs.Add(adapterPoint);
                    mappings.AxisDigitalOutputs.Add(point);
                }
            }
        }

        /// <summary>取得当前 MachineId 下指定硬件配置文件的完整路径。</summary>
        private string GetConfigurationPath(string relativeFilePath)
        {
            return Path.Combine(
                _configurationStore.GetMachineDirectory(_machineId),
                relativeFilePath.Replace('/', Path.DirectorySeparatorChar));
        }

        /// <summary>暂存从设备配置文件转换出的卡轴、逻辑 IO 和页面显示定义。</summary>
        private sealed class LoadedMotionMappings
        {
            /// <summary>创建空的已映射轴和 IO 集合。</summary>
            public LoadedMotionMappings()
            {
                AxisConfigurations = new List<ZMotionAxisConfiguration>();
                AxisHardwareDefinitions = new List<AxisHardwareDefinition>();
                DigitalInputs = new List<ZMotionIoPointConfiguration>();
                DigitalOutputs = new List<ZMotionIoPointConfiguration>();
                AxisDigitalInputs = new List<DigitalIoHardwareDefinition>();
                AxisDigitalOutputs = new List<DigitalIoHardwareDefinition>();
            }

            /// <summary>交给正运动适配器创建的逻辑轴配置。</summary>
            public List<ZMotionAxisConfiguration> AxisConfigurations { get; private set; }

            /// <summary>交给手动调试页检查软限位和显示物理轴号的配置。</summary>
            public List<AxisHardwareDefinition> AxisHardwareDefinitions { get; private set; }

            /// <summary>交给正运动适配器创建的逻辑 DI 配置。</summary>
            public List<ZMotionIoPointConfiguration> DigitalInputs { get; private set; }

            /// <summary>交给正运动适配器创建的逻辑 DO 配置。</summary>
            public List<ZMotionIoPointConfiguration> DigitalOutputs { get; private set; }

            /// <summary>交给手动调试页显示的逻辑 DI 配置。</summary>
            public List<DigitalIoHardwareDefinition> AxisDigitalInputs { get; private set; }

            /// <summary>交给手动调试页显示的逻辑 DO 配置。</summary>
            public List<DigitalIoHardwareDefinition> AxisDigitalOutputs { get; private set; }

        }

        /// <summary>读取指定物理轴的 UNITS、软限位、速度、爬行速度和加减速度。</summary>
        public async Task<AxisMappingCardParameters> ReadAxisParametersAsync(
            int axisNumber,
            CancellationToken cancellationToken)
        {
            EnsureConnected();
            ZMotionPhysicalAxisParameters currentParameters = await _card
                .ReadPhysicalAxisParametersAsync(axisNumber, cancellationToken)
                .ConfigureAwait(false);

            return new AxisMappingCardParameters
            {
                AxisNumber = currentParameters.AxisNumber,
                UnitsPerEngineeringUnit = currentParameters.UnitsPerEngineeringUnit,
                MinimumPosition = currentParameters.MinimumPosition,
                MaximumPosition = currentParameters.MaximumPosition,
                SoftLimitEnabled = currentParameters.SoftLimitEnabled,
                PositiveSoftLimitEffective = currentParameters.PositiveSoftLimitEffective,
                NegativeSoftLimitEffective = currentParameters.NegativeSoftLimitEffective,
                SoftLimitStatusDiagnostic = currentParameters.SoftLimitStatusDiagnostic,
                Speed = currentParameters.Speed,
                CreepSpeed = currentParameters.CreepSpeed,
                Acceleration = currentParameters.Acceleration,
                Deceleration = currentParameters.Deceleration
            };
        }

        /// <summary>按正运动适配器清单返回 DATUM 编号及中英文名称、动作说明的资源键。</summary>
        public IReadOnlyList<AxisHomeModeOption> GetSupportedHomeModes(
            MotionCardSelectionOption motionCard,
            int axisNumber)
        {
            if (motionCard == null || axisNumber < 0 || axisNumber >= 64
                || !SupportedCardOptions.Any(option =>
                    string.Equals(option.AdapterType, motionCard.AdapterType, StringComparison.Ordinal)
                    && string.Equals(option.VendorCode, motionCard.VendorCode, StringComparison.Ordinal)
                    && string.Equals(option.CardTypeCode, motionCard.CardTypeCode, StringComparison.Ordinal)))
            {
                return new List<AxisHomeModeOption>().AsReadOnly();
            }

            var options = new List<AxisHomeModeOption>();
            foreach (int modeNumber in ZMotionHomeModeCatalog.SupportedModeNumbers)
            {
                options.Add(new AxisHomeModeOption
                {
                    ModeNumber = modeNumber,
                    DisplayNameResourceKey = "正运动DATUM模式" + modeNumber + "名称",
                    DescriptionResourceKey = "正运动DATUM模式" + modeNumber + "说明"
                });
            }

            return options.AsReadOnly();
        }

        /// <summary>委托正运动适配器查询控制器规格、连续可映射通道和总线 DI/DO 能力。</summary>
        public Task<MotionCardIoCapabilities> ReadIoCapabilitiesAsync(CancellationToken cancellationToken)
        {
            EnsureConnected();
            if (_cachedIoCapabilities != null)
            {
                return Task.FromResult(_cachedIoCapabilities);
            }

            return _card.ReadIoCapabilitiesAsync(cancellationToken);
        }

        /// <summary>重新调用当前正运动控制器 API，并替换连接期间缓存的 I/O 能力。</summary>
        public async Task<MotionCardIoCapabilities> RefreshIoCapabilitiesAsync(CancellationToken cancellationToken)
        {
            EnsureConnected();
            _cachedIoCapabilities = null;
            _cachedIoCapabilities = await _card.RefreshIoCapabilitiesAsync(cancellationToken)
                .ConfigureAwait(false);
            return _cachedIoCapabilities;
        }

        /// <summary>取得页面当前使用的卡连接资料；首次连接时创建正运动默认项。</summary>
        private static MotionCardConnectionDefinition GetConnectionDefinition(MotionCardSettingsDocument settings)
        {
            if (settings.MotionCards == null)
            {
                settings.MotionCards = new List<MotionCardConnectionDefinition>();
            }

            MotionCardConnectionDefinition definition = settings.MotionCards.FirstOrDefault();
            if (definition == null)
            {
                definition = new MotionCardConnectionDefinition
                {
                    ControllerId = "MotionCard01"
                };
                settings.MotionCards.Add(definition);
            }

            if (string.IsNullOrWhiteSpace(definition.ControllerId))
            {
                definition.ControllerId = "MotionCard01";
            }

            return definition;
        }

        /// <summary>读取已有正运动连接文件；首次使用时返回仿真器默认地址配置。</summary>
        private ZMotionCardConfiguration LoadConnectionConfiguration(string relativeFilePath)
        {
            try
            {
                return _configurationStore.LoadJson<ZMotionCardConfiguration>(_machineId, relativeFilePath);
            }
            catch (FileNotFoundException)
            {
                return new ZMotionCardConfiguration();
            }
        }

        /// <summary>把正运动连接参数写入当前设备自己的 JSON 配置目录。</summary>
        private void SaveConnectionConfiguration(
            string relativeFilePath,
            ZMotionCardConfiguration connectionConfiguration)
        {
            _configurationStore.SaveJson(_machineId, relativeFilePath, connectionConfiguration);
        }

        /// <summary>在执行读写前确认控制器连接仍然有效。</summary>
        private void EnsureConnected()
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException("运动控制卡尚未连接，请先连接控制卡再操作轴参数。");
            }
        }
    }
}
