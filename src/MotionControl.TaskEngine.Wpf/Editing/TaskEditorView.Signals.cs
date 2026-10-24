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
                Details.Children.Add(TaskEditingUi.Label(this, "未知输入不会当作无效信号放行")); AddReference("IO映射", "@Input", "DI");
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
        /// <summary>从当前表单读回明确模型，不调用 IO 或变量运行服务。</summary>
        private void ReadSignals()
        {
            if (_selected is WaitInputNode wait) { wait.InputName = ((ComboBox)_fields["Input"]).SelectedItem as string ?? ""; wait.Expected = (bool)((ComboBox)_fields["Expected"]).SelectedValue; }
            else if (_selected is SetOutputNode output) { output.OutputName = ((ComboBox)_fields["Output"]).SelectedItem as string ?? ""; output.Value = (bool)((ComboBox)_fields["Value"]).SelectedValue; }
            else if (_selected is SetVariableNode variable) { variable.VariableName = ((ComboBox)_fields["Variable"]).SelectedItem as string ?? ""; variable.Value = ((TaskValueEditor)_fields["Binding"]).Read(); }
            else if (_selected is DelayNode delay) delay.Milliseconds = TaskEditingUi.Integer((TextBox)_fields["Milliseconds"], "延时(ms)");
        }
    }
}
