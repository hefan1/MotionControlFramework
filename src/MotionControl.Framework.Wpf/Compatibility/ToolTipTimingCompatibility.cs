using System.Windows;
using System.Windows.Controls;

namespace MotionControl.Framework.Wpf.Compatibility
{
    /// <summary>避免部分 WPF 运行时的极长 Tooltip 默认计时阻塞已经到期的页面刷新。</summary>
    public static class ToolTipTimingCompatibility
    {
        /// <summary>保留一天的持续阅读时间，同时远离有符号毫秒计数的最大边界。</summary>
        private const int PersistentShowDurationMilliseconds = 24 * 60 * 60 * 1000;

        /// <summary>由启动线程在创建控件前调用；只修正极大默认值，顺序重复调用和显式样式时长保持原行为。</summary>
        public static void Initialize()
        {
            var property = ToolTipService.ShowDurationProperty;
            int currentDefault = (int)property.GetMetadata(typeof(DependencyObject)).DefaultValue;
            if (currentDefault != int.MaxValue) return;

            // 不修改控件本地值、样式或系统程序集；旧运行时的有限默认值也保持不变。
            property.OverrideMetadata(typeof(DependencyObject),
                new FrameworkPropertyMetadata(PersistentShowDurationMilliseconds));
        }
    }
}
