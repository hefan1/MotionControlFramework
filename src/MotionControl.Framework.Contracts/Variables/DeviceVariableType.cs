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
        String
    }
}
