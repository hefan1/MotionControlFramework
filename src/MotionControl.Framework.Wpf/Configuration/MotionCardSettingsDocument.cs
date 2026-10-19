using System.Collections.Generic;

namespace MotionControl.Framework.Wpf.Configuration
{
    /// <summary>保存控制卡厂商、卡类型和轴数量等页面设置。</summary>
    public sealed class MotionCardSettingsDocument
    {
        /// <summary>当前控制卡配置文件格式版本。</summary>
        public int SchemaVersion { get; set; }

        /// <summary>当前设备选择的控制卡厂商。</summary>
        public string MotionCardVendor { get; set; }

        /// <summary>当前设备选择的控制卡类型。</summary>
        public string MotionCardType { get; set; }

        /// <summary>当前控制卡可使用的轴通道数量；0 表示模板尚未选择卡型。</summary>
        public int AxisCount { get; set; }

        /// <summary>保留 Host 配置的控制卡标识、适配器和连接文件。</summary>
        public List<MotionCardConnectionDefinition> MotionCards { get; set; }

        /// <summary>创建空白控制卡设置；具体卡型默认值由 Host 已接入的适配器提供。</summary>
        public MotionCardSettingsDocument()
        {
            SchemaVersion = 1;
            MotionCardVendor = string.Empty;
            MotionCardType = string.Empty;
            AxisCount = 0;
            MotionCards = new List<MotionCardConnectionDefinition>();
        }
    }

    /// <summary>保留控制卡运行时连接所需的现有配置字段。</summary>
    public sealed class MotionCardConnectionDefinition
    {
        /// <summary>设备内用于关联轴和 IO 的控制卡标识。</summary>
        public string ControllerId { get; set; }

        /// <summary>Host 组合根选择的运动卡适配器名称。</summary>
        public string AdapterType { get; set; }

        /// <summary>运动卡厂商连接配置文件的相对路径。</summary>
        public string ConnectionConfigurationFile { get; set; }
    }
}
