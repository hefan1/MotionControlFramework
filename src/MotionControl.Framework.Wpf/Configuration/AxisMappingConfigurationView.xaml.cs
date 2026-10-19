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

        /// <summary>当前轴数量范围内显示的物理轴通道。</summary>
        public ObservableCollection<AxisMappingEntry> VisibleAxes { get; private set; }

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

        /// <summary>根据当前物理轴名称刷新已配置轴数量，并更新当前轴的写卡按钮状态。</summary>
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

        /// <summary>从当前设备的轴参数 JSON 读取设置；文件不存在时保持所有通道为空。</summary>
        private void LoadConfiguration()
        {
            _loadingConfiguration = true;
            _configurationCanBeSaved = true;
            SaveConfigurationButton.IsEnabled = true;
            SetConfigurationStatus("轴映射尚未保存");
            ResetAxesToUnconfigured();
            _configuration = new AxisMappingConfigurationDocument();
            _cardConfiguration = new MotionCardSettingsDocument();

            string machineDirectory = _configurationStore.GetMachineDirectory(_machineId);
            string axisConfigurationPath = Path.Combine(
                machineDirectory,
                AxisParametersFilePath.Replace('/', Path.DirectorySeparatorChar));
            string cardConfigurationPath = Path.Combine(
                machineDirectory,
                MotionCardsFilePath.Replace('/', Path.DirectorySeparatorChar));
            UpdateConfigurationFileText();

            try
            {
                if (File.Exists(cardConfigurationPath))
                {
                    _cardConfiguration = _configurationStore.LoadJson<MotionCardSettingsDocument>(
                        _machineId,
                        MotionCardsFilePath);
                    ValidateLoadedCardConfiguration(_cardConfiguration);
                }

                if (File.Exists(axisConfigurationPath))
                {
                    _configuration = _configurationStore.LoadJson<AxisMappingConfigurationDocument>(
                        _machineId,
                        AxisParametersFilePath);
                    ValidateLoadedAxisConfiguration(_configuration);
                    EnsureAxisCapacity(GetRequiredAxisCount(_cardConfiguration.AxisCount, _configuration.Axes));
                    ApplyLoadedAxes(_configuration.Axes);
                }

                ApplyDefaultCardSelectionIfEmpty();
                PopulateCardTypeItems(_cardConfiguration.MotionCardVendor, _cardConfiguration.MotionCardType);
                UpdateAxisCountOptions(GetSelectedMotionCardOption(), _cardConfiguration.AxisCount);
                SelectComboBoxItem(CardVendorComboBox, _cardConfiguration.MotionCardVendor);
                SelectComboBoxItem(CardTypeComboBox, _cardConfiguration.MotionCardType);
                SelectAxisCount(_cardConfiguration.AxisCount);
                RefreshVisibleAxes();
                SelectAxis(_allAxes.FirstOrDefault());
                SetConfigurationStatus("轴映射读取完成");
            }
            catch (Exception error)
            {
                ResetAxesToUnconfigured();
                _configuration = new AxisMappingConfigurationDocument();
                _cardConfiguration = new MotionCardSettingsDocument();
                ApplyDefaultCardSelectionIfEmpty();
                PopulateCardTypeItems(_cardConfiguration.MotionCardVendor, _cardConfiguration.MotionCardType);
                UpdateAxisCountOptions(GetSelectedMotionCardOption(), _cardConfiguration.AxisCount);
                SelectComboBoxItem(CardVendorComboBox, _cardConfiguration.MotionCardVendor);
                SelectComboBoxItem(CardTypeComboBox, _cardConfiguration.MotionCardType);
                SelectAxisCount(_cardConfiguration.AxisCount);
                RefreshVisibleAxes();
                SelectAxis(_allAxes.FirstOrDefault());
                _configurationCanBeSaved = false;
                SaveConfigurationButton.IsEnabled = false;
                SetConfigurationStatus("轴映射读取失败", error.Message);
            }
            finally
            {
                _loadingConfiguration = false;
                UpdateConfiguredAxisCountText();
                UpdateCardConnectionUiState();
            }
        }

        /// <summary>把当前页面已有的物理轴通道恢复为空配置，不伪造未映射轴参数。</summary>
        private void ResetAxesToUnconfigured()
        {
            foreach (AxisMappingEntry axis in _allAxes)
            {
                ResetAxisToUnconfigured(axis);
            }
        }

        /// <summary>清空一个物理轴的映射名称、类型、单位及所有运动参数。</summary>
        private static void ResetAxisToUnconfigured(AxisMappingEntry axis)
        {
            axis.Name = null;
            axis.AxisType = null;
            axis.EngineeringUnit = null;
            axis.UnitsPerEngineeringUnit = null;
            axis.MinimumPosition = null;
            axis.MaximumPosition = null;
            axis.SoftLimitEnabled = null;
            axis.ManualSpeed = null;
            axis.HomeMode = null;
            axis.HomeModeAdapterType = null;
            axis.HomeModeVendor = null;
            axis.HomeSearchSpeed = null;
            axis.HomeCreepSpeed = null;
            axis.HomeAcceleration = null;
            axis.HomeDeceleration = null;
            axis.ControllerId = "MotionCard01";
        }

        /// <summary>检查已读取文件的格式版本、卡选项和物理轴号。</summary>
        private static void ValidateLoadedCardConfiguration(MotionCardSettingsDocument configuration)
        {
            if (configuration == null || configuration.SchemaVersion != 1)
            {
                throw new InvalidOperationException("只支持 schemaVersion 为 1 的 MotionCards.json。未保存原配置。");
            }

            if (configuration.MotionCards == null)
            {
                throw new InvalidOperationException("MotionCards.json 缺少 motionCards 列表。未保存原配置。");
            }

            if (configuration.AxisCount < 0 || configuration.AxisCount > 1024)
            {
                throw new InvalidOperationException("MotionCards.json 中的轴数量必须在 0 到 1024 之间。未保存原配置。");
            }

            bool hasVendor = !string.IsNullOrWhiteSpace(configuration.MotionCardVendor);
            bool hasCardType = !string.IsNullOrWhiteSpace(configuration.MotionCardType);
            bool isEmptyTemplate = !hasVendor && !hasCardType
                && configuration.AxisCount == 0 && configuration.MotionCards.Count == 0;
            if (!isEmptyTemplate && (!hasVendor || !hasCardType || configuration.AxisCount == 0))
            {
                throw new InvalidOperationException("MotionCards.json 的厂商、卡类型或轴数量配置不完整。未保存原配置。");
            }
        }

        /// <summary>检查轴参数 JSON 版本及物理轴通道号是否有效。</summary>
        private static void ValidateLoadedAxisConfiguration(AxisMappingConfigurationDocument configuration)
        {
            if (configuration == null || configuration.SchemaVersion != 1)
            {
                throw new InvalidOperationException("只支持 schemaVersion 为 1 的 AxisParameters.json。未保存原配置。");
            }

            if (configuration.Axes == null)
            {
                throw new InvalidOperationException("AxisParameters.json 缺少 axes 列表。未保存原配置。");
            }

            var usedAxisNumbers = new HashSet<int>();
            foreach (AxisMappingEntry axis in configuration.Axes)
            {
                if (axis == null || axis.AxisNumber < 0 || axis.AxisNumber >= 1024)
                {
                    throw new InvalidOperationException("AxisParameters.json 中存在无效的物理轴号。未保存该文件。");
                }

                if (!usedAxisNumbers.Add(axis.AxisNumber))
                {
                    throw new InvalidOperationException("AxisParameters.json 中存在重复的物理轴号。未保存该文件。");
                }
            }
        }

        /// <summary>将文件中已配置的轴参数复制到对应的物理通道行。</summary>
        private void ApplyLoadedAxes(IEnumerable<AxisMappingEntry> configuredAxes)
        {
            foreach (AxisMappingEntry configuredAxis in configuredAxes)
            {
                if (!configuredAxis.IsConfigured)
                {
                    continue;
                }

                AxisMappingEntry visibleAxis = _allAxes[configuredAxis.AxisNumber];
                visibleAxis.ControllerId = string.IsNullOrWhiteSpace(configuredAxis.ControllerId)
                    ? "MotionCard01"
                    : configuredAxis.ControllerId;
                visibleAxis.Name = configuredAxis.Name;
                visibleAxis.AxisType = configuredAxis.AxisType;
                visibleAxis.EngineeringUnit = configuredAxis.EngineeringUnit;
                visibleAxis.UnitsPerEngineeringUnit = configuredAxis.UnitsPerEngineeringUnit;
                visibleAxis.MinimumPosition = configuredAxis.MinimumPosition;
                visibleAxis.MaximumPosition = configuredAxis.MaximumPosition;
                visibleAxis.SoftLimitEnabled = configuredAxis.SoftLimitEnabled;
                visibleAxis.ManualSpeed = configuredAxis.ManualSpeed;
                visibleAxis.HomeMode = configuredAxis.HomeMode;
                visibleAxis.HomeModeVendor = configuredAxis.HomeModeVendor;
                visibleAxis.HomeModeAdapterType = string.IsNullOrWhiteSpace(configuredAxis.HomeModeAdapterType)
                    ? GetLegacyHomeModeAdapterType(configuredAxis)
                    : configuredAxis.HomeModeAdapterType;
                visibleAxis.HomeSearchSpeed = configuredAxis.HomeSearchSpeed;
                visibleAxis.HomeCreepSpeed = configuredAxis.HomeCreepSpeed;
                visibleAxis.HomeAcceleration = configuredAxis.HomeAcceleration;
                visibleAxis.HomeDeceleration = configuredAxis.HomeDeceleration;
            }
        }

        /// <summary>仅在旧配置能从卡清单确认适配器时，补全原有回零模式归属字段。</summary>
        private string GetLegacyHomeModeAdapterType(AxisMappingEntry configuredAxis)
        {
            if (configuredAxis == null || !configuredAxis.HomeMode.HasValue
                || !string.Equals(
                    configuredAxis.HomeModeVendor,
                    _cardConfiguration.MotionCardVendor,
                    StringComparison.Ordinal)
                || _cardConfiguration.MotionCards == null)
            {
                return null;
            }

            string controllerId = string.IsNullOrWhiteSpace(configuredAxis.ControllerId)
                ? "MotionCard01"
                : configuredAxis.ControllerId;
            MotionCardConnectionDefinition definition = _cardConfiguration.MotionCards.FirstOrDefault(
                card => card != null && string.Equals(
                    card.ControllerId,
                    controllerId,
                    StringComparison.OrdinalIgnoreCase));

            return definition == null ? null : definition.AdapterType;
        }

        /// <summary>设置卡选项下拉框的选中项，并保留配置文件中的厂商文字。</summary>
        private static void SelectComboBoxItem(ComboBox comboBox, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                comboBox.SelectedIndex = -1;
                return;
            }

            foreach (ComboBoxItem item in comboBox.Items)
            {
                if (string.Equals(item.Tag as string, value, StringComparison.Ordinal))
                {
                    comboBox.SelectedItem = item;
                    return;
                }
            }

            ComboBoxItem addedItem = new ComboBoxItem
            {
                Tag = value,
                Content = value
            };
            comboBox.Items.Add(addedItem);
            comboBox.SelectedItem = addedItem;
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

        /// <summary>刷新顶部显示的已配置通道数量和控制卡通道总数。</summary>
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
        }

        /// <summary>根据物理轴号选择当前编辑行。</summary>
        private async void AxisMappingGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SelectAxis(AxisMappingGrid.SelectedItem as AxisMappingEntry);
            ResetCardReadbackStatus();
            if (!_loadingConfiguration && _cardConnection.IsConnected)
            {
                await ReadSelectedAxisParametersAsync(false);
            }
        }

        /// <summary>首次配置时按用户选择连接控制卡，已保存配置则由页面启动流程调用。</summary>
        private async void ConnectCardButton_Click(object sender, RoutedEventArgs e)
        {
            await ConnectCardAsync(false);
        }

        /// <summary>按用户操作断开控制卡，并锁定轴映射编辑区。</summary>
        private async void DisconnectCardButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_cardConnection.IsConnected)
            {
                UpdateCardConnectionUiState();
                return;
            }

            _connectionInProgress = true;
            UpdateCardConnectionUiState();
            try
            {
                await _cardConnection.DisconnectAsync(CancellationToken.None);
                SetCardConnectionStatus("控制卡连接已断开");
                ResetCardReadbackStatus();
            }
            catch (Exception error)
            {
                SetCardConnectionStatus("控制卡连接失败格式", error.Message);
            }
            finally
            {
                _connectionInProgress = false;
                UpdateCardConnectionUiState();
                NotifyCardConnectionChanged();
            }
        }

        /// <summary>首次连接或按已保存配置自动连接，并在成功后读取当前选中物理轴参数。</summary>
        private async Task ConnectCardAsync(bool automatic)
        {
            if (_cardConnection.IsConnected)
            {
                UpdateCardConnectionUiState();
                await ReadSelectedAxisParametersAsync(false);
                NotifyCardConnectionChanged();
                return;
            }

            if (!_configurationCanBeSaved)
            {
                SetCardConnectionStatus("控制卡连接失败格式", (string)FindResource("轴映射读取失败"));
                return;
            }

            if (GetSelectedMotionCardOption() == null)
            {
                SetCardConnectionStatus("控制卡选项未接入");
                UpdateCardConnectionUiState();
                return;
            }

            _connectionInProgress = true;
            SetCardConnectionStatus("正在连接控制卡");
            UpdateCardConnectionUiState();
            try
            {
                await _cardConnection.ConnectAsync(_cardConfiguration, CancellationToken.None);
                SetCardConnectionStatus(
                    "控制卡连接成功格式",
                    _cardConnection.ActualControllerType,
                    _cardConnection.ActualControllerVersion);
                await ReadSelectedAxisParametersAsync(false);
            }
            catch (Exception error)
            {
                SetCardConnectionStatus("控制卡连接失败格式", error.Message);
                if (!automatic)
                {
                    MessageBox.Show(
                        error.Message,
                        (string)FindResource("连接运动控制卡"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
            finally
            {
                _connectionInProgress = false;
                UpdateCardConnectionUiState();
                NotifyCardConnectionChanged();
            }
        }

        /// <summary>依据真实连接状态启用参数编辑、轴映射保存和控制卡操作按钮。</summary>
        private void UpdateCardConnectionUiState()
        {
            if (CardConnectionStatusText == null)
            {
                return;
            }

            bool connected = _cardConnection.IsConnected;
            bool hasSupportedCard = GetSelectedMotionCardOption() != null;
            ConnectionIndicator.Fill = connected ? new SolidColorBrush(Color.FromRgb(35, 153, 85)) : new SolidColorBrush(Color.FromRgb(135, 151, 168));
            CardConnectionStatusBadge.Background = connected ? new SolidColorBrush(Color.FromRgb(232, 247, 237)) : new SolidColorBrush(Color.FromRgb(239, 244, 249));
            CardConnectionStatusText.Foreground = connected ? new SolidColorBrush(Color.FromRgb(28, 126, 70)) : new SolidColorBrush(Color.FromRgb(89, 107, 125));
            CardVendorComboBox.IsEnabled = !connected && !_connectionInProgress;
            CardTypeComboBox.IsEnabled = !connected && !_connectionInProgress;
            AxisCountComboBox.IsEnabled = !connected && !_connectionInProgress;
            ConnectCardButton.IsEnabled = !connected && !_connectionInProgress && hasSupportedCard;
            DisconnectCardButton.IsEnabled = connected && !_connectionInProgress;
            AxisMappingGrid.IsEnabled = !_connectionInProgress;
            AxisDetailsGrid.IsEnabled = !_connectionInProgress;
            ClearAxisMappingButton.IsEnabled = !_connectionInProgress;
            CheckMappingButton.IsEnabled = !_connectionInProgress;
            SaveConfigurationButton.IsEnabled = !_connectionInProgress && _configurationCanBeSaved;
            ReadAxisParametersButton.IsEnabled = connected && !_connectionInProgress;
        }

        /// <summary>读取当前物理轴的控制器参数，并在右侧以只读文本显示卡上实时值。</summary>
        private async Task<AxisMappingCardParameters> ReadSelectedAxisParametersAsync(bool showError)
        {
            if (!_cardConnection.IsConnected || SelectedAxis == null)
            {
                return null;
            }

            int axisNumber = SelectedAxis.AxisNumber;
            try
            {
                AxisMappingCardParameters parameters = await _cardConnection.ReadAxisParametersAsync(
                    axisNumber,
                    CancellationToken.None);
                if (parameters == null)
                {
                    throw new InvalidOperationException("控制卡适配器没有返回轴参数。");
                }

                _cardReadbackAttempted = true;
                _lastReadbackParameters = parameters;
                _lastReadbackError = null;
                _lastReadbackAxisNumber = axisNumber;
                RefreshCardReadbackText();

                return parameters;
            }
            catch (Exception error)
            {
                _cardReadbackAttempted = true;
                _lastReadbackParameters = null;
                _lastReadbackError = error.Message;
                _lastReadbackAxisNumber = axisNumber;
                RefreshCardReadbackText();
                if (showError)
                {
                    MessageBox.Show(
                        error.Message,
                        (string)FindResource("读取控制卡参数"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }

                return null;
            }
        }

        /// <summary>把卡上软限位状态转换成当前界面的本地化文字。</summary>
        private string GetSoftLimitStateText(bool? isEnabled)
        {
            if (!isEnabled.HasValue)
            {
                return (string)FindResource("软限位状态无法判断");
            }

            return isEnabled.Value
                ? (string)FindResource("软限位已启用")
                : (string)FindResource("软限位未启用");
        }

        /// <summary>按用户操作读取卡上参数并回填当前轴编辑区，不自动保存配置或写卡。</summary>
        private async void ReadAxisParametersButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_cardConnection.IsConnected || SelectedAxis == null)
            {
                return;
            }

            AxisMappingEntry axis = SelectedAxis;
            _connectionInProgress = true;
            UpdateCardConnectionUiState();
            try
            {
                AxisMappingCardParameters parameters = await ReadSelectedAxisParametersAsync(true);
                if (parameters == null)
                {
                    return;
                }

                var invalidValues = new List<string>();
                CopyPositiveValue(
                    parameters.UnitsPerEngineeringUnit,
                    value => axis.UnitsPerEngineeringUnit = value,
                    (string)FindResource("单位换算"),
                    invalidValues);

                bool? controllerSoftLimitEnabled = GetCombinedSoftLimitState(parameters);
                if (controllerSoftLimitEnabled.HasValue)
                {
                    axis.SoftLimitEnabled = controllerSoftLimitEnabled;
                    if (controllerSoftLimitEnabled.Value)
                    {
                        if (IsValidSoftLimitRange(parameters.MinimumPosition, parameters.MaximumPosition))
                        {
                            axis.MinimumPosition = parameters.MinimumPosition;
                            axis.MaximumPosition = parameters.MaximumPosition;
                        }
                        else
                        {
                            invalidValues.Add(
                                (string)FindResource("软限位下限") + " / "
                                + (string)FindResource("软限位上限"));
                        }
                    }
                }
                else
                {
                    invalidValues.Add("软限位状态");
                }
                UpdateSoftLimitEnabledEditor(axis);

                CopyPositiveValue(
                    parameters.Speed,
                    value => axis.HomeSearchSpeed = value,
                    (string)FindResource("回零搜索速度"),
                    invalidValues);
                CopyPositiveValue(
                    parameters.CreepSpeed,
                    value => axis.HomeCreepSpeed = value,
                    (string)FindResource("回零爬行速度"),
                    invalidValues);
                CopyPositiveValue(
                    parameters.Acceleration,
                    value => axis.HomeAcceleration = value,
                    (string)FindResource("回零加速度"),
                    invalidValues);
                CopyPositiveValue(
                    parameters.Deceleration,
                    value => axis.HomeDeceleration = value,
                    (string)FindResource("回零减速度"),
                    invalidValues);

                if (invalidValues.Count == 0)
                {
                    SetConfigurationStatus("轴参数读卡回填完成格式", axis.AxisNumber);
                }
                else
                {
                    SetConfigurationStatus(
                        "轴参数读卡回填部分格式",
                        axis.AxisNumber,
                        string.Join("、", invalidValues.Distinct()));
                }

                if (controllerSoftLimitEnabled == false)
                {
                    _configurationStatusShowsSoftLimitNotice = true;
                    RefreshConfigurationStatusText();
                }
            }
            finally
            {
                _connectionInProgress = false;
                UpdateCardConnectionUiState();
            }
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

        /// <summary>保存轴映射；连接时重载运行时轴并应用参数，断开时保存为待应用配置。</summary>
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

            bool applyToConnectedCard = _cardConnection.IsConnected;
            if (applyToConnectedCard
                && MessageBox.Show(
                    (string)FindResource("保存并应用轴映射确认内容"),
                    (string)FindResource("保存并应用轴映射确认标题"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            _configuration.Axes = _allAxes
                .Where(axis => axis.AxisNumber < _cardConfiguration.AxisCount && axis.IsConfigured)
                .OrderBy(axis => axis.AxisNumber)
                .ToList();

            bool configurationSaved = false;
            bool refreshManualPage = false;
            _connectionInProgress = true;
            UpdateCardConnectionUiState();
            try
            {
                _configurationStore.SaveJson(_machineId, MotionCardsFilePath, _cardConfiguration);
                _configurationStore.SaveJson(_machineId, AxisParametersFilePath, _configuration);
                configurationSaved = true;

                if (applyToConnectedCard)
                {
                    SetCardConnectionStatus("正在连接控制卡");
                    await _cardConnection.ReconnectAsync(_cardConfiguration, CancellationToken.None);
                    refreshManualPage = true;
                    SetCardConnectionStatus(
                        "控制卡连接成功格式",
                        _cardConnection.ActualControllerType,
                        _cardConnection.ActualControllerVersion);
                    await ReadSelectedAxisParametersAsync(false);
                    SetConfigurationStatus("轴映射保存成功");
                }
                else
                {
                    SetConfigurationStatus("轴映射保存待下发");
                }
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
                    refreshManualPage = !_cardConnection.IsConnected;
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
                if (refreshManualPage)
                {
                    NotifyCardConnectionChanged();
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

        /// <summary>验证所有已命名轴的通道范围、名称唯一性和运动参数。</summary>
        private List<string> ValidateConfiguredAxes()
        {
            var errors = new List<string>();
            MotionCardSelectionOption selectedMotionCard = GetSelectedMotionCardOption();
            if (selectedMotionCard == null)
            {
                errors.Add((string)FindResource("控制卡选项未接入"));
            }
            else if (selectedMotionCard.SupportedAxisCounts == null
                || !selectedMotionCard.SupportedAxisCounts.Contains(_cardConfiguration.AxisCount))
            {
                errors.Add(string.Format(
                    (string)FindResource("卡轴数量不受支持格式"),
                    _cardConfiguration.AxisCount));
            }

            foreach (AxisMappingEntry axis in _allAxes.Where(axis =>
                axis.AxisNumber < _cardConfiguration.AxisCount
                && !axis.IsConfigured
                && HasAxisParameterValues(axis)))
            {
                string physicalAxisName = "Axis" + axis.AxisNumber.ToString(CultureInfo.InvariantCulture);
                errors.Add(string.Format(
                    CultureInfo.CurrentCulture,
                    (string)FindResource("未命名物理轴仍有参数格式"),
                    physicalAxisName));
            }

            List<AxisMappingEntry> configuredAxes = _allAxes
                .Where(axis => axis.AxisNumber < _cardConfiguration.AxisCount && axis.IsConfigured)
                .ToList();

            foreach (IGrouping<string, AxisMappingEntry> duplicateName in configuredAxes
                .GroupBy(axis => axis.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1))
            {
                errors.Add(string.Format(
                    CultureInfo.CurrentCulture,
                    (string)FindResource("设备轴名称重复格式"),
                    duplicateName.Key));
            }

            foreach (AxisMappingEntry axis in configuredAxes)
            {
                string axisName = "Axis" + axis.AxisNumber.ToString(CultureInfo.InvariantCulture);
                if (string.IsNullOrWhiteSpace(axis.AxisType))
                {
                    errors.Add(string.Format((string)FindResource("轴类型未选择格式"), axisName));
                }

                if (string.IsNullOrWhiteSpace(axis.EngineeringUnit))
                {
                    errors.Add(string.Format((string)FindResource("工程单位未选择格式"), axisName));
                }

                if (!IsFinite(axis.UnitsPerEngineeringUnit) || axis.UnitsPerEngineeringUnit.Value <= 0)
                {
                    errors.Add(string.Format((string)FindResource("轴参数大于零格式"), axisName, (string)FindResource("单位换算")));
                }

                if (!axis.SoftLimitEnabled.HasValue)
                {
                    errors.Add(string.Format((string)FindResource("轴软限位启用未选择格式"), axisName));
                }

                if (axis.SoftLimitEnabled == true || axis.MinimumPosition.HasValue || axis.MaximumPosition.HasValue)
                {
                    if (!HasValidSoftLimitRange(axis))
                    {
                        errors.Add(string.Format((string)FindResource("轴软限位无效格式"), axisName));
                    }
                }

                if (!IsFinite(axis.ManualSpeed) || axis.ManualSpeed.Value <= 0)
                {
                    errors.Add(string.Format((string)FindResource("轴参数大于零格式"), axisName, (string)FindResource("手动调试速度")));
                }

                if (!IsFinite(axis.HomeAcceleration) || axis.HomeAcceleration.Value <= 0
                    || !IsFinite(axis.HomeDeceleration) || axis.HomeDeceleration.Value <= 0)
                {
                    errors.Add(string.Format((string)FindResource("轴加减速度无效格式"), axisName));
                }

                if (!IsFinite(axis.HomeSearchSpeed) || axis.HomeSearchSpeed.Value <= 0
                    || !IsFinite(axis.HomeCreepSpeed) || axis.HomeCreepSpeed.Value <= 0)
                {
                    errors.Add(string.Format((string)FindResource("轴回零速度无效格式"), axisName));
                }

                IReadOnlyList<AxisHomeModeOption> supportedHomeModes = selectedMotionCard == null
                    ? null
                    : _cardConnection.GetSupportedHomeModes(selectedMotionCard, axis.AxisNumber);
                if (supportedHomeModes != null && supportedHomeModes.Count > 0
                    && !IsHomeModeSupported(axis, selectedMotionCard))
                {
                    errors.Add(string.Format((string)FindResource("轴回零模式无效格式"), axisName));
                }
            }

            return errors;
        }

        /// <summary>判断未命名物理轴是否仍保留用户填写的类型、单位或运动参数。</summary>
        private static bool HasAxisParameterValues(AxisMappingEntry axis)
        {
            return !string.IsNullOrWhiteSpace(axis.AxisType)
                || !string.IsNullOrWhiteSpace(axis.EngineeringUnit)
                || axis.UnitsPerEngineeringUnit.HasValue
                || axis.MinimumPosition.HasValue
                || axis.MaximumPosition.HasValue
                || axis.SoftLimitEnabled.HasValue
                || axis.ManualSpeed.HasValue
                || axis.HomeMode.HasValue
                || !string.IsNullOrWhiteSpace(axis.HomeModeAdapterType)
                || !string.IsNullOrWhiteSpace(axis.HomeModeVendor)
                || axis.HomeSearchSpeed.HasValue
                || axis.HomeCreepSpeed.HasValue
                || axis.HomeAcceleration.HasValue
                || axis.HomeDeceleration.HasValue;
        }

        /// <summary>从下拉框已选项目读取用于保存的中文配置值。</summary>
        private static string GetSelectedTag(ComboBox comboBox)
        {
            ComboBoxItem selectedItem = comboBox.SelectedItem as ComboBoxItem;
            return selectedItem == null ? string.Empty : selectedItem.Tag as string;
        }

        /// <summary>判断配置数值是否已填写且为有限数值。</summary>
        private static bool IsFinite(double? value)
        {
            return value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value);
        }

        /// <summary>检查配置中的软限位下限、上限均有效且下限小于上限。</summary>
        private static bool HasValidSoftLimitRange(AxisMappingEntry axis)
        {
            return axis != null && IsValidSoftLimitRange(axis.MinimumPosition, axis.MaximumPosition);
        }

        /// <summary>检查一组正负软限位值是否有效。</summary>
        private static bool IsValidSoftLimitRange(double? minimumPosition, double? maximumPosition)
        {
            return IsFinite(minimumPosition)
                && IsFinite(maximumPosition)
                && minimumPosition.Value < maximumPosition.Value;
        }

        /// <summary>只把大于零的有效卡参数回填，避免零值覆盖用户已配置的运动参数。</summary>
        private static void CopyPositiveValue(
            double controllerValue,
            Action<double> copyValue,
            string parameterName,
            ICollection<string> invalidValues)
        {
            if (IsFinite(controllerValue) && controllerValue > 0)
            {
                copyValue(controllerValue);
                return;
            }

            invalidValues.Add(parameterName);
        }

        /// <summary>只有正负方向都处于同一可判定状态时，才得到整体软限位开关状态。</summary>
        private static bool? GetCombinedSoftLimitState(AxisMappingCardParameters parameters)
        {
            if (parameters == null
                || !parameters.PositiveSoftLimitEffective.HasValue
                || !parameters.NegativeSoftLimitEffective.HasValue
                || parameters.PositiveSoftLimitEffective.Value != parameters.NegativeSoftLimitEffective.Value)
            {
                return null;
            }

            return parameters.PositiveSoftLimitEffective.Value;
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
