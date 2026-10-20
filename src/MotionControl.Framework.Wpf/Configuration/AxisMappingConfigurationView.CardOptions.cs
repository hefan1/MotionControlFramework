using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Controls;
using System.Windows;

namespace MotionControl.Framework.Wpf.Configuration
{
    /// <summary>集中处理轴映射页中由 Host 适配器提供的控制卡和回零选项。</summary>
    public partial class AxisMappingConfigurationView
    {
        /// <summary>根据 Host 提供的卡型计算当前页面需要显示的最大物理轴数量。</summary>
        private static int GetMaximumSupportedAxisCount(
            IReadOnlyList<MotionCardSelectionOption> motionCards)
        {
            int maximumAxisCount = 0;
            if (motionCards == null)
            {
                return maximumAxisCount;
            }

            foreach (MotionCardSelectionOption motionCard in motionCards)
            {
                if (motionCard == null || motionCard.SupportedAxisCounts == null)
                {
                    continue;
                }

                foreach (int axisCount in motionCard.SupportedAxisCounts)
                {
                    if (axisCount > maximumAxisCount)
                    {
                        maximumAxisCount = axisCount;
                    }
                }
            }

            return maximumAxisCount;
        }

        /// <summary>按当前 Host 的卡型清单预先建立厂商下拉框选项。</summary>
        private void InitializeSupportedCardVendorItems()
        {
            CardVendorComboBox.Items.Clear();
            var addedVendorCodes = new HashSet<string>(StringComparer.Ordinal);
            foreach (MotionCardSelectionOption motionCard in _supportedMotionCards)
            {
                if (motionCard == null || string.IsNullOrWhiteSpace(motionCard.VendorCode)
                    || !addedVendorCodes.Add(motionCard.VendorCode))
                {
                    continue;
                }

                CardVendorComboBox.Items.Add(new ComboBoxItem
                {
                    Tag = motionCard.VendorCode,
                    Content = GetResourceText(
                        motionCard.VendorDisplayResourceKey,
                        motionCard.VendorCode)
                });
            }
        }

        /// <summary>读取 Host 支持的厂商与卡型选项，不在通用页面写死厂商清单。</summary>
        private MotionCardSelectionOption FindSupportedMotionCardOption(string vendorCode, string cardTypeCode)
        {
            return _supportedMotionCards.FirstOrDefault(motionCard => motionCard != null
                && string.Equals(motionCard.VendorCode, vendorCode, StringComparison.Ordinal)
                && string.Equals(motionCard.CardTypeCode, cardTypeCode, StringComparison.Ordinal));
        }

        /// <summary>按厂商刷新卡类型选项，并保留配置文件中当前 Host 未接入的旧值。</summary>
        private void PopulateCardTypeItems(string vendorCode, string selectedCardType)
        {
            CardTypeComboBox.Items.Clear();
            var addedCardTypes = new HashSet<string>(StringComparer.Ordinal);
            foreach (MotionCardSelectionOption motionCard in _supportedMotionCards)
            {
                if (motionCard == null || !string.Equals(
                    motionCard.VendorCode,
                    vendorCode,
                    StringComparison.Ordinal)
                    || !addedCardTypes.Add(motionCard.CardTypeCode))
                {
                    continue;
                }

                CardTypeComboBox.Items.Add(new ComboBoxItem
                {
                    Tag = motionCard.CardTypeCode,
                    Content = GetResourceText(
                        motionCard.CardTypeDisplayResourceKey,
                        motionCard.CardTypeCode)
                });
            }

            SelectComboBoxItem(CardTypeComboBox, selectedCardType);
        }

        /// <summary>按当前卡型刷新可选轴数量；旧配置中的未知值仍显示，保存时再明确报错。</summary>
        private void UpdateAxisCountOptions(MotionCardSelectionOption motionCard, int selectedAxisCount)
        {
            AxisCountComboBox.Items.Clear();
            if (motionCard != null && motionCard.SupportedAxisCounts != null)
            {
                foreach (int axisCount in motionCard.SupportedAxisCounts.Distinct().OrderBy(value => value))
                {
                    if (axisCount <= 0)
                    {
                        continue;
                    }

                    AxisCountComboBox.Items.Add(new ComboBoxItem
                    {
                        Tag = axisCount.ToString(CultureInfo.InvariantCulture),
                        Content = axisCount.ToString(CultureInfo.InvariantCulture)
                    });
                }
            }

            SelectAxisCount(selectedAxisCount);
        }

