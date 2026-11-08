using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Execution;
using MotionControl.TaskEngine.Wpf.Monitoring;

namespace MotionControl.TaskEngine.Wpf.Views
{
    /// <summary>IO、条件与变量详情；输入、输出和机构到位语义分别显示。</summary>
    public partial class TaskMonitorView
    {
        /// <summary>显示当前节点确实需要的信号和条件，条件组合保留全部/任一/取反含义。</summary>
        private void AddSignalDetails(TaskNode node, TaskStepDiagnostics diagnostic, List<TaskDetailField> fields,
            List<TaskDetailField> parameters, List<TaskReferenceEventArgs> references)
        {
            if (node is WaitInputNode input)
            {
                parameters.Add(Field("逻辑输入", input.InputName));
                parameters.Add(Field("期望状态", T(input.Expected ? "有效" : "无效")));
                IoReferences(input.InputName, false, references);
            }
            else if (node is WaitOutputNode outputWait)
            {
                parameters.Add(Field("逻辑输出", outputWait.OutputName));
                parameters.Add(Field("期望状态", T(outputWait.Expected ? "有效" : "无效")));
                fields.Add(Field("反馈说明", T("输出写入不等于机构到位")));
                IoReferences(outputWait.OutputName, true, references);
            }
            else if (node is WaitAxisPositionNode axisWait)
            {
                TaskAxisInfo axis;
                bool hasAxis = _session.TaskDevice.Catalog.Axes.TryGetValue(axisWait.AxisName, out axis);
                string unit = hasAxis && !string.IsNullOrWhiteSpace(axis.EngineeringUnit) ? axis.EngineeringUnit : T("单位未配置");
                parameters.Add(Field("逻辑轴", axisWait.AxisName));
                parameters.Add(Field("期望位置", axisWait.Position.ToString(CultureInfo.InvariantCulture) + " " + unit));
                parameters.Add(Field("位置容差", axisWait.Tolerance.ToString(CultureInfo.InvariantCulture) + " " + unit));
                if (hasAxis) AxisReferences(axisWait.AxisName, references);
            }
            else if (node is WriteRecordNode record)
            {
                parameters.Add(Field("记录类别", record.Category));
                foreach (var item in record.Fields ?? new Dictionary<string, string>())
                {
                    parameters.Add(Field(item.Key, item.Value));
                    references.Add(new TaskReferenceEventArgs("设备变量", item.Value));
                }
                fields.Add(Field("记录说明", T("所有字段取同一次变量快照；记录失败时停止后续步骤")));
            }
            else if (node is ReadInputWordNode word)
            {
                parameters.Add(Field("数据有效输入", word.ValidInputName));
                parameters.Add(Field("读取成功输入", word.SuccessInputName));
                parameters.Add(Field("目标变量", word.TargetVariable));
                IoReferences(word.ValidInputName, false, references);
                IoReferences(word.SuccessInputName, false, references);
                references.Add(new TaskReferenceEventArgs("设备变量", word.TargetVariable));
            }
            else if (node is ReadInputNode sample)
            {
                parameters.Add(Field("逻辑输入", sample.InputName));
                parameters.Add(Field("变量", sample.VariableName));
                IoReferences(sample.InputName, false, references);
                references.Add(new TaskReferenceEventArgs("设备变量", sample.VariableName));
            }
            else if (node is WaitExclusiveInputsNode pair)
            {
                parameters.Add(Field("第一路输入", pair.FirstInputName)); parameters.Add(Field("第二路输入", pair.SecondInputName));
                parameters.Add(Field("有效输入要求", T(!pair.RequiredFirstValue.HasValue ? "任一路有效" : pair.RequiredFirstValue.Value ? "第一路有效" : "第二路有效")));
                parameters.Add(Field("第一路目标变量", pair.FirstVariableName)); parameters.Add(Field("第二路目标变量", pair.SecondVariableName));
                IoReferences(pair.FirstInputName, false, references); IoReferences(pair.SecondInputName, false, references);
                references.Add(new TaskReferenceEventArgs("设备变量", pair.FirstVariableName));
                references.Add(new TaskReferenceEventArgs("设备变量", pair.SecondVariableName));
            }
            else if (node is SetOutputNode output)
            {
                parameters.Add(Field("逻辑输出", output.OutputName));
                parameters.Add(Field("写入状态", T(output.Value ? "有效" : "无效")));
                fields.Add(Field("反馈说明", T("输出写入不等于机构到位")));
                IoReferences(output.OutputName, true, references);
            }
            else if (node is SetVariableNode assignment)
            {
                parameters.Add(Field("变量", assignment.VariableName + " (" + VariableTypeName(_session.TaskDevice.Catalog.Variables[assignment.VariableName].ToString()) + ")"));
                TaskVariableInfo variable = _session.TaskDevice.Catalog.VariableDefinitions[assignment.VariableName];
                if (MotionControl.Framework.Contracts.Variables.NumericValueMetadata.IsNumeric(variable.DataType))
                    parameters.Add(Field("工程单位", string.IsNullOrWhiteSpace(variable.EngineeringUnit) ? T("单位未配置") : variable.EngineeringUnit));
                parameters.Add(Field("值来源", BindingText(assignment.Value, references)));
                references.Add(new TaskReferenceEventArgs("设备变量", assignment.VariableName));
            }
            else if (node is WaitConditionNode wait)
                fields.Add(Field("期望条件（定义）", ConditionText(wait.Condition, references)));
            else if (node is ConditionBranchNode branch)
            {
                fields.Add(Field("判断条件（定义）", ConditionText(branch.Condition, references)));
                fields.Add(Field("本次选择", diagnostic?.Decision == null ? T("尚未判断")
                    : diagnostic.Decision.Value ? branch.WhenTrue.Name : branch.WhenFalse?.Name ?? T("不执行分支")));
                fields.Add(Field("记录说明", T("使用判断当时的数据，后续变量改变不改写本次选择")));
            }
            else if (node is DelayNode delay)
            {
                parameters.Add(Field("设定延时", delay.Milliseconds + " ms"));
                fields.Add(Field("剩余延时", diagnostic?.RemainingDelayMilliseconds == null ? "—"
                    : Math.Ceiling(diagnostic.RemainingDelayMilliseconds.Value) + " ms"));
                fields.Add(Field("计时方式", T("暂停期间不消耗延时")));
            }
        }

