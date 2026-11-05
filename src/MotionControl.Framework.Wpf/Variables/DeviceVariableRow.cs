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

        /// <summary>现场人员是否可以手动修改当前值。</summary>
        public bool AllowOperatorWrite { get; private set; }

        /// <summary>仅控制本页筛选显示，不影响变量身份、值或写入权限。</summary>
        public bool IsSearchMatch { get; private set; } = true;

        /// <summary>更新显示匹配状态，不改变设备变量快照。</summary>
        public void UpdateSearchMatch(string query)
        {
            IsSearchMatch = string.IsNullOrEmpty(query)
                || (Name ?? string.Empty).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
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
            DataType = snapshot.DataType;
            CurrentValue = snapshot.CurrentValue;
            HasValue = snapshot.HasValue;
            CurrentValueDisplay = snapshot.HasValue
                ? FormatCurrentValue(snapshot.DataType, snapshot.CurrentValue)
                : "无有效数据";
            LastUpdatedDisplay = snapshot.HasValue
                ? snapshot.LastUpdated.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                : string.Empty;
            Description = snapshot.Description;
            AllowOperatorWrite = snapshot.AllowOperatorWrite;
            NotifyAllPropertiesChanged();
        }

        /// <summary>按当前界面语言更新无效值提示和时间显示格式。</summary>
        public void UpdateValueDisplay(string noValueText)
        {
            CurrentValueDisplay = HasValue ? CurrentValue : noValueText;
            NotifyPropertyChanged(nameof(CurrentValueDisplay));
        }

        /// <summary>按 Host 当前语言刷新数据类型列的显示名称。</summary>
        public void UpdateTypeDisplay(string displayName)
        {
            DataTypeDisplay = displayName;
            NotifyPropertyChanged(nameof(DataTypeDisplay));
        }

        /// <summary>把日期时间变量显示为现场人员熟悉的年月日和时分秒。</summary>
        private static string FormatCurrentValue(string dataType, string valueText)
        {
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
