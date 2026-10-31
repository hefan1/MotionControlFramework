using System;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Execution;

namespace MotionControl.TaskEngine.Device.InMemory
{
    /// <summary>基于同一次内存设备状态检查显式互锁，不连接实际安全回路。</summary>
    public sealed partial class InMemoryTaskDevice
    {
        /// <summary>在设备锁内核对位置、输入和变量；未知或不满足都返回具体原因。</summary>
        public bool CheckInterlock(TaskInterlock interlock, TaskCondition legacyCondition, out string reason)
        {
            lock (_sync)
            {
                reason = "全部条件满足。";
                if (interlock != null)
                {
                    foreach (TaskAxisInterlock item in interlock.Axes)
                    {
                        InMemoryAxis axis = FindAxis(item.AxisName);
                        reason = "轴 " + item.AxisName + " 需要停止、使能、无报警"
                            + (item.RequireHomed ? "、已回零" : "") + "，位置范围 [" + item.MinimumPosition
                            + ", " + item.MaximumPosition + "]，当前位置=" + axis.Position;
                        if (axis.Operation != null || !axis.Enabled || !string.IsNullOrWhiteSpace(axis.Alarm)
                            || (item.RequireHomed && !axis.Homed) || axis.Position < item.MinimumPosition
                            || axis.Position > item.MaximumPosition) return false;
                    }
                    foreach (TaskInputInterlock item in interlock.Inputs)
                    {
                        bool? value = _inputs[item.InputName];
                        reason = "DI " + item.InputName + " 期望=" + item.Expected + "，当前="
                            + (value.HasValue ? value.Value.ToString() : "未知");
                        if (!value.HasValue || value.Value != item.Expected.Value) return false;
                    }
                }
                TaskCondition condition = interlock?.Condition ?? legacyCondition;
                if (condition != null)
                {
                    bool result;
                    if (!TaskConditionEvaluator.TryEvaluate(condition, Catalog, _variables.GetSnapshot(), out result, out reason)
                        || !result) return false;
                }
                reason = "全部条件满足。";
                return true;
            }
        }
    }
}
