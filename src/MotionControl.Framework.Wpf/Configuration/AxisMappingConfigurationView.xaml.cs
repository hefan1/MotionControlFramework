using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MotionControl.Framework.Runtime.Configuration;

namespace MotionControl.Framework.Wpf.Configuration
{
    /// <summary>编辑并保存单台设备的控制卡选择和物理轴映射。</summary>
    public partial class AxisMappingConfigurationView : UserControl, INotifyPropertyChanged
    {
        /// <summary>设备轴参数 JSON 在 MachineId 目录中的相对路径。</summary>
        private const string AxisParametersFilePath = "HardwareConfig/AxisParameters.json";

        /// <summary>控制卡设置 JSON 在 MachineId 目录中的相对路径。</summary>
        private const string MotionCardsFilePath = "HardwareConfig/MotionCards.json";

        /// <summary>按 MachineId 读取和备份保存设备配置。</summary>
        private readonly MachineConfigurationStore _configurationStore;

        /// <summary>由 Host 按所选厂商建立的控制卡连接及轴参数读写入口。</summary>
        private readonly IAxisMappingCardConnection _cardConnection;

        /// <summary>由当前 Host 适配器提供的可选控制卡及通道数量清单。</summary>
        private readonly IReadOnlyList<MotionCardSelectionOption> _supportedMotionCards;

        /// <summary>当前 Host 卡型支持清单中的最大物理轴数量。</summary>
        private readonly int _maximumSupportedAxisCount;

        /// <summary>当前页面对应的物理设备编号。</summary>
        private readonly string _machineId;

        /// <summary>保存全部物理通道，切换卡轴数量时保留各通道内容。</summary>
        private readonly List<AxisMappingEntry> _allAxes = new List<AxisMappingEntry>();

        /// <summary>当前页面编辑的轴参数文件内容。</summary>
        private AxisMappingConfigurationDocument _configuration;

        /// <summary>当前页面编辑的控制卡设置文件内容。</summary>
        private MotionCardSettingsDocument _cardConfiguration;

        /// <summary>当前选中的物理轴参数。</summary>
        private AxisMappingEntry _selectedAxis;

        /// <summary>批量填充页面时暂时忽略下拉框事件。</summary>
        private bool _loadingConfiguration;

        /// <summary>配置文件损坏或版本不支持时禁止覆盖原文件。</summary>
        private bool _configurationCanBeSaved = true;

        /// <summary>页面首次显示时只自动连接一次，避免导航回来重复建立连接。</summary>
        private bool _startupConnectionAttempted;

        /// <summary>连接和断开期间锁定相关按钮，避免重复操作控制器句柄。</summary>
        private bool _connectionInProgress;

        /// <summary>通知页面绑定项已经变化。</summary>
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>通知 Host 当前控制卡连接状态已经变化。</summary>
        public event EventHandler CardConnectionChanged;

        /// <summary>由 Host 注入整机断开流程；检查工位、手动动作和轴状态后才允许释放卡连接。</summary>
        public Func<CancellationToken, Task> PrepareCardDisconnectAsync { get; set; }

        /// <summary>Host 提供在线配置保护；重连型更改由 Host 明确决定是否允许。</summary>
        public Func<Func<Task>, bool, CancellationToken, Task> ApplyConnectedConfigurationAsync { get; set; }

        /// <summary>正式 Host 不在旧 Runtime 仍持卡时重建连接；此类轴映射需先断卡。</summary>
        public bool RequiresDisconnectedForAxisReconnect { get; set; }

        /// <summary>通知 Host 当前卡实例已在线应用轴映射，手动页需要刷新显示定义。</summary>
        public event EventHandler AxisMappingApplied;

        /// <summary>当前轴数量范围内显示的物理轴通道。</summary>
        public ObservableCollection<AxisMappingEntry> VisibleAxes { get; private set; }

        /// <summary>当前轴数量范围内已填写设备轴名称的通道数量。</summary>
        public int ConfiguredAxisCount
        {
            get
            {
                return _cardConfiguration == null ? 0
                    : _allAxes.Count(axis => axis.AxisNumber < _cardConfiguration.AxisCount && axis.IsConfigured);
            }
        }

        /// <summary>当前控制卡配置的物理轴通道总数。</summary>
        public int ConfiguredAxisTotal
        {
            get { return _cardConfiguration == null ? 0 : _cardConfiguration.AxisCount; }
        }

        /// <summary>当前正在右侧编辑的物理轴配置。</summary>
        public AxisMappingEntry SelectedAxis
        {
            get { return _selectedAxis; }
            private set
            {
                if (ReferenceEquals(_selectedAxis, value))
                {
                    return;
                }

                _selectedAxis = value;
                RaisePropertyChanged("SelectedAxis");
            }
        }

