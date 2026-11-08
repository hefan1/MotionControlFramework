using System;

namespace MotionControl.Framework.Contracts.Variables
{
    /// <summary>只检查已声明的单位和量纲；不猜变量用途，也不执行隐式单位换算。</summary>
    public static class NumericValueMetadata
    {
        /// <summary>整理可选文本；空值仍表示未定义。</summary>
        public static string NormalizeText(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }

        /// <summary>仅统一明确等价别名；单位符号保留大小写，mPa 与 MPa 等比例不同的单位不能混同。</summary>
        public static string NormalizeUnit(string unit)
        {
            string value = NormalizeText(unit);
            if (value == "°" || string.Equals(value, "degree", StringComparison.OrdinalIgnoreCase))
                return "deg";
            if (value == "µm" || value == "μm") return "um";
            if (value == "°/s") return "deg/s";
            return value;
        }

        /// <summary>从已配置的常见单位识别物理量纲；自定义单位不作推断。</summary>
        public static string KnownDimension(string unit)
        {
            switch (NormalizeUnit(unit))
            {
                case "mm": case "cm": case "m": case "um": return "Length";
                case "deg": case "rad": return "Angle";
                case "s": case "ms": case "us": return "Time";
                case "mm/s": case "cm/s": case "m/s": return "LinearSpeed";
                case "deg/s": case "rad/s": return "AngularSpeed";
                case "N": case "kN": case "mN": case "MN": return "Force";
                case "Pa": case "kPa": case "MPa": case "mPa": case "GPa": case "bar": return "Pressure";
                default: return string.Empty;
            }
        }

        /// <summary>双方均有明确元数据时拒绝不兼容量纲或单位；缺少元数据由调用方提示未定义。</summary>
        public static string GetCompatibilityError(string leftDimension, string leftUnit,
            string rightDimension, string rightUnit)
        {
            string firstDimension = NormalizeText(leftDimension);
            string secondDimension = NormalizeText(rightDimension);
            if (firstDimension.Length == 0) firstDimension = KnownDimension(leftUnit);
            if (secondDimension.Length == 0) secondDimension = KnownDimension(rightUnit);
            if (firstDimension.Length != 0 && secondDimension.Length != 0
                && !string.Equals(firstDimension, secondDimension, StringComparison.OrdinalIgnoreCase))
                return "量纲不兼容：" + firstDimension + " / " + secondDimension + "。";

            string firstUnit = NormalizeUnit(leftUnit);
            string secondUnit = NormalizeUnit(rightUnit);
            if (firstUnit.Length != 0 && secondUnit.Length != 0
                && !string.Equals(firstUnit, secondUnit, StringComparison.Ordinal))
                return "工程单位不一致：" + firstUnit + " / " + secondUnit + "；当前绑定不会自动换算单位。";
            return null;
        }

        /// <summary>判断可保存上下限和显示精度的数值类型。</summary>
        public static bool IsNumeric(DeviceVariableType type)
        {
            return type == DeviceVariableType.Number || type == DeviceVariableType.Integer
                || type == DeviceVariableType.Byte || type == DeviceVariableType.UInt32;
        }
    }
}
