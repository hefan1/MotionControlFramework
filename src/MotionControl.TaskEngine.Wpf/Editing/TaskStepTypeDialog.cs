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
        /// <summary>只有选中可用步骤类型后才允许插入。</summary>
        private readonly Button _insertButton;
        /// <summary>构造与工厂登记一致的类型清单。</summary>
        internal TaskStepTypeDialog(string language)
        {
            TaskEditingUi.SetLanguage(this, language); Title = TaskEditingUi.T(this, "选择步骤类型"); Width = 430; Height = 580;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var panel = new DockPanel { Margin = new Thickness(15) };
            var bottom = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right }; DockPanel.SetDock(bottom, Dock.Bottom);
            bottom.Children.Add(TaskEditingUi.Button(this, "取消", Cancel));
            _insertButton = TaskEditingUi.Button(this, "插入", Accept);
            _insertButton.IsEnabled = false;
            bottom.Children.Add(_insertButton);
            panel.Children.Add(bottom);
            var hint = new TextBlock
            {
                Text = TaskEditingUi.T(this, "先选择步骤类型，再点插入"),
                TextWrapping = TextWrapping.Wrap,
                Foreground = SystemColors.GrayTextBrush,
                Margin = new Thickness(0, 0, 0, 8)
            };
            DockPanel.SetDock(hint, Dock.Top);
            panel.Children.Add(hint);
            _list = new ListBox();
            foreach (string kind in new[] { "MoveAxis", "MoveAxisGroup", "Home", "Stop", "WaitAxisPosition", "WaitInput", "WaitOutput", "ReadInput", "ReadInputWord", "WaitExclusiveInputs", "SetOutput", "SetVariable", "WriteRecord", "Delay", "Sequence", "ParallelGroup", "ConditionBranch", "WaitCondition", "CallTask", "ResourceScope" })
                _list.Items.Add(new ListBoxItem { Content = TaskEditingUi.T(this, TaskEditorNodes.KindName(kind)), Tag = kind, Padding = new Thickness(8, 6, 8, 6) });
            _list.Items.Add(new ListBoxItem { Content = TaskEditingUi.T(this, "直线插补未接入"), IsEnabled = false });
            _list.SelectionChanged += SelectionChanged;
            panel.Children.Add(_list); Content = panel;
        }
        /// <summary>选中一个可用类型后再启用插入，避免无选择时点击没有反馈。</summary>
        private void SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var selectedItem = _list.SelectedItem as ListBoxItem;
            _insertButton.IsEnabled = selectedItem != null && selectedItem.IsEnabled && selectedItem.Tag is string;
        }
        /// <summary>仅在明确选中支持类型后返回。</summary>
        private void Accept(object sender, RoutedEventArgs e)
        {
            var selectedItem = _list.SelectedItem as ListBoxItem;
            if (selectedItem == null || !(selectedItem.Tag is string kind))
            {
                _list.Focus();
                return;
            }

            SelectedKind = kind;
            DialogResult = true;
        }
        /// <summary>取消不产生节点。</summary>
        private void Cancel(object sender, RoutedEventArgs e) { DialogResult = false; }
    }
}
