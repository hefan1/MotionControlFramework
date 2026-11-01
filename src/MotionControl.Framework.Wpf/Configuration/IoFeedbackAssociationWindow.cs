using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MotionControl.Framework.Contracts.Configuration;
using MotionControl.Framework.Runtime.Configuration;

namespace MotionControl.Framework.Wpf.Configuration
{
    /// <summary>从已保存的 IO 映射选择输出和多个反馈输入；保存仅影响监控展示。</summary>
    internal sealed class IoFeedbackAssociationWindow : Window
    {
        /// <summary>设备反馈关联文件的读写入口。</summary>
        private readonly IoFeedbackAssociationStore _store;
        /// <summary>本次编辑的独立副本，取消时不写文件。</summary>
        private readonly IoFeedbackAssociationConfiguration _configuration;
        /// <summary>已保存的有效 DI 名称。</summary>
        private readonly List<string> _inputs;
        /// <summary>已保存的有效 DO 名称。</summary>
        private readonly List<string> _outputs;
        /// <summary>选择当前需要编辑的输出。</summary>
        private readonly ComboBox _outputSelector;
        /// <summary>当前输出可选择的输入复选框。</summary>
        private readonly StackPanel _inputPanel;
        /// <summary>选中输出和错误信息的说明位置。</summary>
        private readonly TextBlock _status;
        /// <summary>当前复选框所属的输出；切换前先写回内存副本。</summary>
        private string _currentOutput;

        /// <summary>读取已保存的映射与关联；没有关联文件时从空配置开始。</summary>
        internal IoFeedbackAssociationWindow(IoFeedbackAssociationStore store,
            IEnumerable<DigitalIoHardwareDefinition> inputs, IEnumerable<DigitalIoHardwareDefinition> outputs,
            string selectedOutput)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _configuration = _store.Load();
            _inputs = GetNames(inputs);
            _outputs = GetNames(outputs);
            Title = Text("IO反馈关联", "关联反馈");
            Width = 570;
            Height = 600;
            MinWidth = 420;
            MinHeight = 400;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Color.FromRgb(243, 246, 250));

