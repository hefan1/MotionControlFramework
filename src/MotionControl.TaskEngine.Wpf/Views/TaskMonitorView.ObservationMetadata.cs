using System;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Execution;
using MotionControl.TaskEngine.Wpf.Monitoring;

namespace MotionControl.TaskEngine.Wpf.Views
{
    /// <summary>把一次执行采样的值、质量和同会话轴单位放在一起显示，不重新读取实时值。</summary>
    public partial class TaskMonitorView
    {
        /// <summary>按明确对象引用补充数值单位，读取失败和未知不会被替换为有效零。</summary>
        private ObservationRow CreateObservationRow(TaskNode node, TaskObservedValue value)
        {
            bool literal = node is SetVariableNode || node is WaitConditionNode || node is ConditionBranchNode;
            string expected = value.Expected == null ? T("未提供") : literal ? value.Expected : TranslateValue(value.Expected);
            string actual = value.Actual == null ? T("未知") : literal ? value.Actual : TranslateValue(value.Actual);
            bool numericAxis = node is WaitAxisPositionNode || node is MoveAxisNode || node is MoveAxisGroupNode || node is CoordinatedMoveNode;
            if (numericAxis && value.ObjectKind == StationObjectKind.Axis && value.ObjectId != null)
            {
                TaskAxisInfo axis;
                if (_session.TaskDevice.Catalog.Axes.TryGetValue(value.ObjectId, out axis))
                {
                    string unit = string.IsNullOrWhiteSpace(axis.EngineeringUnit) ? T("单位未配置") : axis.EngineeringUnit;
                    if (value.Expected != null) expected += " " + unit;
                    if (value.Actual != null && (value.Quality == StationValueQuality.Good
                        || value.Quality == StationValueQuality.Unstable)) actual += " " + unit;
                }
            }
            if (value.Quality == StationValueQuality.NotProvided) actual = T("未提供");
            else if (value.Quality == StationValueQuality.ReadFailed || value.Quality == StationValueQuality.Unknown)
                actual = T("未知");
            bool uncertain = value.Quality == StationValueQuality.NotProvided
                || value.Quality == StationValueQuality.ReadFailed || value.Quality == StationValueQuality.Unknown
                || value.Quality == StationValueQuality.Unstable || !value.Satisfied.HasValue;
            string result = T(uncertain ? "未知" : value.Satisfied.Value ? "满足" : "未满足")
                + " · " + T(TaskOperatorText.QualityKey(value.Quality));
            return new ObservationRow(value.Name, expected, actual, result);
        }
    }
}
