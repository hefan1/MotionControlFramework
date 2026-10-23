using System;
using System.Collections.Generic;
using System.Linq;
using MotionControl.TaskEngine.Definitions;

namespace MotionControl.TaskEngine.Validation
{
    /// <summary>明确互锁字段和资源占用之间的校验。</summary>
    internal sealed partial class TaskNodeValidator
    {
        /// <summary>检查全部互锁条件；不允许空条件被当作确认成功。</summary>
        private void CheckInterlock(TaskInterlock interlock, ResourceScopeNode owner, string path)
        {
            if (interlock == null) return;
            if (interlock.Axes == null || interlock.Inputs == null)
            {
                Error(owner, path, "轴和输入条件列表不能为 null。");
                return;
            }
            if (interlock.Axes.Count > 256 || interlock.Inputs.Count > 4096)
            {
                Error(owner, path, "互锁定义超过 256 个轴条件或 4096 个输入条件。");
                return;
            }
            if (interlock.Condition == null && interlock.Axes.Count == 0 && interlock.Inputs.Count == 0)
                Error(owner, path, "互锁至少需要一个明确条件；不需要互锁时请省略整个字段。");
            if (interlock.Condition != null)
                CheckCondition(interlock.Condition, owner, path + ".condition", new HashSet<TaskCondition>(), 0);
            var axes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (TaskAxisInterlock item in interlock.Axes)
            {
                if (item == null) { Error(owner, path + ".axes", "轴条件不能为空。"); continue; }
                var axis = FindAxis(item.AxisName, owner, path + ".axes");
                if (!axes.Add(item.AxisName ?? string.Empty)) Error(owner, path + ".axes", "轴条件重复：" + item.AxisName);
                if (!FinitePosition(item.MinimumPosition) || !FinitePosition(item.MaximumPosition)
                    || item.MinimumPosition > item.MaximumPosition)
                    Error(owner, path + ".axes", "轴位置上下限必须为有限数且下限不大于上限：" + item.AxisName);
                if (axis != null && (!axis.MinimumPosition.HasValue || !axis.MaximumPosition.HasValue
                    || item.MinimumPosition < axis.MinimumPosition || item.MaximumPosition > axis.MaximumPosition))
                    Error(owner, path + ".axes", "互锁位置超出轴配置行程或配置缺少行程：" + item.AxisName);
                if (owner.Resources == null || !owner.Resources.Contains("Axis:" + item.AxisName, StringComparer.OrdinalIgnoreCase))
                    Error(owner, path + ".axes", "互锁引用的轴必须纳入本范围资源：Axis:" + item.AxisName);
            }
            var inputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (TaskInputInterlock item in interlock.Inputs)
            {
                if (item == null) { Error(owner, path + ".inputs", "输入条件不能为空。"); continue; }
                if (!ValidName(item.InputName) || !inputs.Add(item.InputName)
                    || !_catalog.Inputs.Contains(item.InputName, StringComparer.OrdinalIgnoreCase))
                    Error(owner, path + ".inputs", "DI 名称为空、重复或不存在：" + item.InputName);
                if (!item.Expected.HasValue) Error(owner, path + ".inputs", "必须指定 DI 期望值：" + item.InputName);
            }
        }

        /// <summary>排除没有填写、非数和无限的位置边界。</summary>
        private static bool FinitePosition(double? value)
        {
            return value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value);
        }
    }
}
