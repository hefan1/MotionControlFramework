using System;
using System.Collections.Generic;
using System.Globalization;

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
                string unit = _device.Catalog.Axes[target.Key].EngineeringUnit ?? string.Empty;
                values.Add(new TaskObservedValue(target.Key,
                    target.Value.ToString("0.###", CultureInfo.InvariantCulture) + " " + unit,
                    axis.Position.ToString("0.###", CultureInfo.InvariantCulture) + " " + unit,
                    string.IsNullOrEmpty(axis.Alarm) && Math.Abs(axis.Position - target.Value) < 0.000001));
            }
            scope.Diagnostics = new TaskStepDiagnostics("本次接受的目标与位置采样", values);
        }
    }
}
