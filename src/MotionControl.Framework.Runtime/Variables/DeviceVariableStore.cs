using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MotionControl.Framework.Contracts.Variables;

namespace MotionControl.Framework.Runtime.Variables
{
    /// <summary>在本机内存中保存一台设备的变量定义、当前值和更新时间。</summary>
    public sealed partial class DeviceVariableStore : IDeviceVariableStore
    {
        /// <summary>保护变量定义和运行值，供流程线程与 WPF 监控线程共同访问。</summary>
        private readonly object _sync = new object();

        /// <summary>按变量名称保存本机当前使用的变量。</summary>
        private readonly Dictionary<string, RuntimeVariable> _variables =
            new Dictionary<string, RuntimeVariable>(StringComparer.OrdinalIgnoreCase);

        /// <summary>使用已校验的变量定义创建运行时变量表，并把初值装入内存。</summary>
        public DeviceVariableStore(IEnumerable<DeviceVariableDefinition> definitions)
        {
            ReplaceDefinitions(definitions);
        }

        /// <summary>变量值或定义变化后通知订阅者。</summary>
        public event EventHandler VariablesChanged;

        /// <summary>返回变量定义副本，避免调用方直接改动运行时定义。</summary>
        public IReadOnlyList<DeviceVariableDefinition> GetDefinitions()
        {
            lock (_sync)
            {
                return _variables.Values
                    .Select(variable => variable.Definition.Copy())
                    .OrderBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList()
                    .AsReadOnly();
            }
        }

        /// <summary>返回变量当前值快照，不向调用方暴露内部字典。</summary>
        public IReadOnlyList<DeviceVariableSnapshot> GetSnapshot()
        {
            lock (_sync)
            {
                return _variables.Values
                    .Select(variable => new DeviceVariableSnapshot(
                        variable.Definition.Name,
                        variable.Definition.DataType,
                        variable.CurrentValue,
                        variable.HasValue,
                        variable.LastUpdated,
                        variable.Definition.Description,
                        variable.Definition.AllowOperatorWrite,
                        variable.Definition.DisplayName,
                        variable.Definition.EngineeringUnit,
                        variable.Definition.Dimension,
                        variable.Definition.MinimumValue,
                        variable.Definition.MaximumValue,
                        variable.Definition.DecimalPlaces,
                        variable.Definition.SourceId,
                        variable.Definition.SourceDescription,
                        variable.ValueNotice,
                        variable.Definition.InitialValue))
                    .OrderBy(snapshot => snapshot.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList()
                    .AsReadOnly();
            }
        }

        /// <summary>按稳定名称、类型及数值来源语义恢复旧值；不兼容记录保留新定义初值并返回原因。</summary>
        public IReadOnlyList<string> RestoreCurrentValues(IEnumerable<DeviceVariableSnapshot> snapshots)
        {
            if (snapshots == null)
            {
                throw new ArgumentNullException(nameof(snapshots), "设备变量恢复快照不能为空。");
            }

            var warnings = new List<string>();
            var restoredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            lock (_sync)
            {
                foreach (DeviceVariableSnapshot snapshot in snapshots)
                {
                    if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.Name))
                    {
                        warnings.Add("跳过名称为空的设备变量恢复记录。");
                        continue;
                    }

                    if (!restoredNames.Add(snapshot.Name))
                    {
                        warnings.Add("设备变量恢复记录重复，已跳过后续记录：" + snapshot.Name + "。");
                        continue;
                    }

                    RuntimeVariable variable;
                    if (!_variables.TryGetValue(snapshot.Name, out variable))
                    {
                        warnings.Add("当前设备配置已删除变量，未恢复：" + snapshot.Name + "。");
                        continue;
                    }

                    if (!string.Equals(
                        variable.Definition.DataType,
                        snapshot.DataType,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        warnings.Add("变量数据类型已变化，未恢复旧值：" + snapshot.Name + "。");
                        continue;
                    }

                    if (!SameValueMeaning(variable.Definition, snapshot.EngineeringUnit, snapshot.Dimension, snapshot.SourceId))
                    {
                        warnings.Add("变量单位、量纲或采集来源已变化，未恢复旧值：" + snapshot.Name + "。");
                        continue;
                    }

                    if (!snapshot.HasValue)
                    {
                        variable.CurrentValue = string.Empty;
                        variable.HasValue = false;
                        variable.LastUpdated = snapshot.LastUpdated;
                        continue;
                    }

                    string normalizedValue;
                    string errorMessage;
                    if (!TryNormalizeValue(
                        variable.Definition.DataType,
                        snapshot.CurrentValue,
                        out normalizedValue,
                        out errorMessage))
                    {
                        warnings.Add("变量恢复值格式无效，未恢复 " + snapshot.Name + "：" + errorMessage);
                        continue;
                    }

                    if (!TryCheckRange(variable.Definition, normalizedValue, out errorMessage))
                    {
                        warnings.Add("变量恢复值不符合当前范围，未恢复 " + snapshot.Name + "：" + errorMessage);
                        continue;
                    }

                    variable.CurrentValue = normalizedValue;
                    variable.HasValue = true;
                    variable.LastUpdated = snapshot.LastUpdated;
                    variable.ValueNotice = "已按当前变量定义核对并恢复历史值；更新时间属于原记录。";
                }
            }

            RaiseVariablesChanged();
            return warnings.AsReadOnly();
        }

