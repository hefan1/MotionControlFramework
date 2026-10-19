using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MotionControl.Framework.Contracts.Configuration;
using MotionControl.Framework.Contracts.IO;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.Framework.Runtime.Machine;

namespace MotionControl.Framework.Wpf.ManualDebug
{
    /// <summary>按已映射逻辑轴生成手动调试卡片，并提供数字 IO 手动查看和操作。</summary>
    public partial class ManualMotionDebugView : UserControl
    {
        /// <summary>人工示教位置配置在单台设备目录中的相对路径。</summary>
        private const string ManualAxisPositionsFilePath = "Parameters/ManualAxisPositions.json";

        /// <summary>定时刷新轴、输入和输出状态的 WPF 计时器。</summary>
        private readonly DispatcherTimer _refreshTimer;

        /// <summary>按逻辑 DI 名称查找界面状态文字控件。</summary>
        private readonly Dictionary<string, TextBlock> _inputStatusTextByName =
            new Dictionary<string, TextBlock>(StringComparer.OrdinalIgnoreCase);

        /// <summary>按逻辑 DO 名称查找界面状态文字控件。</summary>
        private readonly Dictionary<string, TextBlock> _outputStatusTextByName =
            new Dictionary<string, TextBlock>(StringComparer.OrdinalIgnoreCase);

        /// <summary>按逻辑轴名称保存硬件配置，用于显示物理通道并检查软限位。</summary>
        private readonly Dictionary<string, AxisHardwareDefinition> _axisConfigurationByName =
            new Dictionary<string, AxisHardwareDefinition>(StringComparer.OrdinalIgnoreCase);

        /// <summary>按控制卡和物理轴标识保存该轴的人工示教位置组。</summary>
        private readonly Dictionary<string, ManualAxisPositionGroup> _positionGroupByAxisKey =
            new Dictionary<string, ManualAxisPositionGroup>(StringComparer.OrdinalIgnoreCase);

        /// <summary>页面当前显示的轴卡片，用于轮询状态和离开页面时停止点动。</summary>
        private readonly List<ManualAxisControl> _axisControls = new List<ManualAxisControl>();

        /// <summary>提供复位、启停和整机状态的运行上下文。</summary>
        private readonly MachineRuntime _machineRuntime;

        /// <summary>提供本设备的逻辑轴及运动卡状态。</summary>
        private readonly MotionCardGroup _motionCards;

        /// <summary>按逻辑名称读写数字输入和输出的服务。</summary>
        private readonly ILogicalIo _logicalIo;

        /// <summary>按 MachineId 读取和备份保存本设备的位置配置。</summary>
        private readonly MachineConfigurationStore _configurationStore;

        /// <summary>当前页面所属的设备编号。</summary>
        private readonly string _machineId;

        /// <summary>当前页面展示的逻辑 DI 名称列表。</summary>
        private readonly IReadOnlyList<string> _inputNames;

        /// <summary>当前页面展示的逻辑 DO 名称列表。</summary>
        private readonly IReadOnlyList<string> _outputNames;

        /// <summary>当前设备所有轴的示教位置配置。</summary>
        private ManualAxisPositionConfigurationDocument _positionConfiguration;

        /// <summary>位置配置读取失败时禁止保存，避免覆盖现场原配置。</summary>
        private bool _positionConfigurationCanBeSaved = true;

        /// <summary>防止上一次状态刷新未完成时再次启动刷新。</summary>
        private bool _refreshInProgress;

