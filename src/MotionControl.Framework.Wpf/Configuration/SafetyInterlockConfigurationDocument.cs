namespace MotionControl.Framework.Wpf.Configuration
{
    /// <summary>保存设备软件安全联锁所使用的逻辑输入。</summary>
    public sealed class SafetyInterlockConfigurationDocument
    {
        /// <summary>当前安全联锁配置文件格式版本。</summary>
        public int SchemaVersion { get; set; }

        /// <summary>代表整机急停状态的逻辑 DI 名称；为空时运行时按急停触发处理。</summary>
        public string EmergencyStopInputName { get; set; }

        /// <summary>创建未指定软件急停 DI 的安全联锁配置。</summary>
        public SafetyInterlockConfigurationDocument()
        {
            SchemaVersion = 1;
            EmergencyStopInputName = string.Empty;
        }
    }
}
