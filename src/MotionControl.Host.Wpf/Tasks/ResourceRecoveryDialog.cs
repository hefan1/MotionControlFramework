using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MotionControl.TaskEngine.Device;
using MotionControl.TaskEngine.Resources;

namespace MotionControl.Host.Wpf.Tasks
{
    /// <summary>按单个待确认资源核查现场；不会启动设备、移动轴或切换输出。</summary>
    internal sealed class ResourceRecoveryDialog : Window
    {
        /// <summary>同一正式任务会话持有的设备端口。</summary>
        private readonly ITaskDevice _device;
        /// <summary>由调用页面核对连接、会话与停机状态。</summary>
        private readonly Func<bool> _contextValid;
        /// <summary>语言由主窗口在打开时确定。</summary>
        private readonly bool _english;
        /// <summary>明确选择的待确认资源。</summary>
        private readonly ComboBox _resources;
        /// <summary>资源来源及现场采样。</summary>
        private readonly TextBox _sampleText;
        /// <summary>操作者填写实际检查内容。</summary>
        private readonly TextBox _confirmation;
        /// <summary>明确确认已在现场检查人员、夹具、载具及物料。</summary>
        private readonly CheckBox _confirmed;
        /// <summary>显示操作结果及失败原因。</summary>
        private readonly TextBlock _status;
        /// <summary>读取当前状态按钮。</summary>
        private readonly Button _readButton;
        /// <summary>只有操作员确认后才尝试解除单个资源隔离。</summary>
        private readonly Button _recoverButton;
        /// <summary>用户曾看到的最新采样。</summary>
        private ResourceRecoverySample _sample;
        /// <summary>异步采样期间阻止关闭和重复提交。</summary>
        private bool _busy;

        /// <summary>创建独立对话框，只读取本会话的待确认资源。</summary>
        internal ResourceRecoveryDialog(ITaskDevice device, Func<bool> contextValid, bool english)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _contextValid = contextValid ?? throw new ArgumentNullException(nameof(contextValid));
            _english = english;
            Title = T("资源现场核查", "Resource inspection"); Width = 900; Height = 750;
            MinWidth = 620; MinHeight = 520; WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = (Brush)new BrushConverter().ConvertFromString("#F3F6FA");
            var root = new DockPanel { Margin = new Thickness(16) }; Content = root;
            var heading = new StackPanel(); DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
            heading.Children.Add(new TextBlock { Text = T("资源待确认 · 逐项核查", "Inspect each blocked resource"), FontSize = 19, FontWeight = FontWeights.SemiBold });
            heading.Children.Add(new TextBlock { Margin = new Thickness(0, 8, 0, 8), TextWrapping = TextWrapping.Wrap,
                Text = T("下一步：选择资源 → 读取轴与 IO → 现场核查 → 填写说明。维护责任：状态未知、输出仍有效或停止失败时，由设备维护人员按设备规程处理后重新读取。读取只确认轴已停止和 IO 可读；有任何输出有效时不允许解除。区域、人员、夹具、载具和物料是否安全须由现场人员按设备规程确认。此处不会移动轴、切换输出或启动设备。",
                    "Next: select a resource, read axes and IO, inspect the site, and record the inspection. Maintenance must resolve unknown states, active outputs or stop failures under the equipment procedure before reading again. Readback checks stopped axes and readable IO only. Recovery is blocked while any output is active. Personnel must verify the area, fixtures, carriers and workpieces using the equipment procedure. This page does not move axes, switch outputs or start equipment.") });
            _resources = new ComboBox { MinHeight = 30, Margin = new Thickness(0, 0, 0, 8) };
            _resources.SelectionChanged += Resource_SelectionChanged; heading.Children.Add(_resources);
            _readButton = new Button { Content = T("读取轴与 IO", "Read axes and IO"), Padding = new Thickness(12, 6, 12, 6), HorizontalAlignment = HorizontalAlignment.Left };
            _readButton.Click += Read_Click; heading.Children.Add(_readButton);