        /// <summary>创建通用手动调试页，并为每个已映射轴创建独立卡片。</summary>
        public ManualMotionDebugView(
            MachineRuntime machineRuntime,
            MotionCardGroup motionCards,
            ILogicalIo logicalIo,
            IEnumerable<AxisHardwareDefinition> axisConfigurations,
            IEnumerable<string> inputNames,
            IEnumerable<string> outputNames,
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

            if (inputNames == null)
            {
                throw new ArgumentNullException(nameof(inputNames));
            }

            if (outputNames == null)
            {
                throw new ArgumentNullException(nameof(outputNames));
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
            _inputNames = inputNames.ToList().AsReadOnly();
            _outputNames = outputNames.ToList().AsReadOnly();
            _configurationStore = configurationStore;
            _machineId = machineId;

            LoadAxisConfigurations(axisConfigurations);
            LoadPositionConfiguration();
            BuildAxisControls();
            BuildInputRows();
            BuildOutputRows();

            _refreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1000)
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
                var axisControl = new ManualAxisControl(
                    axis,
                    axisConfiguration,
                    _machineRuntime,
                    positionGroup.Positions,
                    () => AddAxisPosition(axisKey),
                    SavePositionConfiguration);
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

        /// <summary>为每个逻辑 DI 建立一行名称和当前状态显示。</summary>
        private void BuildInputRows()
        {
            foreach (string inputName in _inputNames)
            {
                StackPanel row = CreateIoRow(inputName);
                var statusText = new TextBlock
                {
                    Text = "—",
                    Foreground = Brushes.Gray,
                    HorizontalAlignment = HorizontalAlignment.Right
                };
                row.Children.Add(statusText);
                DigitalInputsPanel.Children.Add(row);
                _inputStatusTextByName.Add(inputName, statusText);
            }
        }

        /// <summary>为每个逻辑 DO 建立状态显示及明确的打开、关闭按钮。</summary>
        private void BuildOutputRows()
        {
            foreach (string outputName in _outputNames)
            {
                StackPanel row = CreateIoRow(outputName);
                var stateText = new TextBlock
                {
                    Text = "—",
                    Foreground = Brushes.Gray,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 8, 0)
                };
                row.Children.Add(stateText);
                _outputStatusTextByName.Add(outputName, stateText);
                row.Children.Add(CreateOutputButton("输出打开", outputName, true));
                row.Children.Add(CreateOutputButton("输出关闭", outputName, false));
                DigitalOutputsPanel.Children.Add(row);
            }
        }

