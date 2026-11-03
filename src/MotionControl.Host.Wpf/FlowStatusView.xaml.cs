using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MotionControl.Host.Wpf.Operation;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Runtime.Machine;

namespace MotionControl.Host.Wpf
{
    /// <summary>显示 Runtime 的工位步骤、整机状态和当前报警，供现场核对流程进度。</summary>
    public partial class FlowStatusView : UserControl
    {
        /// <summary>请求打开只读关联页，不执行轴运动或 IO 写入。</summary>
        public event EventHandler<OperatorNavigationEventArgs> NavigationRequested;

        /// <summary>本页观察的整机运行时，与手动轴和 IO 页面共享同一个实例。</summary>
        private readonly MachineRuntime _runtime;

        /// <summary>查询任务会话是否已读取；未提供时不推断任务状态。</summary>
        private readonly Func<bool> _isTaskSessionLoaded;

        /// <summary>控制器会话异常丢失后，工艺步骤和轴状态仅为最后已知值。</summary>
        private bool _controllerConnectionLost;

        /// <summary>当前自动启动条件未满足时显示的具体原因。</summary>
        private string _automaticStartBlockReason;

        /// <summary>当前正在读取自动启动条件。</summary>
        private bool _automaticStartReadinessChecking;

        /// <summary>按工位保存当前步骤文字，避免定时刷新时重建列表。</summary>
        private readonly Dictionary<IStationFlow, TextBlock> _stepTextByFlow =
            new Dictionary<IStationFlow, TextBlock>();

        /// <summary>按工位保存输入核对入口，等待 DI 的工位才能定向跳转。</summary>
        private readonly Dictionary<IStationFlow, Button> _inputButtonByFlow =
            new Dictionary<IStationFlow, Button>();

        /// <summary>页面显示期间刷新可能在后台推进的当前工位步骤。</summary>
        private readonly DispatcherTimer _refreshTimer;

