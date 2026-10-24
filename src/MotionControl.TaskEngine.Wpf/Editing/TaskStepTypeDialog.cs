using System.Windows;
using System.Windows.Controls;

namespace MotionControl.TaskEngine.Wpf.Editing
{
    /// <summary>有限步骤列表选择，不暴露厂商方法名或未接入能力。</summary>
    internal sealed class TaskStepTypeDialog : Window
    {
        /// <summary>确认后返回明确的节点类型。</summary>
        internal string SelectedKind { get; private set; }
        /// <summary>可选择步骤列表。</summary>
        private readonly ListBox _list;
        /// <summary>构造与工厂登记一致的类型清单。</summary>
        internal TaskStepTypeDialog(string language)
        {
            TaskEditingUi.SetLanguage(this, language); Title = TaskEditingUi.T(this, "选择步骤类型"); Width = 430; Height = 580;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var panel = new DockPanel { Margin = new Thickness(15) };
            var bottom = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right }; DockPanel.SetDock(bottom, Dock.Bottom);
            bottom.Children.Add(TaskEditingUi.Button(this, "取消", Cancel)); bottom.Children.Add(TaskEditingUi.Button(this, "插入", Accept)); panel.Children.Add(bottom);
            _list = new ListBox();
            foreach (string kind in new[] { "MoveAxis", "MoveAxisGroup", "Home", "Stop", "WaitInput", "SetOutput", "SetVariable", "Delay", "Sequence", "ParallelGroup", "ConditionBranch", "WaitCondition", "CallTask", "ResourceScope" })
                _list.Items.Add(new ListBoxItem { Content = TaskEditingUi.T(this, TaskEditorNodes.KindName(kind)), Tag = kind, Padding = new Thickness(8, 6, 8, 6) });
            _list.Items.Add(new ListBoxItem { Content = TaskEditingUi.T(this, "直线插补未接入"), IsEnabled = false });
            panel.Children.Add(_list); Content = panel;
        }
        /// <summary>仅在明确选中支持类型后返回。</summary>
        private void Accept(object sender, RoutedEventArgs e) { if (_list.SelectedItem is ListBoxItem item && item.Tag is string kind) { SelectedKind = kind; DialogResult = true; } }
        /// <summary>取消不产生节点。</summary>
        private void Cancel(object sender, RoutedEventArgs e) { DialogResult = false; }
    }
}
