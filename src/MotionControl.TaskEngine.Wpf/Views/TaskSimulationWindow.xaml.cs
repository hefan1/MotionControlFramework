using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MotionControl.TaskEngine.Machine;
using MotionControl.TaskEngine.Wpf.Simulation;

namespace MotionControl.TaskEngine.Wpf.Views
{
    /// <summary>独立内存仿真工作台；窗口按钮只控制自己的任务会话。</summary>
    public partial class TaskSimulationWindow : Window
    {
        /// <summary>当前独占会话。</summary>
        private readonly TaskMonitorSession _session;
        /// <summary>复用的只读监控页面。</summary>
        private readonly TaskMonitorView _monitor;
        /// <summary>只刷新整机按钮状态。</summary>
        private readonly DispatcherTimer _timer;
        /// <summary>后台已收尾后允许关闭窗口。</summary>
        private bool _closed;
        /// <summary>避免关闭过程被重复发起。</summary>
        private bool _closing;
        /// <summary>当前窗口语言，不更改其他窗口的语言。</summary>
        private string _language = "zh-CN";
        /// <summary>本机配置会话可请求 Host 打开既有配置页。</summary>
        public event EventHandler<TaskReferenceEventArgs> ReferenceRequested;

        /// <summary>展示已准备的内存会话，不自动复位或启动。</summary>
        public TaskSimulationWindow(TaskMonitorSession session)
        {
            _session = session;
            InitializeComponent();
            _monitor = new TaskMonitorView(session);
            _monitor.ReferenceRequested += OpenReference;
            MonitorContent.Content = _monitor;
            foreach (string name in session.Device.Catalog.Inputs)
            {
                var input = new CheckBox { Content = name, Tag = name, IsThreeState = true,
                    IsChecked = session.Device.ReadInput(name), Margin = new Thickness(0,4,18,4) };
                input.Click += Input_Click; InputsPanel.Children.Add(input);
            }
            VariableBox.ItemsSource = session.Device.Catalog.Variables.Keys.ToList();
            if (VariableBox.Items.Count > 0) VariableBox.SelectedIndex = 0;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _timer.Tick += State_Tick; _timer.Start();
            Closing += Window_Closing;
            RefreshState();
        }

        /// <summary>所有状态说明明确标记内存仿真。</summary>
        private void RefreshState()
        {
            var state = _session.Machine.GetSnapshot().State;
            Title = _monitor.T("任务运行") + " — " + _monitor.T("内存仿真");
            MachineStateText.Text = _monitor.T("内存仿真") + " · " + _monitor.T(state.ToString());
            ResetButton.IsEnabled = !_closing && (state == TaskMachineState.NeedsReset || state == TaskMachineState.Paused || state == TaskMachineState.Faulted);
            StartButton.IsEnabled = !_closing && state == TaskMachineState.Ready;
            PauseButton.IsEnabled = !_closing && state == TaskMachineState.Running;
        }

        /// <summary>刷新状态栏，不触发控制。</summary>
        private void State_Tick(object sender, EventArgs e) { RefreshState(); }
        /// <summary>复位只检查当前会话状态，不重置已完成动作。</summary>
        private void Reset_Click(object sender, RoutedEventArgs e) { Command(_session.Machine.Reset); }
        /// <summary>只从就绪状态启动或继续。</summary>
        private void Start_Click(object sender, RoutedEventArgs e) { Command(_session.Machine.Start); }
        /// <summary>暂停全部工位并保留进度。</summary>
        private void Pause_Click(object sender, RoutedEventArgs e) { Command(_session.Machine.Pause); }

        /// <summary>统一反馈操作结果，错误保留完整上下文。</summary>
        private void Command(Action action)
        {
            try { action(); CommandFeedback.Text = _monitor.T("操作已完成"); }
            catch (Exception error) { CommandFeedback.Text = error.Message; }
            RefreshState(); _monitor.Refresh();
        }

        /// <summary>三态输入只注入内存；空值表示未知，不当成关闭。</summary>
        private void Input_Click(object sender, RoutedEventArgs e)
        {
            var box = (CheckBox)sender;
            Command(() => _session.Device.SetInput((string)box.Tag, box.IsChecked));
        }

        /// <summary>按已有类型校验后写入本窗口内存变量。</summary>
        private void WriteVariable_Click(object sender, RoutedEventArgs e)
        {
            if (VariableBox.SelectedItem is string name) Command(() => _session.Device.SetVariable(name, VariableValue.Text));
        }

        /// <summary>只允许首次运行前建立内存轴参考，避免改变已接受的运动目标。</summary>
        private void PrepareAxes_Click(object sender, RoutedEventArgs e)
        {
            Command(() =>
            {
                var snapshot = _session.Machine.GetSnapshot();
                if (snapshot.Stations.Any(station => station.Run != null) || snapshot.Initialization?.Run != null)
                    throw new InvalidOperationException(_monitor.T("运行开始后不能重建内存轴参考"));
                if (snapshot.State != TaskMachineState.NeedsReset && snapshot.State != TaskMachineState.Ready)
                    throw new InvalidOperationException(_monitor.T("请在首次启动前准备"));
                foreach (var axis in _session.Device.Catalog.Axes.Values)
                {
                    double position = Math.Max(axis.MinimumPosition ?? 0, Math.Min(0, axis.MaximumPosition ?? 0));
                    _session.Device.ConfigureAxis(axis.Name, position, true, true);
                }
            });
        }

        /// <summary>切换本窗口的静态标签及监控行文字。</summary>
        private void Language_Click(object sender, RoutedEventArgs e)
        {
            _language = _language == "zh-CN" ? "en-US" : "zh-CN";
            Resources.MergedDictionaries[1] = new ResourceDictionary { Source = new Uri("/MotionControl.TaskEngine.Wpf;component/Localization/Strings." + _language + ".xaml", UriKind.Relative) };
            _monitor.SetLanguage(_language); RefreshState();
        }

        /// <summary>示例只查看示例目录，本机配置才允许跳转已有页面。</summary>
        private void OpenReference(object sender, TaskReferenceEventArgs e)
        {
            if (!_session.IsExample && ReferenceRequested != null) { ReferenceRequested(this, e); return; }
            var window = new TaskCatalogReferenceWindow(_session.Device.Catalog, e.Page, e.Name, _monitor.T) { Owner = this };
            window.Show();
        }

        /// <summary>关闭窗口先等待整机任务退出，避免窗口消失后仍有后台工位。</summary>
        private async void Window_Closing(object sender, CancelEventArgs e)
        {
            if (_closed) return;
            e.Cancel = true;
            if (_closing) return;
            _closing = true; _timer.Stop(); IsEnabled = false;
            try { await CloseSessionAsync(); }
            catch (Exception error) { _closing = false; IsEnabled = true; _timer.Start(); CommandFeedback.Text = error.ToString(); }
        }

        /// <summary>供所属 Host 关闭前等待内存工位收尾，避免拥有者关闭跳过子窗口 Closing。</summary>
        public async System.Threading.Tasks.Task CloseSessionAsync()
        {
            if (_closed) return;
            await _session.Machine.CloseAsync();
            _closed = true; _timer.Stop(); Close();
        }
    }
}
