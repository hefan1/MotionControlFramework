using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MotionControl.Framework.Wpf.Configuration;
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
        /// <summary>上一次已完成的上料周期数，用于在下一周期到来前撤下当前产品信号。</summary>
        private long _observedLoadCycles;
        /// <summary>下一件物料到达供料位的时间；暂停时保持等待，不继续生成物料。</summary>
        private DateTime? _nextProductArrivalUtc;
        /// <summary>上一次观察到的夹紧输出；本机目录没有该输出时保持未知。</summary>
        private bool? _lastClampOutput;
        /// <summary>夹紧输出变化后反馈夹紧传感器的时间。</summary>
        private DateTime _clampFeedbackDueUtc;
        /// <summary>上料、机构安全和夹紧输入的手动控件；自动信号开启时由页面状态驱动。</summary>
        private readonly System.Collections.Generic.List<ComboBox> _productionInputSelectors = new System.Collections.Generic.List<ComboBox>();
        /// <summary>最近一次成功修改的逻辑输入名，用于显示操作员反馈。</summary>
        private string _lastInputFeedbackName;
        /// <summary>最近一次成功修改的输入状态，空值表示未知。</summary>
        private bool? _lastInputFeedbackValue;
        /// <summary>当前反馈是否需要显示最近一次输入状态变更。</summary>
        private bool _showInputStateFeedback;
        /// <summary>最近一次成功写入的流程变量名，用于明确显示写入反馈。</summary>
        private string _lastVariableFeedbackName;
        /// <summary>最近一次成功写入的流程变量值，用于明确显示写入反馈。</summary>
        private string _lastVariableFeedbackValue;
        /// <summary>当前反馈是否需要显示最近一次流程变量写入。</summary>
        private bool _showVariableFeedback;
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
            var loadStation = session.Machine.GetSnapshot().Stations.FirstOrDefault(station => station.StationId == "load");
            _observedLoadCycles = loadStation?.CompletedCycles ?? 0;
            // 仅当当前设备目录确实包含示例夹紧输出时读取，普通任务不依赖示例 IO 名称。
            _lastClampOutput = session.Device.Catalog.Outputs.Any(name =>
                string.Equals(name, "夹紧输出", StringComparison.OrdinalIgnoreCase))
                ? session.Device.ReadOutput("夹紧输出")
                : (bool?)null;
            _clampFeedbackDueUtc = DateTime.UtcNow;
            AutomaticProductionSignalsCheckBox.Visibility = session.IsExample ? Visibility.Visible : Visibility.Collapsed;
            foreach (string name in session.Device.Catalog.Inputs)
            {
                if (IsControlButtonInput(name, session.ControlInputConfiguration))
                {
                    var button = new Button { Content = name, Tag = name, MinWidth = 86,
                        Margin = new Thickness(0, 4, 8, 4) };
                    button.SetResourceReference(FrameworkElement.ToolTipProperty, "任务监控.按钮输入提示");
                    button.Click += ControlButton_Click;
                    InputsPanel.Children.Add(button);
                    continue;
                }

                bool? inputValue = session.Device.ReadInput(name);
                var input = new ComboBox { Tag = name, Width = 96, Height = 30,
                    Margin = new Thickness(0), Padding = new Thickness(6, 2, 6, 2),
                    Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(202, 213, 226)),
                    Foreground = new SolidColorBrush(Color.FromRgb(38, 56, 77)),
                    VerticalContentAlignment = VerticalAlignment.Center };
                SetInputStateOptions(input, inputValue);
                AutomationProperties.SetName(input, name);
                input.SetResourceReference(FrameworkElement.ToolTipProperty, "任务监控.普通输入状态提示");
                input.SelectionChanged += InputState_SelectionChanged;
                if (name == "产品到位" || name == "上料机构安全位" || name == "夹紧到位")
                    _productionInputSelectors.Add(input);

                var inputRow = new StackPanel { Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 14, 4) };
                var inputLabel = new Label { Content = name, Target = input, Padding = new Thickness(0),
                    Margin = new Thickness(0, 0, 6, 0), VerticalContentAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(Color.FromRgb(64, 83, 106)), FontSize = 13 };
                inputRow.Children.Add(inputLabel);
                inputRow.Children.Add(input);
                InputsPanel.Children.Add(inputRow);
            }
            UpdateProductionInputControlAvailability();
            VariableBox.ItemsSource = session.Device.Catalog.Variables.Keys.ToList();
            if (VariableBox.Items.Count > 0) VariableBox.SelectedIndex = 0;
            VariableBox.SelectionChanged += VariableBox_SelectionChanged;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _timer.Tick += State_Tick; _timer.Start();
            Closing += Window_Closing;
            RefreshState();
            RefreshVariableCurrentValue();
        }

        /// <summary>刷新流程状态和按钮权限；暂停后必须复位，再从保留步骤继续。</summary>
        private void RefreshState()
        {
            var state = _session.Machine.GetSnapshot().State;
            Title = _monitor.T("流程检查");
            MachineStateText.Text = _monitor.T("设备状态") + " · " + _monitor.T(state.ToString());
            ResetButton.IsEnabled = !_closing && (state == TaskMachineState.NeedsReset || state == TaskMachineState.Paused || state == TaskMachineState.Faulted);
            StartButton.IsEnabled = !_closing && state == TaskMachineState.Ready;
            PauseButton.IsEnabled = !_closing && state == TaskMachineState.Running;
        }

        /// <summary>更新内置生产信号并刷新页面状态，不触发控制卡操作。</summary>
        private void State_Tick(object sender, EventArgs e)
        {
            UpdateAutomaticProductionInputs();
            RefreshState();
            RefreshInputStateSelectors();
            RefreshVariableCurrentValue();
        }

        /// <summary>开关自动工艺信号响应；关闭后可在面板中手动设置输入状态。</summary>
        private void AutomaticProductionSignals_Click(object sender, RoutedEventArgs e)
        {
            UpdateProductionInputControlAvailability();
        }

        /// <summary>自动供料按周期更新产品到位信号，夹紧传感器在夹紧输出变化后延时反馈。</summary>
        private void UpdateAutomaticProductionInputs()
        {
            if (!_session.IsExample || AutomaticProductionSignalsCheckBox.IsChecked != true) return;

            var machine = _session.Machine.GetSnapshot();
            var loadStation = machine.Stations.FirstOrDefault(station => station.StationId == "load");
            if (loadStation != null && loadStation.CompletedCycles > _observedLoadCycles)
            {
                _observedLoadCycles = loadStation.CompletedCycles;
                if (_session.Device.ReadInput("产品到位") != false)
                    _session.Device.SetInput("产品到位", false);
                _nextProductArrivalUtc = DateTime.UtcNow.AddMilliseconds(1000);
            }

            if (machine.State == TaskMachineState.Running
                && _nextProductArrivalUtc.HasValue
                && DateTime.UtcNow >= _nextProductArrivalUtc.Value)
            {
                if (_session.Device.ReadInput("产品到位") != true)
                    _session.Device.SetInput("产品到位", true);
                _nextProductArrivalUtc = null;
            }

            bool? clampOutput = _session.Device.ReadOutput("夹紧输出");
            if (!clampOutput.HasValue) return;

            if (clampOutput != _lastClampOutput)
            {
                _lastClampOutput = clampOutput;
                _clampFeedbackDueUtc = DateTime.UtcNow.AddMilliseconds(300);
            }

            if (DateTime.UtcNow >= _clampFeedbackDueUtc
                && _session.Device.ReadInput("夹紧到位") != clampOutput)
                _session.Device.SetInput("夹紧到位", clampOutput);
        }

        /// <summary>自动信号开启时锁定由场景驱动的输入，关闭后恢复操作员手动选择。</summary>
        private void UpdateProductionInputControlAvailability()
        {
            bool automatic = _session.IsExample && AutomaticProductionSignalsCheckBox.IsChecked == true;
            foreach (ComboBox selector in _productionInputSelectors)
                selector.IsEnabled = !automatic;
        }
        /// <summary>复位后提示设备就绪，可开始生产或继续保留步骤。</summary>
        private void Reset_Click(object sender, RoutedEventArgs e) { Command(_session.Machine.Reset, "复位后就绪提示"); }
        /// <summary>就绪后启动自动运行。</summary>
        private void Start_Click(object sender, RoutedEventArgs e) { Command(_session.Machine.Start, "启动运行提示"); }
        /// <summary>暂停全部工位并保留进度，同时说明恢复顺序。</summary>
        private void Pause_Click(object sender, RoutedEventArgs e) { Command(_session.Machine.Pause, "暂停后恢复提示"); }

        /// <summary>把现场控制 DI 作为瞬时按钮按下并自动释放，避免把启动、停止或复位信号留在有效状态。</summary>
        private void ControlButton_Click(object sender, RoutedEventArgs e)
        {
            var button = (Button)sender;
            string inputName = (string)button.Tag;
            Command(() =>
            {
                _session.Device.SetInput(inputName, true);
                try
                {
                    ExecuteMappedButtonPress(inputName);
                }
                finally
                {
                    _session.Device.SetInput(inputName, false);
                }
            }, ControlButtonSuccessKey(inputName));
        }

        /// <summary>统一显示操作结果；成功返回真，失败保留错误上下文并返回假。</summary>
        private bool Command(Action action, string successMessageKey = null)
        {
            _showInputStateFeedback = false;
            _showVariableFeedback = false;
            bool succeeded;
            try
            {
                action();
                // 成功反馈保持动态资源引用，切换页面语言时同步更新。
                CommandFeedback.SetResourceReference(TextBlock.TextProperty,
                    "任务监控." + (successMessageKey ?? "操作已完成"));
                CommandFeedback.Foreground = new SolidColorBrush(Color.FromRgb(22, 130, 75));
                succeeded = true;
            }
            catch (Exception error)
            {
                CommandFeedback.Text = _monitor.T("操作失败") + "：" + error.Message;
                CommandFeedback.Foreground = new SolidColorBrush(Color.FromRgb(180, 35, 24));
                succeeded = false;
            }
            RefreshState();
            // 监控视图已有 200 ms 采样周期；命令回调不再同步重建步骤树，让按钮反馈先返回界面。
            RefreshVariableCurrentValue();
            return succeeded;
        }

        /// <summary>按操作员选定的有效、无效或未知状态更新内存输入；有效状态会触发已映射的按钮动作。</summary>
        private void InputState_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var selector = (ComboBox)sender;
            if (selector.SelectedIndex < 0) return;

            string inputName = (string)selector.Tag;
            bool? nextValue = selector.SelectedIndex == 0 ? true
                : selector.SelectedIndex == 1 ? false
                : (bool?)null;
            if (_session.Device.ReadInput(inputName) == nextValue) return;

            bool succeeded = Command(() =>
            {
                _session.Device.SetInput(inputName, nextValue);
                if (nextValue == true) ExecuteMappedButtonPress(inputName);
            }, "输入状态已更新");

            if (succeeded)
            {
                _lastInputFeedbackName = inputName;
                _lastInputFeedbackValue = nextValue;
                _showInputStateFeedback = true;
                RefreshInputStateFeedback();
            }

            bool? currentValue = _session.Device.ReadInput(inputName);
            if (currentValue != nextValue) selector.SelectedIndex = InputStateIndex(currentValue);
        }

        /// <summary>按当前页面语言显示最近修改的逻辑输入名和状态。</summary>
        private void RefreshInputStateFeedback()
        {
            if (!_showInputStateFeedback || string.IsNullOrWhiteSpace(_lastInputFeedbackName)) return;

            string stateKey = _lastInputFeedbackValue == true ? "有效"
                : _lastInputFeedbackValue == false ? "无效"
                : "未知";
            bool faultLatched = _session.Machine.GetSnapshot().State == TaskMachineState.Faulted;
            string feedbackKey = faultLatched ? "故障暂停输入更新格式" : "输入状态更新格式";
            CommandFeedback.Text = string.Format(_monitor.T(feedbackKey),
                _lastInputFeedbackName, _monitor.T(stateKey));
            CommandFeedback.Foreground = new SolidColorBrush(faultLatched
                ? Color.FromRgb(166, 94, 0) : Color.FromRgb(22, 130, 75));
        }

        /// <summary>为输入状态下拉框设置三种可直接选择的状态，并选中当前内存值。</summary>
        private void SetInputStateOptions(ComboBox selector, bool? value)
        {
            selector.Items.Clear();
            selector.Items.Add(_monitor.T("有效"));
            selector.Items.Add(_monitor.T("无效"));
            selector.Items.Add(_monitor.T("未知"));
            selector.SelectedIndex = InputStateIndex(value);
        }

        /// <summary>把有效、无效和未知状态映射到下拉框的固定选项位置。</summary>
        private static int InputStateIndex(bool? value)
        {
            if (value == true) return 0;
            if (value == false) return 1;
            return 2;
        }

        /// <summary>语言或内存信号变化后刷新输入选项和当前值，不重复触发输入写入。</summary>
        private void RefreshInputStateSelectors()
        {
            foreach (var selector in InputsPanel.Children.OfType<StackPanel>()
                .SelectMany(row => row.Children.OfType<ComboBox>()))
            {
                string inputName = (string)selector.Tag;
                bool? currentValue = _session.Device.ReadInput(inputName);
                bool optionsNeedRefresh = selector.Items.Count != 3
                    || !Equals(selector.Items[0], _monitor.T("有效"))
                    || !Equals(selector.Items[1], _monitor.T("无效"))
                    || !Equals(selector.Items[2], _monitor.T("未知"));
                int selectedIndex = InputStateIndex(currentValue);
                if (!optionsNeedRefresh && selector.SelectedIndex == selectedIndex) continue;

                selector.SelectionChanged -= InputState_SelectionChanged;
                if (optionsNeedRefresh) SetInputStateOptions(selector, currentValue);
                else selector.SelectedIndex = selectedIndex;
                selector.SelectionChanged += InputState_SelectionChanged;
            }
        }

        /// <summary>按本机配置把模拟 DI 上升沿转发到整机命令，并遵守复位后才能启动的状态门控。</summary>
        private void ExecuteMappedButtonPress(string inputName)
        {
            var configuration = _session.ControlInputConfiguration;
            var state = _session.Machine.GetSnapshot().State;

            if (IsConfiguredInput(inputName, configuration.PauseInputName))
            {
                if (state == TaskMachineState.Running)
                {
                    _session.Machine.Pause();
                    return;
                }

                throw new InvalidOperationException(
                    _monitor.T("按钮当前状态不允许操作") + " " + _monitor.T(state.ToString()));
            }

            if (IsConfiguredInput(inputName, configuration.ResetInputName))
            {
                if (state == TaskMachineState.NeedsReset
                    || state == TaskMachineState.Paused
                    || state == TaskMachineState.Faulted)
                {
                    _session.Machine.Reset();
                    return;
                }

                throw new InvalidOperationException(
                    _monitor.T("按钮当前状态不允许操作") + " " + _monitor.T(state.ToString()));
            }

            if (IsConfiguredInput(inputName, configuration.StartInputName))
            {
                if (state == TaskMachineState.Ready)
                {
                    _session.Machine.Start();
                    return;
                }

                if (state == TaskMachineState.Paused)
                    throw new InvalidOperationException(_monitor.T("启动前需要复位"));

                throw new InvalidOperationException(
                    _monitor.T("按钮当前状态不允许操作") + " " + _monitor.T(state.ToString()));
            }
        }

        /// <summary>为现场启动、停止和复位按钮显示各自的操作结果提示。</summary>
        private string ControlButtonSuccessKey(string inputName)
        {
            var configuration = _session.ControlInputConfiguration;
            if (IsConfiguredInput(inputName, configuration.PauseInputName)) return "暂停后恢复提示";
            if (IsConfiguredInput(inputName, configuration.ResetInputName)) return "复位后就绪提示";
            if (IsConfiguredInput(inputName, configuration.StartInputName)) return "启动运行提示";
            return "操作已完成";
        }

        /// <summary>按逻辑名称忽略大小写比较当前 DI 是否映射到指定现场按钮。</summary>
        private static bool IsConfiguredInput(string inputName, string configuredName)
        {
            return !string.IsNullOrWhiteSpace(configuredName)
                && string.Equals(inputName, configuredName.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>判断逻辑 DI 是否配置为现场启动、停止或复位按钮。</summary>
        private static bool IsControlButtonInput(string inputName, SafetyInterlockConfigurationDocument configuration)
        {
            return IsConfiguredInput(inputName, configuration.StartInputName)
                || IsConfiguredInput(inputName, configuration.PauseInputName)
                || IsConfiguredInput(inputName, configuration.ResetInputName);
        }

        /// <summary>按已有类型校验后写入本窗口内存变量，并显示变量名和值。</summary>
        private void WriteVariable_Click(object sender, RoutedEventArgs e)
        {
            if (!(VariableBox.SelectedItem is string name)) return;

            string value = VariableValue.Text;
            if (!Command(() => _session.Device.SetVariable(name, value))) return;

            _lastVariableFeedbackName = name;
            _lastVariableFeedbackValue = value;
            _showVariableFeedback = true;
            RefreshVariableWriteFeedback();
        }

        /// <summary>按当前页面语言显示最近成功写入的变量名和值。</summary>
        private void RefreshVariableWriteFeedback()
        {
            if (!_showVariableFeedback || string.IsNullOrWhiteSpace(_lastVariableFeedbackName)) return;

            CommandFeedback.Text = string.Format(_monitor.T("流程变量写入成功格式"),
                _lastVariableFeedbackName, _lastVariableFeedbackValue ?? string.Empty);
            CommandFeedback.Foreground = new SolidColorBrush(Color.FromRgb(22, 130, 75));
        }

        /// <summary>切换变量时清空旧输入和操作反馈，再刷新当前运行值，避免把旧反馈误认为新变量的结果。</summary>
        private void VariableBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _showVariableFeedback = false;
            VariableValue.Clear();
            CommandFeedback.Text = string.Empty;
            CommandFeedback.Foreground = new SolidColorBrush(Color.FromRgb(64, 83, 106));
            RefreshVariableCurrentValue();
        }

        /// <summary>从内存设备快照显示当前变量值，未写入的变量明确显示为无效。</summary>
        private void RefreshVariableCurrentValue()
        {
            string name = VariableBox.SelectedItem as string;
            var variable = name == null ? null : _session.Device.ReadVariables().FirstOrDefault(item =>
                string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            bool hasValue = variable != null && variable.HasValue;
            VariableCurrentValue.Text = hasValue ? variable.CurrentValue ?? string.Empty : _monitor.T("变量尚未写入");
            VariableCurrentValue.Foreground = new SolidColorBrush(hasValue
                ? Color.FromRgb(38, 56, 77) : Color.FromRgb(89, 107, 125));
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
            _monitor.SetLanguage(_language); RefreshState(); RefreshInputStateSelectors(); RefreshVariableCurrentValue();
            RefreshInputStateFeedback();
            RefreshVariableWriteFeedback();
        }

        /// <summary>示例只查看示例目录，本机配置才允许跳转已有页面。</summary>
        private void OpenReference(object sender, TaskReferenceEventArgs e)
        {
            if (!_session.IsExample && ReferenceRequested != null) { ReferenceRequested(this, e); return; }
            var window = new TaskCatalogReferenceWindow(_session.Device.Catalog, e.Page, e.Name, e.Direction, _monitor.T) { Owner = this };
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

        /// <summary>等待内存工位收尾，并把窗口关闭排到当前关闭事件之后执行。</summary>
        public async System.Threading.Tasks.Task CloseSessionAsync()
        {
            if (_closed) return;
            await _session.Machine.CloseAsync();
            _closed = true;
            _timer.Stop();
            await Dispatcher.InvokeAsync(new System.Action(Close), DispatcherPriority.Background);
        }
    }
}