        /// <summary>明确列出条件结构，避免把任一成立误显示成全部成立。</summary>
        private string ConditionText(TaskCondition condition, List<TaskReferenceEventArgs> references)
        {
            if (condition.Kind == TaskConditionKind.Compare)
                return BindingText(condition.Left, references) + " " + ComparisonText(condition.Comparison.Value)
                    + " " + BindingText(condition.Right, references);
            string join = condition.Kind == TaskConditionKind.All ? "全部满足" : condition.Kind == TaskConditionKind.Any ? "任一满足" : "条件取反";
            return T(join) + " (" + string.Join("；", condition.Conditions.Select(child => ConditionText(child, references))) + ")";
        }

        /// <summary>比较运算使用现场常见数学符号。</summary>
        private static string ComparisonText(TaskComparison comparison)
        {
            switch (comparison)
            {
                case TaskComparison.Equal: return "="; case TaskComparison.NotEqual: return "≠";
                case TaskComparison.Less: return "<"; case TaskComparison.LessOrEqual: return "≤";
                case TaskComparison.Greater: return ">"; default: return "≥";
            }
        }

        /// <summary>逻辑信号关联已有映射和监控入口。</summary>
        private void IoReferences(string name, bool output, List<TaskReferenceEventArgs> references)
        { references.Add(new TaskReferenceEventArgs("IO映射", name, output ? "DO" : "DI")); }
    }
}
