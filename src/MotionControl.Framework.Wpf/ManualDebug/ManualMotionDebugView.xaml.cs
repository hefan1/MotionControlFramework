using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MotionControl.Framework.Contracts.Configuration;
using MotionControl.Framework.Contracts.IO;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.Framework.Runtime.Machine;

namespace MotionControl.Framework.Wpf.ManualDebug
{
    /// <summary>按已映射逻辑轴生成轴手动卡片，不在轴调试页混放 DI/DO 控件。</summary>
    public partial class ManualMotionDebugView : UserControl
    {
        /// <summary>人工示教位置配置在单台设备目录中的相对路径。</summary>
        private const string ManualAxisPositionsFilePath = "Parameters/ManualAxisPositions.json";

        /// <summary>轴卡片关联 IO 配置在单台设备目录中的相对路径。</summary>
        private const string ManualAxisIoAssociationsFilePath = "Parameters/ManualAxisIoAssociations.json";

        /// <summary>手动页可见时约每 200 毫秒刷新轴位置和状态。</summary>
        private readonly DispatcherTimer _refreshTimer;

        /// <summary>按逻辑轴名称保存硬件配置，用于显示物理通道并检查软限位。</summary>
        private readonly Dictionary<string, AxisHardwareDefinition> _axisConfigurationByName =
            new Dictionary<string, AxisHardwareDefinition>(StringComparer.OrdinalIgnoreCase);

        /// <summary>按控制卡和物理轴标识保存该轴的人工示教位置组。</summary>
        private readonly Dictionary<string, ManualAxisPositionGroup> _positionGroupByAxisKey =
            new Dictionary<string, ManualAxisPositionGroup>(StringComparer.OrdinalIgnoreCase);

        /// <summary>按控制卡和物理轴标识保存该轴卡片的逻辑 IO 关联。</summary>
        private readonly Dictionary<string, ManualAxisIoAssociationGroup> _ioAssociationGroupByAxisKey =
            new Dictionary<string, ManualAxisIoAssociationGroup>(StringComparer.OrdinalIgnoreCase);

        /// <summary>当前设备已命名的逻辑 DI 点。</summary>
        private readonly List<DigitalIoHardwareDefinition> _digitalInputs;

        /// <summary>当前设备已命名的逻辑 DO 点。</summary>
        private readonly List<DigitalIoHardwareDefinition> _digitalOutputs;

        /// <summary>页面当前显示的轴卡片，用于轮询状态和离开页面时停止点动。</summary>
        private readonly List<ManualAxisControl> _axisControls = new List<ManualAxisControl>();

        /// <summary>提供整机复位和轴手动操作联锁的运行上下文。</summary>
        private readonly MachineRuntime _machineRuntime;

        /// <summary>提供本设备的逻辑轴及运动卡状态。</summary>
        private readonly MotionCardGroup _motionCards;

        /// <summary>按逻辑名称读取和写入当前设备 DI/DO 的通用服务。</summary>
        private readonly ILogicalIo _logicalIo;

        /// <summary>按 MachineId 读取和备份保存本设备的位置配置。</summary>
        private readonly MachineConfigurationStore _configurationStore;

        /// <summary>当前页面所属的设备编号。</summary>
        private readonly string _machineId;

        /// <summary>当前设备所有轴的示教位置配置。</summary>
        private ManualAxisPositionConfigurationDocument _positionConfiguration;

        /// <summary>当前设备所有轴卡片的关联 IO 配置。</summary>
        private ManualAxisIoAssociationConfigurationDocument _ioAssociationConfiguration;

        /// <summary>位置配置读取失败时禁止保存，避免覆盖现场原配置。</summary>
        private bool _positionConfigurationCanBeSaved = true;

        /// <summary>关联 IO 配置读取失败时禁止覆盖原文件。</summary>
        private bool _ioAssociationConfigurationCanBeSaved = true;

        /// <summary>防止上一次状态刷新未完成时再次启动刷新。</summary>
        private bool _refreshInProgress;