        /// <summary>创建轴映射页面并加载指定设备自己的 JSON 配置。</summary>
        public AxisMappingConfigurationView(
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
            _cardConnection = cardConnection;
            _supportedMotionCards = new List<MotionCardSelectionOption>(
                cardConnection.SupportedMotionCards ?? new MotionCardSelectionOption[0]).AsReadOnly();
            _maximumSupportedAxisCount = GetMaximumSupportedAxisCount(_supportedMotionCards);
            _machineId = machineId;
            VisibleAxes = new ObservableCollection<AxisMappingEntry>();
            DataContext = this;

            EnsureAxisCapacity(_maximumSupportedAxisCount);
            InitializeSupportedCardVendorItems();

            LoadConfiguration();
            Loaded += AxisMappingConfigurationView_Loaded;
        }

        /// <summary>页面首次显示时，按已保存的控制卡配置自动连接。</summary>
        private async void AxisMappingConfigurationView_Loaded(object sender, RoutedEventArgs e)
        {
            await InitializeSavedCardConnectionAsync();
        }

        /// <summary>应用启动或页面首次显示时，按已保存配置自动连接一次控制卡。</summary>
        public async Task InitializeSavedCardConnectionAsync()
        {
            if (_startupConnectionAttempted)
            {
                return;
            }

            _startupConnectionAttempted = true;
            if (_cardConfiguration.MotionCards != null && _cardConfiguration.MotionCards.Count > 0)
            {
                await ConnectCardAsync(true);
                return;
            }

            UpdateCardConnectionUiState();
            NotifyCardConnectionChanged();
        }