        /// <summary>创建工位进度页，并建立当前 Runtime 的每个工位行。</summary>
        public FlowStatusView(MachineRuntime runtime, Func<bool> isTaskSessionLoaded = null)
        {
            _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
            _isTaskSessionLoaded = isTaskSessionLoaded;
            InitializeComponent();
            foreach (IStationFlow flow in _runtime.StationFlows)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
                row.Children.Add(new TextBlock
                {
                    Text = flow.StationName + "：",
                    Width = 140,
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromRgb(64, 83, 106))
                });
                var stepText = new TextBlock { FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(38, 56, 77)) };
                row.Children.Add(stepText);
                var inputButton = new Button { Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(8, 0, 0, 0), Tag = flow };
                inputButton.SetResourceReference(ContentControl.ContentProperty, "流程页.核对输入");
                inputButton.SetResourceReference(FrameworkElement.ToolTipProperty, "流程页.核对输入说明");
                inputButton.Click += OpenStationInput_Click;
                row.Children.Add(inputButton);
                StationRowsPanel.Children.Add(row);
                _stepTextByFlow.Add(flow, stepText);
                _inputButtonByFlow.Add(flow, inputButton);
            }

            if (_stepTextByFlow.Count == 0)
            {
                var emptyText = new TextBlock
                {
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromRgb(89, 107, 125))
                };
                emptyText.SetResourceReference(TextBlock.TextProperty, "未配置自动工位");
                StationRowsPanel.Children.Add(emptyText);
            }

            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _refreshTimer.Tick += RefreshTimer_Tick;
            Loaded += FlowStatusView_Loaded;
            Unloaded += FlowStatusView_Unloaded;
            RefreshDisplay();
        }

        /// <summary>页面进入可见区域时订阅 Runtime 事件并启动步骤刷新。</summary>
        private void FlowStatusView_Loaded(object sender, RoutedEventArgs e)
        {
            _runtime.StatusChanged += Runtime_Changed;
            _runtime.AlarmsChanged += Runtime_Changed;
            _refreshTimer.Start();
            RefreshDisplay();
        }

        /// <summary>离开页面时停止轮询并解除事件订阅。</summary>
        private void FlowStatusView_Unloaded(object sender, RoutedEventArgs e)
        {
            _refreshTimer.Stop();
            _runtime.StatusChanged -= Runtime_Changed;
            _runtime.AlarmsChanged -= Runtime_Changed;
        }

        /// <summary>Runtime 可能从后台线程通知，统一切回界面线程读取状态。</summary>
        private void Runtime_Changed(object sender, EventArgs e)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(RefreshDisplay));
                return;
            }

            RefreshDisplay();
        }

        /// <summary>定时读取工位步骤，使轴动作和传感器等待进度实时可见。</summary>
        private void RefreshTimer_Tick(object sender, EventArgs e)
        {
            RefreshDisplay();
        }

        /// <summary>语言切换后重新读取代码生成的状态和报警文字。</summary>
        public void RefreshLocalizedText()
        {
            RefreshDisplay();
        }

        /// <summary>控制器断开后避免继续把旧工位步骤显示为当前运行状态。</summary>
        public void MarkControllerConnectionLost()
        {
            _controllerConnectionLost = true;
            RefreshDisplay();
        }

        /// <summary>同步总览和顶部状态栏使用的自动启动条件结果。</summary>
        public void SetAutomaticStartReadiness(string blockReason, bool checking)
        {
            _automaticStartBlockReason = blockReason;
            _automaticStartReadinessChecking = checking;
            RefreshDisplay();
        }

        /// <summary>打开 IO 监控；Host 会选中 DI 输入供现场核对传感器状态。</summary>
        private void OpenIoMonitor_Click(object sender, RoutedEventArgs e)
        {
            NavigationRequested?.Invoke(this, new OperatorNavigationEventArgs("IO监控"));
        }

        /// <summary>打开只读轴状态页，供复位前核对各工位相关轴是否停止。</summary>
        private void OpenAxisMonitor_Click(object sender, RoutedEventArgs e)
        {
            NavigationRequested?.Invoke(this, new OperatorNavigationEventArgs("轴监控"));
        }

        /// <summary>打开当前报警页查看设备停机原因。</summary>
        private void OpenCurrentAlarms_Click(object sender, RoutedEventArgs e)
        {
            NavigationRequested?.Invoke(this, new OperatorNavigationEventArgs("当前报警"));
        }

        /// <summary>进入真实任务运行页，仅导航，不读取任务或启动设备。</summary>
        private void OpenTaskRun_Click(object sender, RoutedEventArgs e)
        {
            NavigationRequested?.Invoke(this, new OperatorNavigationEventArgs("任务接入状态"));
        }

        /// <summary>打开 DI 监控并按当前工位等待状态定位对应输入。</summary>
        private void OpenStationInput_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var flow = button == null ? null : button.Tag as IStationFlow;
            var inputState = flow as IStationInputWaitState;
            if (flow == null || inputState == null || string.IsNullOrWhiteSpace(inputState.WaitingInputName))
            {
                return;
            }

            NavigationRequested?.Invoke(this, new OperatorNavigationEventArgs(
                "IO监控",
                flow.StationName,
                inputState.WaitingInputName));
        }

        /// <summary>从总览或报警按工位名称只读定位，保留原页面布局和设备状态。</summary>
        public bool FocusStationByName(string stationName)
        {
            if (_controllerConnectionLost || _runtime.ControlOwner != MachineControlOwner.StationFlows) return false;
            bool found = false;
            foreach (var row in _stepTextByFlow)
            {
                bool selected = string.Equals(row.Key.StationName, stationName, StringComparison.OrdinalIgnoreCase);
                row.Value.Background = selected ? new SolidColorBrush(Color.FromRgb(230, 241, 255)) : Brushes.Transparent;
                if (selected) { row.Value.BringIntoView(); found = true; }
            }
            return found;
        }

        /// <summary>刷新当前状态、步骤、报警及最近故障文字。</summary>
        private void RefreshDisplay()
        {
            bool showStationFlows = !_controllerConnectionLost && _runtime.ControlOwner == MachineControlOwner.StationFlows;
            bool showTasks = !_controllerConnectionLost && _runtime.ControlOwner == MachineControlOwner.TaskEngine;
            StationStepHeadingText.Visibility = showStationFlows ? Visibility.Visible : Visibility.Collapsed;
            StationRowsPanel.Visibility = showStationFlows ? Visibility.Visible : Visibility.Collapsed;
            TaskRunPanel.Visibility = showTasks ? Visibility.Visible : Visibility.Collapsed;
            NoAutomaticRunText.Visibility = !_controllerConnectionLost && _runtime.ControlOwner == MachineControlOwner.None
                ? Visibility.Visible : Visibility.Collapsed;
            if (showTasks)
                TaskRunStatusText.Text = (string)FindResource(_isTaskSessionLoaded == null ? "流程页.任务状态待查看"
                    : _isTaskSessionLoaded() ? "流程页.任务已读取" : "流程页.任务未读取");
            StationStepHeadingText.Text = (string)FindResource("流程页." + OperationUi.StepLabelKey(_runtime.State));
            string stateResourceKey = _controllerConnectionLost ? "状态未连接" : !string.IsNullOrWhiteSpace(_automaticStartBlockReason)
                ? "状态启动条件未满足"
                : _automaticStartReadinessChecking
                    ? "状态检查启动条件"
                    : GetMachineStateResourceKey(_runtime.State);
            MachineStateText.Text = ((string)FindResource("整机当前状态"))
                + (string)FindResource(stateResourceKey);
            StartReadinessText.Text = _controllerConnectionLost ? (string)FindResource("软件控制器通讯中断") : _automaticStartBlockReason ?? string.Empty;
            StartReadinessText.Visibility = !_controllerConnectionLost && string.IsNullOrWhiteSpace(_automaticStartBlockReason)
                ? Visibility.Collapsed
                : Visibility.Visible;
            string resetGuidance = !_controllerConnectionLost && _runtime.State == MachineRunState.ResetRequired
                ? (string)FindResource("整机复位提示")
                : string.Empty;
            ResetGuidanceText.Text = resetGuidance;
            ResetGuidanceText.Visibility = string.IsNullOrWhiteSpace(resetGuidance)
                ? Visibility.Collapsed
                : Visibility.Visible;
            foreach (KeyValuePair<IStationFlow, TextBlock> row in _stepTextByFlow)
            {
                row.Value.Text = row.Key.CurrentStepName;
                Button inputButton = _inputButtonByFlow[row.Key];
                var inputState = row.Key as IStationInputWaitState;
                string inputName = inputState == null ? null : inputState.WaitingInputName;
                inputButton.Visibility = string.IsNullOrWhiteSpace(inputName) ? Visibility.Collapsed : Visibility.Visible;
                inputButton.IsEnabled = !string.IsNullOrWhiteSpace(inputName);
                inputButton.ToolTip = string.IsNullOrWhiteSpace(inputName)
                    ? null
                    : string.Format((string)FindResource("流程页.核对输入说明"), inputName);
            }

            MachineAlarmRecord[] alarms = _runtime.AlarmService.Alarms.ToArray();
            if (alarms.Length == 0)
            {
                AlarmText.Text = _controllerConnectionLost
                    ? (string)FindResource("软件控制器通讯中断")
                    : (string)FindResource("无当前报警");
            }
            else
            {
                int stopAlarmCount = alarms.Count(alarm => alarm.RequiresReset);
                string summary = string.Format(
                    (string)FindResource("报警数量摘要"), alarms.Length, stopAlarmCount, alarms.Length - stopAlarmCount);
                string details = string.Join("\n", alarms.Select(alarm =>
                    alarm.SourceName + " / " + alarm.Definition.Code + " / " + alarm.Definition.Message
                    + "：" + alarm.Detail
                    + (alarm.ConditionActive ? string.Empty : (string)FindResource("报警待复位"))));
                AlarmText.Text = summary + "\n" + details;
                if (_controllerConnectionLost) AlarmText.Text += "\n" + (string)FindResource("软件控制器通讯中断");
            }

            LastErrorText.Text = _runtime.LastError == null
                ? string.Empty
                : ((string)FindResource("最近故障")) + _runtime.LastError.GetBaseException().Message;
        }

        /// <summary>把 Runtime 状态映射到与主窗口一致的多语言资源键。</summary>
        private static string GetMachineStateResourceKey(MachineRunState state)
        {
            switch (state)
            {
                case MachineRunState.ResetRequired: return "状态等待复位";
                case MachineRunState.Ready: return "状态就绪";
                case MachineRunState.Running: return "状态运行中";
                case MachineRunState.Pausing: return "状态暂停中";
                case MachineRunState.Paused: return "状态已暂停";
                case MachineRunState.Stopped: return "状态已停止";
                case MachineRunState.Alarmed: return "状态报警";
                default: return "状态未连接";
            }
        }
    }
}
