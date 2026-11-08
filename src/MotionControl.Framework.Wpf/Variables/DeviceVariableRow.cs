using System;
using System.ComponentModel;
using System.Globalization;
using MotionControl.Framework.Contracts.Variables;

namespace MotionControl.Framework.Wpf.Variables
{
    /// <summary>把设备变量快照转换为适合 WPF 表格绑定的行数据。</summary>
    public sealed class DeviceVariableRow : INotifyPropertyChanged
    {
        /// <summary>变量名称，在配置和设备流程中使用。</summary>
        public string Name { get; private set; }

        /// <summary>可选显示名称，不用于查找或写入。</summary>
        public string DisplayName { get; private set; }

        /// <summary>同时显示人读名称与稳定键，避免同名显示导致误认对象。</summary>
        public string NameDisplay { get; private set; }

        /// <summary>数据类型的内部英文名称。</summary>
        public string DataType { get; private set; }

        /// <summary>按当前界面语言显示的数据类型名称。</summary>
        public string DataTypeDisplay { get; private set; }

        /// <summary>变量的本机运行值文本。</summary>
        public string CurrentValue { get; private set; }

        /// <summary>当前运行值是否有效。</summary>
        public bool HasValue { get; private set; }

        /// <summary>用于表格显示的当前值；未收到数据时显示“无有效数据”。</summary>
        public string CurrentValueDisplay { get; private set; }

        /// <summary>当前值最后一次更新的本机时间文本。</summary>
        public string LastUpdatedDisplay { get; private set; }

        /// <summary>变量用途说明。</summary>
        public string Description { get; private set; }

        /// <summary>与当前值同批取得的初始值定义，空值仍为空。</summary>
        public string InitialValue { get; private set; }

        /// <summary>工程单位原值，未定义时为空。</summary>
        public string EngineeringUnit { get; private set; }

        /// <summary>按当前语言显示的单位或未定义说明。</summary>
        public string EngineeringUnitDisplay { get; private set; }

        /// <summary>量纲元数据，未定义时为空。</summary>
        public string Dimension { get; private set; }

        /// <summary>允许写入的下限，未配置时为空。</summary>
        public double? MinimumValue { get; private set; }

        /// <summary>允许写入的上限，未配置时为空。</summary>
        public double? MaximumValue { get; private set; }

        /// <summary>显示精度，不改变写入或存储的值文本。</summary>
        public int? DecimalPlaces { get; private set; }

        /// <summary>稳定来源标识，未定义时为空。</summary>
        public string SourceId { get; private set; }

        /// <summary>来源的说明文字。</summary>
        public string SourceDescription { get; private set; }

        /// <summary>旧值隔离或恢复来源说明，属于本次快照。</summary>
        public string ValueNotice { get; private set; }

        /// <summary>现场人员是否可以手动修改当前值。</summary>
        public bool AllowOperatorWrite { get; private set; }

        /// <summary>仅控制本页筛选显示，不影响变量身份、值或写入权限。</summary>
        public bool IsSearchMatch { get; private set; } = true;

        /// <summary>更新显示匹配状态，不改变设备变量快照。</summary>
        public void UpdateSearchMatch(string query)
        {
            IsSearchMatch = string.IsNullOrEmpty(query)
                || (Name ?? string.Empty).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                || (DisplayName ?? string.Empty).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                || (Description ?? string.Empty).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
            NotifyPropertyChanged(nameof(IsSearchMatch));
        }

        /// <summary>监控值更新时通知 WPF 刷新当前行。</summary>
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>创建绑定到一条变量快照的表格行。</summary>
        public DeviceVariableRow(DeviceVariableSnapshot snapshot)
        {
            Update(snapshot);
        }

