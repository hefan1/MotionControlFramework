using System;
using System.Globalization;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.TaskEngine.Execution;
using MotionControl.TaskEngine.Machine;

namespace MotionControl.TaskEngine.Wpf.Monitoring
{
    /// <summary>只按结构化执行证据生成操作员文字；原始说明不参与分类和数值判断。</summary>
    internal static class TaskOperatorText
    {
        /// <summary>等待类别由执行器显式提供；缺失时保持未提供，不猜测中文前缀。</summary>
        internal static string WaitingStateKey(TaskNodeSnapshot snapshot)
        {
            if (snapshot == null || snapshot.State != TaskNodeState.Waiting) return "等待";
            return WaitKindKey(snapshot.Diagnostics == null ? StationWaitKind.NotProvided : snapshot.Diagnostics.WaitKind);
        }

        /// <summary>把固定类别转换为语言资源键；对象名称和说明文字不影响结果。</summary>
        internal static string WaitKindKey(StationWaitKind kind)
        {
            return StationOperatorPresentation.WaitKindKey(kind);
        }

        /// <summary>按当前语言返回等待状态文字。</summary>
        internal static string WaitingState(TaskNodeSnapshot snapshot, Func<string, string> text)
        {
            return text(WaitingStateKey(snapshot));
        }

        /// <summary>保留所有等待对象；是否超时及实际时限直接读取结构化字段。</summary>
        internal static string Detail(TaskNodeSnapshot snapshot, Func<string, string> text)
        {
            if (snapshot == null) return string.Empty;
            TaskStepDiagnostics diagnostics = snapshot.Diagnostics;
            if (diagnostics == null)
                return snapshot.State == TaskNodeState.Waiting
                    ? WithRaw(text("等待诊断未提供"), snapshot.Detail) : snapshot.Detail;
            string waits = DescribeWaitItems(diagnostics, text);
            if (snapshot.State == TaskNodeState.Failed)
            {
                if (diagnostics.IsTimeout)
                    return WithRaw(string.Format(text("结构化等待超时格式"), TimeoutText(diagnostics.TimeoutMilliseconds, text)), waits);
                return WithRaw(waits, diagnostics.FailureMessage ?? snapshot.Detail);
            }
            if (snapshot.State == TaskNodeState.Waiting)
            {
                if (!diagnostics.HasWaitInformation || diagnostics.WaitKind == StationWaitKind.NotProvided)
                    return WithRaw(text("等待诊断未提供"), snapshot.Detail);
                if (diagnostics.WaitKind == StationWaitKind.Delay && diagnostics.RemainingDelayMilliseconds.HasValue)
                    return string.Format(text("剩余延时格式"),
                        diagnostics.RemainingDelayMilliseconds.Value.ToString("0.###", CultureInfo.InvariantCulture));
                return WithRaw(text(WaitKindKey(diagnostics.WaitKind)), waits);
            }
            if (snapshot.State == TaskNodeState.Completed && diagnostics.WaitKind == StationWaitKind.Input
                && diagnostics.WaitItems.Count == 1 && diagnostics.WaitItems[0].Quality == StationValueQuality.Good)
            {
                StationWaitItem item = diagnostics.WaitItems[0];
                return string.Format(text("输入条件满足格式"), item.ObjectName ?? item.ObjectId, SignalValue(item.Actual, text));
            }
            return snapshot.Detail;
        }

        /// <summary>使用稳定交接原因代码显示阶段，两个信号的完整名称与采样值仍保留在明细中。</summary>
        internal static string StationDetail(StationTaskSnapshot station, Func<string, string> text)
        {
            TaskStepDiagnostics diagnostic = station.Diagnostics;
            if (diagnostic == null)
                return station.State == StationTaskState.WaitingRequest
                    ? WithRaw(text("等待诊断未提供"), station.Detail) : station.Detail;
            string stage;
            switch (diagnostic.ReasonCode)
            {
                case "handshake.unknown": stage = "交接信号状态未知"; break;
                case "handshake.wait_request_reset": stage = "等待请求方复位信号"; break;
                case "handshake.wait_completion_reset": stage = "等待完成信号复位"; break;
                case "handshake.request_ready": stage = "已收到请求，等待工位启动"; break;
                case "handshake.wait_request": stage = "等待请求信号"; break;
                case "handshake.wait_request_reset_after_completion": stage = "本工位已完成，等待请求方复位信号"; break;
                default: return WithRaw(DescribeWaitItems(diagnostic, text), diagnostic.FailureMessage ?? station.Detail);
            }
            return WithRaw(text(stage), DescribeWaitItems(diagnostic, text));
        }

        /// <summary>按原顺序显示全部等待对象，不从列表中随意选一个作为唯一原因。</summary>
        private static string DescribeWaitItems(TaskStepDiagnostics diagnostic, Func<string, string> text)
        {
            return StationOperatorPresentation.DescribeWaitItems(diagnostic.WaitItems, diagnostic.HasWaitInformation, text);
        }

        /// <summary>将固定的数据质量转换成语言资源键。</summary>
        internal static string QualityKey(StationValueQuality quality)
        {
            return StationOperatorPresentation.QualityKey(quality);
        }

        /// <summary>把已经明确为布尔信号的值翻译成操作员文字；此步骤不决定等待类别。</summary>
        private static string SignalValue(string value, Func<string, string> text)
        {
            if (value == null) return text("未知");
            bool parsed;
            return bool.TryParse(value, out parsed) ? text(parsed ? "有效" : "无效") : value;
        }

        /// <summary>格式化数值时限；没有数值时明确显示未提供。</summary>
        internal static string TimeoutText(int? milliseconds, Func<string, string> text)
        {
            if (!milliseconds.HasValue) return text("未提供");
            return milliseconds.Value >= 1000 && milliseconds.Value % 1000 == 0
                ? string.Format(text("秒格式"), milliseconds.Value / 1000)
                : string.Format(text("毫秒格式"), milliseconds.Value);
        }

        /// <summary>组合简短提示和原始说明，不分析说明内部的分号、等号或编号。</summary>
        private static string WithRaw(string summary, string raw)
        {
            if (string.IsNullOrEmpty(summary)) return raw;
            if (string.IsNullOrEmpty(raw)) return summary;
            return summary + "；" + raw;
        }
    }
}
