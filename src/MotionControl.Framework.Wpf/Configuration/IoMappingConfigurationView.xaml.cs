using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using MotionControl.Framework.Contracts.Configuration;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Runtime.Configuration;

namespace MotionControl.Framework.Wpf.Configuration
{
    /// <summary>编辑设备 DI/DO 逻辑名称与控制卡物理通道映射。</summary>
    public partial class IoMappingConfigurationView : UserControl
    {
        /// <summary>IO 映射文件在每台设备目录中的固定位置。</summary>
        private const string IoMappingFilePath = "HardwareConfig/IoMapping.json";

        /// <summary>控制卡清单在每台设备目录中的固定位置。</summary>
        private const string MotionCardsFilePath = "HardwareConfig/MotionCards.json";

        /// <summary>按 MachineId 读取并备份保存设备配置。</summary>
        private readonly MachineConfigurationStore _configurationStore;

        /// <summary>当前页面编辑的物理设备编号。</summary>
        private readonly string _machineId;

        /// <summary>轴映射页共用的控制卡连接，确保断连时不能改 IO 映射。</summary>
        private readonly IAxisMappingCardConnection _cardConnection;

        /// <summary>当前控制卡连接提供的映射热更新能力；不支持时禁止保存，避免文件与运行时不一致。</summary>
        private readonly IIoMappingCardConnection _ioMappingCardConnection;

        /// <summary>从控制卡配置读取出的可选控制卡标识。</summary>
        public ObservableCollection<string> ControllerIds { get; private set; }

        /// <summary>当前页面编辑的数字输入映射。</summary>
        public ObservableCollection<DigitalIoHardwareDefinition> DigitalInputs { get; private set; }

        /// <summary>当前页面编辑的数字输出映射。</summary>
        public ObservableCollection<DigitalIoHardwareDefinition> DigitalOutputs { get; private set; }

        /// <summary>IO 映射已热更新后通知 Host 刷新相关手动调试和安全配置页面。</summary>
        public event EventHandler IoMappingApplied;

        /// <summary>保存检查通过后可以覆盖当前 IO 映射文件。</summary>
        private bool _configurationCanBeSaved = true;

        /// <summary>标记 IO 配置正在保存和热更新，防止重复提交。</summary>
        private bool _configurationApplyInProgress;

        /// <summary>创建 IO 映射页并读取设备自己的 IO 和控制卡配置。</summary>
        public IoMappingConfigurationView(
            MachineConfigurationStore configurationStore,
            string machineId,
            IAxisMappingCardConnection cardConnection)
        {
            if (configurationStore == null)
            {
                throw new ArgumentNullException(nameof(configurationStore));
            }

            if (string.IsNullOrWhiteSpace(machineId))
            {
                throw new ArgumentException("设备编号不能为空。", nameof(machineId));
            }

            if (cardConnection == null)
            {
                throw new ArgumentNullException(nameof(cardConnection));
            }

            InitializeComponent();
            _configurationStore = configurationStore;
            _machineId = machineId;
            _cardConnection = cardConnection;
            _ioMappingCardConnection = cardConnection as IIoMappingCardConnection;
            _ioCapabilityProvider = cardConnection as IMotionCardIoCapabilityProvider;
            ControllerIds = new ObservableCollection<string>();
            DigitalInputs = new ObservableCollection<DigitalIoHardwareDefinition>();
            DigitalOutputs = new ObservableCollection<DigitalIoHardwareDefinition>();
            DataContext = this;

            LoadConfiguration();
            Loaded += IoMappingConfigurationView_Loaded;
        }

        /// <summary>根据连接、热更新能力和保存状态启用配置控件。</summary>
        private void UpdateConnectionState()
        {
            bool canEdit = _cardConnection.IsConnected
                && _ioMappingCardConnection != null
                && _configurationCanBeSaved
                && !_configurationApplyInProgress;
            DigitalInputsGrid.IsEnabled = canEdit;
            DigitalOutputsGrid.IsEnabled = canEdit;
            CheckMappingButton.IsEnabled = !_configurationApplyInProgress;
            SaveConfigurationButton.IsEnabled = canEdit;
            CancelChangesButton.IsEnabled = !_configurationApplyInProgress;
            RefreshIoCapabilitiesButton.IsEnabled = _cardConnection.IsConnected
                && _ioCapabilityProvider != null
                && !_isReadingIoCapabilities
                && !_configurationApplyInProgress;
            UpdateRemoveButtons(canEdit);
        }

