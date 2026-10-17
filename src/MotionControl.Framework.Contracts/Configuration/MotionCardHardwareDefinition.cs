namespace MotionControl.Framework.Contracts.Configuration
{
    /// <summary>设备配置中的一张运动卡信息。</summary>
    public sealed class MotionCardHardwareDefinition
    {
        /// <summary>本机配置中用于识别这张卡的稳定标识。</summary>
        public string ControllerId { get; set; }

        /// <summary>由 Host 选择的具体运动卡适配器名称。</summary>
        public string AdapterType { get; set; }

        /// <summary>相对于设备配置目录的厂商连接配置文件。</summary>
        public string ConnectionConfigurationFile { get; set; }
    }
}