        /// <summary>更新一行的变量元数据和运行值，并通知界面刷新变化列。</summary>
        public void Update(DeviceVariableSnapshot snapshot)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            Name = snapshot.Name;
            DisplayName = snapshot.DisplayName;
            NameDisplay = string.IsNullOrWhiteSpace(DisplayName) || string.Equals(Name, DisplayName, StringComparison.Ordinal)
                ? Name : DisplayName + " [" + Name + "]";
            DataType = snapshot.DataType;
            InitialValue = snapshot.InitialValue;
            EngineeringUnit = snapshot.EngineeringUnit;
            EngineeringUnitDisplay = ReadUnitDisplay("未定义", "不适用");
            Dimension = snapshot.Dimension;
            MinimumValue = snapshot.MinimumValue;
            MaximumValue = snapshot.MaximumValue;
            DecimalPlaces = snapshot.DecimalPlaces;
            SourceId = snapshot.SourceId;
            SourceDescription = snapshot.SourceDescription;
            ValueNotice = snapshot.ValueNotice;
            CurrentValue = snapshot.CurrentValue;
            HasValue = snapshot.HasValue;
            CurrentValueDisplay = snapshot.HasValue
                ? FormatCurrentValue(snapshot.DataType, snapshot.CurrentValue, snapshot.DecimalPlaces)
                : "无有效数据";
            LastUpdatedDisplay = snapshot.HasValue
                ? snapshot.LastUpdated.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                : string.Empty;
            Description = snapshot.Description;
            AllowOperatorWrite = snapshot.AllowOperatorWrite;
            NotifyAllPropertiesChanged();
        }

        /// <summary>按当前界面语言更新无效值提示和时间显示格式。</summary>
        public void UpdateValueDisplay(string noValueText, string undefinedText = "未定义", string notApplicableText = "不适用")
        {
            CurrentValueDisplay = HasValue ? FormatCurrentValue(DataType, CurrentValue, DecimalPlaces) : noValueText;
            EngineeringUnitDisplay = ReadUnitDisplay(undefinedText, notApplicableText);
            NotifyPropertyChanged(nameof(CurrentValueDisplay));
            NotifyPropertyChanged(nameof(EngineeringUnitDisplay));
        }

        /// <summary>非数值类型不需要单位，数值类型缺单位才显示未定义。</summary>
        private string ReadUnitDisplay(string undefinedText, string notApplicableText)
        {
            DeviceVariableType type;
            if (Enum.TryParse(DataType, true, out type) && Enum.IsDefined(typeof(DeviceVariableType), type)
                && !NumericValueMetadata.IsNumeric(type)) return notApplicableText;
            return string.IsNullOrWhiteSpace(EngineeringUnit) ? undefinedText : EngineeringUnit;
        }

        /// <summary>按 Host 当前语言刷新数据类型列的显示名称。</summary>
        public void UpdateTypeDisplay(string displayName)
        {
            DataTypeDisplay = displayName;
            NotifyPropertyChanged(nameof(DataTypeDisplay));
        }

        /// <summary>格式化已有效的值；精度只影响显示，格式失败仍保留原文，不显示伪造的零。</summary>
        private static string FormatCurrentValue(string dataType, string valueText, int? decimalPlaces)
        {
            if (decimalPlaces.HasValue && decimalPlaces.Value >= 0 && decimalPlaces.Value <= 15)
            {
                double value;
                if (double.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                    && !double.IsNaN(value) && !double.IsInfinity(value))
                    return value.ToString("F" + decimalPlaces.Value, CultureInfo.InvariantCulture);
            }
            if (string.Equals(dataType, "DateTime", StringComparison.OrdinalIgnoreCase))
            {
                DateTime dateTimeValue;
                if (DateTime.TryParse(
                    valueText,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out dateTimeValue))
                {
                    return dateTimeValue.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                }
            }

            return valueText;
        }

        /// <summary>通知表格所有绑定列重新读取当前行属性。</summary>
        private void NotifyAllPropertiesChanged()
        {
            NotifyPropertyChanged(string.Empty);
        }

        /// <summary>通知 WPF 指定绑定属性已经变化。</summary>
        private void NotifyPropertyChanged(string propertyName)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(propertyName));
            }
        }
    }
}
