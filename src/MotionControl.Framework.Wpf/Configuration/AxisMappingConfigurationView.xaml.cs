using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

        /// <summary>通知页面绑定项已经变化。</summary>
        public event PropertyChangedEventHandler PropertyChanged;

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
        public AxisMappingConfigurationView(MachineConfigurationStore configurationStore, string machineId)
        {
            if (configurationStore == null)
            {
                throw new ArgumentNullException(nameof(configurationStore));
            }

            if (string.IsNullOrWhiteSpace(machineId))
            {
                throw new ArgumentException("设备编号不能为空。", nameof(machineId));
            }

            InitializeComponent();
            _configurationStore = configurationStore;
            _machineId = machineId;
            VisibleAxes = new ObservableCollection<AxisMappingEntry>();
            DataContext = this;

            for (int axisNumber = 0; axisNumber < 64; axisNumber++)
            {
                AxisMappingEntry axis = new AxisMappingEntry
                {
                    AxisNumber = axisNumber
                };
                axis.PropertyChanged += Axis_PropertyChanged;
                _allAxes.Add(axis);
            }

            LoadConfiguration();
        }

        /// <summary>根据当前物理轴名称刷新已配置轴数量。</summary>
        private void Axis_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (string.Equals(e.PropertyName, "Name", StringComparison.Ordinal))
            {
                UpdateConfiguredAxisCountText();
            }
        }

        /// <summary>从当前设备的轴参数 JSON 读取设置；文件不存在时显示约定的默认值。</summary>
        private void LoadConfiguration()
        {
            _loadingConfiguration = true;
            _configurationCanBeSaved = true;
            SaveConfigurationButton.IsEnabled = true;
            ConfigurationStatusText.Text = (string)FindResource("轴映射尚未保存");
            ResetAxesToDefaults();
            _configuration = new AxisMappingConfigurationDocument();
            _cardConfiguration = new MotionCardSettingsDocument();

            string machineDirectory = _configurationStore.GetMachineDirectory(_machineId);
            string axisConfigurationPath = Path.Combine(
                machineDirectory,
                AxisParametersFilePath.Replace('/', Path.DirectorySeparatorChar));
            string cardConfigurationPath = Path.Combine(
                machineDirectory,
                MotionCardsFilePath.Replace('/', Path.DirectorySeparatorChar));
            ConfigurationFileText.Text = string.Format(
                CultureInfo.CurrentCulture,
                (string)FindResource("轴映射保存路径格式"),
                cardConfigurationPath,
                axisConfigurationPath);

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
                    ApplyLoadedAxes(_configuration.Axes);
                }

                SelectComboBoxItem(CardVendorComboBox, _cardConfiguration.MotionCardVendor);
                SelectComboBoxItem(CardTypeComboBox, _cardConfiguration.MotionCardType);
                SelectAxisCount(_cardConfiguration.AxisCount);
                RefreshVisibleAxes();
                SelectAxis(_allAxes[0]);
                ConfigurationStatusText.Text = (string)FindResource("轴映射读取完成");
            }
            catch (Exception error)
            {
                ResetAxesToDefaults();
                _configuration = new AxisMappingConfigurationDocument();
                _cardConfiguration = new MotionCardSettingsDocument();
                SelectComboBoxItem(CardVendorComboBox, _cardConfiguration.MotionCardVendor);
                SelectComboBoxItem(CardTypeComboBox, _cardConfiguration.MotionCardType);
                SelectAxisCount(_cardConfiguration.AxisCount);
                RefreshVisibleAxes();
                SelectAxis(_allAxes[0]);
                _configurationCanBeSaved = false;
                SaveConfigurationButton.IsEnabled = false;
                ConfigurationStatusText.Text = string.Format(
                    CultureInfo.CurrentCulture,
                    (string)FindResource("轴映射读取失败"),
                    error.Message);
            }
            finally
            {
                _loadingConfiguration = false;
                UpdateConfiguredAxisCountText();
            }
        }

        /// <summary>把新建配置中的 64 个物理轴通道恢复为直观的默认参数。</summary>
        private void ResetAxesToDefaults()
        {
            foreach (AxisMappingEntry axis in _allAxes)
            {
                axis.Name = string.Empty;
                axis.AxisType = "直线轴";
                axis.EngineeringUnit = "mm";
                axis.UnitsPerEngineeringUnit = 1000;
                axis.MinimumPosition = -100;
                axis.MaximumPosition = 100;
                axis.ManualSpeed = 50;
                axis.ManualAcceleration = 10;
                axis.ManualDeceleration = 10;
                axis.JogAcceleration = 10;
                axis.JogDeceleration = 10;
                axis.HomeMode = 3;
                axis.HomeSearchSpeed = 5;
                axis.HomeCreepSpeed = 1;
                axis.HomeAcceleration = 10;
                axis.HomeDeceleration = 10;
                axis.ControllerId = "MotionCard01";
            }
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

            if (configuration.AxisCount != 8
                && configuration.AxisCount != 16
                && configuration.AxisCount != 32
                && configuration.AxisCount != 64)
            {
                throw new InvalidOperationException("控制卡轴数量只支持 8、16、32 或 64。未保存原配置。");
            }

            if (string.IsNullOrWhiteSpace(configuration.MotionCardVendor)
                || string.IsNullOrWhiteSpace(configuration.MotionCardType))
            {
                throw new InvalidOperationException("MotionCards.json 缺少控制卡厂商或卡类型。未保存原配置。");
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
                if (axis == null || axis.AxisNumber < 0 || axis.AxisNumber >= 64)
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
                visibleAxis.ManualSpeed = configuredAxis.ManualSpeed;
                visibleAxis.ManualAcceleration = configuredAxis.ManualAcceleration;
                visibleAxis.ManualDeceleration = configuredAxis.ManualDeceleration;
                visibleAxis.JogAcceleration = configuredAxis.JogAcceleration;
                visibleAxis.JogDeceleration = configuredAxis.JogDeceleration;
                visibleAxis.HomeMode = configuredAxis.HomeMode;
                visibleAxis.HomeSearchSpeed = configuredAxis.HomeSearchSpeed;
                visibleAxis.HomeCreepSpeed = configuredAxis.HomeCreepSpeed;
                visibleAxis.HomeAcceleration = configuredAxis.HomeAcceleration;
                visibleAxis.HomeDeceleration = configuredAxis.HomeDeceleration;
            }
        }

        /// <summary>设置卡选项下拉框的选中项，并保留配置文件中的厂商文字。</summary>
        private static void SelectComboBoxItem(ComboBox comboBox, string value)
        {
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

        /// <summary>按已保存的轴通道数量选择 8、16、32 或 64 轴。</summary>
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

            throw new InvalidOperationException("找不到已保存的控制卡轴数量选项。");
        }

        /// <summary>根据当前控制卡轴数量刷新左侧物理通道表格。</summary>
        private void RefreshVisibleAxes()
        {
            VisibleAxes.Clear();
            for (int axisNumber = 0; axisNumber < _cardConfiguration.AxisCount; axisNumber++)
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

            SelectedAxisText.Text = string.Format(
                CultureInfo.CurrentCulture,
                (string)FindResource("当前选择物理轴格式"),
                axis.AxisNumber);
            PhysicalAxisTextBox.Text = "Axis" + axis.AxisNumber.ToString(CultureInfo.InvariantCulture);
            if (AxisMappingGrid != null && ReferenceEquals(AxisMappingGrid.SelectedItem, axis) == false)
            {
                AxisMappingGrid.SelectedItem = axis;
                AxisMappingGrid.ScrollIntoView(axis);
            }

            SelectComboBoxItem(AxisTypeComboBox, axis.AxisType);
            SelectComboBoxItem(EngineeringUnitComboBox, axis.EngineeringUnit);
            SelectHomeMode(axis.HomeMode);
        }

        /// <summary>根据物理轴号选择当前编辑行。</summary>
        private void AxisMappingGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SelectAxis(AxisMappingGrid.SelectedItem as AxisMappingEntry);
        }

        /// <summary>记录控制卡厂商下拉框的当前值。</summary>
        private void CardVendorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loadingConfiguration && _cardConfiguration != null)
            {
                _cardConfiguration.MotionCardVendor = GetSelectedTag(CardVendorComboBox);
            }
        }

        /// <summary>记录控制卡类型下拉框的当前值。</summary>
        private void CardTypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loadingConfiguration && _cardConfiguration != null)
            {
                _cardConfiguration.MotionCardType = GetSelectedTag(CardTypeComboBox);
            }
        }

        /// <summary>切换可配置的物理轴通道数量，并避免隐藏仍在使用的轴。</summary>
        private void AxisCountComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingConfiguration || _cardConfiguration == null || AxisCountComboBox.SelectedItem == null)
            {
                return;
            }

            int selectedAxisCount = int.Parse(GetSelectedTag(AxisCountComboBox), CultureInfo.InvariantCulture);
            if (_allAxes.Any(axis => axis.AxisNumber >= selectedAxisCount && axis.IsConfigured))
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
            RefreshVisibleAxes();
            SelectAxis(_allAxes[0]);
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

        /// <summary>显示当前控制卡厂商对应的回零模式选项。</summary>
        private void SelectHomeMode(int homeMode)
        {
            foreach (ComboBoxItem item in HomeModeComboBox.Items)
            {
                int itemMode;
                if (int.TryParse(item.Tag as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out itemMode)
                    && itemMode == homeMode)
                {
                    HomeModeComboBox.SelectedItem = item;
                    return;
                }
            }

            HomeModeComboBox.SelectedIndex = -1;
        }

        /// <summary>将回零模式下拉框选择写入当前物理轴。</summary>
        private void HomeModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingConfiguration || SelectedAxis == null || HomeModeComboBox.SelectedItem == null)
            {
                return;
            }

            SelectedAxis.HomeMode = int.Parse(GetSelectedTag(HomeModeComboBox), CultureInfo.InvariantCulture);
        }

        /// <summary>检查设备轴名称、单位换算、软限位、速度和加减速度参数。</summary>
        private void CheckMappingButton_Click(object sender, RoutedEventArgs e)
        {
            Keyboard.ClearFocus();
            List<string> errors = ValidateConfiguredAxes();
            if (errors.Count > 0)
            {
                ConfigurationStatusText.Text = errors[0];
                MessageBox.Show(
                    string.Join(Environment.NewLine, errors),
                    (string)FindResource("轴映射配置"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            int configuredCount = _allAxes.Count(axis => axis.AxisNumber < _cardConfiguration.AxisCount && axis.IsConfigured);
            ConfigurationStatusText.Text = configuredCount == 0
                ? (string)FindResource("还没有配置设备轴")
                : string.Format(
                    CultureInfo.CurrentCulture,
                    (string)FindResource("轴映射检查通过格式"),
                    configuredCount);
        }

        /// <summary>验证并通过 Runtime 配置存储分别保存控制卡与轴参数 JSON。</summary>
        private void SaveConfigurationButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_configurationCanBeSaved)
            {
                return;
            }

            Keyboard.ClearFocus();
            List<string> errors = ValidateConfiguredAxes();
            if (errors.Count > 0)
            {
                ConfigurationStatusText.Text = errors[0];
                MessageBox.Show(
                    string.Join(Environment.NewLine, errors),
                    (string)FindResource("轴映射配置"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            _configuration.Axes = _allAxes
                .Where(axis => axis.AxisNumber < _cardConfiguration.AxisCount && axis.IsConfigured)
                .OrderBy(axis => axis.AxisNumber)
                .ToList();

            try
            {
                _configurationStore.SaveJson(_machineId, MotionCardsFilePath, _cardConfiguration);
                _configurationStore.SaveJson(_machineId, AxisParametersFilePath, _configuration);
                ConfigurationStatusText.Text = (string)FindResource("轴映射保存成功");
            }
            catch (Exception error)
            {
                ConfigurationStatusText.Text = string.Format(
                    CultureInfo.CurrentCulture,
                    (string)FindResource("轴映射保存失败"),
                    error.Message);
            }
        }

        /// <summary>清空当前物理轴的设备名称，使该通道回到未配置状态。</summary>
        private void ClearAxisMappingButton_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedAxis == null)
            {
                return;
            }

            SelectedAxis.Name = string.Empty;
            ConfigurationStatusText.Text = (string)FindResource("当前轴映射已清除");
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
                if (!IsFinite(axis.UnitsPerEngineeringUnit) || axis.UnitsPerEngineeringUnit <= 0)
                {
                    errors.Add(string.Format((string)FindResource("轴参数大于零格式"), axisName, (string)FindResource("单位换算")));
                }

                if (!IsFinite(axis.MinimumPosition)
                    || !IsFinite(axis.MaximumPosition)
                    || axis.MinimumPosition >= axis.MaximumPosition)
                {
                    errors.Add(string.Format((string)FindResource("轴软限位无效格式"), axisName));
                }

                if (!IsFinite(axis.ManualSpeed) || axis.ManualSpeed <= 0)
                {
                    errors.Add(string.Format((string)FindResource("轴参数大于零格式"), axisName, (string)FindResource("手动速度")));
                }

                if (!IsFinite(axis.ManualAcceleration) || axis.ManualAcceleration <= 0
                    || !IsFinite(axis.ManualDeceleration) || axis.ManualDeceleration <= 0
                    || !IsFinite(axis.JogAcceleration) || axis.JogAcceleration <= 0
                    || !IsFinite(axis.JogDeceleration) || axis.JogDeceleration <= 0
                    || !IsFinite(axis.HomeAcceleration) || axis.HomeAcceleration <= 0
                    || !IsFinite(axis.HomeDeceleration) || axis.HomeDeceleration <= 0)
                {
                    errors.Add(string.Format((string)FindResource("轴加减速度无效格式"), axisName));
                }

                if (!IsFinite(axis.HomeSearchSpeed) || axis.HomeSearchSpeed <= 0
                    || !IsFinite(axis.HomeCreepSpeed) || axis.HomeCreepSpeed <= 0)
                {
                    errors.Add(string.Format((string)FindResource("轴回零速度无效格式"), axisName));
                }

                if (axis.HomeMode < 0)
                {
                    errors.Add(string.Format((string)FindResource("轴回零模式无效格式"), axisName));
                }
            }

            return errors;
        }

        /// <summary>从下拉框已选项目读取用于保存的中文配置值。</summary>
        private static string GetSelectedTag(ComboBox comboBox)
        {
            ComboBoxItem selectedItem = comboBox.SelectedItem as ComboBoxItem;
            return selectedItem == null ? string.Empty : selectedItem.Tag as string;
        }

        /// <summary>判断配置数值是否为有限数值。</summary>
        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
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
