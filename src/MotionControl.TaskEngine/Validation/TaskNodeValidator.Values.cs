using System;
using System.Collections.Generic;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.TaskEngine.Definitions;

namespace MotionControl.TaskEngine.Validation
{
    /// <summary>数据来源和条件的类型检查，不读取设备运行值。</summary>
    internal sealed partial class TaskNodeValidator
    {
        /// <summary>检查互斥的数据来源，返回实际类型和可以静态确定的数值。</summary>
        private DeviceVariableType? CheckValue(TaskValueBinding value, TaskNode node, string path, out double? number)
        {
            number = null;
            if (value == null) { Error(node, path, "没有指定数据来源。"); return null; }
            switch (value.Source)
            {
                case TaskValueSource.Constant:
                    if (value.VariableName != null || value.AxisName != null || value.PointId != null)
                        Error(node, path, "常量来源不能同时填写变量或示教点引用。");
                    if (!value.DataType.HasValue || !TaskLiteral.TryRead(value.DataType.Value, value.Constant, out number))
                    {
                        Error(node, path, "常量类型或内容无效；数字须有限，日期须为 ISO 8601 往返格式。");
                        return null;
                    }
                    return value.DataType;
                case TaskValueSource.Variable:
                    if (value.Constant != null || value.DataType.HasValue || value.AxisName != null || value.PointId != null)
                        Error(node, path, "变量来源只能填写 variableName，类型由现有变量定义提供。");
                    DeviceVariableType type;
                    if (!ValidName(value.VariableName) || !_catalog.Variables.TryGetValue(value.VariableName, out type))
                    {
                        Error(node, path + ".variableName", "设备变量不存在：" + value.VariableName);
                        return null;
                    }
                    Warning(node, path, "变量的当前有效值、采集周期和动态范围仍须在执行前检查。");
                    return type;
                case TaskValueSource.TeachPoint:
                    if (value.Constant != null || value.DataType.HasValue || value.VariableName != null)
                        Error(node, path, "示教点来源只能填写 axisName 和 pointId。");
                    var point = _catalog.FindTeachPoint(value.AxisName, value.PointId);
                    if (point == null || !point.Position.HasValue || double.IsNaN(point.Position.Value)
                        || double.IsInfinity(point.Position.Value))
                    {
                        Error(node, path, "示教点不存在、尚未示教或数值无效：" + value.AxisName + "/" + value.PointId);
                        return null;
                    }
                    number = point.Position;
                    return DeviceVariableType.Number;
                default:
                    Error(node, path + ".source", "不支持的数据来源。");
                    return null;
            }
        }

        /// <summary>检查比较和组合条件，并限制循环引用及展开数量。</summary>
        private void CheckCondition(TaskCondition condition, TaskNode node, string path,
            HashSet<TaskCondition> parents, int depth)
        {
            if (condition == null) { Error(node, path, "条件不能为空。"); return; }
            _conditionCount++;
            if (depth > 32 || _conditionCount > 5000 || !parents.Add(condition))
            {
                Error(node, path, "条件树过深、过大或形成循环引用。");
                return;
            }
            if (condition.Kind == TaskConditionKind.Compare)
            {
                if (condition.Conditions == null || condition.Conditions.Count != 0)
                    Error(node, path + ".conditions", "比较条件不能同时包含组合子条件。");
                double? ignored;
                var left = CheckValue(condition.Left, node, path + ".left", out ignored);
                var right = CheckValue(condition.Right, node, path + ".right", out ignored);
                if (!condition.Comparison.HasValue || !Enum.IsDefined(typeof(TaskComparison), condition.Comparison.Value))
                    Error(node, path + ".comparison", "比较符没有配置或不受支持。");
                if (left.HasValue && right.HasValue)
                {
                    bool bothNumeric = TaskLiteral.IsNumeric(left.Value) && TaskLiteral.IsNumeric(right.Value);
                    if (left != right && !bothNumeric) Error(node, path, "比较两侧的数据类型不兼容。");
                    bool equality = condition.Comparison == TaskComparison.Equal || condition.Comparison == TaskComparison.NotEqual;
                    if (!equality && !bothNumeric
                        && !(left == DeviceVariableType.DateTime && right == DeviceVariableType.DateTime))
                        Error(node, path, "大小比较只支持数值或 DateTime；其他类型请使用相等或不等。");
                }
            }
            else if (condition.Kind == TaskConditionKind.All || condition.Kind == TaskConditionKind.Any
                || condition.Kind == TaskConditionKind.Not)
            {
                if (condition.Left != null || condition.Right != null || condition.Comparison.HasValue)
                    Error(node, path, "组合条件不能同时填写比较左右值或比较符。");
                if (condition.Conditions == null || condition.Conditions.Count == 0
                    || condition.Conditions.Count > 5000
                    || (condition.Kind == TaskConditionKind.Not && condition.Conditions.Count != 1))
                    Error(node, path + ".conditions", "All/Any 至少一个子条件，Not 必须且只能一个子条件，最多 5000 项。");
                else
                    for (int index = 0; index < condition.Conditions.Count; index++)
                        CheckCondition(condition.Conditions[index], node, path + ".conditions[" + index + "]", parents, depth + 1);
            }
            else Error(node, path + ".kind", "条件类型不受支持。");
            parents.Remove(condition);
        }
    }
}