        /// <summary>仅在控制卡已连接且选中映射行时启用对应删除按钮。</summary>
        private void UpdateRemoveButtons(bool canEdit)
        {
            RemoveInputButton.IsEnabled = canEdit && DigitalInputsGrid.SelectedItem != null;
            RemoveOutputButton.IsEnabled = canEdit && DigitalOutputsGrid.SelectedItem != null;
        }

        /// <summary>读取设备卡清单，并将可用卡标识提供给 DI/DO 下拉框。</summary>
        private void LoadControllerIds()
        {
            ControllerIds.Clear();
            string cardPath = GetConfigurationPath(MotionCardsFilePath);

            if (!File.Exists(cardPath))
            {
                UpdateConnectionState();
                SetConfigurationStatus("IO映射请先连接控制卡");
                return;
            }

            try
            {
                MotionCardSettingsDocument cards = _configurationStore.LoadJson<MotionCardSettingsDocument>(
                    _machineId,
                    MotionCardsFilePath);
                if (cards == null || cards.SchemaVersion != 1 || cards.MotionCards == null)
                {
                    throw new InvalidOperationException("MotionCards.json 格式无效或版本不受支持。");
                }

                foreach (MotionCardConnectionDefinition card in cards.MotionCards)
                {
                    if (card != null && !string.IsNullOrWhiteSpace(card.ControllerId)
                        && !ControllerIds.Contains(card.ControllerId))
                    {
                        ControllerIds.Add(card.ControllerId);
                    }
                }

                UpdateConnectionState();
                SetConfigurationStatus(ControllerIds.Count == 0
                    ? "IO映射请先连接控制卡"
                    : "IO映射读取完成");
            }
            catch (Exception error)
            {
                _configurationCanBeSaved = false;
                UpdateConnectionState();
                SetConfigurationStatus("IO映射控制卡读取失败", error.Message);
            }
        }

        /// <summary>读取当前设备 IO 映射；新设备没有文件时从空映射开始。</summary>
        private void LoadConfiguration()
        {
            _configurationCanBeSaved = true;
            string mappingPath = GetConfigurationPath(IoMappingFilePath);
            ConfigurationFileText.Text = mappingPath;
            DigitalInputs.Clear();
            DigitalOutputs.Clear();

            try
            {
                if (File.Exists(mappingPath))
                {
                    IoMappingConfigurationDocument configuration = _configurationStore.LoadJson<IoMappingConfigurationDocument>(
                        _machineId,
                        IoMappingFilePath);
                    ValidateDocumentVersion(configuration);
                    AddPoints(DigitalInputs, configuration.DigitalInputs);
                    AddPoints(DigitalOutputs, configuration.DigitalOutputs);
                }

                SetConfigurationStatus("IO映射读取完成");
            }
            catch (Exception error)
            {
                DigitalInputs.Clear();
                DigitalOutputs.Clear();
                _configurationCanBeSaved = false;
                SetConfigurationStatus("IO映射读取失败", error.Message);
            }
        }

        /// <summary>确认当前配置使用受支持的文件版本且两个点位列表存在。</summary>
        private static void ValidateDocumentVersion(IoMappingConfigurationDocument configuration)
        {
            if (configuration == null || configuration.SchemaVersion != 1)
            {
                throw new InvalidOperationException("只支持 schemaVersion 为 1 的 IoMapping.json。未保存原配置。");
            }

            if (configuration.DigitalInputs == null || configuration.DigitalOutputs == null)
            {
                throw new InvalidOperationException("IoMapping.json 缺少 digitalInputs 或 digitalOutputs 列表。未保存原配置。");
            }
        }

        /// <summary>复制已保存的点位，避免页面编辑时直接修改反序列化文档。</summary>
        private static void AddPoints(
            ICollection<DigitalIoHardwareDefinition> destination,
            IEnumerable<DigitalIoHardwareDefinition> points)
        {
            foreach (DigitalIoHardwareDefinition point in points)
            {
                if (point == null)
                {
                    throw new InvalidOperationException("IO 映射列表包含空点位配置。未保存原配置。");
                }

                destination.Add(new DigitalIoHardwareDefinition
                {
                    Name = point.Name,
                    ControllerId = point.ControllerId,
                    Channel = point.Channel,
                    ActiveHigh = point.ActiveHigh
                });
            }
        }