        /// <summary>创建轴手动调试页，并为每个已映射轴创建独立卡片。</summary>
        public ManualMotionDebugView(
            MachineRuntime machineRuntime,
            MotionCardGroup motionCards,
            IEnumerable<AxisHardwareDefinition> axisConfigurations,
            ILogicalIo logicalIo,
            IEnumerable<DigitalIoHardwareDefinition> digitalInputs,
            IEnumerable<DigitalIoHardwareDefinition> digitalOutputs,
            MachineConfigurationStore configurationStore,
            string machineId)
        {
            if (machineRuntime == null)
            {
                throw new ArgumentNullException(nameof(machineRuntime));
            }

            if (motionCards == null)
            {
                throw new ArgumentNullException(nameof(motionCards));
            }

            if (logicalIo == null)
            {
                throw new ArgumentNullException(nameof(logicalIo));
            }

            if (axisConfigurations == null)
            {
                throw new ArgumentNullException(nameof(axisConfigurations));
            }

            if (configurationStore == null)
            {
                throw new ArgumentNullException(nameof(configurationStore));
            }

            if (string.IsNullOrWhiteSpace(machineId))
            {
                throw new ArgumentException("设备编号不能为空。", nameof(machineId));
            }

            InitializeComponent();
            _machineRuntime = machineRuntime;
            _motionCards = motionCards;
            _logicalIo = logicalIo;
            _configurationStore = configurationStore;
            _machineId = machineId;
            _digitalInputs = GetNamedIoPoints(digitalInputs);
            _digitalOutputs = GetNamedIoPoints(digitalOutputs);

            LoadAxisConfigurations(axisConfigurations);
            LoadPositionConfiguration();
            LoadAxisIoAssociationConfiguration();
            BuildAxisControls();

            _refreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(200)
            };
            _refreshTimer.Tick += RefreshTimer_Tick;
            Loaded += ManualMotionDebugView_Loaded;
            Unloaded += ManualMotionDebugView_Unloaded;
        }

        /// <summary>把各轴软限位配置按逻辑名称保存，并确认每个运行轴都有配置。</summary>
        private void LoadAxisConfigurations(IEnumerable<AxisHardwareDefinition> axisConfigurations)
        {
            foreach (AxisHardwareDefinition axisConfiguration in axisConfigurations)
            {
                if (axisConfiguration == null)
                {
                    throw new ArgumentException("手动调试页的轴配置包含空对象。", nameof(axisConfigurations));
                }

                if (string.IsNullOrWhiteSpace(axisConfiguration.Name))
                {
                    throw new ArgumentException("手动调试页的轴配置缺少逻辑轴名称。", nameof(axisConfigurations));
                }

                if (_axisConfigurationByName.ContainsKey(axisConfiguration.Name))
                {
                    throw new ArgumentException("手动调试页的逻辑轴配置重复：" + axisConfiguration.Name + "。", nameof(axisConfigurations));
                }

                _axisConfigurationByName.Add(axisConfiguration.Name, axisConfiguration);
            }

            foreach (IAxis axis in _motionCards.Axes)
            {
                if (!_axisConfigurationByName.ContainsKey(axis.Name))
                {
                    throw new ArgumentException("逻辑轴 '" + axis.Name + "' 缺少手动调试所需的软限位配置。", nameof(axisConfigurations));
                }
            }
        }

        /// <summary>读取当前设备的示教位置；文件首次不存在时保持所有轴的位置列表为空。</summary>
        private void LoadPositionConfiguration()
        {
            string configurationPath = Path.Combine(
                _configurationStore.GetMachineDirectory(_machineId),
                ManualAxisPositionsFilePath.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(configurationPath))
            {
                _positionConfiguration = new ManualAxisPositionConfigurationDocument();
                return;
            }

            try
            {
                _positionConfiguration = _configurationStore.LoadJson<ManualAxisPositionConfigurationDocument>(
                    _machineId,
                    ManualAxisPositionsFilePath);
                ValidatePositionConfiguration(_positionConfiguration);
                foreach (ManualAxisPositionGroup group in _positionConfiguration.Axes)
                {
                    _positionGroupByAxisKey.Add(group.AxisKey, group);
                }
            }
            catch (Exception error)
            {
                _positionConfigurationCanBeSaved = false;
                _positionConfiguration = new ManualAxisPositionConfigurationDocument();
                OperationMessageText.Text = string.Format(GetText("示教位置配置读取失败"), error.Message);
            }
        }

        /// <summary>过滤掉未命名映射，轴卡关联功能只允许选用逻辑名称。</summary>
        private static List<DigitalIoHardwareDefinition> GetNamedIoPoints(
            IEnumerable<DigitalIoHardwareDefinition> ioPoints)
        {
            return (ioPoints ?? Enumerable.Empty<DigitalIoHardwareDefinition>())
                .Where(point => point != null && !string.IsNullOrWhiteSpace(point.Name))
                .ToList();
        }

