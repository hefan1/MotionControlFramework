using System.Collections.Generic;

namespace MotionControl.Framework.Wpf.Configuration
{
    /// <summary>描述当前 Host 实际接入的一种控制卡选项及其可配置轴数量。</summary>
    public sealed class MotionCardSelectionOption
    {
        /// <summary>保存到设备配置中的厂商标识，不作为界面显示文字。</summary>
        public string VendorCode { get; set; }

        /// <summary>从当前语言资源中读取厂商显示名称的资源键。</summary>
        public string VendorDisplayResourceKey { get; set; }

        /// <summary>保存到设备配置中的总线型或脉冲型标识。</summary>
        public string CardTypeCode { get; set; }

        /// <summary>从当前语言资源中读取卡类型显示名称的资源键。</summary>
        public string CardTypeDisplayResourceKey { get; set; }

        /// <summary>Host 用于选择具体驱动适配器的稳定标识。</summary>
        public string AdapterType { get; set; }

        /// <summary>当前卡型可在页面配置的轴通道数量。</summary>
        public IReadOnlyList<int> SupportedAxisCounts { get; set; }

        /// <summary>首次显示该卡型时建议选择的轴通道数量。</summary>
        public int DefaultAxisCount { get; set; }
    }
}