        /// <summary>根据设备配置目录和相对文件位置生成完整文件路径。</summary>
        private string GetConfigurationPath(string relativePath)
        {
            return Path.Combine(
                _configurationStore.GetMachineDirectory(_machineId),
                relativePath.Replace('/', Path.DirectorySeparatorChar));
        }

        /// <summary>从任务步骤选择指定 DI 或 DO 映射行，只定位现有配置，不切换输出。</summary>
        public bool FocusPointByLogicalName(string logicalName, bool output)
        {
            ObservableCollection<DigitalIoHardwareDefinition> points = output ? DigitalOutputs : DigitalInputs;
            DataGrid grid = output ? DigitalOutputsGrid : DigitalInputsGrid;
            DigitalIoHardwareDefinition point = points.FirstOrDefault(item =>
                string.Equals(item.Name, logicalName, StringComparison.OrdinalIgnoreCase));
            if (point == null) return false;
            grid.SelectedItem = point;
            grid.ScrollIntoView(point);
            return true;
        }

        /// <summary>清空选中 DI 的逻辑名称，通道行保留在自动探测清单中。</summary>
        private void RemoveInputButton_Click(object sender, RoutedEventArgs e)
        {
            DigitalIoHardwareDefinition selectedPoint = DigitalInputsGrid.SelectedItem as DigitalIoHardwareDefinition;
            if (selectedPoint != null)
            {
                selectedPoint.Name = string.Empty;
                MarkConfigurationAsChanged();
            }
        }

        /// <summary>清空选中 DO 的逻辑名称，通道行保留在自动探测清单中。</summary>
        private void RemoveOutputButton_Click(object sender, RoutedEventArgs e)
        {
            DigitalIoHardwareDefinition selectedPoint = DigitalOutputsGrid.SelectedItem as DigitalIoHardwareDefinition;
            if (selectedPoint != null)
            {
                selectedPoint.Name = string.Empty;
                MarkConfigurationAsChanged();
            }
        }

        /// <summary>检查当前 DI/DO 名称、卡关联和通道唯一性。</summary>
        private void CheckMappingButton_Click(object sender, RoutedEventArgs e)
        {
            if (!CommitGridEdits())
            {
                SetConfigurationStatus("IO映射表格输入无效");
                return;
            }

            string errorMessage;
            if (!TryValidateConfiguration(out errorMessage))
            {
                SetConfigurationStatusText(errorMessage, true);
                return;
            }

            SetConfigurationStatus("IO映射检查通过");
        }

        /// <summary>放弃当前未保存修改，重读配置并恢复已探测的 DI/DO 通道。</summary>
        private void CancelChangesButton_Click(object sender, RoutedEventArgs e)
        {
            LoadConfiguration();
            LoadControllerIds();

            if (_cardConnection.IsConnected && _lastIoCapabilities != null)
            {
                PopulateDetectedChannels(_lastIoCapabilities);
            }
        }

        /// <summary>结束两个表格当前单元格和行编辑，确保校验读取到最新输入值。</summary>
        private bool CommitGridEdits()
        {
            bool inputCellCommitted = DigitalInputsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            bool inputRowCommitted = DigitalInputsGrid.CommitEdit(DataGridEditingUnit.Row, true);
            bool outputCellCommitted = DigitalOutputsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            bool outputRowCommitted = DigitalOutputsGrid.CommitEdit(DataGridEditingUnit.Row, true);

            return inputCellCommitted && inputRowCommitted && outputCellCommitted && outputRowCommitted;
        }

        /// <summary>结束 DI 单元格编辑时提示配置已改变但尚未保存。</summary>
        private void DigitalInputsGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            MarkConfigurationAsChanged();
        }

