namespace MotionControl.Framework.Contracts.Variables
{
    /// <summary>一条设备变量的稳定名称、可选显示元数据、初值和现场写入权限。</summary>
    public sealed class DeviceVariableDefinition
    {
        /// <summary>设备流程引用变量时使用的稳定名称，例如 Vision.OffsetX。</summary>
        public string Name { get; set; }

        /// <summary>可选显示名称；修改它不会修改 Name，也不会改变任务引用。</summary>
        public string DisplayName { get; set; }

        /// <summary>数据类型名称，取值为 Boolean、Integer、Number、String、Byte、Character、DateTime 或 UInt32。</summary>
        public string DataType { get; set; }

        /// <summary>每次程序启动时装入当前值的初始值文本；留空表示启动时尚无有效值。</summary>
        public string InitialValue { get; set; }

        /// <summary>显示给现场人员的用途说明。</summary>
        public string Description { get; set; }

        /// <summary>数值的工程单位，例如 mm、deg；未填写表示未定义，不按变量名称猜测。</summary>
        public string EngineeringUnit { get; set; }

        /// <summary>数值的量纲或设备约定用途，例如 Length、Angle、Force；未填写的旧配置仍可读取。</summary>
        public string Dimension { get; set; }

        /// <summary>允许写入的数值下限，包含边界；未填写表示没有配置这一侧限制。</summary>
        public double? MinimumValue { get; set; }

        /// <summary>允许写入的数值上限，包含边界；不替代运动轴自身的行程限制。</summary>
        public double? MaximumValue { get; set; }

        /// <summary>可选显示小数位数，范围为 0 到 15；只控制显示，不修改存储精度。</summary>
        public int? DecimalPlaces { get; set; }

        /// <summary>可选的稳定采集来源标识；来源变化后不能沿用旧来源的运行值。</summary>
        public string SourceId { get; set; }

        /// <summary>供人员阅读的来源说明；修改说明文字不改变变量或来源身份。</summary>
        public string SourceDescription { get; set; }

        /// <summary>是否允许现场人员在设备未运行时手动写入当前值。</summary>
        public bool AllowOperatorWrite { get; set; }

        /// <summary>复制定义，避免页面修改配置对象时影响运行时变量表。</summary>
        public DeviceVariableDefinition Copy()
        {
            return new DeviceVariableDefinition
            {
                Name = Name,
                DisplayName = DisplayName,
                DataType = DataType,
                InitialValue = InitialValue,
                Description = Description,
                EngineeringUnit = EngineeringUnit,
                Dimension = Dimension,
                MinimumValue = MinimumValue,
                MaximumValue = MaximumValue,
                DecimalPlaces = DecimalPlaces,
                SourceId = SourceId,
                SourceDescription = SourceDescription,
                AllowOperatorWrite = AllowOperatorWrite
            };
        }
    }
}