        /// <summary>返回界面当前选择且与已保存适配器标识一致的卡型。</summary>
        private MotionCardSelectionOption GetSelectedMotionCardOption()
        {
            if (_cardConfiguration == null)
            {
                return null;
            }

            MotionCardSelectionOption motionCard = FindSupportedMotionCardOption(
                _cardConfiguration.MotionCardVendor,
                _cardConfiguration.MotionCardType);
            if (motionCard == null || _cardConfiguration.MotionCards == null)
            {
                return motionCard;
            }

            MotionCardConnectionDefinition connection = _cardConfiguration.MotionCards.FirstOrDefault();
            if (connection != null && !string.IsNullOrWhiteSpace(connection.AdapterType)
                && !string.Equals(connection.AdapterType, motionCard.AdapterType, StringComparison.Ordinal))
            {
                return null;
            }

            return motionCard;
        }

        /// <summary>切换适配器时更新连接标识并清除旧适配器的连接配置文件路径。</summary>
        private void UpdateExistingConnectionAdapterType(MotionCardSelectionOption motionCard)
        {
            if (motionCard == null || _cardConfiguration.MotionCards == null)
            {
                return;
            }

            MotionCardConnectionDefinition connection = _cardConfiguration.MotionCards.FirstOrDefault();
            if (connection != null)
            {
                if (!string.Equals(connection.AdapterType, motionCard.AdapterType, StringComparison.Ordinal))
                {
                    connection.ConnectionConfigurationFile = null;
                }

                connection.AdapterType = motionCard.AdapterType;
            }
        }

        /// <summary>对空白模板或首次启动配置使用 Host 当前支持清单中的默认卡型。</summary>
        private void ApplyDefaultCardSelectionIfEmpty()
        {
            if (!string.IsNullOrWhiteSpace(_cardConfiguration.MotionCardVendor)
                || !string.IsNullOrWhiteSpace(_cardConfiguration.MotionCardType)
                || _cardConfiguration.AxisCount > 0)
            {
                return;
            }

            MotionCardSelectionOption defaultMotionCard = _supportedMotionCards.FirstOrDefault();
            if (defaultMotionCard == null)
            {
                return;
            }

            _cardConfiguration.MotionCardVendor = defaultMotionCard.VendorCode;
            _cardConfiguration.MotionCardType = defaultMotionCard.CardTypeCode;
            _cardConfiguration.AxisCount = defaultMotionCard.DefaultAxisCount;
        }

        /// <summary>按已保存的轴数量和轴映射记录确保每个物理轴都有页面数据行。</summary>
        private int GetRequiredAxisCount(int savedAxisCount, IEnumerable<AxisMappingEntry> configuredAxes)
        {
            int requiredAxisCount = Math.Max(0, savedAxisCount);
            if (configuredAxes == null)
            {
                return requiredAxisCount;
            }

            foreach (AxisMappingEntry axis in configuredAxes)
            {
                if (axis != null)
                {
                    requiredAxisCount = Math.Max(requiredAxisCount, axis.AxisNumber + 1);
                }
            }

            return requiredAxisCount;
        }

        /// <summary>为 Host 报告的通道范围创建缺少的物理轴行。</summary>
        private void EnsureAxisCapacity(int requiredAxisCount)
        {
            while (_allAxes.Count < requiredAxisCount)
            {
                int axisNumber = _allAxes.Count;
                var axis = new AxisMappingEntry
                {
                    AxisNumber = axisNumber
                };
                axis.PropertyChanged += Axis_PropertyChanged;
                _allAxes.Add(axis);
            }
        }