            var layout = new DockPanel { Margin = new Thickness(16) };
            Content = layout;
            var header = new StackPanel();
            DockPanel.SetDock(header, Dock.Top);
            layout.Children.Add(header);
            header.Children.Add(new TextBlock { Text = Text("IO反馈标题", "为输出选择反馈输入"), FontSize = 17, FontWeight = FontWeights.SemiBold });
            header.Children.Add(new TextBlock
            {
                Text = Text("IO反馈说明", "依据已保存的 IO 映射；反馈显示实际输入状态，不会自动改变输入。"),
                Margin = new Thickness(0, 8, 0, 10), TextWrapping = TextWrapping.Wrap
            });
            header.Children.Add(new TextBlock { Text = Text("IO反馈输出", "输出") });
            _outputSelector = new ComboBox { Margin = new Thickness(0, 4, 0, 8), MinHeight = 30 };
            header.Children.Add(_outputSelector);
            header.Children.Add(new TextBlock { Text = Text("IO反馈输入", "反馈输入（可多选）") });
            _status = new TextBlock { Margin = new Thickness(0, 7, 0, 7), TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(154, 78, 27)) };
            header.Children.Add(_status);

            var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0) };
            DockPanel.SetDock(footer, Dock.Bottom);
            layout.Children.Add(footer);
            var clear = new Button { Content = Text("IO反馈清除", "清除当前关联"), MinWidth = 110, Height = 32, Margin = new Thickness(0, 0, 8, 0) };
            clear.Click += Clear_Click;
            footer.Children.Add(clear);
            var save = new Button { Content = Text("保存", "保存"), MinWidth = 80, Height = 32, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            save.Click += Save_Click;
            footer.Children.Add(save);
            var cancel = new Button { Content = Text("取消", "取消"), MinWidth = 80, Height = 32, IsCancel = true };
            footer.Children.Add(cancel);
            _inputPanel = new StackPanel { Margin = new Thickness(8) };
            layout.Children.Add(new ScrollViewer { Content = _inputPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(217, 224, 232)), BorderThickness = new Thickness(1) });

            foreach (string name in _outputs) _outputSelector.Items.Add(name);
            foreach (IoFeedbackAssociation item in _configuration.Associations)
                if (!ContainsName(_outputs, item.OutputName)) _outputSelector.Items.Add(item.OutputName);
            _outputSelector.SelectionChanged += OutputSelector_SelectionChanged;
            string initialOutput = _outputs.FirstOrDefault(name => string.Equals(name, selectedOutput, StringComparison.OrdinalIgnoreCase));
            if (initialOutput != null) _outputSelector.SelectedItem = initialOutput;
            else if (_outputSelector.Items.Count > 0) _outputSelector.SelectedIndex = 0;
            else _status.Text = Text("IO反馈无输出", "请先在 IO 映射中保存输出。");
        }

        /// <summary>从当前应用语言资源读取文字；独立页面检查没有资源时使用中文后备文本。</summary>
        private static string Text(string key, string fallback)
        {
            return Application.Current?.TryFindResource(key) as string ?? fallback;
        }

        /// <summary>提取去重的已映射名称，保留原始显示顺序。</summary>
        private static List<string> GetNames(IEnumerable<DigitalIoHardwareDefinition> points)
        {
            return (points ?? Enumerable.Empty<DigitalIoHardwareDefinition>())
                .Where(point => point != null && !string.IsNullOrWhiteSpace(point.Name))
                .Select(point => point.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>使用不区分大小写的逻辑名称匹配。</summary>
        private static bool ContainsName(IEnumerable<string> names, string name)
        {
            return names.Any(item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>找到输出关系；尚未配置时返回空。</summary>
        private IoFeedbackAssociation FindAssociation(string output)
        {
            return _configuration.Associations.FirstOrDefault(item =>
                string.Equals(item.OutputName, output, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>切换输出前保存当前复选框到内存，再展示目标输出的反馈。</summary>
        private void OutputSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            CaptureCurrent();
            _currentOutput = _outputSelector.SelectedItem as string;
            ShowCurrent();
        }

        /// <summary>把当前选择写入编辑副本；零个输入时移除该关系。</summary>
        private void CaptureCurrent()
        {
            if (string.IsNullOrWhiteSpace(_currentOutput)) return;
            var selected = _inputPanel.Children.OfType<CheckBox>().Where(box => box.IsChecked == true)
                .Select(box => (string)box.Tag).ToList();
            IoFeedbackAssociation existing = FindAssociation(_currentOutput);
            if (existing != null) _configuration.Associations.Remove(existing);
            if (selected.Count > 0)
                _configuration.Associations.Add(new IoFeedbackAssociation { OutputName = _currentOutput, InputNames = selected });
        }

        /// <summary>展示有效输入及已失效的引用，允许取消勾选后修复。</summary>
        private void ShowCurrent()
        {
            _inputPanel.Children.Clear();
            if (_currentOutput == null) return;
            IoFeedbackAssociation item = FindAssociation(_currentOutput);
            var selected = item?.InputNames ?? new List<string>();
            var missing = selected.Where(name => !ContainsName(_inputs, name)).ToList();
            bool missingOutput = !ContainsName(_outputs, _currentOutput);
            _status.Text = missingOutput ? Text("IO反馈输出失效", "此输出已不在已保存映射中，请清除关联。")
                : missing.Count > 0 ? Text("IO反馈输入失效", "以下标记“未找到”的输入已失效，请取消勾选。") : "";
            foreach (string input in _inputs) AddInput(input, ContainsName(selected, input), false);
            foreach (string input in missing) AddInput(input, true, true);
        }

        /// <summary>增加一个反馈输入选项，并清晰标示已失效的名称。</summary>
        private void AddInput(string name, bool selected, bool missing)
        {
            _inputPanel.Children.Add(new CheckBox { Content = missing ? name + Text("IO反馈未找到", "（未找到）") : name,
                Tag = name, IsChecked = selected, Margin = new Thickness(4, 5, 4, 5), FontSize = 13 });
        }

        /// <summary>取消勾选当前输出全部反馈，不直接写入文件。</summary>
        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            foreach (CheckBox box in _inputPanel.Children.OfType<CheckBox>()) box.IsChecked = false;
        }

        /// <summary>保存前逐项检查引用仍属于已保存映射；失败时保留窗口和编辑内容。</summary>
        private void Save_Click(object sender, RoutedEventArgs e)
        {
            CaptureCurrent();
            foreach (IoFeedbackAssociation item in _configuration.Associations)
            {
                if (!ContainsName(_outputs, item.OutputName))
                {
                    _status.Text = string.Format(Text("IO反馈缺少输出", "输出“{0}”已不存在，请选择它并清除关联。"), item.OutputName);
                    return;
                }
                string missing = item.InputNames.FirstOrDefault(name => !ContainsName(_inputs, name));
                if (missing != null)
                {
                    _status.Text = string.Format(Text("IO反馈缺少输入", "输出“{0}”的反馈“{1}”已不存在，请取消勾选。"), item.OutputName, missing);
                    return;
                }
            }
            try
            {
                _store.Save(_configuration);
                DialogResult = true;
            }
            catch (Exception error)
            {
                _status.Text = string.Format(Text("IO反馈保存失败", "保存失败：{0}"), error.Message);
            }
        }
    }
}
