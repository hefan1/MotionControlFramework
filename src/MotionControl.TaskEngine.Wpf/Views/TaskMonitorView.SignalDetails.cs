using System;
using System.Collections.Generic;
using System.Linq;
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
                parameters.Add(Field("值来源", BindingText(assignment.Value, references)));
                references.Add(new TaskReferenceEventArgs("设备变量", assignment.VariableName));
            }
            else if (node is WaitConditionNode wait)
                fields.Add(Field("等待条件", ConditionText(wait.Condition, references)));
            else if (node is ConditionBranchNode branch)
            {
                fields.Add(Field("判断条件", ConditionText(branch.Condition, references)));
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