        /// <summary>读取每轴关联 IO 配置；首次使用时建立空列表且不提前写入文件。</summary>
        private void LoadAxisIoAssociationConfiguration()
        {
            string configurationPath = Path.Combine(
                _configurationStore.GetMachineDirectory(_machineId),
                ManualAxisIoAssociationsFilePath.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(configurationPath))
            {
                _ioAssociationConfiguration = new ManualAxisIoAssociationConfigurationDocument();
                return;
            }

            try
            {
                _ioAssociationConfiguration = _configurationStore
                    .LoadJson<ManualAxisIoAssociationConfigurationDocument>(
                        _machineId,
                        ManualAxisIoAssociationsFilePath);
                ValidateAxisIoAssociationConfiguration(_ioAssociationConfiguration);
                foreach (ManualAxisIoAssociationGroup group in _ioAssociationConfiguration.Axes)
                {
                    _ioAssociationGroupByAxisKey.Add(group.AxisKey, group);
                }
            }
            catch (Exception error)
            {
                _ioAssociationConfigurationCanBeSaved = false;
                _ioAssociationConfiguration = new ManualAxisIoAssociationConfigurationDocument();
                OperationMessageText.Text = string.Format(GetText("轴关联IO配置读取失败"), error.Message);
            }
        }

        /// <summary>检查关联配置结构、稳定标识及 DO 按钮文字，避免覆盖无效文件。</summary>
        private static void ValidateAxisIoAssociationConfiguration(
            ManualAxisIoAssociationConfigurationDocument configuration)
        {
            if (configuration == null || configuration.SchemaVersion != 1 || configuration.Axes == null)
            {
                throw new InvalidOperationException("ManualAxisIoAssociations.json 格式无效或版本不受支持。未覆盖原文件。");
            }

            var axisKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ManualAxisIoAssociationGroup group in configuration.Axes)
            {
                if (group == null || string.IsNullOrWhiteSpace(group.AxisKey)
                    || group.Points == null || !axisKeys.Add(group.AxisKey))
                {
                    throw new InvalidOperationException("轴关联 IO 配置中的轴信息无效或重复。未覆盖原文件。");
                }

                var associationIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var pointKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (ManualAxisIoAssociation point in group.Points)
                {
                    if (point == null || string.IsNullOrWhiteSpace(point.Id)
                        || !associationIds.Add(point.Id)
                        || !Enum.IsDefined(typeof(ManualAxisIoPointType), point.PointType)
                        || string.IsNullOrWhiteSpace(point.PointName)
                        || string.IsNullOrWhiteSpace(point.DisplayName)
                        || !pointKeys.Add(point.PointType + ":" + point.PointName.Trim()))
                    {
                        throw new InvalidOperationException(
                            "轴 '" + group.AxisKey + "' 的关联 IO 信息无效或重复。未覆盖原文件。");
                    }

                    if (point.PointType == ManualAxisIoPointType.DigitalOutput
                        && (string.IsNullOrWhiteSpace(point.OnActionName)
                            || string.IsNullOrWhiteSpace(point.OffActionName)
                            || string.Equals(point.OnActionName, point.OffActionName, StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new InvalidOperationException(
                            "轴 '" + group.AxisKey + "' 的 DO 按钮文字无效。未覆盖原文件。");
                    }
                }
            }
        }

        /// <summary>检查示教位置配置版本、轴名称、位置编号和数值，防止错误覆盖文件。</summary>
        private static void ValidatePositionConfiguration(ManualAxisPositionConfigurationDocument configuration)
        {
            if (configuration == null || configuration.SchemaVersion != 1 || configuration.Axes == null)
            {
                throw new InvalidOperationException("ManualAxisPositions.json 格式无效或版本不受支持。未覆盖原文件。");
            }

            var axisNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ManualAxisPositionGroup group in configuration.Axes)
            {
                if (group == null || string.IsNullOrWhiteSpace(group.AxisKey) || group.Positions == null
                    || group.NextPositionNumber < 1 || !axisNames.Add(group.AxisKey))
                {
                    throw new InvalidOperationException("示教位置配置中的轴信息无效或重复。未覆盖原文件。");
                }

                var positionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var positionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (ManualAxisPosition position in group.Positions)
                {
                    if (position == null || string.IsNullOrWhiteSpace(position.Id)
                        || string.IsNullOrWhiteSpace(position.Name) || !positionIds.Add(position.Id)
                        || !positionNames.Add(position.Name.Trim()))
                    {
                        throw new InvalidOperationException("轴 '" + group.AxisKey + "' 的位置编号或名称无效、重复。未覆盖原文件。");
                    }

                    if (position.Position.HasValue
                        && (double.IsNaN(position.Position.Value) || double.IsInfinity(position.Position.Value)))
                    {
                        throw new InvalidOperationException("轴 '" + group.AxisKey + "' 的位置数值不是有效有限数。未覆盖原文件。");
                    }
                }
            }
        }

