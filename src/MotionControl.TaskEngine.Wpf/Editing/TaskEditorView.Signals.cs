using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.TaskEngine.Definitions;

namespace MotionControl.TaskEngine.Wpf.Editing
{
    /// <summary>逻辑 IO 和变量参数的具体表单。</summary>
    public partial class TaskEditorView
    {
        /// <summary>按信号步骤只显示相关字段。</summary>
        private void BuildSignals()
        {
            if (_selected is WaitInputNode wait)
            {
                AddField("Input", "逻辑输入", TaskEditingUi.Names(_catalog.Inputs, wait.InputName));
                AddField("Expected", "期望状态", TaskEditingUi.Choices(this, new object[] { true, false }, new[] { "有效", "无效" }, wait.Expected));
                AddField("TimeoutAlarms", "等待超时关联报警（可多选）",
                    new TaskAlarmSelector(_catalog.AlarmDefinitions, wait.TimeoutAlarmCodes,
                        code => T("报警库不存在") + code, T("当前设备报警库为空，请先维护报警代码"),
                        T("已关联 {0} 条报警"), T("停机报警"), T("非停机报警"), T("检查："), T("恢复：")));
                Details.Children.Add(TaskEditingUi.Label(this, "未知输入不会当作无效信号放行")); AddReference("IO映射", "@Input", "DI");
                Details.Children.Add(TaskEditingUi.Label(this, "任务报警关联规则"));
            }
            else if (_selected is WaitOutputNode outputWait)
            {
                AddField("Output", "逻辑输出", TaskEditingUi.Names(_catalog.Outputs, outputWait.OutputName));
                AddField("Expected", "期望状态", TaskEditingUi.Choices(this, new object[] { true, false }, new[] { "有效", "无效" }, outputWait.Expected));
                AddField("TimeoutAlarms", "等待超时关联报警（可多选）",
                    new TaskAlarmSelector(_catalog.AlarmDefinitions, outputWait.TimeoutAlarmCodes,
                        code => T("报警库不存在") + code, T("当前设备报警库为空，请先维护报警代码"),
                        T("已关联 {0} 条报警"), T("停机报警"), T("非停机报警"), T("检查："), T("恢复：")));
                Details.Children.Add(TaskEditingUi.Label(this, "输出读回不代表机构已到位")); AddReference("IO映射", "@Output", "DO");
            }
            else if (_selected is WaitAxisPositionNode axisWait)
            {
                AddField("Axis", "逻辑轴", TaskEditingUi.Names(_catalog.Axes.Keys, axisWait.AxisName));
                AddField("Position", "期望位置", TaskEditingUi.Text(axisWait.Position.ToString(CultureInfo.InvariantCulture)));
                AddField("Tolerance", "位置容差", TaskEditingUi.Text(axisWait.Tolerance.ToString(CultureInfo.InvariantCulture)));
                AddField("TimeoutAlarms", "等待超时关联报警（可多选）", new TaskAlarmSelector(_catalog.AlarmDefinitions, axisWait.TimeoutAlarmCodes,
                    code => T("报警库不存在") + code, T("当前设备报警库为空，请先维护报警代码"), T("已关联 {0} 条报警"), T("停机报警"), T("非停机报警"), T("检查："), T("恢复：")));
                Details.Children.Add(TaskEditingUi.Label(this, "需轴已使能、回零、停止且无报警；不代替安全联锁"));
            }
            else if (_selected is WriteRecordNode record)
            {
                AddField("Category", "记录类别", TaskEditingUi.Text(record.Category));
                var text = TaskEditingUi.Text(string.Join(Environment.NewLine, (record.Fields ?? new Dictionary<string, string>()).Select(item => item.Key + " = " + item.Value)));
                text.AcceptsReturn = true; text.TextWrapping = TextWrapping.Wrap; text.MinHeight = 120;
                AddField("Fields", "记录字段（每行：字段名 = 字符串变量名）", text);
                Details.Children.Add(TaskEditingUi.Label(this, "所有字段取同一次变量快照；记录失败时停止后续步骤"));
            }
            else if (_selected is ReadInputWordNode word)
            {
                AddField("Valid", "数据有效输入", TaskEditingUi.Names(_catalog.Inputs, word.ValidInputName));
                AddField("Success", "读取成功输入", TaskEditingUi.Names(_catalog.Inputs, word.SuccessInputName));
                for (int bit = 0; bit < 16; bit++)
                    AddField("Bit" + bit, "数据位 " + bit, TaskEditingUi.Names(_catalog.Inputs, word.InputNames != null && bit < word.InputNames.Count ? word.InputNames[bit] : ""));
                AddField("Variable", "设备变量", TaskEditingUi.Names(_catalog.Variables.Where(item => item.Value == DeviceVariableType.String).Select(item => item.Key), word.TargetVariable));
                AddField("Prefix", "编码前缀", TaskEditingUi.Text(word.Prefix));
                AddField("TimeoutAlarms", "等待超时关联报警（可多选）", new TaskAlarmSelector(_catalog.AlarmDefinitions, word.TimeoutAlarmCodes,
                    code => T("报警库不存在") + code, T("当前设备报警库为空，请先维护报警代码"), T("已关联 {0} 条报警"), T("停机报警"), T("非停机报警"), T("检查："), T("恢复：")));
                Details.Children.Add(TaskEditingUi.Label(this, "输入须由设备锁存；两轮采样不一致或数据为零时拒绝"));
            }
            else if (_selected is ReadInputNode sample)
            {
                AddField("Input", "逻辑输入", TaskEditingUi.Names(_catalog.Inputs, sample.InputName));
                AddField("Variable", "设备变量", TaskEditingUi.Names(_catalog.Variables.Where(item => item.Value == DeviceVariableType.Boolean).Select(item => item.Key), sample.VariableName));
                Details.Children.Add(TaskEditingUi.Label(this, "未知输入不写入变量，步骤会报错"));
                AddReference("IO映射", "@Input", "DI"); AddReference("设备变量", "@Variable");
            }
            else if (_selected is WaitExclusiveInputsNode pair)
            {
                var booleans = _catalog.Variables.Where(item => item.Value == DeviceVariableType.Boolean).Select(item => item.Key);
                AddField("FirstInput", "第一路输入", TaskEditingUi.Names(_catalog.Inputs, pair.FirstInputName));
                AddField("SecondInput", "第二路输入", TaskEditingUi.Names(_catalog.Inputs, pair.SecondInputName));
                AddField("RequiredFirst", "有效输入要求", TaskEditingUi.Choices(this,
                    new object[] { "Any", "First", "Second" }, new[] { "任一路有效", "第一路有效", "第二路有效" },
                    pair.RequiredFirstValue.HasValue ? (pair.RequiredFirstValue.Value ? "First" : "Second") : "Any"));
                AddField("FirstVariable", "第一路目标变量", TaskEditingUi.Names(booleans, pair.FirstVariableName));
                AddField("SecondVariable", "第二路目标变量", TaskEditingUi.Names(booleans, pair.SecondVariableName));
                AddField("TimeoutAlarms", "等待超时关联报警（可多选）",
                    new TaskAlarmSelector(_catalog.AlarmDefinitions, pair.TimeoutAlarmCodes,
                        code => T("报警库不存在") + code, T("当前设备报警库为空，请先维护报警代码"),
                        T("已关联 {0} 条报警"), T("停机报警"), T("非停机报警"), T("检查："), T("恢复：")));
                Details.Children.Add(TaskEditingUi.Label(this, "两路须由设备锁存且恰好一路有效；未知、读错或状态变化不会放行"));
                AddReference("IO映射", "@FirstInput", "DI"); AddReference("IO映射", "@SecondInput", "DI");
                AddReference("设备变量", "@FirstVariable"); AddReference("设备变量", "@SecondVariable");
            }
            else if (_selected is SetOutputNode output)
            {
                AddField("Output", "逻辑输出", TaskEditingUi.Names(_catalog.Outputs, output.OutputName));
                AddField("Value", "写入状态", TaskEditingUi.Choices(this, new object[] { true, false }, new[] { "有效", "无效" }, output.Value));
                Details.Children.Add(TaskEditingUi.Label(this, "这里只编辑输出定义，信号极性由IO映射处理")); AddReference("IO映射", "@Output", "DO");
            }
            else if (_selected is SetVariableNode variable)
            {
                var choice = TaskEditingUi.Names(_catalog.Variables.Keys, variable.VariableName);
                AddField("Variable", "设备变量", choice);
                AddField("Binding", "值来源", new TaskValueEditor(_catalog, variable.Value, _language));
                AddField("TypeNote", "变量类型", new TextBlock());
                UpdateVariableType(false); choice.SelectionChanged += VariableChanged; AddReference("设备变量", "@Variable");
            }
            else if (_selected is DelayNode delay) AddField("Milliseconds", "延时(ms)", TaskEditingUi.Text(delay.Milliseconds.ToString()));
        }
        /// <summary>显式切换变量后显示类型并同步常量类型。</summary>
        private void VariableChanged(object sender, SelectionChangedEventArgs e) { UpdateVariableType(true); }
        /// <summary>类型来自共享变量目录，不在任务中重新定义变量。</summary>
        private void UpdateVariableType(bool updateConstant)
        {
            DeviceVariableType type;
            string name = ((ComboBox)_fields["Variable"]).SelectedItem as string ?? "";
            bool known = _catalog.Variables.TryGetValue(name, out type);
            ((TextBlock)_fields["TypeNote"]).Text = known ? TaskValueEditor.TypeName(type) : T("未选择或引用失效");
            if (known && updateConstant) ((TaskValueEditor)_fields["Binding"]).SelectConstantType(type);
        }
        /// <summary>先完成字段解析再替换模型；失败时保留原值，不调用 IO 或变量运行服务。</summary>
        private void ReadSignals()
        {
            if (_selected is WaitInputNode wait)
            {
                wait.InputName = ((ComboBox)_fields["Input"]).SelectedItem as string ?? "";
                wait.Expected = (bool)((ComboBox)_fields["Expected"]).SelectedValue;
                wait.TimeoutAlarmCodes = ((TaskAlarmSelector)_fields["TimeoutAlarms"]).SelectedCodes.ToList();
            }
            else if (_selected is WaitOutputNode outputWait)
            {
                outputWait.OutputName = ((ComboBox)_fields["Output"]).SelectedItem as string ?? "";
                outputWait.Expected = (bool)((ComboBox)_fields["Expected"]).SelectedValue;
                outputWait.TimeoutAlarmCodes = ((TaskAlarmSelector)_fields["TimeoutAlarms"]).SelectedCodes.ToList();
            }
            else if (_selected is WaitAxisPositionNode axisWait)
            {
                axisWait.AxisName = ((ComboBox)_fields["Axis"]).SelectedItem as string ?? "";
                axisWait.Position = TaskEditingUi.Number((TextBox)_fields["Position"], "期望位置");
                axisWait.Tolerance = TaskEditingUi.Number((TextBox)_fields["Tolerance"], "位置容差");
                axisWait.TimeoutAlarmCodes = ((TaskAlarmSelector)_fields["TimeoutAlarms"]).SelectedCodes.ToList();
            }
            else if (_selected is WriteRecordNode record)
            {
                var fields = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (string line in ((TextBox)_fields["Fields"]).Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int separator = line.IndexOf('=');
                    if (separator <= 0 || separator == line.Length - 1) throw new InvalidOperationException("记录字段每行须填写：字段名 = 字符串变量名。");
                    string name = line.Substring(0, separator).Trim(); string variableName = line.Substring(separator + 1).Trim();
                    if (name.Length == 0 || variableName.Length == 0 || fields.ContainsKey(name)) throw new InvalidOperationException("记录字段名称不能为空或重复。");
                    fields.Add(name, variableName);
                }
                record.Category = ((TextBox)_fields["Category"]).Text.Trim(); record.Fields = fields;
            }
            else if (_selected is ReadInputWordNode word)
            {
                word.ValidInputName = ((ComboBox)_fields["Valid"]).SelectedItem as string ?? "";
                word.SuccessInputName = ((ComboBox)_fields["Success"]).SelectedItem as string ?? "";
                word.InputNames = Enumerable.Range(0, 16).Select(bit => ((ComboBox)_fields["Bit" + bit]).SelectedItem as string ?? "").ToList();
                word.TargetVariable = ((ComboBox)_fields["Variable"]).SelectedItem as string ?? "";
                word.Prefix = ((TextBox)_fields["Prefix"]).Text;
                word.TimeoutAlarmCodes = ((TaskAlarmSelector)_fields["TimeoutAlarms"]).SelectedCodes.ToList();
            }
            else if (_selected is ReadInputNode sample)
            {
                sample.InputName = ((ComboBox)_fields["Input"]).SelectedItem as string ?? "";
                sample.VariableName = ((ComboBox)_fields["Variable"]).SelectedItem as string ?? "";
            }
            else if (_selected is WaitExclusiveInputsNode pair)
            {
                pair.FirstInputName = ((ComboBox)_fields["FirstInput"]).SelectedItem as string ?? "";
                pair.SecondInputName = ((ComboBox)_fields["SecondInput"]).SelectedItem as string ?? "";
                string required = ((ComboBox)_fields["RequiredFirst"]).SelectedValue as string;
                pair.RequiredFirstValue = required == "First" ? (bool?)true : required == "Second" ? (bool?)false : null;
                pair.FirstVariableName = ((ComboBox)_fields["FirstVariable"]).SelectedItem as string ?? "";
                pair.SecondVariableName = ((ComboBox)_fields["SecondVariable"]).SelectedItem as string ?? "";
                pair.TimeoutAlarmCodes = ((TaskAlarmSelector)_fields["TimeoutAlarms"]).SelectedCodes.ToList();
            }
            else if (_selected is SetOutputNode output) { output.OutputName = ((ComboBox)_fields["Output"]).SelectedItem as string ?? ""; output.Value = (bool)((ComboBox)_fields["Value"]).SelectedValue; }
            else if (_selected is SetVariableNode variable)
            {
                TaskValueBinding value = ((TaskValueEditor)_fields["Binding"]).Read();
                variable.VariableName = ((ComboBox)_fields["Variable"]).SelectedItem as string ?? "";
                variable.Value = value;
            }
            else if (_selected is DelayNode delay) delay.Milliseconds = TaskEditingUi.Integer((TextBox)_fields["Milliseconds"], "延时(ms)");
        }
    }
}
