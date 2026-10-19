namespace MotionControl.Framework.Wpf.Configuration
{
    /// <summary>由当前卡连接提供并显示在轴参数页中的回零模式编号。</summary>
    public sealed class AxisHomeModeOption
    {
        /// <summary>控制卡适配器定义的原始回零模式编号。</summary>
        public int ModeNumber { get; set; }
    }
}
