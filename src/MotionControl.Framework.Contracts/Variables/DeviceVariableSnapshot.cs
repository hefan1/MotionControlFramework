using System;

namespace MotionControl.Framework.Contracts.Variables
{
    /// <summary>供监控页面显示的一次设备变量当前值快照。</summary>
    public sealed class DeviceVariableSnapshot
    {
        /// <summary>使用稳定名称识别变量。</summary>
        public string Name { get; private set; }

        /// <summary>当前变量的数据类型名称。</summary>
        public string DataType { get; private set; }

        /// <summary>当前运行值的可读文本。</summary>
        public string CurrentValue { get; private set; }

        /// <summary>当前运行值是否有效；流程尚未写入时为 false。</summary>
        public bool HasValue { get; private set; }

        /// <summary>当前值最后一次被修改的本机时间。</summary>
        public DateTime LastUpdated { get; private set; }

        /// <summary>变量的用途说明。</summary>
        public string Description { get; private set; }

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
            bool allowOperatorWrite)
        {
            Name = name;
            DataType = dataType;
            CurrentValue = currentValue;
            HasValue = hasValue;
            LastUpdated = lastUpdated;
            Description = description;
            AllowOperatorWrite = allowOperatorWrite;
        }
    }
}
