namespace MotionControl.Host.Wpf
{
    /// <summary>
    /// 设备配置中的设备定义选择。Host 按这里的名称显式选择设备工程，
    /// 不使用反射扫描，避免启动时加载未知程序集。
    /// </summary>
    internal sealed class EquipmentSelectionSettings
    {
        /// <summary>创建默认选择示例设备的配置；首次运行的设备目录会写入该默认值。</summary>
        internal EquipmentSelectionSettings()
        {
            SchemaVersion = 1;
            Definition = "Demo";
        }

        /// <summary>配置格式版本，当前只接受 1。</summary>
        public int SchemaVersion { get; set; }

        /// <summary>设备定义名称，必须在 Host 已登记的定义中选择；例如 "Demo"。</summary>
        public string Definition { get; set; }

        /// <summary>
        /// 本设备的执行入口："StationFlows" 表示 C# 工位流程，"TaskEngine" 表示图形化任务定义，
        /// "None" 表示只能手动调试。留空时按是否配置工位流程自动判断。
        /// </summary>
        public string ExecutionOwner { get; set; }
    }
}
