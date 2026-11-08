using System;
using System.Collections.Generic;
using System.Linq;
using MotionControl.Framework.Contracts.Flow;

namespace MotionControl.TaskEngine.Wpf.Monitoring
{
    /// <summary>两种工位执行入口共用的只读显示规则；只格式化明确字段，不分析流程代码或原始说明。</summary>
    public static class StationOperatorPresentation
    {
        /// <summary>将独立工位阶段转换为语言资源键；缺失状态不会被整机状态替代。</summary>
        public static string StateKey(StationOperationState state)
        {
            switch (state)
            {
                case StationOperationState.Idle: return "工位空闲";
                case StationOperationState.Running: return "工位执行中";
                case StationOperationState.Waiting: return "工位等待中";
                case StationOperationState.Paused: return "工位已暂停";
                case StationOperationState.WaitingReset: return "工位等待复位";
                case StationOperationState.Completed: return "工位已完成";
                case StationOperationState.Stopped: return "工位已停止";
                case StationOperationState.Disabled: return "工位未参与运行";
                default: return "工位状态未提供";
            }
        }

        /// <summary>只按明确的等待类别返回语言资源键。</summary>
        public static string WaitKindKey(StationWaitKind kind)
        {
            switch (kind)
            {
                case StationWaitKind.Input: return "等待输入";
                case StationWaitKind.Output: return "等待读回输出";
                case StationWaitKind.Axis: return "等待轴到位";
                case StationWaitKind.Condition: return "等待条件";
                case StationWaitKind.Resource: return "等待资源";
                case StationWaitKind.Interlock: return "等待互锁";
                case StationWaitKind.Delay: return "工艺延时";
                case StationWaitKind.Parallel: return "等待并行步骤";
                case StationWaitKind.Handshake: return "等待交接";
                case StationWaitKind.Sampling: return "等待读取采样";
                default: return "等待类别未提供";
            }
        }

        /// <summary>区分没有提供、已采样但未知、值不稳定及读取失败。</summary>
        public static string QualityKey(StationValueQuality quality)
        {
            switch (quality)
            {
                case StationValueQuality.Good: return "采样有效";
                case StationValueQuality.Unknown: return "采样未知";
                case StationValueQuality.Unstable: return "采样不稳定";
                case StationValueQuality.ReadFailed: return "采样读取失败";
                default: return "未提供";
            }
        }

        /// <summary>按来源顺序显示完整等待集合，空集合与未提供分别表达。</summary>
        public static string DescribeWaitItems(IEnumerable<StationWaitItem> items, bool provided, Func<string, string> text)
        {
            var values = new List<StationWaitItem>(items ?? new StationWaitItem[0]);
            if (values.Count == 0) return text(provided ? "当前无等待项" : "等待诊断未提供");
            string known = string.Join("；", values.Select(item => DescribeWaitItem(item, text)));
            return provided ? known : known + "；" + text("部分等待诊断未提供");
        }

        /// <summary>显示一个等待对象的名称、期望、实际和质量，名称中的标点完整保留。</summary>
        public static string DescribeWaitItem(StationWaitItem item, Func<string, string> text)
        {
            if (item == null) return text("等待诊断未提供");
            string result = text(WaitKindKey(item.Kind)) + " · "
                + string.Format(text("等待对象采样格式"), item.ObjectName ?? item.ObjectId ?? text("未提供"),
                    DescribeValue(item, item.Expected, false, text), DescribeValue(item, item.Actual, true, text),
                    text(QualityKey(item.Quality)));
            if (item.TimeoutMilliseconds.HasValue)
                result += " · " + string.Format(text("等待项时限格式"), item.TimeoutMilliseconds.Value);
            return result;
        }

        /// <summary>仅已声明的信号转换布尔显示；有效数值追加来源给出的单位，未知值不追加单位。</summary>
        public static string DescribeValue(StationWaitItem item, string value, bool actual, Func<string, string> text)
        {
            if (actual && item.Quality == StationValueQuality.NotProvided) return text("未提供");
            if (actual && (item.Quality == StationValueQuality.Unknown || item.Quality == StationValueQuality.ReadFailed))
                return text("未知");
            if (value == null)
                return text(actual && item.Quality != StationValueQuality.NotProvided ? "未知" : "未提供");
            bool signal = item.ObjectKind == StationObjectKind.Input || item.ObjectKind == StationObjectKind.Output
                || item.Kind == StationWaitKind.Handshake;
            bool boolean;
            if (signal && bool.TryParse(value, out boolean)) return text(boolean ? "有效" : "无效");
            return string.IsNullOrWhiteSpace(item.Unit) ? value : value + " " + item.Unit;
        }
    }
}