        /// <summary>通知 Host 顶部状态栏同步显示当前控制卡连接状态。</summary>
        private void NotifyCardConnectionChanged()
        {
            EventHandler handler = CardConnectionChanged;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        /// <summary>通知 Host 刷新手动页中的逻辑轴名称和工程单位。</summary>
        private void NotifyAxisMappingApplied()
        {
            EventHandler handler = AxisMappingApplied;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        /// <summary>根据当前物理轴名称刷新已命名通道数量，并更新当前轴的写卡按钮状态。</summary>
        private void Axis_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (string.Equals(e.PropertyName, "Name", StringComparison.Ordinal))
            {
                UpdateConfiguredAxisCountText();
            }

            if (ReferenceEquals(SelectedAxis, sender))
            {
                UpdateCardConnectionUiState();
            }
        }

        /// <summary>清空轴映射后同步清除右侧下拉框，避免残留旧参数显示。</summary>
        private void ResetAxisAndRefreshEditor(AxisMappingEntry axis)
        {
            bool wasLoadingConfiguration = _loadingConfiguration;
            _loadingConfiguration = true;
            try
            {
                ResetAxisToUnconfigured(axis);
                if (ReferenceEquals(SelectedAxis, axis))
                {
                    SelectComboBoxItem(AxisTypeComboBox, axis.AxisType);
                    SelectComboBoxItem(EngineeringUnitComboBox, axis.EngineeringUnit);
                    UpdateSoftLimitEnabledEditor(axis);
                    UpdateHomeModeEditor(axis);
                }
            }
            finally
            {
                _loadingConfiguration = wasLoadingConfiguration;
            }
        }

        /// <summary>按已保存的轴通道数量恢复下拉框选择，并保留当前 Host 未提供的旧值。</summary>
        private void SelectAxisCount(int axisCount)
        {
            foreach (ComboBoxItem item in AxisCountComboBox.Items)
            {
                if (string.Equals(item.Tag as string, axisCount.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
                {
                    AxisCountComboBox.SelectedItem = item;
                    return;
                }
            }

            ComboBoxItem savedValueItem = new ComboBoxItem
            {
                Tag = axisCount.ToString(CultureInfo.InvariantCulture),
                Content = axisCount.ToString(CultureInfo.InvariantCulture)
            };
            AxisCountComboBox.Items.Add(savedValueItem);
            AxisCountComboBox.SelectedItem = savedValueItem;
        }

        /// <summary>根据当前控制卡轴数量刷新左侧物理通道表格。</summary>
        private void RefreshVisibleAxes()
        {
            VisibleAxes.Clear();
            int visibleAxisCount = Math.Min(_cardConfiguration.AxisCount, _allAxes.Count);
            for (int axisNumber = 0; axisNumber < visibleAxisCount; axisNumber++)
            {
                VisibleAxes.Add(_allAxes[axisNumber]);
            }

            UpdateConfiguredAxisCountText();
        }

        /// <summary>刷新顶部显示的已命名通道数量；参数完整性仍由映射检查确定。</summary>
        private void UpdateConfiguredAxisCountText()
        {
            if (_cardConfiguration == null || ConfiguredAxisCountText == null)
            {
                return;
            }

            int configuredCount = _allAxes.Count(axis => axis.AxisNumber < _cardConfiguration.AxisCount && axis.IsConfigured);
            ConfiguredAxisCountText.Text = string.Format(
                CultureInfo.CurrentCulture,
                (string)FindResource("已配置轴数量格式"),
                configuredCount,
                _cardConfiguration.AxisCount);
        }

        /// <summary>选中一个物理轴，并更新右侧参数编辑区。</summary>
        private void SelectAxis(AxisMappingEntry axis)
        {
            SelectedAxis = axis;
            if (axis == null)
            {
                SelectedAxisText.Text = string.Empty;
                PhysicalAxisTextBox.Text = string.Empty;
                return;
            }

            UpdateSelectedAxisText();
            PhysicalAxisTextBox.Text = "Axis" + axis.AxisNumber.ToString(CultureInfo.InvariantCulture);
            if (AxisMappingGrid != null && ReferenceEquals(AxisMappingGrid.SelectedItem, axis) == false)
            {
                AxisMappingGrid.SelectedItem = axis;
                AxisMappingGrid.ScrollIntoView(axis);
            }

            SelectComboBoxItem(AxisTypeComboBox, axis.AxisType);
            SelectComboBoxItem(EngineeringUnitComboBox, axis.EngineeringUnit);
            UpdateSoftLimitEnabledEditor(axis);
            UpdateHomeModeEditor(axis);
            UpdateDatumInputChannelEditor(axis);
        }

        /// <summary>从任务步骤定位到指定逻辑轴，只选择并滚动页面，不读取或修改控制卡。</summary>
        public bool FocusAxisByLogicalName(string logicalName)
        {
            AxisMappingEntry axis = _allAxes.FirstOrDefault(item =>
                string.Equals(item.Name, logicalName, StringComparison.OrdinalIgnoreCase));
            if (axis == null) return false;
            SelectAxis(axis);
            return true;
        }

        /// <summary>保存当前厂商选择，并刷新该厂商实际接入的卡型和轴数量。</summary>
        private void CardVendorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingConfiguration || _cardConfiguration == null)
            {
                return;
            }

            bool wasLoadingConfiguration = _loadingConfiguration;
            _loadingConfiguration = true;
            try
            {
                _cardConfiguration.MotionCardVendor = GetSelectedTag(CardVendorComboBox);
                MotionCardSelectionOption option = FindSupportedMotionCardOption(
                    _cardConfiguration.MotionCardVendor,
                    _cardConfiguration.MotionCardType);
                if (option == null)
                {
                    option = _supportedMotionCards.FirstOrDefault(card => string.Equals(
                        card.VendorCode,
                        _cardConfiguration.MotionCardVendor,
                        StringComparison.Ordinal));
                }

                if (option != null)
                {
                    _cardConfiguration.MotionCardType = option.CardTypeCode;
                }

                PopulateCardTypeItems(_cardConfiguration.MotionCardVendor, _cardConfiguration.MotionCardType);
                UpdateAxisCountOptions(option, _cardConfiguration.AxisCount);
                UpdateExistingConnectionAdapterType(option);
            }
            finally
            {
                _loadingConfiguration = wasLoadingConfiguration;
            }

            UpdateHomeModeEditor(SelectedAxis);
            UpdateDatumInputChannelEditor(SelectedAxis);
            UpdateCardConnectionUiState();
        }

        /// <summary>记录控制卡类型下拉框的当前值。</summary>
        private void CardTypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingConfiguration || _cardConfiguration == null)
            {
                return;
            }

            _cardConfiguration.MotionCardType = GetSelectedTag(CardTypeComboBox);
            MotionCardSelectionOption option = FindSupportedMotionCardOption(
                _cardConfiguration.MotionCardVendor,
                _cardConfiguration.MotionCardType);
            UpdateExistingConnectionAdapterType(option);
            bool wasLoadingConfiguration = _loadingConfiguration;
            _loadingConfiguration = true;
            try
            {
                UpdateAxisCountOptions(option, _cardConfiguration.AxisCount);
            }
            finally
            {
                _loadingConfiguration = wasLoadingConfiguration;
            }

            UpdateHomeModeEditor(SelectedAxis);
            UpdateDatumInputChannelEditor(SelectedAxis);
            UpdateCardConnectionUiState();
        }

