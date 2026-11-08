using System;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Definitions;

namespace MotionControl.TaskEngine.Validation
{
    /// <summary>检查数值引用的明确物理含义；不把 Number 类型相同当成量纲相同。</summary>
    internal sealed partial class TaskNodeValidator
    {
        /// <summary>比较变量或示教点的单位；常量使用所在条件的数值语境，不臆造单位。</summary>
        private void CheckNumericComparison(TaskValueBinding left, TaskValueBinding right, TaskNode node, string path)
        {
            string leftUnit, leftDimension, rightUnit, rightDimension;
            ReadNumericMetadata(left, out leftUnit, out leftDimension);
            ReadNumericMetadata(right, out rightUnit, out rightDimension);
            string error = NumericValueMetadata.GetCompatibilityError(leftDimension, leftUnit, rightDimension, rightUnit);
            if (error != null) Error(node, path, error);
        }

        /// <summary>写变量前核对已声明的单位和能静态确定的数值范围，执行时仍由变量表复核范围。</summary>
        private void CheckNumericAssignment(SetVariableNode assignment, double? number, string path)
        {
            TaskVariableInfo target = _catalog.VariableDefinitions[assignment.VariableName];
            string unit, dimension;
            ReadNumericMetadata(assignment.Value, out unit, out dimension);
            string error = NumericValueMetadata.GetCompatibilityError(target.Dimension, target.EngineeringUnit, dimension, unit);
            if (error != null) Error(assignment, path, error);
            if (number.HasValue && ((target.MinimumValue.HasValue && number.Value < target.MinimumValue.Value)
                || (target.MaximumValue.HasValue && number.Value > target.MaximumValue.Value)))
                Error(assignment, path, "赋值超出目标变量已声明的数值范围：" + target.Name);
        }

        /// <summary>位置来源须与所选轴的工程单位兼容；不会自动把角度当位移或换算 mm/m。</summary>
        private void CheckAxisTargetMetadata(TaskAxisInfo axis, TaskValueBinding target, TaskNode node, string path)
        {
            string unit, dimension;
            ReadNumericMetadata(target, out unit, out dimension);
            string error = NumericValueMetadata.GetCompatibilityError(
                NumericValueMetadata.KnownDimension(axis.EngineeringUnit), axis.EngineeringUnit, dimension, unit);
            if (error != null) Error(node, path, "轴 " + axis.Name + " 的位置来源" + error);
            if (string.IsNullOrWhiteSpace(axis.EngineeringUnit))
                Warning(node, path, "轴工程单位未定义，无法确认位置来源的物理含义：" + axis.Name);
        }

        /// <summary>只从权威变量定义或示教点所属轴取得元数据，常量和缺失定义不提供虚构单位。</summary>
        private void ReadNumericMetadata(TaskValueBinding value, out string unit, out string dimension)
        {
            unit = null;
            dimension = null;
            if (value == null) return;
            if (value.Source == TaskValueSource.Variable)
            {
                TaskVariableInfo variable;
                if (value.VariableName != null && _catalog.VariableDefinitions.TryGetValue(value.VariableName, out variable))
                {
                    unit = variable.EngineeringUnit;
                    dimension = variable.Dimension;
                }
            }
            else if (value.Source == TaskValueSource.TeachPoint)
            {
                TaskAxisInfo axis;
                if (value.AxisName != null && _catalog.Axes.TryGetValue(value.AxisName, out axis))
                {
                    unit = axis.EngineeringUnit;
                    dimension = NumericValueMetadata.KnownDimension(unit);
                }
            }
        }
    }
}