        /// <summary>读取布尔变量，并检查定义类型是否为 Boolean。</summary>
        public bool GetBoolean(string name)
        {
            return ReadValue<bool>(name, DeviceVariableType.Boolean);
        }

        /// <summary>读取整数变量，并检查定义类型是否为 Integer。</summary>
        public int GetInteger(string name)
        {
            return ReadValue<int>(name, DeviceVariableType.Integer);
        }

        /// <summary>读取小数变量，并检查定义类型是否为 Number。</summary>
        public double GetNumber(string name)
        {
            return ReadValue<double>(name, DeviceVariableType.Number);
        }

        /// <summary>读取字符串变量，并检查定义类型是否为 String。</summary>
        public string GetString(string name)
        {
            return ReadValue<string>(name, DeviceVariableType.String);
        }

        /// <summary>读取字节变量，并检查定义类型是否为 Byte。</summary>
        public byte GetByte(string name)
        {
            return ReadValue<byte>(name, DeviceVariableType.Byte);
        }

        /// <summary>读取单字符变量，并检查定义类型是否为 Character。</summary>
        public char GetCharacter(string name)
        {
            return ReadValue<char>(name, DeviceVariableType.Character);
        }

        /// <summary>读取日期时间变量，并检查定义类型是否为 DateTime。</summary>
        public DateTime GetDateTime(string name)
        {
            return ReadValue<DateTime>(name, DeviceVariableType.DateTime);
        }

        /// <summary>读取 32 位无符号整数变量，并检查定义类型是否为 UInt32。</summary>
        public uint GetUInt32(string name)
        {
            return ReadValue<uint>(name, DeviceVariableType.UInt32);
        }

        /// <summary>由设备流程更新布尔变量并记录更新时间。</summary>
        public void SetBoolean(string name, bool value)
        {
            SetRuntimeValue(name, DeviceVariableType.Boolean, value.ToString());
        }

