namespace MotionControl.Framework.Contracts.Variables
{
    /// <summary>一条设备变量的名称、类型、初值和现场写入权限。</summary>
    public sealed class DeviceVariableDefinition
    {
        /// <summary>设备流程引用变量时使用的稳定名称，例如 Vision.OffsetX。</summary>
        public string Name { get; set; }

        /// <summary>数据类型名称，取值为 Boolean、Integer、Number、String、Byte、Character、DateTime 或 UInt32。</summary>
        public string DataType { get; set; }

        /// <summary>每次程序启动时装入当前值的初始值文本；留空表示启动时尚无有效值。</summary>
        public string InitialValue { get; set; }

        /// <summary>显示给现场人员的用途说明。</summary>
        public string Description { get; set; }

        /// <summary>是否允许现场人员在设备未运行时手动写入当前值。</summary>
        public bool AllowOperatorWrite { get; set; }

        /// <summary>复制定义，避免页面修改配置对象时影响运行时变量表。</summary>
        public DeviceVariableDefinition Copy()
        {
            return new DeviceVariableDefinition
            {
                Name = Name,
                DataType = DataType,
                InitialValue = InitialValue,
                Description = Description,
                AllowOperatorWrite = AllowOperatorWrite
            };
        }
    }
}
