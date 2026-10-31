namespace MotionControl.Framework.Wpf.Configuration
{
    /// <summary>保存设备整机控制按钮和软件安全联锁所使用的逻辑输入。</summary>
    public sealed class SafetyInterlockConfigurationDocument
    {
        /// <summary>当前整机控制与安全联锁配置文件格式版本。</summary>
        public int SchemaVersion { get; set; }

        /// <summary>代表整机急停状态的逻辑 DI 名称；为空时不启用软件急停监控。</summary>
        public string EmergencyStopInputName { get; set; }

        /// <summary>代表现场启动按钮的逻辑 DI 名称；为空时只能从上位机界面启动。</summary>
        public string StartInputName { get; set; }

        /// <summary>代表现场停止按钮的逻辑 DI 名称；按用户确认的设备习惯将其作为整机暂停输入。</summary>
        public string PauseInputName { get; set; }

        /// <summary>代表现场复位按钮的逻辑 DI 名称；为空时只能从上位机界面复位。</summary>
        public string ResetInputName { get; set; }

        /// <summary>创建未指定现场按钮和软件急停 DI 的设备控制配置。</summary>
        public SafetyInterlockConfigurationDocument()
        {
            SchemaVersion = 1;
            EmergencyStopInputName = string.Empty;
            StartInputName = string.Empty;
            PauseInputName = string.Empty;
            ResetInputName = string.Empty;
        }
    }
}
