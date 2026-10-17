namespace MotionControl.Framework.Contracts.Configuration
{
    /// <summary>设备配置中的一个逻辑数字 IO 到物理通道的映射。</summary>
    public sealed class DigitalIoHardwareDefinition
    {
        /// <summary>设备流程使用的逻辑输入或输出名称。</summary>
        public string Name { get; set; }

        /// <summary>该物理 IO 所属的控制卡标识。</summary>
        public string ControllerId { get; set; }

        /// <summary>厂商控制卡上的数字 IO 通道号。</summary>
        public int Channel { get; set; }

        /// <summary>物理高电平是否表示逻辑有效。</summary>
        public bool ActiveHigh { get; set; }
    }
}