        /// <summary>结束 DO 单元格编辑时提示配置已改变但尚未保存。</summary>
        private void DigitalOutputsGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            MarkConfigurationAsChanged();
        }

        /// <summary>DI 或 DO 行选择变化后，更新对应删除按钮的可用状态。</summary>
        private void MappingGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            bool canEdit = _cardConnection.IsConnected
                && _ioMappingCardConnection != null
                && _configurationCanBeSaved;
            UpdateRemoveButtons(canEdit);
        }

        /// <summary>更新页面底部状态，避免已编辑数据仍显示为“已读取”。</summary>
        private void MarkConfigurationAsChanged()
        {
            if (IsLoaded && _configurationCanBeSaved)
            {
                SetConfigurationStatus("IO映射尚未保存");
            }
        }

        /// <summary>校验两类 IO 的逻辑名称、卡号和物理通道。</summary>
        private bool TryValidateConfiguration(out string errorMessage)
        {
            if (!_configurationCanBeSaved)
            {
                errorMessage = (string)FindResource("IO映射禁止保存");
                return false;
            }

            if (!_cardConnection.IsConnected)
            {
                errorMessage = (string)FindResource("IO映射请先连接控制卡");
                return false;
            }

            if (!ValidatePoints(DigitalInputs, (string)FindResource("数字输入"), true, out errorMessage))
            {
                return false;
            }

            if (!ValidatePoints(DigitalOutputs, (string)FindResource("数字输出"), false, out errorMessage))
            {
                return false;
            }

            errorMessage = string.Empty;
            return true;
        }

        /// <summary>检查 DI 或 DO 的名称、控制卡标识和物理通道是否重复或缺失。</summary>
        private bool ValidatePoints(
            IEnumerable<DigitalIoHardwareDefinition> points,
            string pointTypeName,
            bool input,
            out string errorMessage)
        {
            var logicalNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var physicalChannels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (DigitalIoHardwareDefinition point in points)
            {
                if (string.IsNullOrEmpty(point.Name))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(point.Name) || point.Name != point.Name.Trim())
                {
                    errorMessage = string.Format(
                        CultureInfo.CurrentCulture,
                        (string)FindResource("IO映射名称无效格式"),
                        pointTypeName);
                    return false;
                }

                if (!logicalNames.Add(point.Name))
                {
                    errorMessage = string.Format(
                        CultureInfo.CurrentCulture,
                        (string)FindResource("IO映射名称重复格式"),
                        pointTypeName,
                        point.Name);
                    return false;
                }

                if (string.IsNullOrWhiteSpace(point.ControllerId)
                    || !ControllerIds.Contains(point.ControllerId))
                {
                    errorMessage = string.Format(
                        CultureInfo.CurrentCulture,
                        (string)FindResource("IO映射控制卡无效格式"),
                        pointTypeName,
                        point.Name);
                    return false;
                }

                if (point.Channel < 0)
                {
                    errorMessage = string.Format(
                        CultureInfo.CurrentCulture,
                        (string)FindResource("IO映射通道无效格式"),
                        pointTypeName,
                        point.Name);
                    return false;
                }

                int? mappableChannelCount = GetMappableChannelCount(point.ControllerId, input);
                if (mappableChannelCount.HasValue && point.Channel >= mappableChannelCount.Value)
                {
                    errorMessage = string.Format(
                        CultureInfo.CurrentCulture,
                        (string)FindResource("IO映射通道超出可映射范围格式"),
                        pointTypeName,
                        point.Name,
                        point.Channel,
                        mappableChannelCount.Value);
                    return false;
                }

                string physicalAddress = point.ControllerId + "/" + point.Channel.ToString(CultureInfo.InvariantCulture);
                if (!physicalChannels.Add(physicalAddress))
                {
                    errorMessage = string.Format(
                        CultureInfo.CurrentCulture,
                        (string)FindResource("IO映射通道重复格式"),
                        pointTypeName,
                        point.Channel);
                    return false;
                }
            }

            errorMessage = string.Empty;
            return true;
        }

        /// <summary>复制一个逻辑点位并保留其控制卡、通道和有效电平。</summary>
        private static DigitalIoHardwareDefinition CopyPoint(DigitalIoHardwareDefinition point)
        {
            return new DigitalIoHardwareDefinition
            {
                Name = point.Name.Trim(),
                ControllerId = point.ControllerId,
                Channel = point.Channel,
                ActiveHigh = point.ActiveHigh
            };
        }
    }
}
