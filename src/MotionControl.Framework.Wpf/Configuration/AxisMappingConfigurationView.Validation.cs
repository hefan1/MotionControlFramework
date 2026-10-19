using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Controls;

namespace MotionControl.Framework.Wpf.Configuration
{
    /// <summary>负责轴映射配置的校验和卡参数回填值检查。</summary>
    public partial class AxisMappingConfigurationView
    {
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


    }
}
