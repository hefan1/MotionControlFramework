using System;

namespace MotionControl.Framework.Contracts.Variables
{
    /// <summary>供监控页面显示的一次设备变量当前值快照。</summary>
    public sealed class DeviceVariableSnapshot
    {
        /// <summary>使用稳定名称识别变量。</summary>
        public string Name { get; private set; }

        /// <summary>可选显示名称；命令和任务引用始终使用 Name。</summary>
        public string DisplayName { get; private set; }

        /// <summary>当前变量的数据类型名称。</summary>
        public string DataType { get; private set; }

        /// <summary>当前运行值的可读文本。</summary>
        public string CurrentValue { get; private set; }

        /// <summary>当前运行值是否有效；流程尚未写入时为 false。</summary>
        public bool HasValue { get; private set; }

        /// <summary>当前值最后一次被修改的本机时间；不是硬件采样时间或有效期证明。</summary>
        public DateTime LastUpdated { get; private set; }

        /// <summary>变量的用途说明。</summary>
        public string Description { get; private set; }

        /// <summary>本次值对应定义的工程单位；为空表示未定义。</summary>
        public string EngineeringUnit { get; private set; }

        /// <summary>本次值对应定义的量纲或用途；为空表示未定义。</summary>
        public string Dimension { get; private set; }

        /// <summary>定义允许的数值下限；为空表示未配置。</summary>
        public double? MinimumValue { get; private set; }

        /// <summary>定义允许的数值上限；为空表示未配置。</summary>
        public double? MaximumValue { get; private set; }

        /// <summary>可选显示精度，不对 CurrentValue 作舍入。</summary>
        public int? DecimalPlaces { get; private set; }

        /// <summary>本次值所属的稳定采集来源标识；为空表示未定义。</summary>
        public string SourceId { get; private set; }

        /// <summary>来源的显示说明，不参与命令定位。</summary>
        public string SourceDescription { get; private set; }

        /// <summary>定义变更导致旧值不再适用时的说明；只用于显示，不作为控制协议。</summary>
        public string ValueNotice { get; private set; }

        /// <summary>同一批定义的初始值；不等于设备本次实际采样。</summary>
        public string InitialValue { get; private set; }

        /// <summary>现场人员是否可以手动写入当前值。</summary>
        public bool AllowOperatorWrite { get; private set; }

        /// <summary>创建包含变量定义和当前运行值的只读快照。</summary>
        public DeviceVariableSnapshot(
            string name,
            string dataType,
            string currentValue,
            bool hasValue,
            DateTime lastUpdated,
            string description,
            bool allowOperatorWrite,
            string displayName = null,
            string engineeringUnit = null,
            string dimension = null,
            double? minimumValue = null,
            double? maximumValue = null,
            int? decimalPlaces = null,
            string sourceId = null,
            string sourceDescription = null,
            string valueNotice = null,
            string initialValue = null)
        {
            Name = name;
            DisplayName = displayName;
            DataType = dataType;
            CurrentValue = currentValue;
            HasValue = hasValue;
            LastUpdated = lastUpdated;
            Description = description;
            EngineeringUnit = engineeringUnit;
            Dimension = dimension;
            MinimumValue = minimumValue;
            MaximumValue = maximumValue;
            DecimalPlaces = decimalPlaces;
            SourceId = sourceId;
            SourceDescription = sourceDescription;
            ValueNotice = valueNotice;
            InitialValue = initialValue;
            AllowOperatorWrite = allowOperatorWrite;
        }
    }
}
