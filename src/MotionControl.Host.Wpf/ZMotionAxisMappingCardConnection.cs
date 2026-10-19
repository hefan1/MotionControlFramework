using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Adapter.MotionCard.ZMotion;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.Framework.Wpf.Configuration;

namespace MotionControl.Host.Wpf
{
    /// <summary>由 Host 选择正运动适配器，并为轴映射页面提供连接和参数读写。</summary>
    public sealed class ZMotionAxisMappingCardConnection : IAxisMappingCardConnection, IMotionCardIoCapabilityProvider
    {
        /// <summary>运动卡选择配置在单机配置目录中的相对路径。</summary>
        private const string MotionCardsFilePath = "HardwareConfig/MotionCards.json";

        /// <summary>首次连接正运动仿真器时使用的连接参数文件。</summary>
        private const string DefaultConnectionConfigurationFile = "HardwareConfig/ZDevelopSimulation.json";

        /// <summary>当前 Host 所属设备的 JSON 配置存储。</summary>
        private readonly MachineConfigurationStore _configurationStore;

        /// <summary>当前 Host 所属设备的唯一编号。</summary>
        private readonly string _machineId;

        /// <summary>当前由本页面连接并维护的正运动仿真器实例。</summary>
        private ZMotionCard _card;

        /// <summary>控制卡连接成功后预扫描并缓存的 I/O 能力。</summary>
        private MotionCardIoCapabilities _cachedIoCapabilities;

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

        /// <summary>校验所选厂商和卡类型，连接正运动仿真器，并保存连接资料供下次启动使用。</summary>
        public async Task ConnectAsync(MotionCardSettingsDocument settings, CancellationToken cancellationToken)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            if (!string.Equals(settings.MotionCardVendor, "正运动", StringComparison.Ordinal))
            {
                throw new NotSupportedException(
                    "当前 Host 已接入正运动仿真适配器；雷赛、凌华和其他厂商尚未接入可连接的卡适配器。");
            }

            if (!string.Equals(settings.MotionCardType, "总线型", StringComparison.Ordinal))
            {
                throw new NotSupportedException("当前正运动适配器只支持总线型 ZDevelop 仿真控制器。");
            }

            if (IsConnected)
            {
                return;
            }

            MotionCardConnectionDefinition connectionDefinition = GetConnectionDefinition(settings);
            connectionDefinition.AdapterType = "ZMotion";
            if (string.IsNullOrWhiteSpace(connectionDefinition.ConnectionConfigurationFile))
            {
                connectionDefinition.ConnectionConfigurationFile = DefaultConnectionConfigurationFile;
            }

            ZMotionCardConfiguration connectionConfiguration = LoadConnectionConfiguration(
                connectionDefinition.ConnectionConfigurationFile);
            connectionConfiguration.Validate();

            var newCard = new ZMotionCard(
                connectionDefinition.ControllerId,
                connectionConfiguration,
                new ZMotionAxisConfiguration[0],
                new ZMotionIoPointConfiguration[0],
                new ZMotionIoPointConfiguration[0]);

            try
            {
                await newCard.ConnectAsync(cancellationToken).ConfigureAwait(false);
                SaveConnectionConfiguration(
                    connectionDefinition.ConnectionConfigurationFile,
                    connectionConfiguration);
                _configurationStore.SaveJson(_machineId, MotionCardsFilePath, settings);
                _card = newCard;

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
            _cachedIoCapabilities = null;
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

        /// <summary>显式下发选中物理轴的单位换算、软限位和当前运动参数。</summary>
        public Task WriteAxisParametersAsync(
            AxisMappingCardParameters parameters,
            CancellationToken cancellationToken)
        {
            if (parameters == null)
            {
                throw new ArgumentNullException(nameof(parameters));
            }

            EnsureConnected();
            var physicalParameters = new ZMotionPhysicalAxisParameters
            {
                AxisNumber = parameters.AxisNumber,
                UnitsPerEngineeringUnit = parameters.UnitsPerEngineeringUnit,
                MinimumPosition = parameters.MinimumPosition,
                MaximumPosition = parameters.MaximumPosition,
                SoftLimitEnabled = parameters.SoftLimitEnabled,
                Speed = parameters.Speed,
                CreepSpeed = parameters.CreepSpeed,
                Acceleration = parameters.Acceleration,
                Deceleration = parameters.Deceleration
            };

            return _card.WritePhysicalAxisParametersAsync(physicalParameters, cancellationToken);
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