        /// <summary>显示控制卡原始编号和适配器提供的回零名称，并核对保存的适配器归属。</summary>
        private void UpdateHomeModeEditor(AxisMappingEntry axis)
        {
            bool wasLoadingConfiguration = _loadingConfiguration;
            _loadingConfiguration = true;
            try
            {
                HomeModeComboBox.Items.Clear();
                MotionCardSelectionOption motionCard = GetSelectedMotionCardOption();
                IReadOnlyList<AxisHomeModeOption> homeModes = motionCard == null
                    ? new List<AxisHomeModeOption>().AsReadOnly()
                    : _cardConnection.GetSupportedHomeModes(motionCard, axis == null ? -1 : axis.AxisNumber);

                if (homeModes != null)
                {
                    foreach (AxisHomeModeOption homeMode in homeModes)
                    {
                        if (homeMode == null)
                        {
                            continue;
                        }

                        HomeModeComboBox.Items.Add(new ComboBoxItem
                        {
                            Tag = homeMode.ModeNumber.ToString(CultureInfo.InvariantCulture),
                            Content = GetHomeModeDisplayText(homeMode)
                        });
                    }
                }

                HomeModeComboBox.IsEnabled = HomeModeComboBox.Items.Count > 0;
                if (HomeModeComboBox.Items.Count == 0)
                {
                    HomeModeComboBox.SelectedIndex = -1;
                    HomeModeHintTextBlock.SetResourceReference(
                        TextBlock.TextProperty,
                        "回零模式适配器未提供");
                    return;
                }

                if (axis == null || !axis.HomeMode.HasValue)
                {
                    HomeModeComboBox.SelectedIndex = -1;
                    HomeModeHintTextBlock.SetResourceReference(
                        TextBlock.TextProperty,
                        "回零模式手册提示");
                    return;
                }

                if (!string.Equals(axis.HomeModeAdapterType, motionCard.AdapterType, StringComparison.Ordinal))
                {
                    HomeModeComboBox.SelectedIndex = -1;
                    HomeModeHintTextBlock.SetResourceReference(
                        TextBlock.TextProperty,
                        "回零模式属于其他适配器");
                    return;
                }

                foreach (ComboBoxItem item in HomeModeComboBox.Items)
                {
                    int itemMode;
                    if (int.TryParse(item.Tag as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out itemMode)
                        && itemMode == axis.HomeMode.Value)
                    {
                        HomeModeComboBox.SelectedItem = item;
                        AxisHomeModeOption selectedHomeMode = homeModes.FirstOrDefault(
                            mode => mode != null && mode.ModeNumber == itemMode);
                        SetHomeModeDescription(selectedHomeMode);
                        return;
                    }
                }

                HomeModeComboBox.SelectedIndex = -1;
                HomeModeHintTextBlock.SetResourceReference(
                    TextBlock.TextProperty,
                    "回零模式不受支持");
            }
            finally
            {
                _loadingConfiguration = wasLoadingConfiguration;
            }
        }

        /// <summary>组合控制卡原始模式编号和当前语言的简明名称；缺少资源时保留原编号。</summary>
        private string GetHomeModeDisplayText(AxisHomeModeOption homeMode)
        {
            string modeNumber = homeMode.ModeNumber.ToString(CultureInfo.InvariantCulture);
            string displayName = TryFindResource(homeMode.DisplayNameResourceKey) as string;
            return string.IsNullOrWhiteSpace(displayName)
                ? modeNumber
                : modeNumber + " - " + displayName;
        }

        /// <summary>在回零模式提示区显示当前适配器提供的动作说明，并保留通用安全提示作工具提示。</summary>
        private void SetHomeModeDescription(AxisHomeModeOption homeMode)
        {
            string descriptionKey = homeMode == null ? null : homeMode.DescriptionResourceKey;
            if (string.IsNullOrWhiteSpace(descriptionKey) || TryFindResource(descriptionKey) == null)
            {
                descriptionKey = "回零模式适用提示";
            }

            HomeModeHintTextBlock.SetResourceReference(TextBlock.TextProperty, descriptionKey);
            HomeModeHintTextBlock.ToolTip = TryFindResource("回零模式适用提示");
        }

        /// <summary>将当前选择的回零模式编号和适配器归属保存到所选物理轴。</summary>
        private void HomeModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingConfiguration || SelectedAxis == null || HomeModeComboBox.SelectedItem == null)
            {
                return;
            }

            MotionCardSelectionOption motionCard = GetSelectedMotionCardOption();
            int modeNumber;
            if (motionCard == null || !int.TryParse(
                GetSelectedTag(HomeModeComboBox),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out modeNumber))
            {
                return;
            }

            SelectedAxis.HomeMode = modeNumber;
            SelectedAxis.HomeModeAdapterType = motionCard.AdapterType;
            SelectedAxis.HomeModeVendor = null;
            UpdateHomeModeEditor(SelectedAxis);
        }

        /// <summary>判断当前卡适配器是否提供了该轴保存的回零模式。</summary>
        private bool IsHomeModeSupported(AxisMappingEntry axis, MotionCardSelectionOption motionCard)
        {
            if (axis == null || !axis.HomeMode.HasValue || motionCard == null
                || !string.Equals(axis.HomeModeAdapterType, motionCard.AdapterType, StringComparison.Ordinal))
            {
                return false;
            }

            IReadOnlyList<AxisHomeModeOption> homeModes = _cardConnection.GetSupportedHomeModes(
                motionCard,
                axis.AxisNumber);
            return homeModes != null && homeModes.Any(homeMode =>
                homeMode != null && homeMode.ModeNumber == axis.HomeMode.Value);
        }

        /// <summary>按当前资源语言获取显示文字；缺少资源时使用卡选项中携带的标识。</summary>
        private string GetResourceText(string resourceKey, string fallbackText)
        {
            object resource = string.IsNullOrWhiteSpace(resourceKey) ? null : TryFindResource(resourceKey);
            return resource as string ?? fallbackText;
        }
    }
}
