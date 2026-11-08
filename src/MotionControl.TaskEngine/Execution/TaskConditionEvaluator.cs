using System;
using System.Collections.Generic;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Definitions;

namespace MotionControl.TaskEngine.Execution
{
    /// <summary>在同一份变量快照上判断条件，未知数据与条件不满足分开处理。</summary>
    internal static class TaskConditionEvaluator
    {
        /// <summary>返回是否能够得出结论；未知子项不会被当作不满足分支。</summary>
        internal static bool TryEvaluate(TaskCondition condition, TaskResourceCatalog catalog,
            IReadOnlyList<DeviceVariableSnapshot> variables, out bool result, out string reason,
            List<TaskObservedValue> observations = null)
        {
            result = false;
            reason = null;
            if (condition.Kind == TaskConditionKind.Compare)
            {
                object left, right;
                // 与原判断一样先读左侧；左侧未知时不再读取右侧。
                if (!TaskRuntimeValues.TryRead(condition.Left, catalog, variables, out left, out reason))
                {
                    AddObservation(observations, condition.Left,
                        condition.Comparison + " " + TaskRuntimeValues.Describe(condition.Right),
                        null, null, StationValueQuality.Unknown);
                    AddRightVariable(observations, condition.Right, null, null, StationValueQuality.NotProvided);
                    return false;
                }
                if (!TaskRuntimeValues.TryRead(condition.Right, catalog, variables, out right, out reason))
                {
                    AddObservation(observations, condition.Left,
                        condition.Comparison + " " + TaskRuntimeValues.Describe(condition.Right),
                        left, null, StationValueQuality.Good);
                    AddObservation(observations, condition.Right, null, null, null, StationValueQuality.Unknown);
                    return false;
                }
                result = Compare(left, right, condition.Comparison.Value);
                AddObservation(observations, condition.Left,
                    condition.Comparison + " " + Convert.ToString(right, System.Globalization.CultureInfo.InvariantCulture),
                    left, result, StationValueQuality.Good);
                AddRightVariable(observations, condition.Right, right, result, StationValueQuality.Good);
                reason = TaskRuntimeValues.Describe(condition.Left) + " " + condition.Comparison + " "
                    + TaskRuntimeValues.Describe(condition.Right) + "；当前值：" + left + " / " + right;
                return true;
            }
            bool combined = condition.Kind == TaskConditionKind.All;
            var descriptions = new List<string>();
            for (int index = 0; index < condition.Conditions.Count; index++)
            {
                TaskCondition child = condition.Conditions[index];
                bool childResult;
                string childReason;
                if (!TryEvaluate(child, catalog, variables, out childResult, out childReason, observations))
                {
                    reason = childReason;
                    // 后面的条件没有求值，但它们仍是本次配置的等待对象，不能从集合中消失。
                    for (int pending = index + 1; pending < condition.Conditions.Count; pending++)
                        AddUnobservedCondition(observations, condition.Conditions[pending]);
                    return false;
                }
                descriptions.Add(childReason);
                if (condition.Kind == TaskConditionKind.All) combined = combined && childResult;
                else if (condition.Kind == TaskConditionKind.Any) combined = combined || childResult;
                else combined = !childResult;
            }
            result = combined;
            reason = condition.Kind + "(" + string.Join("；", descriptions) + ")";
            return true;
        }

        /// <summary>保存已读取或尚未读取的条件对象；显示名称中的符号不影响对象引用。</summary>
        private static void AddObservation(List<TaskObservedValue> observations, TaskValueBinding binding,
            string expected, object actual, bool? satisfied, StationValueQuality quality)
        {
            if (observations == null) return;
            bool variable = binding.Source == TaskValueSource.Variable;
            observations.Add(new TaskObservedValue(TaskRuntimeValues.Describe(binding), expected,
                actual == null ? null : Convert.ToString(actual, System.Globalization.CultureInfo.InvariantCulture),
                satisfied, variable ? StationObjectKind.Variable : StationObjectKind.NotProvided,
                variable ? binding.VariableName : null, quality));
        }

        /// <summary>右侧也是变量时保留它的独立引用；未读取的右侧不填写实际值或反向猜测期望值。</summary>
        private static void AddRightVariable(List<TaskObservedValue> observations, TaskValueBinding binding,
            object actual, bool? satisfied, StationValueQuality quality)
        {
            if (binding.Source == TaskValueSource.Variable)
                AddObservation(observations, binding, null, actual, satisfied, quality);
        }

        /// <summary>仅遍历条件配置来补齐未采样的等待对象，供短路条件和互锁诊断共用，不改变放行判断。</summary>
        internal static void AddUnobservedCondition(List<TaskObservedValue> observations, TaskCondition condition)
        {
            if (observations == null) return;
            if (condition.Kind == TaskConditionKind.Compare)
            {
                AddObservation(observations, condition.Left,
                    condition.Comparison + " " + TaskRuntimeValues.Describe(condition.Right),
                    null, null, StationValueQuality.NotProvided);
                AddRightVariable(observations, condition.Right, null, null, StationValueQuality.NotProvided);
                return;
            }
            foreach (TaskCondition child in condition.Conditions) AddUnobservedCondition(observations, child);
        }

        /// <summary>按已校验的类型比较，字符串使用固定序号相等规则。</summary>
        private static bool Compare(object left, object right, TaskComparison comparison)
        {
            int order;
            if (left is double || left is int || left is uint || left is byte)
                order = TaskRuntimeValues.Number(left).CompareTo(TaskRuntimeValues.Number(right));
            else if (left is DateTime date) order = CompareDates(date, (DateTime)right);
            else order = Equals(left, right) ? 0 : 1;
            switch (comparison)
            {
                case TaskComparison.Equal: return order == 0;
                case TaskComparison.NotEqual: return order != 0;
                case TaskComparison.Less: return order < 0;
                case TaskComparison.LessOrEqual: return order <= 0;
                case TaskComparison.Greater: return order > 0;
                case TaskComparison.GreaterOrEqual: return order >= 0;
                default: throw new NotSupportedException("条件比较操作无效。");
            }
        }

        /// <summary>有时区的日期按同一 UTC 时刻比较；没有时区的值不能与有时区的值混比。</summary>
        private static int CompareDates(DateTime left, DateTime right)
        {
            bool leftUnspecified = left.Kind == DateTimeKind.Unspecified;
            bool rightUnspecified = right.Kind == DateTimeKind.Unspecified;
            if (leftUnspecified != rightUnspecified)
                throw new InvalidOperationException("DateTime 比较两侧的时区信息不一致，请明确填写时区。");
            if (leftUnspecified) return left.CompareTo(right);
            return left.ToUniversalTime().CompareTo(right.ToUniversalTime());
        }
    }
}
