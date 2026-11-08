using System;
using MotionControl.Framework.Contracts.Variables;

namespace MotionControl.TaskEngine.Catalog
{
    /// <summary>目录中的变量定义副本；只读元数据不会随编辑窗口中的对象改动。</summary>
    public sealed class TaskVariableInfo
    {
        /// <summary>任务引用的稳定名称。</summary>
        public string Name { get; private set; }
        /// <summary>可选显示名称，不作为查找键。</summary>
        public string DisplayName { get; private set; }
        /// <summary>权威定义中的数据类型。</summary>
        public DeviceVariableType DataType { get; private set; }
        /// <summary>数值工程单位；为空表示未定义。</summary>
        public string EngineeringUnit { get; private set; }
        /// <summary>数值量纲；为空表示未定义。</summary>
        public string Dimension { get; private set; }
        /// <summary>可选数值下限，包含边界。</summary>
        public double? MinimumValue { get; private set; }
        /// <summary>可选数值上限，包含边界。</summary>
        public double? MaximumValue { get; private set; }
        /// <summary>可选显示精度，不参与任务数值运算。</summary>
        public int? DecimalPlaces { get; private set; }
        /// <summary>可选稳定来源标识。</summary>
        public string SourceId { get; private set; }
        /// <summary>给人员阅读的来源说明。</summary>
        public string SourceDescription { get; private set; }
        /// <summary>变量用途说明。</summary>
        public string Description { get; private set; }

        /// <summary>复制明确的数据字段，不保留可编辑定义对象。</summary>
        public TaskVariableInfo(DeviceVariableDefinition definition)
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            DeviceVariableType type;
            if (!Enum.TryParse(definition.DataType, true, out type) || !Enum.IsDefined(typeof(DeviceVariableType), type))
                throw new ArgumentException("变量类型不受支持：" + definition.Name);
            Name = definition.Name;
            DisplayName = definition.DisplayName;
            DataType = type;
            EngineeringUnit = definition.EngineeringUnit;
            Dimension = definition.Dimension;
            MinimumValue = definition.MinimumValue;
            MaximumValue = definition.MaximumValue;
            DecimalPlaces = definition.DecimalPlaces;
            SourceId = definition.SourceId;
            SourceDescription = definition.SourceDescription;
            Description = definition.Description;
        }

        /// <summary>为隔离执行器创建独立定义，初值仍由目录的初值集合提供。</summary>
        public DeviceVariableDefinition CreateDefinition(string initialValue)
        {
            return new DeviceVariableDefinition
            {
                Name = Name, DisplayName = DisplayName, DataType = DataType.ToString(), InitialValue = initialValue,
                EngineeringUnit = EngineeringUnit, Dimension = Dimension, MinimumValue = MinimumValue,
                MaximumValue = MaximumValue, DecimalPlaces = DecimalPlaces, SourceId = SourceId,
                SourceDescription = SourceDescription, Description = Description, AllowOperatorWrite = false
            };
        }
    }
}
