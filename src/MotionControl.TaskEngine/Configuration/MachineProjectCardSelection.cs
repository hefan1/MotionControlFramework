namespace MotionControl.TaskEngine.Configuration
{
    /// <summary>工程要求的控制器后端及卡型；由 Host 用已安装连接能力核验。</summary>
    public sealed class MachineProjectCardSelection
    {
        /// <summary>仅由工程预检器创建选择快照。</summary>
        internal MachineProjectCardSelection() { }
        /// <summary>上位机控制器后端名称。</summary>
        public string Backend { get; internal set; }
        /// <summary>控制卡厂商配置名称。</summary>
        public string Vendor { get; internal set; }
        /// <summary>控制卡类型配置名称。</summary>
        public string CardType { get; internal set; }
        /// <summary>硬件映射所需的控制卡适配器名称。</summary>
        public string AdapterType { get; internal set; }
        /// <summary>配置的物理轴数量。</summary>
        public int AxisCount { get; internal set; }
        /// <summary>配置的控制卡数量。</summary>
        public int CardCount { get; internal set; }
    }
}
