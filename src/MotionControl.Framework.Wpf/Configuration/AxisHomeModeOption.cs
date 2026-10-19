namespace MotionControl.Framework.Wpf.Configuration
{
    /// <summary>由当前卡连接提供并显示在轴参数页中的回零模式选项。</summary>
    public sealed class AxisHomeModeOption
    {
        /// <summary>控制卡适配器定义的原始回零模式编号。</summary>
        public int ModeNumber { get; set; }

        /// <summary>当前适配器提供的可读名称资源键，供通用页面按当前语言显示。</summary>
        public string DisplayNameResourceKey { get; set; }

        /// <summary>当前适配器提供的大白话动作说明资源键；不会改变发送给控制卡的原始编号。</summary>
        public string DescriptionResourceKey { get; set; }
    }
}
