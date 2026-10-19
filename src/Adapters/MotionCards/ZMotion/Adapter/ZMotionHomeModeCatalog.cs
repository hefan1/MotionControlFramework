using System;
using System.Collections.Generic;

namespace MotionControl.Adapter.MotionCard.ZMotion
{
    /// <summary>集中保存当前 ZMotion 适配器允许传给 DATUM 的常规回零模式编号。</summary>
    public static class ZMotionHomeModeCatalog
    {
        /// <summary>保存现有轴映射页列出的常规 DATUM 模式，不包含清错模式和特殊组合。</summary>
        private static readonly int[] HomeModeNumbers =
        {
            1, 2, 3, 4, 5, 6, 8, 9, 11, 12, 13, 14, 15, 16, 18, 19
        };

        /// <summary>返回 ZMotion 适配器支持的回零模式编号只读清单。</summary>
        public static IReadOnlyList<int> SupportedModeNumbers
        {
            get { return Array.AsReadOnly((int[])HomeModeNumbers.Clone()); }
        }

        /// <summary>判断给定编号是否属于适配器允许执行的常规 DATUM 模式。</summary>
        public static bool IsSupported(int modeNumber)
        {
            return Array.IndexOf(HomeModeNumbers, modeNumber) >= 0;
        }
    }
}
