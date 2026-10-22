using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Validation;

namespace MotionControl.TaskEngine.Execution
{
    /// <summary>将已有变量快照、常量或示教点转换成明确的基础类型。</summary>
    internal static class TaskRuntimeValues
    {
        /// <summary>读取一个值；尚无有效变量时返回原因，不猜测默认值。</summary>
        internal static bool TryRead(TaskValueBinding binding, TaskResourceCatalog catalog,
            IReadOnlyList<DeviceVariableSnapshot> variables, out object value, out string reason)
        {
            value = null;
            reason = null;
            if (binding.Source == TaskValueSource.Constant)
                value = Parse(binding.DataType.Value, binding.Constant);
            else if (binding.Source == TaskValueSource.TeachPoint)
                value = catalog.FindTeachPoint(binding.AxisName, binding.PointId).Position.Value;
            else
            {
                var variable = variables.FirstOrDefault(item => string.Equals(item.Name, binding.VariableName, StringComparison.OrdinalIgnoreCase));
                if (variable == null || !variable.HasValue)
                {
                    reason = "变量尚无有效值：" + binding.VariableName;
                    return false;
                }
                DeviceVariableType type = catalog.Variables[binding.VariableName];
                if (!string.Equals(variable.DataType, type.ToString(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("变量运行类型与目录不一致：" + binding.VariableName);
                value = Parse(type, variable.CurrentValue);
            }
            return true;
        }

        /// <summary>以与离线检查相同的规则解析八种基础值。</summary>
        internal static object Parse(DeviceVariableType type, string text)
        {
            double? number;
            if (!TaskLiteral.TryRead(type, text, out number)) throw new FormatException("变量或常量格式无效：" + type + "，" + text);
            switch (type)
            {
                case DeviceVariableType.Boolean: return bool.Parse(text);
                case DeviceVariableType.Integer: return int.Parse(text, CultureInfo.InvariantCulture);
                case DeviceVariableType.UInt32: return uint.Parse(text, CultureInfo.InvariantCulture);
                case DeviceVariableType.Byte: return byte.Parse(text, CultureInfo.InvariantCulture);
                case DeviceVariableType.Number: return number.Value;
                case DeviceVariableType.String: return text;
                case DeviceVariableType.Character: return text[0];
                case DeviceVariableType.DateTime: return DateTime.ParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                default: throw new NotSupportedException("不支持变量类型：" + type);
            }
        }

        /// <summary>只接受框架已有数值类型，避免把 true 或数字字符串转换成坐标。</summary>
        internal static double Number(object value)
        {
            if (!(value is double) && !(value is int) && !(value is uint) && !(value is byte))
                throw new InvalidOperationException("值不是数值类型。");
            double result = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (double.IsNaN(result) || double.IsInfinity(result)) throw new InvalidOperationException("数值必须有限。");
            return result;
        }

        /// <summary>通过已有变量接口写入；只允许整数到 double，不做浮点截断。</summary>
        internal static void Write(IDeviceVariableStore store, string name, DeviceVariableType type, object value)
        {
            switch (type)
            {
                case DeviceVariableType.Boolean: store.SetBoolean(name, (bool)value); break;
                case DeviceVariableType.Integer: store.SetInteger(name, (int)value); break;
                case DeviceVariableType.UInt32: store.SetUInt32(name, (uint)value); break;
                case DeviceVariableType.Byte: store.SetByte(name, (byte)value); break;
                case DeviceVariableType.Number: store.SetNumber(name, Number(value)); break;
                case DeviceVariableType.String: store.SetString(name, (string)value); break;
                case DeviceVariableType.Character: store.SetCharacter(name, (char)value); break;
                case DeviceVariableType.DateTime: store.SetDateTime(name, (DateTime)value); break;
                default: throw new NotSupportedException("不支持变量类型：" + type);
            }
        }

        /// <summary>显示等待中的具体数据来源。</summary>
        internal static string Describe(TaskValueBinding binding)
        {
            if (binding.Source == TaskValueSource.Variable) return binding.VariableName;
            if (binding.Source == TaskValueSource.TeachPoint) return binding.AxisName + "/" + binding.PointId;
            return binding.Constant;
        }
    }
}