        /// <summary>先验证两个变量，再在同一锁内发布完整布尔结果。</summary>
        public void SetBooleanPair(string firstName, bool firstValue, string secondName, bool secondValue)
        {
            if (string.Equals(firstName, secondName, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("两个目标变量不能相同。");
            lock (_sync)
            {
                RuntimeVariable first = GetVariable(firstName);
                RuntimeVariable second = GetVariable(secondName);
                EnsureTypeMatches(first.Definition, DeviceVariableType.Boolean);
                EnsureTypeMatches(second.Definition, DeviceVariableType.Boolean);
                DateTime timestamp = DateTime.Now;
                first.CurrentValue = firstValue.ToString(); first.HasValue = true; first.LastUpdated = timestamp;
                second.CurrentValue = secondValue.ToString(); second.HasValue = true; second.LastUpdated = timestamp;
                first.ValueNotice = null;
                second.ValueNotice = null;
            }
            RaiseVariablesChanged();
        }

        /// <summary>原子核对两个布尔变量并更新完成值，避免工位或现场线程在检查和写入之间改变握手状态。</summary>
        public bool TryTransitionBooleanPair(string requestName, bool expectedRequest, string completedName, bool expectedCompleted, bool newCompleted)
        {
            if (string.Equals(requestName, completedName, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("请求变量和完成变量不能相同。");

            lock (_sync)
            {
                RuntimeVariable request = GetVariable(requestName);
                RuntimeVariable completed = GetVariable(completedName);
                EnsureTypeMatches(request.Definition, DeviceVariableType.Boolean);
                EnsureTypeMatches(completed.Definition, DeviceVariableType.Boolean);
                if (!request.HasValue || !completed.HasValue) return false;

                bool requestValue, completedValue;
                if (!bool.TryParse(request.CurrentValue, out requestValue) || !bool.TryParse(completed.CurrentValue, out completedValue))
                    throw new InvalidOperationException("握手变量的布尔值无效：" + requestName + " / " + completedName + "。");
                if (requestValue != expectedRequest || completedValue != expectedCompleted) return false;

                completed.CurrentValue = newCompleted.ToString();
                completed.LastUpdated = DateTime.Now;
                completed.ValueNotice = null;
            }

            RaiseVariablesChanged();
            return true;
        }

        /// <summary>由设备流程更新整数变量并记录更新时间。</summary>
        public void SetInteger(string name, int value)
        {
            SetRuntimeValue(name, DeviceVariableType.Integer, value.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>由设备流程更新小数变量并记录更新时间。</summary>
        public void SetNumber(string name, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), "设备变量小数值必须是有限数字。");
            }

            SetRuntimeValue(name, DeviceVariableType.Number, value.ToString("R", CultureInfo.InvariantCulture));
        }

        /// <summary>由设备流程更新字符串变量并记录更新时间。</summary>
        public void SetString(string name, string value)
        {
            SetRuntimeValue(name, DeviceVariableType.String, value ?? string.Empty);
        }

        /// <summary>由设备流程更新字节变量并记录更新时间。</summary>
        public void SetByte(string name, byte value)
        {
            SetRuntimeValue(name, DeviceVariableType.Byte, value.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>由设备流程更新单字符变量并记录更新时间。</summary>
        public void SetCharacter(string name, char value)
        {
            SetRuntimeValue(name, DeviceVariableType.Character, value.ToString());
        }

        /// <summary>由设备流程更新日期时间变量并记录更新时间。</summary>
        public void SetDateTime(string name, DateTime value)
        {
            SetRuntimeValue(name, DeviceVariableType.DateTime, value.ToString("O", CultureInfo.InvariantCulture));
        }

        /// <summary>由设备流程更新 32 位无符号整数变量并记录更新时间。</summary>
        public void SetUInt32(string name, uint value)
        {
            SetRuntimeValue(name, DeviceVariableType.UInt32, value.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>替换定义；仅同名、同类型、同单位/量纲/来源且仍符合范围时保留现值，显示改名不改引用。</summary>
        public void ReplaceDefinitions(IEnumerable<DeviceVariableDefinition> definitions)
        {
            List<DeviceVariableDefinition> checkedDefinitions = ValidateDefinitions(definitions);
            lock (_sync)
            {
                Dictionary<string, RuntimeVariable> previousVariables =
                    new Dictionary<string, RuntimeVariable>(_variables, StringComparer.OrdinalIgnoreCase);

                _variables.Clear();
                foreach (DeviceVariableDefinition definition in checkedDefinitions)
                {
                    RuntimeVariable previousVariable;
                    if (previousVariables.TryGetValue(definition.Name, out previousVariable)
                        && string.Equals(
                            previousVariable.Definition.DataType,
                            definition.DataType,
                            StringComparison.OrdinalIgnoreCase)
                        && SameValueMeaning(definition, previousVariable.Definition.EngineeringUnit,
                            previousVariable.Definition.Dimension, previousVariable.Definition.SourceId)
                        && (!previousVariable.HasValue || TryCheckRange(definition, previousVariable.CurrentValue, out _)))
                    {
                        _variables.Add(definition.Name, new RuntimeVariable(
                            definition,
                            previousVariable.CurrentValue,
                            previousVariable.HasValue,
                            previousVariable.LastUpdated,
                            previousVariable.ValueNotice));
                    }
                    else
                    {
                        _variables.Add(definition.Name, new RuntimeVariable(
                            definition,
                            NormalizeInitialValue(definition),
                            definition.InitialValue != null,
                            definition.InitialValue == null ? DateTime.MinValue : DateTime.Now,
                            previousVariable == null ? null : "变量类型、单位、量纲、来源或允许范围已变化，未沿用旧值；"
                                + (definition.InitialValue == null ? "当前未初始化。" : "已装入新定义初值，尚不是新的设备采样。")));
                    }
                }
            }

            RaiseVariablesChanged();
        }

        /// <summary>保留原三参数入口，仍执行当前定义的范围和权限检查。</summary>
        public bool TrySetOperatorValue(string name, string valueText, out string errorMessage)
        {
            return TrySetOperatorValue(name, valueText, out errorMessage, null);
        }

        /// <summary>在同一锁内复核确认时定义、类型、范围和写入权限，防止旧确认框写入新含义变量。</summary>
        public bool TrySetOperatorValue(string name, string valueText, out string errorMessage,
            DeviceVariableDefinition expectedDefinition)
        {
            errorMessage = string.Empty;
            RuntimeVariable variable;
            string normalizedValue;
            lock (_sync)
            {
                if (!_variables.TryGetValue(name ?? string.Empty, out variable))
                {
                    errorMessage = "找不到设备变量：" + name + "。";
                    return false;
                }

                if (!variable.Definition.AllowOperatorWrite)
                {
                    errorMessage = "变量“" + name + "”由设备流程维护，不允许现场手动写入。";
                    return false;
                }

                if (expectedDefinition != null && !SameWriteDefinition(variable.Definition, expectedDefinition))
                {
                    errorMessage = "变量“" + name + "”的定义、单位、范围或来源已变化，请重新核对后写入。";
                    return false;
                }

                if (!TryNormalizeValue(variable.Definition.DataType, valueText, out normalizedValue, out errorMessage))
                {
                    errorMessage = "变量“" + name + "”的输入值无效：" + errorMessage;
                    return false;
                }

                if (!TryCheckRange(variable.Definition, normalizedValue, out errorMessage))
                {
                    errorMessage = "变量“" + name + "”的输入值无效：" + errorMessage;
                    return false;
                }

                variable.CurrentValue = normalizedValue;
                variable.HasValue = true;
                variable.LastUpdated = DateTime.Now;
                variable.ValueNotice = null;
            }

            RaiseVariablesChanged();
            return true;
        }

        /// <summary>检查全部定义并返回副本，供配置文件校验和运行时加载共用。</summary>
        public static List<DeviceVariableDefinition> ValidateDefinitions(
            IEnumerable<DeviceVariableDefinition> definitions)
        {
            if (definitions == null)
            {
                throw new ArgumentNullException(nameof(definitions), "设备变量定义列表不能为空。");
            }

            List<DeviceVariableDefinition> sourceDefinitions = definitions.ToList();
            List<DeviceVariableDefinition> checkedDefinitions = sourceDefinitions
                .Where(definition => definition != null)
                .Select(definition => definition.Copy())
                .ToList();

            if (checkedDefinitions.Count != sourceDefinitions.Count)
            {
                throw new InvalidOperationException("设备变量定义列表中不能包含空行。");
            }

            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DeviceVariableDefinition definition in checkedDefinitions)
            {
                ValidateName(definition.Name);
                DeviceVariableType definedType;
                if (!TryParseType(definition.DataType, out definedType))
                {
                    throw new InvalidOperationException(
                        "设备变量“" + definition.Name + "”的数据类型无效：" + definition.DataType + "。");
                }

                if (!names.Add(definition.Name))
                {
                    throw new InvalidOperationException("设备变量名称重复：" + definition.Name + "。");
                }

                ValidateMetadata(definition, definedType);

                string normalizedInitialValue;
                string valueError;
                if (definition.InitialValue != null)
                {
                    if (!TryNormalizeValue(
                        definition.DataType,
                        definition.InitialValue,
                        out normalizedInitialValue,
                        out valueError))
                    {
                        throw new InvalidOperationException(
                            "设备变量“" + definition.Name + "”的初始值无效：" + valueError);
                    }

                    definition.InitialValue = normalizedInitialValue;
                    if (!TryCheckRange(definition, normalizedInitialValue, out valueError))
                        throw new InvalidOperationException("设备变量“" + definition.Name + "”的初始值无效：" + valueError);
                }
                definition.Description = definition.Description ?? string.Empty;
            }

            return checkedDefinitions;
        }

        /// <summary>按变量键和预期类型读取当前值，缺失或类型不匹配时提供明确异常。</summary>
        private T ReadValue<T>(string name, DeviceVariableType expectedType)
        {
            string valueText;
            lock (_sync)
            {
                RuntimeVariable variable = GetVariable(name);
                EnsureTypeMatches(variable.Definition, expectedType);
                if (!variable.HasValue)
                {
                    throw new InvalidOperationException("设备变量“" + name + "”当前没有有效值，流程写入后才能读取。");
                }

                valueText = variable.CurrentValue;
            }

            T value;
            string errorMessage;
            if (!TryParseValue(valueText, expectedType, out value, out errorMessage))
            {
                throw new InvalidOperationException("设备变量“" + name + "”的当前值无法读取：" + errorMessage);
            }

            return value;
        }

        /// <summary>检查变量定义类型及显式范围后更新运行值；无效写入保留旧值。</summary>
        private void SetRuntimeValue(string name, DeviceVariableType expectedType, string valueText)
        {
            string normalizedValue;
            string errorMessage;
            if (!TryNormalizeValue(expectedType.ToString(), valueText, out normalizedValue, out errorMessage))
            {
                throw new ArgumentException("设备变量“" + name + "”的运行值无效：" + errorMessage, nameof(valueText));
            }

            lock (_sync)
            {
                RuntimeVariable variable = GetVariable(name);
                EnsureTypeMatches(variable.Definition, expectedType);
                if (!TryCheckRange(variable.Definition, normalizedValue, out errorMessage))
                    throw new ArgumentOutOfRangeException(nameof(valueText), "设备变量“" + name + "”的运行值无效：" + errorMessage);
                variable.CurrentValue = normalizedValue;
                variable.HasValue = true;
                variable.LastUpdated = DateTime.Now;
                variable.ValueNotice = null;
            }

            RaiseVariablesChanged();
        }

        /// <summary>根据名称查找变量；调用方必须持有变量表锁。</summary>
        private RuntimeVariable GetVariable(string name)
        {
            RuntimeVariable variable;
            if (!_variables.TryGetValue(name ?? string.Empty, out variable))
            {
                throw new KeyNotFoundException("设备变量不存在：" + name + "。");
            }

            return variable;
        }

        /// <summary>确认变量定义类型与流程调用的读写方法一致。</summary>
        private static void EnsureTypeMatches(
            DeviceVariableDefinition definition,
            DeviceVariableType expectedType)
        {
            DeviceVariableType actualType;
            if (!TryParseType(definition.DataType, out actualType) || actualType != expectedType)
            {
                throw new InvalidOperationException(
                    "设备变量“" + definition.Name + "”定义为 " + definition.DataType
                    + "，当前流程要求 " + expectedType + "。");
            }
        }

        /// <summary>检查变量键不能为空、不能带空白字符，并且仅包含易搜索的常用符号。</summary>
        private static void ValidateName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException("设备变量名称不能为空。");
            }

            foreach (char character in name)
            {
                if (!(char.IsLetterOrDigit(character) || character == '_' || character == '.' || character == '-'))
                {
                    throw new InvalidOperationException(
                        "设备变量名称只能包含字母、数字、下划线、点和连字符：" + name + "。");
                }
            }
        }

        /// <summary>把初始值校验后转成运行时使用的文本。</summary>
        private static string NormalizeInitialValue(DeviceVariableDefinition definition)
        {
            if (definition.InitialValue == null)
            {
                return string.Empty;
            }

            string normalizedValue;
            string errorMessage;
            if (!TryNormalizeValue(definition.DataType, definition.InitialValue, out normalizedValue, out errorMessage))
            {
                throw new InvalidOperationException(
                    "设备变量“" + definition.Name + "”的初始值无效：" + errorMessage);
            }

            return normalizedValue;
        }

        /// <summary>解析类型名称并统一规范值文本；数值使用不受系统区域影响的小数点格式。</summary>
        private static bool TryNormalizeValue(
            string typeName,
            string valueText,
            out string normalizedValue,
            out string errorMessage)
        {
            normalizedValue = string.Empty;
            errorMessage = string.Empty;
            DeviceVariableType variableType;
            if (!TryParseType(typeName, out variableType))
            {
                errorMessage = "数据类型只能是 Boolean、Integer、Number、String、Byte、Character、DateTime 或 UInt32。";
                return false;
            }

            switch (variableType)
            {
                case DeviceVariableType.Boolean:
                    bool booleanValue;
                    if (!bool.TryParse(valueText, out booleanValue))
                    {
                        errorMessage = "布尔值只能填写 true 或 false。";
                        return false;
                    }

                    normalizedValue = booleanValue.ToString();
                    return true;

                case DeviceVariableType.Integer:
                    int integerValue;
                    if (!int.TryParse(valueText, NumberStyles.Integer, CultureInfo.InvariantCulture, out integerValue))
                    {
                        errorMessage = "整数值格式无效。";
                        return false;
                    }

                    normalizedValue = integerValue.ToString(CultureInfo.InvariantCulture);
                    return true;

                case DeviceVariableType.Number:
                    double numberValue;
                    if (!double.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out numberValue)
                        || double.IsNaN(numberValue)
                        || double.IsInfinity(numberValue))
                    {
                        errorMessage = "小数值格式无效，且不能是无穷大或非数字。";
                        return false;
                    }

                    normalizedValue = valueText.Trim();
                    return true;

                case DeviceVariableType.String:
                    normalizedValue = valueText ?? string.Empty;
                    return true;

                case DeviceVariableType.Byte:
                    byte byteValue;
                    if (!byte.TryParse(valueText, NumberStyles.Integer, CultureInfo.InvariantCulture, out byteValue))
                    {
                        errorMessage = "字节值必须是 0 到 255 之间的整数。";
                        return false;
                    }

                    normalizedValue = byteValue.ToString(CultureInfo.InvariantCulture);
                    return true;

                case DeviceVariableType.UInt32:
                    uint unsignedIntegerValue;
                    if (!uint.TryParse(
                        valueText,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out unsignedIntegerValue))
                    {
                        errorMessage = "UInt32 值必须是 0 到 4,294,967,295 之间的整数。";
                        return false;
                    }

                    normalizedValue = unsignedIntegerValue.ToString(CultureInfo.InvariantCulture);
                    return true;

                case DeviceVariableType.Character:
                    char characterValue;
                    if (!char.TryParse(valueText, out characterValue))
                    {
                        errorMessage = "字符类型只能填写一个字符。";
                        return false;
                    }

                    normalizedValue = characterValue.ToString();
                    return true;

                case DeviceVariableType.DateTime:
                    DateTime dateTimeValue;
                    if (!DateTime.TryParse(
                        valueText,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind,
                        out dateTimeValue))
                    {
                        errorMessage = "日期时间格式无效，请使用 yyyy-MM-dd HH:mm:ss 或 ISO 8601 格式。";
                        return false;
                    }

                    normalizedValue = dateTimeValue.ToString("O", CultureInfo.InvariantCulture);
                    return true;

                default:
                    errorMessage = "数据类型不受支持。";
                    return false;
            }
        }

        /// <summary>把当前值文本解析为流程指定的基础数据类型。</summary>
        private static bool TryParseValue<T>(
            string valueText,
            DeviceVariableType variableType,
            out T value,
            out string errorMessage)
        {
            value = default(T);
            string normalizedValue;
            if (!TryNormalizeValue(variableType.ToString(), valueText, out normalizedValue, out errorMessage))
            {
                return false;
            }

            object parsedValue;
            switch (variableType)
            {
                case DeviceVariableType.Boolean:
                    parsedValue = bool.Parse(normalizedValue);
                    break;
                case DeviceVariableType.Integer:
                    parsedValue = int.Parse(normalizedValue, CultureInfo.InvariantCulture);
                    break;
                case DeviceVariableType.Number:
                    parsedValue = double.Parse(normalizedValue, CultureInfo.InvariantCulture);
                    break;
                case DeviceVariableType.String:
                    parsedValue = normalizedValue;
                    break;
                case DeviceVariableType.Byte:
                    parsedValue = byte.Parse(normalizedValue, CultureInfo.InvariantCulture);
                    break;
                case DeviceVariableType.UInt32:
                    parsedValue = uint.Parse(normalizedValue, CultureInfo.InvariantCulture);
                    break;
                case DeviceVariableType.Character:
                    parsedValue = char.Parse(normalizedValue);
                    break;
                case DeviceVariableType.DateTime:
                    parsedValue = DateTime.Parse(
                        normalizedValue,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind);
                    break;
                default:
                    errorMessage = "数据类型不受支持。";
                    return false;
            }

            if (!(parsedValue is T))
            {
                errorMessage = "流程读取类型与变量数据类型不一致。";
                return false;
            }

            value = (T)parsedValue;
            errorMessage = string.Empty;
            return true;
        }

        /// <summary>把可配置的类型名称转换为明确的数据类型枚举。</summary>
        private static bool TryParseType(string typeName, out DeviceVariableType variableType)
        {
            if (Enum.TryParse(typeName, true, out variableType))
            {
                return Enum.IsDefined(typeof(DeviceVariableType), variableType);
            }

            return false;
        }

        /// <summary>在锁外触发变量变化事件，避免页面回调期间占用变量表锁。</summary>
        private void RaiseVariablesChanged()
        {
            EventHandler handler = VariablesChanged;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        /// <summary>保存一条变量定义和它在当前进程中的值。</summary>
        private sealed class RuntimeVariable
        {
            /// <summary>当前变量的配置定义。</summary>
            public DeviceVariableDefinition Definition { get; private set; }

            /// <summary>当前运行值文本。</summary>
            public string CurrentValue { get; set; }

            /// <summary>当前文本值是否有效，未初始化时不伪造默认数据。</summary>
            public bool HasValue { get; set; }

            /// <summary>当前运行值最后更新的时间。</summary>
            public DateTime LastUpdated { get; set; }

            /// <summary>保留定义变更或历史恢复的来源说明，新的明确写入后清除。</summary>
            public string ValueNotice { get; set; }

            /// <summary>创建本机内存中的变量状态。</summary>
            public RuntimeVariable(
                DeviceVariableDefinition definition,
                string currentValue,
                bool hasValue,
                DateTime lastUpdated,
                string valueNotice = null)
            {
                Definition = definition.Copy();
                CurrentValue = currentValue;
                HasValue = hasValue;
                LastUpdated = lastUpdated;
                ValueNotice = valueNotice;
            }
        }
    }
}
