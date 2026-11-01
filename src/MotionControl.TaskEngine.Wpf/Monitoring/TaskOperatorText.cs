using System;
using System.Globalization;
using System.Linq;
using MotionControl.TaskEngine.Execution;

namespace MotionControl.TaskEngine.Wpf.Monitoring
{
    /// <summary>把执行快照整理成操作员能直接采取动作的状态文字。</summary>
    internal static class TaskOperatorText
    {
        /// <summary>把常见等待映射成工位表和步骤树可直接辨认的状态。</summary>
        internal static string WaitingStateKey(TaskNodeSnapshot snapshot)
        {
            if (snapshot == null || snapshot.State != TaskNodeState.Waiting) return "等待";
            string detail = snapshot.Detail ?? string.Empty;
            string summary = snapshot.Diagnostics?.Summary ?? string.Empty;
            if (summary.StartsWith("等待输入：", StringComparison.Ordinal)
                || detail.StartsWith("等待 DI ", StringComparison.Ordinal)) return "等待输入";
            if (detail.StartsWith("等待资源 [", StringComparison.Ordinal)) return "等待资源";
            if (detail.StartsWith("等待进入条件：", StringComparison.Ordinal)
                || detail.StartsWith("等待退出确认：", StringComparison.Ordinal)) return "等待互锁";
            if (detail.StartsWith("等待工艺延时 ", StringComparison.Ordinal)) return "工艺延时";
            if (detail.StartsWith("等待条件：", StringComparison.Ordinal)) return "等待条件";
            if (detail.StartsWith("等待 ", StringComparison.Ordinal)
                && detail.EndsWith(" 个并行分支全部完成。", StringComparison.Ordinal)) return "等待并行步骤";
            return "等待";
        }

        /// <summary>按当前语言返回等待状态文字。</summary>
        internal static string WaitingState(TaskNodeSnapshot snapshot, Func<string, string> text)
        {
            return text(WaitingStateKey(snapshot));
        }

        /// <summary>等待 DI 时显示期望和采样值；故障时隐藏内部调用编号。</summary>
        internal static string Detail(TaskNodeSnapshot snapshot, Func<string, string> text)
        {
            if (snapshot == null) return string.Empty;
            var diagnostics = snapshot.Diagnostics;
            if (snapshot.State == TaskNodeState.Waiting && snapshot.Detail != null
                && snapshot.Detail.StartsWith("等待条件：", StringComparison.Ordinal) && diagnostics != null)
                return text(diagnostics.Decision.HasValue ? "等待条件满足" : "条件状态未知");
            var observed = diagnostics?.Values.FirstOrDefault();
            if (observed != null && diagnostics.Summary != null
                && diagnostics.Summary.StartsWith("等待输入：", StringComparison.Ordinal))
            {
                string expected = SignalValue(observed.Expected, text);
                string actual = SignalValue(observed.Actual, text);
                if (snapshot.State == TaskNodeState.Failed)
                {
                    string timeout = TimeoutLimit(snapshot.Detail, text);
                    return string.Format(text("等待输入超时格式"), observed.Name, expected, actual, timeout);
                }
                if (snapshot.State == TaskNodeState.Completed)
                    return string.Format(text("输入条件满足格式"), observed.Name, actual);
                if (snapshot.State != TaskNodeState.Waiting) return snapshot.Detail;
                return string.Format(text("等待输入详情格式"), observed.Name, expected, actual);
            }
            return snapshot.State == TaskNodeState.Failed ? RemoveExecutionIdentity(snapshot.Detail) : snapshot.Detail;
        }

        /// <summary>将请求握手阶段整理为简短提示，无法识别的原始说明保持原样。</summary>
        internal static string StationDetail(string detail, Func<string, string> text)
        {
            if (string.IsNullOrEmpty(detail)) return detail;
            const string prefix = "等待请求：";
            if (detail.StartsWith(prefix, StringComparison.Ordinal))
            {
                string[] parts = detail.Substring(prefix.Length).Split('；');
                if (parts.Length != 2) return detail;
                bool? request, completed;
                if (!TrySignal(parts[0], out request) || !TrySignal(parts[1], out completed)) return detail;
                if (!request.HasValue || !completed.HasValue) return text("交接信号状态未知");
                if (completed.Value) return text(request.Value ? "等待请求方复位信号" : "等待完成信号复位");
                return text(request.Value ? "已收到请求，等待工位启动" : "等待请求信号");
            }
            if (detail.StartsWith("已完成，等待请求方清除 ", StringComparison.Ordinal)
                && detail.EndsWith(" 后复归完成信号。", StringComparison.Ordinal))
                return text("本工位已完成，等待请求方复位信号");
            return detail;
        }

        /// <summary>读取引擎请求明细末尾的布尔采样；未知不视为无效。</summary>
        private static bool TrySignal(string part, out bool? value)
        {
            value = null;
            int separator = part.LastIndexOf('=');
            if (separator < 1) return false;
            string sample = part.Substring(separator + 1);
            if (sample == "未知") return true;
            bool parsed;
            if (!bool.TryParse(sample, out parsed)) return false;
            value = parsed;
            return true;
        }

        /// <summary>把布尔输入采样转换成有效、无效或未知。</summary>
        private static string SignalValue(string value, Func<string, string> text)
        {
            bool parsed;
            if (value == null) return text("未知");
            if (bool.TryParse(value, out parsed)) return text(parsed ? "有效" : "无效");
            return value;
        }

        /// <summary>从时限错误中读取实际到期的调用期限，避免误报子节点自己的时限。</summary>
        private static string TimeoutLimit(string detail, Func<string, string> text)
        {
            const string marker = "活动时限 ";
            int start = detail == null ? -1 : detail.LastIndexOf(marker, StringComparison.Ordinal);
            if (start < 0) return text("未知");
            start += marker.Length;
            int end = detail.IndexOf(" ms", start, StringComparison.Ordinal);
            int milliseconds;
            if (end < 0 || !int.TryParse(detail.Substring(start, end - start), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out milliseconds)) return text("未知");
            return milliseconds >= 1000 && milliseconds % 1000 == 0
                ? string.Format(text("秒格式"), milliseconds / 1000)
                : string.Format(text("毫秒格式"), milliseconds);
        }

        /// <summary>移除供程序定位使用的任务和调用编号，保留现场原因。</summary>
        private static string RemoveExecutionIdentity(string detail)
        {
            if (string.IsNullOrEmpty(detail)) return detail;
            int metadata = detail.IndexOf("；任务 ", StringComparison.Ordinal);
            int metadataLength = 4;
            if (metadata < 0 && detail.StartsWith("任务 ", StringComparison.Ordinal))
            {
                metadata = 0;
                metadataLength = 0;
            }
            if (metadata < 0) return detail;
            int separator = detail.IndexOf('：', metadata + metadataLength);
            if (separator < 0) return detail;
            string prefix = detail.Substring(0, metadata).TrimEnd('；');
            string cause = detail.Substring(separator + 1).Trim();
            return string.IsNullOrEmpty(prefix) ? cause : prefix + "；" + cause;
        }
    }
}