        /// <summary>按物理轴号排列已映射轴，并为每个轴创建一个手动调试卡片。</summary>
        private void BuildAxisControls()
        {
            List<IAxis> configuredAxes = _motionCards.Axes
                .OrderBy(axis => _axisConfigurationByName[axis.Name].ControllerId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(axis => _axisConfigurationByName[axis.Name].AxisNumber)
                .ToList();

            NoMappedAxesText.Visibility = configuredAxes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (IAxis axis in configuredAxes)
            {
                AxisHardwareDefinition axisConfiguration = _axisConfigurationByName[axis.Name];
                string axisKey = GetAxisPositionKey(axisConfiguration);
                ManualAxisPositionGroup positionGroup = GetOrCreatePositionGroup(axisKey);
                ManualAxisIoAssociationGroup ioAssociationGroup = GetOrCreateIoAssociationGroup(axisKey);
                var axisControl = new ManualAxisControl(
                    axis,
                    axisConfiguration,
                    _machineRuntime,
                    positionGroup.Positions,
                    ioAssociationGroup,
                    _logicalIo,
                    _digitalInputs,
                    _digitalOutputs,
                    () => AddAxisPosition(axisKey),
                    SavePositionConfiguration,
                    SaveAxisIoAssociationConfiguration,
                    _ioAssociationConfigurationCanBeSaved);
                _axisControls.Add(axisControl);
                AxisCardsPanel.Children.Add(axisControl);
            }
        }

        /// <summary>按控制卡和物理轴号生成稳定键，避免改设备显示名称后丢失示教位置。</summary>
        private static string GetAxisPositionKey(AxisHardwareDefinition axisConfiguration)
        {
            return axisConfiguration.ControllerId + ".Axis"
                + axisConfiguration.AxisNumber.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>读取或在内存中建立指定物理轴的位置组；新增位置前不会写文件。</summary>
        private ManualAxisPositionGroup GetOrCreatePositionGroup(string axisKey)
        {
            ManualAxisPositionGroup group;
            if (_positionGroupByAxisKey.TryGetValue(axisKey, out group))
            {
                return group;
            }

            group = new ManualAxisPositionGroup { AxisKey = axisKey };
            _positionConfiguration.Axes.Add(group);
            _positionGroupByAxisKey.Add(axisKey, group);
            return group;
        }

        /// <summary>读取或在内存中建立指定物理轴的关联 IO 组。</summary>
        private ManualAxisIoAssociationGroup GetOrCreateIoAssociationGroup(string axisKey)
        {
            ManualAxisIoAssociationGroup group;
            if (_ioAssociationGroupByAxisKey.TryGetValue(axisKey, out group))
            {
                return group;
            }

            group = new ManualAxisIoAssociationGroup { AxisKey = axisKey };
            _ioAssociationConfiguration.Axes.Add(group);
            _ioAssociationGroupByAxisKey.Add(axisKey, group);
            return group;
        }

        /// <summary>创建 PositionN 稳定编号和可编辑的默认中文名称，并登记到当前物理轴。</summary>
        private ManualAxisPosition AddAxisPosition(string axisKey)
        {
            ManualAxisPositionGroup group = GetOrCreatePositionGroup(axisKey);
            int positionNumber = group.NextPositionNumber;
            string positionId = "Position" + positionNumber.ToString(CultureInfo.InvariantCulture);
            while (group.Positions.Any(item => string.Equals(item.Id, positionId, StringComparison.OrdinalIgnoreCase)))
            {
                if (positionNumber == int.MaxValue)
                {
                    throw new InvalidOperationException("位置编号已达到允许的最大值，不能继续添加。");
                }

                positionNumber++;
                positionId = "Position" + positionNumber.ToString(CultureInfo.InvariantCulture);
            }

            if (positionNumber == int.MaxValue)
            {
                throw new InvalidOperationException("位置编号已达到允许的最大值，不能继续添加。");
            }

            group.NextPositionNumber = positionNumber + 1;
            var position = new ManualAxisPosition
            {
                Id = positionId,
                Name = string.Format(GetText("默认示教位置名称"), positionNumber),
                Position = null
            };
            group.Positions.Add(position);
            return position;
        }

        /// <summary>保存当前设备全部轴的示教位置，并拒绝覆盖无法读取的原配置。</summary>
        private void SavePositionConfiguration()
        {
            if (!_positionConfigurationCanBeSaved)
            {
                throw new InvalidOperationException(GetText("示教位置配置禁止覆盖"));
            }

            _configurationStore.SaveJson(_machineId, ManualAxisPositionsFilePath, _positionConfiguration);
        }

        /// <summary>验证并保存设备全部轴的关联 IO 配置。</summary>
        private void SaveAxisIoAssociationConfiguration()
        {
            if (!_ioAssociationConfigurationCanBeSaved)
            {
                throw new InvalidOperationException(GetText("轴关联IO配置禁止覆盖"));
            }

            ValidateAxisIoAssociationConfiguration(_ioAssociationConfiguration);
            _configurationStore.SaveJson(
                _machineId,
                ManualAxisIoAssociationsFilePath,
                _ioAssociationConfiguration);
        }

        /// <summary>页面显示时启动状态刷新定时器并立即读取一次设备状态。</summary>
        private async void ManualMotionDebugView_Loaded(object sender, RoutedEventArgs e)
        {
            _refreshTimer.Start();
            await RefreshStatusSafelyAsync().ConfigureAwait(true);
        }

        /// <summary>页面离开视觉树时停止轮询，并停止所有卡片上可能仍按住的点动轴。</summary>
        private async void ManualMotionDebugView_Unloaded(object sender, RoutedEventArgs e)
        {
            _refreshTimer.Stop();
            foreach (ManualAxisControl axisControl in _axisControls)
            {
                await axisControl.StopJogIfActiveAsync().ConfigureAwait(true);
            }
        }

        /// <summary>定时刷新所有轴卡片和整机状态。</summary>
        private async void RefreshTimer_Tick(object sender, EventArgs e)
        {
            await RefreshStatusSafelyAsync().ConfigureAwait(true);
        }

        /// <summary>统一保护首次读取和定时读取，避免同一时间重复查询控制卡。</summary>
        private async Task RefreshStatusSafelyAsync()
        {
            if (_refreshInProgress)
            {
                return;
            }

            _refreshInProgress = true;
            try
            {
                await RefreshAllStatusAsync().ConfigureAwait(true);
            }
            catch (Exception error)
            {
                OperationMessageText.Text = string.Format(GetText("读取状态失败"), error.Message);
            }
            finally
            {
                _refreshInProgress = false;
            }
        }

        /// <summary>读取手动联锁并更新轴卡按钮可用状态和各轴状态。</summary>
        private async Task RefreshAllStatusAsync()
        {
            ManualOperationBlockReason blockReason;
            try
            {
                blockReason = await _machineRuntime
                    .GetManualOperationBlockReasonAsync(CancellationToken.None)
                    .ConfigureAwait(true);
            }
            catch (Exception)
            {
                // 联锁状态读取异常时按禁止普通手动操作处理。
                blockReason = ManualOperationBlockReason.EmergencyStopStatusUnavailable;
            }

            foreach (ManualAxisControl axisControl in _axisControls)
            {
                axisControl.SetManualOperationAvailability(blockReason);
            }

            foreach (ManualAxisControl axisControl in _axisControls)
            {
                try
                {
                    await axisControl.RefreshStatusAsync().ConfigureAwait(true);
                }
                catch (Exception error)
                {
                    axisControl.ShowStatusReadError(error);
                }
            }

        }

        /// <summary>从 Host 当前语言资源中读取界面文字，资源缺失时返回键名供开发者定位。</summary>
        private string GetText(string resourceKey)
        {
            object resourceValue = TryFindResource(resourceKey);
            return resourceValue == null ? resourceKey : resourceValue.ToString();
        }

    }
}
