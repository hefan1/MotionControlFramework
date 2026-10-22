namespace MotionControl.Framework.Contracts.Variables
{
    /// <summary>设备变量表支持的基础数据类型。</summary>
    public enum DeviceVariableType
    {
        /// <summary>布尔值，只允许 true 或 false。</summary>
        Boolean,

        /// <summary>32 位整数。</summary>
        Integer,

        /// <summary>双精度小数，使用国际化无关的小数点格式保存。</summary>
        Number,

        /// <summary>字符串。</summary>
        String,

        /// <summary>无符号 8 位整数，取值范围为 0 到 255。</summary>
        Byte,

        /// <summary>单个字符。</summary>
        Character,

        /// <summary>日期和时间，使用 ISO 8601 文本格式保存。</summary>
        DateTime
    }
}
