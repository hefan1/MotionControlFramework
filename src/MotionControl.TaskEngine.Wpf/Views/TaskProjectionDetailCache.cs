using System;
using System.Collections.Generic;
using System.Linq;
using MotionControl.TaskEngine.Projection;
using MotionControl.TaskEngine.Wpf.Monitoring;
using MotionControl.TaskEngine.Wpf.Simulation;
namespace MotionControl.TaskEngine.Wpf.Views
{
    /// <summary>只保留当前选择的定义引用显示，避免轮询重复扫描或缓存无限增长。</summary>
    internal sealed class TaskProjectionDetailCache
    {
        /// <summary>上次使用的不可变投影对象。</summary>
        private TaskProjectProjection _projection;
        /// <summary>上次投影失败证据。</summary>
        private Exception _error;
        /// <summary>选择身份使用复合键，不混合不同工位或任务。</summary>
        private string _station, _task, _node;
        /// <summary>缓存字段的语言。</summary>
        private bool _english;
        /// <summary>当前选择的有界显示字段。</summary>
        private IReadOnlyList<TaskDetailField> _fields;
        /// <summary>本监控页已发布会话的真实身份，未提供时保持空值。</summary>
        private TaskSessionMetadata _sessionMetadata;
        /// <summary>当前引用是否仍属于已确认的任务会话。</summary>
        private bool _sessionCurrent = true;

        /// <summary>会话身份变化时作废显示缓存，不改变任务或设备状态。</summary>
        internal void SetSessionMetadata(TaskSessionMetadata metadata)
        {
            if (ReferenceEquals(_sessionMetadata, metadata)) return;
            _sessionMetadata = metadata;
            _fields = null;
        }
        /// <summary>会话失效时保留引用供核查，但不再称为当前运行来源。</summary>
        internal void SetSessionValidity(bool current)
        {
            if (_sessionCurrent == current) return;
            _sessionCurrent = current;
            _fields = null;
        }

        /// <summary>仅选择或来源变化时扫描一次；引用总量保留，界面最多100条，诊断最多20条。</summary>
        internal IReadOnlyList<TaskDetailField> Read(TaskProjectProjection projection, Exception error,
            string station, string task, string node, bool english)
        {
            if (_fields != null && ReferenceEquals(_projection, projection) && ReferenceEquals(_error, error)
                && _station == station && _task == task && _node == node && _english == english) return _fields;
            _projection = projection; _error = error; _station = station; _task = task; _node = node; _english = english;
            var fields = new List<TaskDetailField>();
            string origin = !_sessionCurrent
                ? (english ? "Task session is not confirmed; retained definition references are not current active instances."
                    : "任务会话未确认；保留的定义引用不代表当前活动实例。")
                : _sessionMetadata == null
                    ? (english ? "Loaded session; project version and session ID not provided. Definition paths are not active instances."
                        : "已加载会话；工程版本与会话标识未提供；定义路径不表示当前活动实例。")
                    : _sessionMetadata.FormatSummary(english) + (english
                        ? "; definition paths are not active instances." : "；定义路径不表示当前活动实例。");
            fields.Add(new TaskDetailField(english ? "Definition source" : "定义来源", origin));
            if (error != null) fields.Add(new TaskDetailField(english ? "Projection failed" : "引用投影失败", error.GetBaseException().Message));
            int total = 0;
            if (projection != null && station != null)
                foreach (var reference in projection.References)
                    if (string.Equals(reference.StationId, station, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(reference.TaskId, task, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(reference.NodeId, node, StringComparison.OrdinalIgnoreCase))
                    {
                        if (total < 100)
                        {
                            string detail = reference.Path + " · " + reference.Status;
                            if (reference.Kind == "Axis" || reference.Kind == "TeachPoint" || reference.Kind == "Variable")
                            {
                                string undefined = english ? "undefined" : "未定义";
                                if (reference.IsNumericValue)
                                    detail += (english ? " · unit: " : " · 单位：")
                                        + (string.IsNullOrWhiteSpace(reference.EngineeringUnit) ? undefined : reference.EngineeringUnit);
                                if (!string.IsNullOrWhiteSpace(reference.DisplayName))
                                    detail += (english ? " · display name: " : " · 显示名：") + reference.DisplayName;
                            }
                            fields.Add(new TaskDetailField(reference.Kind + " · " + reference.Target, detail));
                        }
                        total++;
                    }
            bool available = projection != null && error == null;
            fields.Add(new TaskDetailField(english ? "Definition references" : "定义引用数量", !available
                ? (english ? "Count unknown: reference projection unavailable or failed." : "数量未知：引用投影未提供或失败。")
                : english ? $"Showing {Math.Min(total, 100)}; {total} matches within projected items. Completeness is not provided; see diagnostics."
                    + (total > 100 ? " Incomplete display; full projected references remain in the session projection." : "")
                : $"显示 {Math.Min(total, 100)} 条；已投影项中匹配 {total} 条。完整性未提供，请查看诊断。"
                    + (total > 100 ? "显示不全；全部已投影引用保留在会话投影中。" : "")));
            if (total == 0 && available) fields.Add(new TaskDetailField(english ? "References" : "引用", english ? "No matches within projected items; completeness unknown." : "已投影项中无匹配；完整性未知。"));
            if (projection != null && projection.Diagnostics.Count > 0)
            {
                fields.Add(new TaskDetailField(english ? "Project-wide projection diagnostics" : "工程范围投影诊断", english
                    ? $"Showing {Math.Min(20, projection.Diagnostics.Count)} of {projection.Diagnostics.Count}."
                    : $"显示 {Math.Min(20, projection.Diagnostics.Count)} / 共 {projection.Diagnostics.Count} 条诊断。"));
                foreach (var diagnostic in projection.Diagnostics.Take(20)) fields.Add(new TaskDetailField(english ? "Diagnostic" : "诊断", diagnostic));
            }
            _fields = fields.AsReadOnly();
            return _fields;
        }
    }
}
