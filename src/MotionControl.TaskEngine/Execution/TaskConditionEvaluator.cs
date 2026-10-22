using System;
using System.Collections.Generic;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Definitions;

namespace MotionControl.TaskEngine.Execution
{
    /// <summary>在同一份变量快照上判断条件，未知数据与条件不满足分开处理。</summary>
    internal static class TaskConditionEvaluator
    {
        /// <summary>返回是否能够得出结论；未知子项不会被当作不满足分支。</summary>
        internal static bool TryEvaluate(TaskCondition condition, TaskResourceCatalog catalog,
            IReadOnlyList<DeviceVariableSnapshot> variables, out bool result, out string reason)
        {
            result = false;
            reason = null;
            if (condition.Kind == TaskConditionKind.Compare)
            {
                object left, right;
                if (!TaskRuntimeValues.TryRead(condition.Left, catalog, variables, out left, out reason)
                    || !TaskRuntimeValues.TryRead(condition.Right, catalog, variables, out right, out reason)) return false;
                result = Compare(left, right, condition.Comparison.Value);
                reason = TaskRuntimeValues.Describe(condition.Left) + " " + condition.Comparison + " "
                    + TaskRuntimeValues.Describe(condition.Right) + "；当前值：" + left + " / " + right;
                return true;
            }
            bool combined = condition.Kind == TaskConditionKind.All;
            var descriptions = new List<string>();
            foreach (TaskCondition child in condition.Conditions)
            {
                bool childResult;
                string childReason;
                if (!TryEvaluate(child, catalog, variables, out childResult, out childReason))
                {
                    reason = childReason;
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