            var bottom = new StackPanel { Margin = new Thickness(0, 8, 0, 0) }; DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
            _confirmed = new CheckBox { Margin = new Thickness(0, 0, 0, 8), Content = new TextBlock { TextWrapping = TextWrapping.Wrap,
                Text = T("我已到现场核查人员、机构、夹具、载具和物料，并按设备规程确认此资源区域可以解除隔离。", "I have inspected personnel, machinery, fixtures, carriers and workpieces and verified this area can be released under the equipment procedure.") } };
            _confirmed.Checked += Confirmation_Changed; _confirmed.Unchecked += Confirmation_Changed; bottom.Children.Add(_confirmed);
            bottom.Children.Add(new TextBlock { Text = T("填写本次现场核查说明：", "Describe the inspection performed:"), Margin = new Thickness(0, 0, 0, 4) });
            _confirmation = new TextBox { MinHeight = 32, TextWrapping = TextWrapping.Wrap }; _confirmation.TextChanged += Confirmation_Changed; bottom.Children.Add(_confirmation);
            _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Firebrick, Margin = new Thickness(0, 8, 0, 8) }; bottom.Children.Add(_status);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            _recoverButton = new Button { Content = T("确认并解除当前资源", "Confirm and release selected resource"), Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 8, 0) };
            _recoverButton.Click += Recover_Click; buttons.Children.Add(_recoverButton);
            var close = new Button { Content = T("返回", "Back"), Padding = new Thickness(12, 7, 12, 7) }; close.Click += Close_Click; buttons.Children.Add(close); bottom.Children.Add(buttons);
            _sampleText = new TextBox { Margin = new Thickness(0, 10, 0, 0), IsReadOnly = true, TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
            root.Children.Add(_sampleText);
            Closing += Dialog_Closing;
            LoadResources(); UpdateControls();
        }

        /// <summary>只列出当前会话仍待确认的资源，不自动选择或释放。</summary>
        private void LoadResources()
        {
            _resources.ItemsSource = _device.ReadResources().Where(item => item.State == TaskResourceState.Blocked)
                .Select(item => new ResourceChoice(item)).ToList();
            _resources.SelectedIndex = -1;
        }

        /// <summary>选中另一条资源时废弃之前的采样与人工确认。</summary>
        private void Resource_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _sample = null;
            if (_sampleText != null) _sampleText.Text = string.Empty;
            if (_confirmed != null) _confirmed.IsChecked = false;
            if (_confirmation != null) _confirmation.Text = string.Empty;
            if (_status != null) _status.Text = string.Empty;
            UpdateControls();
        }

        /// <summary>仅在同一已停止会话中读取完整状态；后台读取不阻塞界面。</summary>
        private async void Read_Click(object sender, RoutedEventArgs e)
        {
            var selected = _resources.SelectedItem as ResourceChoice;
            if (_busy || selected == null) return;
            _sample = null; _confirmed.IsChecked = false; _confirmation.Text = string.Empty;
            _status.Text = string.Empty; _sampleText.Text = string.Empty; _busy = true; UpdateControls();
            try
            {
                if (!_contextValid()) throw new InvalidOperationException("设备未连接、任务尚未终止或会话已变化；保留隔离。");
                var sample = await Task.Run(() => ResourceRecoverySample.Capture(_device, selected.Id, _english));
                if (!_contextValid()) throw new InvalidOperationException("读取期间设备或会话状态发生变化；保留隔离。");
                _sample = sample; _sampleText.Text = sample.Description;
                if (sample.ActiveOutputs.Count != 0)
                {
                    _status.Foreground = Brushes.Firebrick;
                    _status.Text = T("输出仍有效：", "Active outputs: ") + string.Join("、", sample.ActiveOutputs)
                        + T("。当前不能解除隔离；请按设备规程核查，系统不会自动切换输出。", ". Isolation cannot be released. Follow the equipment procedure; outputs will not be switched automatically.");
                }
                else
                {
                    _status.Foreground = Brushes.DarkSlateGray;
                    _status.Text = T("请核对采样并检查实际现场；命名区域的安全状态不能由采样自动判断。", "Review the samples and the physical area; named-area safety cannot be inferred automatically.");
                }
            }
            catch (Exception error) { ShowFailure(error.Message); }
            finally { _busy = false; UpdateControls(); }
        }

        /// <summary>采样后必须明确勾选并填写真实核查说明。</summary>
        private void Confirmation_Changed(object sender, RoutedEventArgs e) { UpdateControls(); }

        /// <summary>再次读取并比较全部采样，再调用设备既有的双次退出核查；失败始终保留隔离。</summary>
        private async void Recover_Click(object sender, RoutedEventArgs e)
        {
            var selected = _resources.SelectedItem as ResourceChoice;
            if (_busy || selected == null || _sample == null || _sample.ActiveOutputs.Count != 0 || _confirmed.IsChecked != true || string.IsNullOrWhiteSpace(_confirmation.Text)) return;
            var previous = _sample;
            string confirmation = _confirmation.Text.Trim();
            _busy = true; UpdateControls();
            try
            {
                if (!_contextValid()) throw new InvalidOperationException("设备未连接、任务尚未终止或会话已变化；保留隔离。");
                string result = await Task.Run(() =>
                {
                    string reason;
                    if (!previous.TryConfirm(_device, true, confirmation, _contextValid, out reason)) throw new InvalidOperationException(reason);
                    return reason;
                });
                _sample = null; _sampleText.Text = string.Empty; _confirmed.IsChecked = false; _confirmation.Text = string.Empty;
                LoadResources(); _status.Foreground = Brushes.DarkGreen;
                _status.Text = result + T(" 请逐项核对剩余资源；全部处理后返回任务页重试关闭。", " Inspect remaining resources. Retry closing from the task page after all are handled.");
            }
            catch (Exception error) { _sample = null; _sampleText.Text = string.Empty; _confirmed.IsChecked = false; ShowFailure(error.Message); }
            finally { _busy = false; UpdateControls(); }
        }

        /// <summary>失败显示具体原因，保留原资源及任务控制权。</summary>
        private void ShowFailure(string message) { _status.Foreground = Brushes.Firebrick; _status.Text = message; }
        /// <summary>防止同时执行采样和确认，确认内容不完整时禁止提交。</summary>
        private void UpdateControls()
        {
            if (_readButton == null || _recoverButton == null) return;
            _resources.IsEnabled = !_busy; _readButton.IsEnabled = !_busy && _resources.SelectedItem != null;
            _confirmed.IsEnabled = !_busy; _confirmation.IsEnabled = !_busy;
            _recoverButton.IsEnabled = !_busy && _resources.SelectedItem != null && _sample != null
                && _sample.ActiveOutputs.Count == 0 && _confirmed.IsChecked == true && !string.IsNullOrWhiteSpace(_confirmation.Text);
        }
        /// <summary>完成后台操作前不能关闭，避免不确定的确认结果。</summary>
        private void Dialog_Closing(object sender, System.ComponentModel.CancelEventArgs e) { if (_busy) e.Cancel = true; }
        /// <summary>返回不会解除隔离或关闭任务会话。</summary>
        private void Close_Click(object sender, RoutedEventArgs e) { Close(); }
        /// <summary>按窗口语言选择提示。</summary>
        private string T(string chinese, string english) { return _english ? english : chinese; }

        /// <summary>资源选择项保留唯一申请编号，显示关联任务与步骤。</summary>
        private sealed class ResourceChoice
        {
            /// <summary>用于确认的申请编号。</summary>
            internal string Id { get; }
            /// <summary>操作者识别的简洁描述。</summary>
            private readonly string _name;
            /// <summary>复制资源快照名称与编号。</summary>
            internal ResourceChoice(TaskResourceSnapshot item)
            { Id = item.RequestId; _name = item.TaskName + " · " + item.NodeName + " · " + string.Join("、", item.Resources) + " · " + item.RequestId; }
            /// <summary>作为组合框选项显示。</summary>
            public override string ToString() { return _name; }
        }
    }
}