        /// <summary>创建 IO 行中的逻辑点位名称文字。</summary>
        private static StackPanel CreateIoRow(string pointName)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 3, 0, 3)
            };
            row.Children.Add(new TextBlock
            {
                Text = pointName,
                Width = 190,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            return row;
        }

        /// <summary>创建带逻辑输出名称和值的按钮。</summary>
        private Button CreateOutputButton(string resourceKey, string outputName, bool value)
        {
            var button = new Button
            {
                Tag = new OutputCommand(outputName, value),
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(2, 0, 2, 0)
            };
            button.SetResourceReference(ContentControl.ContentProperty, resourceKey);
            button.Click += OutputButton_Click;
            return button;
        }

        /// <summary>页面显示时启动状态刷新定时器并立即读取一次设备状态。</summary>
        private async void ManualMotionDebugView_Loaded(object sender, RoutedEventArgs e)
        {
            _refreshTimer.Start();
            try
            {
                await RefreshAllStatusAsync().ConfigureAwait(true);
            }
            catch (Exception error)
            {
                OperationMessageText.Text = string.Format(GetText("读取状态失败"), error.Message);
            }
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

        /// <summary>定时刷新所有轴卡片、DI/DO 和整机状态。</summary>
        private async void RefreshTimer_Tick(object sender, EventArgs e)
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

        /// <summary>更新整机状态、各轴状态和全部已配置 DI/DO 状态。</summary>
        private async Task RefreshAllStatusAsync()
        {
            MachineStateText.Text = GetText("整机状态") + GetText("状态分隔符")
                + GetMachineStateText(_machineRuntime.State);

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

            if (_machineRuntime.State == MachineRunState.Disconnected)
            {
                ShowIoDisconnected();
                return;
            }

            await RefreshIoStatusAsync().ConfigureAwait(true);
        }

        /// <summary>控制卡断开时把所有 DI/DO 行标记为未连接。</summary>
        private void ShowIoDisconnected()
        {
            foreach (TextBlock statusText in _inputStatusTextByName.Values)
            {
                statusText.Text = GetText("控制卡未连接");
                statusText.Foreground = Brushes.Gray;
            }

            foreach (TextBlock statusText in _outputStatusTextByName.Values)
            {
                statusText.Text = GetText("控制卡未连接");
                statusText.Foreground = Brushes.Gray;
            }
        }

        /// <summary>逐个读取已配置的逻辑 DI/DO 状态并更新对应行文字。</summary>
        private async Task RefreshIoStatusAsync()
        {
            foreach (string inputName in _inputNames)
            {
                bool active = await _logicalIo.ReadInputAsync(inputName, CancellationToken.None).ConfigureAwait(true);
                _inputStatusTextByName[inputName].Text = GetBooleanText(active, "有效", "无效");
                _inputStatusTextByName[inputName].Foreground = active ? Brushes.ForestGreen : Brushes.Gray;
            }

            foreach (string outputName in _outputNames)
            {
                bool active = await _logicalIo.ReadOutputAsync(outputName, CancellationToken.None).ConfigureAwait(true);
                _outputStatusTextByName[outputName].Text = GetBooleanText(active, "输出已开", "输出已关");
                _outputStatusTextByName[outputName].Foreground = active ? Brushes.ForestGreen : Brushes.Gray;
            }
        }

        /// <summary>读取逻辑 DO 并在允许手动操作时写入按钮要求的状态。</summary>
        private async void OutputButton_Click(object sender, RoutedEventArgs e)
        {
            Button button = sender as Button;
            OutputCommand command = button == null ? null : button.Tag as OutputCommand;
            if (command == null)
            {
                OperationMessageText.Text = GetText("无法读取当前DO操作参数");
                return;
            }

            await RunIoActionAsync(GetText("写入DO") + " " + command.OutputName, async () =>
            {
                await _logicalIo.WriteOutputAsync(command.OutputName, command.Value, CancellationToken.None)
                    .ConfigureAwait(true);
                bool actualValue = await _logicalIo.ReadOutputAsync(command.OutputName, CancellationToken.None)
                    .ConfigureAwait(true);
                if (actualValue != command.Value)
                {
                    throw new InvalidOperationException(GetText("DO写入后读回不一致"));
                }

                TextBlock statusText = _outputStatusTextByName[command.OutputName];
                statusText.Text = actualValue ? GetText("输出已开") : GetText("输出已关");
                statusText.Foreground = actualValue ? Brushes.ForestGreen : Brushes.Gray;
            }).ConfigureAwait(true);
        }

        /// <summary>执行手动 IO 命令并显示操作结果或异常原因。</summary>
        private async Task RunIoActionAsync(string operationName, Func<Task> operation)
        {
            OperationMessageText.Text = string.Empty;
            try
            {
                EnsureManualOperationAllowed();
                await operation().ConfigureAwait(true);
                OperationMessageText.Text = string.Format(GetText("操作完成"), operationName);
            }
            catch (Exception error)
            {
                OperationMessageText.Text = string.Format(GetText("操作失败"), operationName, error.Message);
            }
        }

        /// <summary>确认控制卡已连接且当前没有运行自动流程或未复位的急停报警。</summary>
        private void EnsureManualOperationAllowed()
        {
            MachineRunState state = _machineRuntime.State;
            if (state == MachineRunState.Disconnected)
            {
                throw new InvalidOperationException(GetText("运动卡未连接操作被拒绝"));
            }

            if (state == MachineRunState.Running || state == MachineRunState.Pausing)
            {
                throw new InvalidOperationException(GetText("自动流程期间禁止手动操作"));
            }

            if (_machineRuntime.AlarmService.Alarms.Any(
                alarm => string.Equals(alarm.Definition.Code, "EMERGENCY_STOP", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(GetText("急停报警未复位禁止手动动作"));
            }
        }

        /// <summary>从 Host 当前语言资源中读取界面文字，资源缺失时返回键名供开发者定位。</summary>
        private string GetText(string resourceKey)
        {
            object resourceValue = TryFindResource(resourceKey);
            return resourceValue == null ? resourceKey : resourceValue.ToString();
        }

        /// <summary>把整机运行状态转换成当前界面语言。</summary>
        private string GetMachineStateText(MachineRunState state)
        {
            switch (state)
            {
                case MachineRunState.Disconnected:
                    return GetText("状态未连接");
                case MachineRunState.ResetRequired:
                    return GetText("状态等待复位");
                case MachineRunState.Ready:
                    return GetText("状态就绪");
                case MachineRunState.Running:
                    return GetText("状态运行中");
                case MachineRunState.Pausing:
                    return GetText("状态暂停中");
                case MachineRunState.Paused:
                    return GetText("状态已暂停");
                case MachineRunState.Alarmed:
                    return GetText("状态报警");
                default:
                    return state.ToString();
            }
        }

        /// <summary>根据当前语言返回逻辑 IO 布尔值对应的界面文字。</summary>
        private string GetBooleanText(bool value, string trueResourceKey, string falseResourceKey)
        {
            return GetText(value ? trueResourceKey : falseResourceKey);
        }

        /// <summary>保存逻辑 DO 名称和要写入的值，避免按钮事件依赖物理通道号。</summary>
        private sealed class OutputCommand
        {
            /// <summary>创建一个明确的逻辑 DO 写入命令。</summary>
            public OutputCommand(string outputName, bool value)
            {
                OutputName = outputName;
                Value = value;
            }

            /// <summary>要写入的设备逻辑 DO 名称。</summary>
            public string OutputName { get; private set; }

            /// <summary>要写入的逻辑输出值；true 表示有效，电平转换由适配层处理。</summary>
            public bool Value { get; private set; }
        }
    }
}
