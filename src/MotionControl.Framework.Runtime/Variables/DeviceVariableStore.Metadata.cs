using System;
using System.Globalization;
using MotionControl.Framework.Contracts.Variables;

namespace MotionControl.Framework.Runtime.Variables
{
    /// <summary>变量可选元数据的校验和旧值兼容规则，不改变设备采样或控制器行为。</summary>
    public sealed partial class DeviceVariableStore
    {
        /// <summary>校验已填写的单位、范围和精度；旧文件未填写时保持未定义。</summary>
        private static void ValidateMetadata(DeviceVariableDefinition definition, DeviceVariableType type)
        {
            definition.DisplayName = OptionalText(definition.DisplayName);
            definition.EngineeringUnit = OptionalText(definition.EngineeringUnit);
            definition.Dimension = OptionalText(definition.Dimension);
            definition.SourceId = OptionalText(definition.SourceId);
            definition.SourceDescription = OptionalText(definition.SourceDescription);

            bool numeric = NumericValueMetadata.IsNumeric(type);
            if (!numeric && (definition.EngineeringUnit != null || definition.Dimension != null
                || definition.MinimumValue.HasValue || definition.MaximumValue.HasValue || definition.DecimalPlaces.HasValue))
                throw new InvalidOperationException("非数值变量不能填写数值单位、量纲、范围或精度：" + definition.Name + "。");

            if ((definition.MinimumValue.HasValue && !IsFiniteNumber(definition.MinimumValue.Value))
                || (definition.MaximumValue.HasValue && !IsFiniteNumber(definition.MaximumValue.Value))
                || (definition.MinimumValue.HasValue && definition.MaximumValue.HasValue
                    && definition.MinimumValue.Value > definition.MaximumValue.Value))
                throw new InvalidOperationException("设备变量上下限必须是有限数且下限不大于上限：" + definition.Name + "。");

            if (definition.DecimalPlaces.HasValue && (definition.DecimalPlaces.Value < 0 || definition.DecimalPlaces.Value > 15))
                throw new InvalidOperationException("设备变量显示小数位数必须在 0 到 15 之间：" + definition.Name + "。");

            string knownDimension = NumericValueMetadata.KnownDimension(definition.EngineeringUnit);
            if (knownDimension.Length != 0 && definition.Dimension != null
                && !string.Equals(knownDimension, definition.Dimension, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("设备变量的单位与量纲不一致：" + definition.Name
                    + "，单位 " + definition.EngineeringUnit + " 对应 " + knownDimension + "。");
        }

        /// <summary>空白元数据保存为未定义，不补造来源、单位或显示名称。</summary>
        private static string OptionalText(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        /// <summary>只有物理含义和稳定来源相同时才能保留旧值；纯显示文字不参与身份比较。</summary>
        private static bool SameValueMeaning(DeviceVariableDefinition definition, string unit, string dimension, string sourceId)
        {
            return string.Equals(NumericValueMetadata.NormalizeUnit(definition.EngineeringUnit),
                    NumericValueMetadata.NormalizeUnit(unit), StringComparison.Ordinal)
                && string.Equals(NumericValueMetadata.NormalizeText(definition.Dimension),
                    NumericValueMetadata.NormalizeText(dimension), StringComparison.OrdinalIgnoreCase)
                && string.Equals(NumericValueMetadata.NormalizeText(definition.SourceId),
                    NumericValueMetadata.NormalizeText(sourceId), StringComparison.Ordinal);
        }

        /// <summary>确认弹窗只适用于当时的稳定键、类型、物理含义、范围和权限，显示改名不改变写入对象。</summary>
        private static bool SameWriteDefinition(DeviceVariableDefinition current, DeviceVariableDefinition expected)
        {
            return string.Equals(current.Name, expected.Name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(current.DataType, expected.DataType, StringComparison.OrdinalIgnoreCase)
                && SameValueMeaning(current, expected.EngineeringUnit, expected.Dimension, expected.SourceId)
                && current.MinimumValue == expected.MinimumValue && current.MaximumValue == expected.MaximumValue
                && current.AllowOperatorWrite == expected.AllowOperatorWrite;
        }

        /// <summary>对已声明数值范围作最终写入校验；未知当前值不会调用本方法来伪造零值。</summary>
        private static bool TryCheckRange(DeviceVariableDefinition definition, string valueText, out string error)
        {
            error = string.Empty;
            if (!definition.MinimumValue.HasValue && !definition.MaximumValue.HasValue) return true;
            double value;
            if (!double.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out value) || !IsFiniteNumber(value))
            {
                error = "需要有效的有限数值。";
                return false;
            }
            if (definition.MinimumValue.HasValue && value < definition.MinimumValue.Value)
            {
                error = "低于配置下限 " + definition.MinimumValue.Value.ToString(CultureInfo.InvariantCulture) + "。";
                return false;
            }
            if (definition.MaximumValue.HasValue && value > definition.MaximumValue.Value)
            {
                error = "超过配置上限 " + definition.MaximumValue.Value.ToString(CultureInfo.InvariantCulture) + "。";
                return false;
            }
            return true;
        }

        /// <summary>排除无法用于数值判断的 NaN 和无穷大。</summary>
        private static bool IsFiniteNumber(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
