using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Controls;

namespace MotionControl.Framework.Wpf.Configuration
{
    /// <summary>负责轴映射页面的配置读取、格式检查和旧配置兼容。</summary>
    public partial class AxisMappingConfigurationView
    {
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


    }
}
