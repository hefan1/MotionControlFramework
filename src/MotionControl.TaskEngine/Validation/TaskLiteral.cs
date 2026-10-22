using System;
using System.Globalization;
using MotionControl.Framework.Contracts.Variables;

namespace MotionControl.TaskEngine.Validation
{
    /// <summary>校验定义内的常量文本；与设备变量的有限基础类型保持一致。</summary>
    internal static class TaskLiteral
    {
        /// <summary>检查常量并在数值类型时返回数值，供静态行程检查使用。</summary>
        internal static bool TryRead(DeviceVariableType type, string text, out double? number)
        {
            number = null;
            if (text == null) return false;
            switch (type)
            {
                case DeviceVariableType.Boolean:
                    bool boolean;
                    return bool.TryParse(text, out boolean);
                case DeviceVariableType.Integer:
                    int integer;
                    if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out integer)) return false;
                    number = integer;
                    return true;
                case DeviceVariableType.UInt32:
                    uint unsigned;
                    if (!uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out unsigned)) return false;
                    number = unsigned;
                    return true;
                case DeviceVariableType.Byte:
                    byte oneByte;
                    if (!byte.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out oneByte)) return false;
                    number = oneByte;
                    return true;
                case DeviceVariableType.Number:
                    double value;
                    if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                        || double.IsNaN(value) || double.IsInfinity(value)) return false;
                    number = value;
                    return true;
                case DeviceVariableType.String:
                    return true;
                case DeviceVariableType.Character:
                    return text.Length == 1;
                case DeviceVariableType.DateTime:
                    DateTime date;
                    return DateTime.TryParseExact(text, "O", CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out date);
                default:
                    return false;
            }
        }

        /// <summary>判断类型能否作为数值参数；不把布尔或数字字符串当作数字。</summary>
        internal static bool IsNumeric(DeviceVariableType type)
        {
            return type == DeviceVariableType.Number || type == DeviceVariableType.Integer
                || type == DeviceVariableType.Byte || type == DeviceVariableType.UInt32;
        }
    }
}