        /// <summary>切换可配置的物理轴通道数量，并避免隐藏仍在使用的轴。</summary>
        private void AxisCountComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingConfiguration || _cardConfiguration == null || AxisCountComboBox.SelectedItem == null)
            {
                return;
            }

            int selectedAxisCount = int.Parse(GetSelectedTag(AxisCountComboBox), CultureInfo.InvariantCulture);
            if (_allAxes.Any(axis => axis.AxisNumber >= selectedAxisCount
                && (axis.IsConfigured || HasAxisParameterValues(axis))))
            {
                _loadingConfiguration = true;
                SelectAxisCount(_cardConfiguration.AxisCount);
                _loadingConfiguration = false;
                MessageBox.Show(
                    (string)FindResource("轴数量减少时仍有轴已映射"),
                    (string)FindResource("轴映射配置"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            _cardConfiguration.AxisCount = selectedAxisCount;
            EnsureAxisCapacity(selectedAxisCount);
            RefreshVisibleAxes();
            SelectAxis(_allAxes.FirstOrDefault());
        }

        /// <summary>将轴类型下拉框选择写入当前物理轴。</summary>
        private void AxisTypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loadingConfiguration && SelectedAxis != null)
            {
                SelectedAxis.AxisType = GetSelectedTag(AxisTypeComboBox);
            }
        }

        /// <summary>将工程单位下拉框选择写入当前物理轴。</summary>
        private void EngineeringUnitComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loadingConfiguration && SelectedAxis != null)
            {
                SelectedAxis.EngineeringUnit = GetSelectedTag(EngineeringUnitComboBox);
            }
        }

        /// <summary>把软限位下拉框选项保存到当前物理轴配置。</summary>
        private void SoftLimitEnabledComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingConfiguration || SelectedAxis == null || SoftLimitEnabledComboBox.SelectedItem == null)
            {
                return;
            }

            string selectedValue = GetSelectedTag(SoftLimitEnabledComboBox);
            bool enabled;
            SelectedAxis.SoftLimitEnabled = bool.TryParse(selectedValue, out enabled) ? (bool?)enabled : null;
        }

        /// <summary>按当前轴配置恢复软限位下拉框选项。</summary>
        private void UpdateSoftLimitEnabledEditor(AxisMappingEntry axis)
        {
            bool wasLoadingConfiguration = _loadingConfiguration;
            _loadingConfiguration = true;
            try
            {
                SoftLimitEnabledComboBox.SelectedIndex = 0;
                if (axis == null || !axis.SoftLimitEnabled.HasValue)
                {
                    return;
                }

                string desiredTag = axis.SoftLimitEnabled.Value ? "True" : "False";
                foreach (ComboBoxItem item in SoftLimitEnabledComboBox.Items)
                {
                    if (string.Equals(item.Tag as string, desiredTag, StringComparison.OrdinalIgnoreCase))
                    {
                        SoftLimitEnabledComboBox.SelectedItem = item;
                        return;
                    }
                }
            }
            finally
            {
                _loadingConfiguration = wasLoadingConfiguration;
            }
        }

        /// <summary>检查设备轴名称、单位换算、软限位、速度和加减速度参数。</summary>
        private void CheckMappingButton_Click(object sender, RoutedEventArgs e)
        {
            Keyboard.ClearFocus();
            List<string> errors = ValidateConfiguredAxes();
            if (errors.Count > 0)
            {
                SetConfigurationValidationStatus();
                MessageBox.Show(
                    string.Join(Environment.NewLine, errors),
                    (string)FindResource("轴映射配置"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            int configuredCount = _allAxes.Count(axis => axis.AxisNumber < _cardConfiguration.AxisCount && axis.IsConfigured);
            if (configuredCount == 0)
            {
                SetConfigurationStatus("还没有配置设备轴");
            }
            else
            {
                SetConfigurationStatus("轴映射检查通过格式", configuredCount);
            }
        }

        /// <summary>保存轴映射；适配器支持且物理轴集合未变时在线应用，否则确认后重新连接。</summary>
        private async void SaveConfigurationButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_configurationCanBeSaved)
            {
                return;
            }

            Keyboard.ClearFocus();
            List<string> errors = ValidateConfiguredAxes();
            if (errors.Count > 0)
            {
                SetConfigurationValidationStatus();
                MessageBox.Show(
                    string.Join(Environment.NewLine, errors),
                    (string)FindResource("轴映射配置"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var configurationToSave = new AxisMappingConfigurationDocument
            {
                SchemaVersion = _configuration.SchemaVersion,
                Axes = _allAxes
                    .Where(axis => axis.AxisNumber < _cardConfiguration.AxisCount && axis.IsConfigured)
                    .OrderBy(axis => axis.AxisNumber)
                    .ToList()
            };

            bool applyToConnectedCard = _cardConnection.IsConnected;
            IAxisMappingCardOnlineApply onlineApply = _cardConnection as IAxisMappingCardOnlineApply;
            bool canApplyWithoutReconnect = applyToConnectedCard
                && onlineApply != null
                && onlineApply.CanApplyAxisMappingsWithoutReconnect(_cardConfiguration, configurationToSave);
            bool requiresReconnect = applyToConnectedCard && !canApplyWithoutReconnect;
            if (requiresReconnect && RequiresDisconnectedForAxisReconnect)
            {
                SetConfigurationStatus("轴映射需先断卡");
                return;
            }
            if (requiresReconnect
                && MessageBox.Show(
                    (string)FindResource("保存并应用轴映射确认内容"),
                    (string)FindResource("保存并应用轴映射确认标题"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            _configuration.Axes = configurationToSave.Axes;

            bool configurationSaved = false;
            bool connectionChanged = false;
            bool axisMappingChanged = false;
            _connectionInProgress = true;
            UpdateCardConnectionUiState();
            try
            {
                async Task SaveAndApplyAsync()
                {
                    _configurationStore.SaveJson(_machineId, MotionCardsFilePath, _cardConfiguration);
                    _configurationStore.SaveJson(_machineId, AxisParametersFilePath, _configuration);
                    configurationSaved = true;

                    if (applyToConnectedCard)
                    {
                        if (canApplyWithoutReconnect)
                        {
                            SetConfigurationStatus("正在在线应用轴映射");
                            axisMappingChanged = await onlineApply.ApplyAxisMappingsWithoutReconnectAsync(
                                _cardConfiguration,
                                configurationToSave,
                                CancellationToken.None);
                        }
                        else
                        {
                            SetCardConnectionStatus("正在连接控制卡");
                            await _cardConnection.ReconnectAsync(_cardConfiguration, CancellationToken.None);
                            connectionChanged = true;
                            SetCardConnectionStatus(
                                "控制卡连接成功格式",
                                _cardConnection.ActualControllerType,
                                _cardConnection.ActualControllerVersion);
                        }

                        await ReadSelectedAxisParametersAsync(false);
                        SetConfigurationStatus(canApplyWithoutReconnect
                            ? "轴映射在线应用成功"
                            : "轴映射保存成功");
                    }
                    else
                    {
                        SetConfigurationStatus("轴映射保存待下发");
                    }
                }
                if (applyToConnectedCard && ApplyConnectedConfigurationAsync != null)
                    await ApplyConnectedConfigurationAsync(SaveAndApplyAsync, requiresReconnect, CancellationToken.None);
                else
                    await SaveAndApplyAsync();
            }
            catch (Exception error)
            {
                if (applyToConnectedCard)
                {
                    if (_cardConnection.IsConnected)
                    {
                        SetCardConnectionStatus(
                            "控制卡连接成功格式",
                            _cardConnection.ActualControllerType,
                            _cardConnection.ActualControllerVersion);
                    }
                    else
                    {
                        SetCardConnectionStatus("控制卡连接失败格式", error.Message);
                    }
                }

                if (configurationSaved)
                {
                    SetConfigurationStatus("轴映射保存下发失败", error.Message);
                    connectionChanged = connectionChanged || !_cardConnection.IsConnected;
                }
                else
                {
                    SetConfigurationStatus("轴映射保存失败", error.Message);
                }

                MessageBox.Show(
                    error.Message,
                    (string)FindResource("轴映射配置"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            finally
            {
                _connectionInProgress = false;
                UpdateCardConnectionUiState();
                if (connectionChanged)
                {
                    NotifyCardConnectionChanged();
                }

                if (axisMappingChanged)
                {
                    NotifyAxisMappingApplied();
                }
            }
        }

        /// <summary>清空当前物理轴的设备名称和运动参数，使该通道回到未配置状态。</summary>
        private void ClearAxisMappingButton_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedAxis == null)
            {
                return;
            }

            ResetAxisAndRefreshEditor(SelectedAxis);
            UpdateCardConnectionUiState();
            SetConfigurationStatus("当前轴映射已清除");
        }

        /// <summary>取消当前未保存修改，并重新读取设备配置文件。</summary>
        private void CancelChangesButton_Click(object sender, RoutedEventArgs e)
        {
            LoadConfiguration();
        }

        /// <summary>向 WPF 页面发送指定属性已变化的通知。</summary>
        private void RaisePropertyChanged(string propertyName)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(propertyName));
            }
        }
    }
}
