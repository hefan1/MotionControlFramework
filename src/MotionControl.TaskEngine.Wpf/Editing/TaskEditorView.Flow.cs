using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MotionControl.TaskEngine.Definitions;

namespace MotionControl.TaskEngine.Wpf.Editing
{
    /// <summary>顺序、并行、分支、子任务与资源的具体字段。</summary>
    public partial class TaskEditorView
    {
        /// <summary>容器显示结构操作说明，其余类型显示明确字段。</summary>
        private void BuildFlow()
        {
            if (_selected is SequenceNode) Details.Children.Add(TaskEditingUi.Label(this, "选中顺序段后新增步骤，子步骤按列表顺序执行"));
            else if (_selected is ParallelGroupNode) Details.Children.Add(TaskEditingUi.Label(this, "新增步骤会建立一个顺序分支，全部分支完成后汇合"));
            else if (_selected is ConditionBranchNode branch)
            {
                AddField("Condition", "条件判断", new TaskConditionEditor(_catalog, branch.Condition, _language));
                AddField("FalseAlarms", "条件不成立关联报警（可多选）",
                    new TaskAlarmSelector(_catalog.AlarmDefinitions, branch.WhenFalseAlarmCodes,
                        code => T("报警库不存在") + code, T("当前设备报警库为空，请先维护报警代码"),
                        T("已关联 {0} 条报警"), T("停机报警"), T("非停机报警"), T("检查："), T("恢复：")));
                Details.Children.Add(TaskEditingUi.Label(this, "选中成立或不成立分支的顺序段后添加动作"));
                Details.Children.Add(TaskEditingUi.Label(this, "任务报警关联规则"));
            }
            else if (_selected is WaitConditionNode wait)
            {
                AddField("Condition", "等待成立的条件", new TaskConditionEditor(_catalog, wait.Condition, _language));
                AddField("TimeoutAlarms", "等待超时关联报警（可多选）",
                    new TaskAlarmSelector(_catalog.AlarmDefinitions, wait.TimeoutAlarmCodes,
                        code => T("报警库不存在") + code, T("当前设备报警库为空，请先维护报警代码"),
                        T("已关联 {0} 条报警"), T("停机报警"), T("非停机报警"), T("检查："), T("恢复：")));
                Details.Children.Add(TaskEditingUi.Label(this, "任务报警关联规则"));
            }
            else if (_selected is CallTaskNode call)
            {
                AddField("Task", "子任务标识", TaskEditingUi.Names(_others.Select(task => task.TaskId), call.TaskId));
                Details.Children.Add(TaskEditingUi.Label(this, "不能调用自己，递归引用会在检查时拒绝"));
            }
            else if (_selected is ResourceScopeNode scope)
            {
                var resources = new StackPanel();
                foreach (string name in _catalog.Resources.Concat(scope.Resources).Distinct(System.StringComparer.OrdinalIgnoreCase))
                    resources.Children.Add(new CheckBox { Content = name, Tag = name, IsChecked = scope.Resources.Contains(name, System.StringComparer.OrdinalIgnoreCase), Margin = new Thickness(3) });
                AddField("Resources", "整组申请的资源", resources);
                AddField("Entry", "进入联锁", new TaskInterlockEditor(_catalog, scope.EntryInterlock, _language));
                AddField("Exit", "退出联锁", new TaskInterlockEditor(_catalog, scope.ExitInterlock, _language));
                if (scope.ExitCondition != null)
                {
                    var useLegacy = new CheckBox { Content = T("使用旧版退出变量条件"), IsChecked = true, Margin = new Thickness(0, 8, 0, 4) };
                    AddField("UseLegacy", "旧版兼容", useLegacy);
                    AddField("Legacy", "旧版退出条件", new TaskConditionEditor(_catalog, scope.ExitCondition, _language));
                    useLegacy.Checked += LegacyVisibility; useLegacy.Unchecked += LegacyVisibility;
                }
                Details.Children.Add(TaskEditingUi.Label(this, "进入条件不等于持续防碰撞；退出确认后才让出区域"));
            }
            else Details.Children.Add(TaskEditingUi.Label(this, "直线插补未接入，原定义保留，只能查看"));
        }
        /// <summary>旧版退出条件保留兼容，不要求新任务使用。</summary>
        private void LegacyVisibility(object sender, RoutedEventArgs e)
        { _fields["Legacy"].Visibility = ((CheckBox)sender).IsChecked == true ? Visibility.Visible : Visibility.Collapsed; }
        /// <summary>具体读回各容器参数，子步骤结构由列表操作处理。</summary>
        private void ReadFlow()
        {
            if (_selected is ConditionBranchNode branch)
            {
                branch.Condition = ((TaskConditionEditor)_fields["Condition"]).Read();
                branch.WhenFalseAlarmCodes = ((TaskAlarmSelector)_fields["FalseAlarms"]).SelectedCodes.ToList();
            }
            else if (_selected is WaitConditionNode wait)
            {
                wait.Condition = ((TaskConditionEditor)_fields["Condition"]).Read();
                wait.TimeoutAlarmCodes = ((TaskAlarmSelector)_fields["TimeoutAlarms"]).SelectedCodes.ToList();
            }
            else if (_selected is CallTaskNode call) call.TaskId = ((ComboBox)_fields["Task"]).SelectedItem as string ?? "";
            else if (_selected is ResourceScopeNode scope)
            {
                var entry = ((TaskInterlockEditor)_fields["Entry"]).Read(); var exit = ((TaskInterlockEditor)_fields["Exit"]).Read();
                FrameworkElement legacyCheck;
                var legacy = _fields.TryGetValue("UseLegacy", out legacyCheck) && ((CheckBox)legacyCheck).IsChecked == true ? ((TaskConditionEditor)_fields["Legacy"]).Read() : null;
                scope.Resources = ((StackPanel)_fields["Resources"]).Children.OfType<CheckBox>().Where(box => box.IsChecked == true).Select(box => (string)box.Tag).ToList();
                scope.EntryInterlock = entry; scope.ExitInterlock = exit; scope.ExitCondition = legacy;
            }
        }
    }
}
