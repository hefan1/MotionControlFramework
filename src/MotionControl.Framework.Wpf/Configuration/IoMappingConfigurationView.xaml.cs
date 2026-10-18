using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MotionControl.Framework.Contracts.Configuration;
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

        /// <summary>从控制卡配置读取出的可选控制卡标识。</summary>
        public ObservableCollection<string> ControllerIds { get; private set; }

        /// <summary>当前页面编辑的数字输入映射。</summary>
        public ObservableCollection<DigitalIoHardwareDefinition> DigitalInputs { get; private set; }

        /// <summary>当前页面编辑的数字输出映射。</summary>
        public ObservableCollection<DigitalIoHardwareDefinition> DigitalOutputs { get; private set; }

        /// <summary>从当前 DI 名称生成的急停输入候选项。</summary>
        public ObservableCollection<string> EmergencyStopInputNames { get; private set; }

        /// <summary>急停状态对应的逻辑输入名称。</summary>
        public string EmergencyStopInputName { get; set; }

        /// <summary>保存检查通过后可以覆盖当前 IO 映射文件。</summary>
        private bool _configurationCanBeSaved = true;

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
            ControllerIds = new ObservableCollection<string>();
            DigitalInputs = new ObservableCollection<DigitalIoHardwareDefinition>();
            DigitalOutputs = new ObservableCollection<DigitalIoHardwareDefinition>();
            EmergencyStopInputNames = new ObservableCollection<string>();
            DataContext = this;

            LoadConfiguration();
            Loaded += IoMappingConfigurationView_Loaded;
        }

        /// <summary>每次打开页面都刷新控制卡清单，取得轴映射页刚保存的卡标识。</summary>
        private void IoMappingConfigurationView_Loaded(object sender, RoutedEventArgs e)
        {
            LoadControllerIds();
            UpdateConnectionState();
        }

        /// <summary>根据轴映射页共用的实时连接状态锁定或开放 IO 配置控件。</summary>
        private void UpdateConnectionState()
        {
            bool canEdit = _cardConnection.IsConnected && _configurationCanBeSaved;
            EmergencyStopInputComboBox.IsEnabled = canEdit;
            DigitalInputsGrid.IsEnabled = canEdit;
            DigitalOutputsGrid.IsEnabled = canEdit;
            AddInputButton.IsEnabled = canEdit && ControllerIds.Count > 0;
            AddOutputButton.IsEnabled = canEdit && ControllerIds.Count > 0;
        }

        /// <summary>读取设备卡清单，并将可用卡标识提供给 DI/DO 下拉框。</summary>
        private void LoadControllerIds()
        {
            ControllerIds.Clear();
            string cardPath = GetConfigurationPath(MotionCardsFilePath);

            if (!File.Exists(cardPath))
            {
                UpdateConnectionState();
                ConfigurationStatusText.Text = (string)FindResource("IO映射请先连接控制卡");
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
                ConfigurationStatusText.Text = ControllerIds.Count == 0
                    ? (string)FindResource("IO映射请先连接控制卡")
                    : (string)FindResource("IO映射读取完成");
            }
            catch (Exception error)
            {
                _configurationCanBeSaved = false;
                UpdateConnectionState();
                ConfigurationStatusText.Text = string.Format(
                    CultureInfo.CurrentCulture,
                    (string)FindResource("IO映射控制卡读取失败"),
                    error.Message);
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
            EmergencyStopInputName = string.Empty;

            try
            {
                if (File.Exists(mappingPath))
                {
                    IoMappingConfigurationDocument configuration = _configurationStore.LoadJson<IoMappingConfigurationDocument>(
                        _machineId,
                        IoMappingFilePath);
                    ValidateDocumentVersion(configuration);
                    EmergencyStopInputName = configuration.EmergencyStopInputName ?? string.Empty;
                    AddPoints(DigitalInputs, configuration.DigitalInputs);
                    AddPoints(DigitalOutputs, configuration.DigitalOutputs);
                }

                RefreshEmergencyStopInputNames();
                ConfigurationStatusText.Text = (string)FindResource("IO映射读取完成");
            }
            catch (Exception error)
            {
                DigitalInputs.Clear();
                DigitalOutputs.Clear();
                EmergencyStopInputName = string.Empty;
                EmergencyStopInputNames.Clear();
                EmergencyStopInputComboBox.GetBindingExpression(ComboBox.TextProperty)?.UpdateTarget();
                _configurationCanBeSaved = false;
                ConfigurationStatusText.Text = string.Format(
                    CultureInfo.CurrentCulture,
                    (string)FindResource("IO映射读取失败"),
                    error.Message);
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

        /// <summary>新增一个未命名 DI，并自动选择当前首张卡和下一个未占用通道号。</summary>
        private void AddInputButton_Click(object sender, RoutedEventArgs e)
        {
            DigitalInputs.Add(CreateNewPoint(DigitalInputs));
            DigitalInputsGrid.SelectedIndex = DigitalInputs.Count - 1;
            DigitalInputsGrid.ScrollIntoView(DigitalInputsGrid.SelectedItem);
            MarkConfigurationAsChanged();
        }

        /// <summary>新增一个未命名 DO，并自动选择当前首张卡和下一个未占用通道号。</summary>
        private void AddOutputButton_Click(object sender, RoutedEventArgs e)
        {
            DigitalOutputs.Add(CreateNewPoint(DigitalOutputs));
            DigitalOutputsGrid.SelectedIndex = DigitalOutputs.Count - 1;
            DigitalOutputsGrid.ScrollIntoView(DigitalOutputsGrid.SelectedItem);
            MarkConfigurationAsChanged();
        }

        /// <summary>创建不带虚构逻辑名称的点位草稿，通道号取当前列表下一空闲值。</summary>
        private DigitalIoHardwareDefinition CreateNewPoint(
            IEnumerable<DigitalIoHardwareDefinition> existingPoints)
        {
            int nextChannel = 0;
            while (existingPoints.Any(point => point.ControllerId == GetFirstControllerId()
                && point.Channel == nextChannel))
            {
                nextChannel++;
            }

            return new DigitalIoHardwareDefinition
            {
                Name = string.Empty,
                ControllerId = GetFirstControllerId(),
                Channel = nextChannel,
                ActiveHigh = true
            };
        }

        /// <summary>返回目前卡清单中的首张控制卡；未连接控制卡时返回空值。</summary>
        private string GetFirstControllerId()
        {
            return ControllerIds.Count == 0 ? string.Empty : ControllerIds[0];
        }

        /// <summary>删除当前选中的 DI 映射行。</summary>
        private void RemoveInputButton_Click(object sender, RoutedEventArgs e)
        {
            DigitalIoHardwareDefinition selectedPoint = DigitalInputsGrid.SelectedItem as DigitalIoHardwareDefinition;
            if (selectedPoint != null)
            {
                DigitalInputs.Remove(selectedPoint);
                RefreshEmergencyStopInputNames();
                MarkConfigurationAsChanged();
            }
        }

        /// <summary>删除当前选中的 DO 映射行。</summary>
        private void RemoveOutputButton_Click(object sender, RoutedEventArgs e)
        {
            DigitalIoHardwareDefinition selectedPoint = DigitalOutputsGrid.SelectedItem as DigitalIoHardwareDefinition;
            if (selectedPoint != null)
            {
                DigitalOutputs.Remove(selectedPoint);
                MarkConfigurationAsChanged();
            }
        }

        /// <summary>检查当前 IO 名称、卡关联、通道唯一性和急停输入引用。</summary>
        private void CheckMappingButton_Click(object sender, RoutedEventArgs e)
        {
            if (!CommitGridEdits())
            {
                ConfigurationStatusText.Text = (string)FindResource("IO映射表格输入无效");
                return;
            }

            string errorMessage;
            if (!TryValidateConfiguration(out errorMessage))
            {
                ConfigurationStatusText.Text = errorMessage;
                return;
            }

            ConfigurationStatusText.Text = (string)FindResource("IO映射检查通过");
        }

        /// <summary>检查通过后，将 DI/DO 与急停名称保存到本设备的 IoMapping.json。</summary>
        private void SaveConfigurationButton_Click(object sender, RoutedEventArgs e)
        {
            if (!CommitGridEdits())
            {
                ConfigurationStatusText.Text = (string)FindResource("IO映射表格输入无效");
                return;
            }

            if (!_configurationCanBeSaved)
            {
                ConfigurationStatusText.Text = (string)FindResource("IO映射禁止保存");
                return;
            }

            string errorMessage;
            if (!TryValidateConfiguration(out errorMessage))
            {
                ConfigurationStatusText.Text = errorMessage;
                return;
            }

            var configuration = new IoMappingConfigurationDocument
            {
                EmergencyStopInputName = EmergencyStopInputName.Trim(),
                DigitalInputs = DigitalInputs.Select(CopyPoint).ToList(),
                DigitalOutputs = DigitalOutputs.Select(CopyPoint).ToList()
            };

            try
            {
                _configurationStore.SaveJson(_machineId, IoMappingFilePath, configuration);
                ConfigurationStatusText.Text = (string)FindResource("IO映射保存成功");
            }
            catch (Exception error)
            {
                ConfigurationStatusText.Text = string.Format(
                    CultureInfo.CurrentCulture,
                    (string)FindResource("IO映射保存失败"),
                    error.Message);
            }
        }

        /// <summary>放弃当前未保存修改并重新读取本机配置文件。</summary>
        private void CancelChangesButton_Click(object sender, RoutedEventArgs e)
        {
            LoadConfiguration();
            LoadControllerIds();
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

        /// <summary>打开急停下拉框时刷新候选项，便于从已配置 DI 名称中选择。</summary>
        private void EmergencyStopInputComboBox_DropDownOpened(object sender, EventArgs e)
        {
            RefreshEmergencyStopInputNames();
        }

        /// <summary>离开急停输入编辑框后提示配置已改变但尚未保存。</summary>
        private void EmergencyStopInputComboBox_LostFocus(object sender, RoutedEventArgs e)
        {
            MarkConfigurationAsChanged();
        }

        /// <summary>结束 DI 单元格编辑时提示配置已改变但尚未保存。</summary>
        private void DigitalInputsGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            RefreshEmergencyStopInputNames();
            MarkConfigurationAsChanged();
        }

        /// <summary>结束 DO 单元格编辑时提示配置已改变但尚未保存。</summary>
        private void DigitalOutputsGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            MarkConfigurationAsChanged();
        }

        /// <summary>更新页面底部状态，避免已编辑数据仍显示为“已读取”。</summary>
        private void MarkConfigurationAsChanged()
        {
            if (IsLoaded && _configurationCanBeSaved)
            {
                ConfigurationStatusText.Text = (string)FindResource("IO映射尚未保存");
            }
        }

        /// <summary>把当前已填写的 DI 名称放入急停候选列表，并清除已删除的引用。</summary>
        private void RefreshEmergencyStopInputNames()
        {
            string selectedName = EmergencyStopInputName;
            EmergencyStopInputNames.Clear();

            foreach (DigitalIoHardwareDefinition input in DigitalInputs)
            {
                if (string.IsNullOrWhiteSpace(input.Name)
                    || EmergencyStopInputNames.Any(name => string.Equals(
                        name,
                        input.Name,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                EmergencyStopInputNames.Add(input.Name);
            }

            bool selectedNameStillExists = !string.IsNullOrWhiteSpace(selectedName)
                && EmergencyStopInputNames.Any(name => string.Equals(
                    name,
                    selectedName,
                    StringComparison.OrdinalIgnoreCase));
            EmergencyStopInputName = selectedNameStillExists ? selectedName : string.Empty;

            EmergencyStopInputComboBox.GetBindingExpression(ComboBox.TextProperty)?.UpdateTarget();
        }

        /// <summary>校验两类 IO 的逻辑名称、卡号、物理通道及急停输入配置。</summary>
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

            if (!ValidatePoints(DigitalInputs, (string)FindResource("数字输入"), out errorMessage))
            {
                return false;
            }

            if (!ValidatePoints(DigitalOutputs, (string)FindResource("数字输出"), out errorMessage))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(EmergencyStopInputName)
                || !DigitalInputs.Any(point => string.Equals(
                    point.Name,
                    EmergencyStopInputName.Trim(),
                    StringComparison.OrdinalIgnoreCase)))
            {
                errorMessage = (string)FindResource("IO映射急停必须关联DI");
                return false;
            }

            errorMessage = string.Empty;
            return true;
        }

        /// <summary>检查 DI 或 DO 的名称、控制卡标识和物理通道是否重复或缺失。</summary>
        private bool ValidatePoints(
            IEnumerable<DigitalIoHardwareDefinition> points,
            string pointTypeName,
            out string errorMessage)
        {
            var logicalNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var physicalChannels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (DigitalIoHardwareDefinition point in points)
            {
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
