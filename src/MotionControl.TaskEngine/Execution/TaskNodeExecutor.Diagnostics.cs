using System;
using System.Collections.Generic;
using System.Globalization;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.TaskEngine.Catalog;

namespace MotionControl.TaskEngine.Execution
{
    /// <summary>在执行许可内采集动作证据，页面只读取这些不可变记录。</summary>
    internal sealed partial class TaskNodeExecutor
    {
        /// <summary>记录本次接受的绝对终点与各轴位置，不重新读取可能已经变化的目标变量。</summary>
        private void ObserveAxes(TaskExecutionScope scope, IDictionary<string, double> targets)
        {
            var values = new List<TaskObservedValue>();
            foreach (var target in targets)
            {
                var axis = _device.ReadAxis(target.Key);
                bool known = !double.IsNaN(axis.Position) && !double.IsInfinity(axis.Position);
                values.Add(new TaskObservedValue(target.Key,
                    target.Value.ToString("0.###", CultureInfo.InvariantCulture),
                    known ? axis.Position.ToString("0.###", CultureInfo.InvariantCulture) : null,
                    known ? (bool?)(string.IsNullOrEmpty(axis.Alarm) && Math.Abs(axis.Position - target.Value) < 0.000001) : null,
                    StationObjectKind.Axis, target.Key, known ? StationValueQuality.Good : StationValueQuality.Unknown));
            }
            RecordWait(scope, StationWaitKind.Axis, "axis.target_position", "本次接受的目标与位置采样", values);
        }
        /// <summary>把本次采样逐项保存为等待集合；名称只用于显示，类别、对象和时限分别保存。</summary>
        private void RecordWait(TaskExecutionScope scope, StationWaitKind kind, string reasonCode,
            string description, IEnumerable<TaskObservedValue> observations, bool? decision = null,
            double? remainingDelayMilliseconds = null)
        {
            var values = new List<TaskObservedValue>();
            var items = new List<StationWaitItem>();
            TaskExecutionScope timeoutOwner = scope.FindTimeoutOwner();
            int? timeout = timeoutOwner == null ? (int?)null : timeoutOwner.Node.TimeoutMs;
            foreach (TaskObservedValue value in observations ?? new TaskObservedValue[0])
            {
                string unit = value.Unit ?? ReadObservedUnit(value);
                values.Add(new TaskObservedValue(value.Name, value.Expected, value.Actual, value.Satisfied,
                    value.ObjectKind, value.ObjectId, value.Quality, unit));
                items.Add(new StationWaitItem(kind, reasonCode, value.ObjectKind, value.ObjectId,
                    value.Name, value.Expected, value.Actual, value.Quality, value.Satisfied, timeout, description, unit));
            }
            scope.Diagnostics = new TaskStepDiagnostics(description, values, decision,
                remainingDelayMilliseconds: remainingDelayMilliseconds, waitKind: kind, reasonCode: reasonCode,
                waitItems: items, timeoutMilliseconds: timeout,
                timeoutNodeId: timeoutOwner == null ? null : timeoutOwner.Node.Id);
        }

        /// <summary>只按明确对象类别和原始标识读取目录单位，不从显示名称或说明猜测。</summary>
        private string ReadObservedUnit(TaskObservedValue value)
        {
            if (string.IsNullOrWhiteSpace(value.ObjectId)) return null;
            TaskAxisInfo axis;
            if (value.ObjectKind == StationObjectKind.Axis
                && _device.Catalog.Axes.TryGetValue(value.ObjectId, out axis)) return axis.EngineeringUnit;
            TaskVariableInfo variable;
            if (value.ObjectKind == StationObjectKind.Variable
                && _device.Catalog.VariableDefinitions.TryGetValue(value.ObjectId, out variable)) return variable.EngineeringUnit;
            return null;
        }
    }
}
