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

        /// <summary>按实际工位对象保留列表行身份，不用显示名称作为选择键。</summary>
        private readonly Dictionary<IStationFlow, ListBoxItem> _itemByFlow = new Dictionary<IStationFlow, ListBoxItem>();

        /// <summary>本页观察到的最近整机错误，当前报警为空不清除该记录。</summary>
        private string _lastObservedError;

        /// <summary>本次选中的真实工位对象，仅用于只读显示。</summary>
        private IStationFlow _selectedFlow;

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
                var text = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap };
                var item = new ListBoxItem { Content = text, Tag = flow, MinHeight = 44, Padding = new Thickness(8) };
                StationList.Items.Add(item);
                _stepTextByFlow.Add(flow, text);
                _itemByFlow.Add(flow, item);
            }
            StationList.SelectionChanged += StationList_SelectionChanged;
            if (StationList.Items.Count > 0) StationList.SelectedIndex = 0;

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

        /// <summary>只读选择当前真实工位，不改变执行位置或控制权。</summary>
        private void StationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _selectedFlow = (StationList.SelectedItem as ListBoxItem)?.Tag as IStationFlow;
            RefreshDisplay();
        }

        /// <summary>读取当前选中工位的等待输入后沿原逻辑名路由导航，不猜测输入。</summary>
        private void OpenStationInput_Click(object sender, RoutedEventArgs e)
        {
            if (_controllerConnectionLost || _selectedFlow == null) return;
            try
            {
                var input = _selectedFlow as IStationInputWaitState;
                string name = input?.WaitingInputName;
                if (!string.IsNullOrWhiteSpace(name))
                    NavigationRequested?.Invoke(this, new OperatorNavigationEventArgs("IO监控", _selectedFlow.StationName, name));
            }
            catch (Exception error)
            {
                StationDetailText.Text = (string)FindResource("流程页.读取失败") + "：" + error.GetBaseException().Message;
                StationInputButton.IsEnabled = false;
            }
        }

        /// <summary>名称仅在唯一匹配真实工位时用于只读定位，重名不猜测。</summary>
        public bool FocusStationByName(string stationName)
        {
            if (_controllerConnectionLost || _runtime.ControlOwner != MachineControlOwner.StationFlows || string.IsNullOrWhiteSpace(stationName)) return false;
            var matches = _itemByFlow.Where(row => string.Equals(row.Key.StationName, stationName, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1) return false;
            StationList.SelectedItem = matches[0].Value;
            matches[0].Value.BringIntoView();
            return true;
        }

        /// <summary>采集单个工位的项目步骤及输入；失败清除旧值，缺接口不推断独立状态。</summary>
        private string ReadStation(IStationFlow flow, out string inputName, out bool valid)
        {
            inputName = null;
            valid = false;
            if (_controllerConnectionLost) return (string)FindResource("流程页.断连未知");
            try
            {
                string step = flow.CurrentStepName;
                inputName = (flow as IStationInputWaitState)?.WaitingInputName;
                valid = true;
                return (string.IsNullOrWhiteSpace(step) ? (string)FindResource("流程页.步骤未提供") : step) + "\n" + (string)FindResource("流程页.数据有效");
            }
            catch (Exception error)
            {
                return (string)FindResource("流程页.读取失败") + "：" + error.GetBaseException().Message;
            }
        }

        /// <summary>刷新当前状态、步骤、报警及最近故障文字。</summary>
        private void RefreshDisplay()
        {
            bool showStationFlows = !_controllerConnectionLost && _runtime.ControlOwner == MachineControlOwner.StationFlows;
            bool showTasks = !_controllerConnectionLost && _runtime.ControlOwner == MachineControlOwner.TaskEngine;
            StationStepHeadingText.Visibility = showStationFlows ? Visibility.Visible : Visibility.Collapsed;
            StationRowsPanel.Visibility = showStationFlows || (_controllerConnectionLost && _runtime.ControlOwner == MachineControlOwner.StationFlows && _stepTextByFlow.Count > 0) ? Visibility.Visible : Visibility.Collapsed;
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
            string recoveryBlock = _runtime.RecoveryBlockReason;
            string visibleBlock = string.IsNullOrWhiteSpace(recoveryBlock) ? _automaticStartBlockReason : recoveryBlock;
            StartReadinessText.Text = _controllerConnectionLost ? (string)FindResource("软件控制器通讯中断") : visibleBlock ?? string.Empty;
            StartReadinessText.Visibility = !_controllerConnectionLost && string.IsNullOrWhiteSpace(visibleBlock)
                ? Visibility.Collapsed
                : Visibility.Visible;
            string resetGuidance = !_controllerConnectionLost && _runtime.State == MachineRunState.ResetRequired && string.IsNullOrWhiteSpace(visibleBlock)
                ? (string)FindResource("整机复位提示")
                : string.Empty;
            ResetGuidanceText.Text = resetGuidance;
            ResetGuidanceText.Visibility = string.IsNullOrWhiteSpace(resetGuidance)
                ? Visibility.Collapsed
                : Visibility.Visible;
            foreach (var row in _stepTextByFlow)
            {
                string input;
                bool valid;
                string step = ReadStation(row.Key, out input, out valid);
                row.Value.Text = row.Key.StationName + " — " + step + "\n" + (string)FindResource(valid ? "流程页.独立状态未提供" : "流程页.数据未知");
                if (ReferenceEquals(row.Key, _selectedFlow))
                {
                    StationDetailText.Text = row.Key.StationName + "\n" + (string)FindResource("流程页.CSharp来源") + "\n" + step
                        + "\n" + (string)FindResource(valid ? "流程页.独立状态未提供" : "流程页.数据未知")
                        + "\n" + (string.IsNullOrWhiteSpace(input) ? (string)FindResource("流程页.输入未提供") : string.Format((string)FindResource("流程页.等待输入"), input));
                    StationInputButton.IsEnabled = valid && !string.IsNullOrWhiteSpace(input);
                    StationInputButton.ToolTip = StationInputButton.IsEnabled ? input : (string)FindResource("流程页.输入未提供");
                }
            }
            if (_selectedFlow == null)
            {
                StationDetailText.Text = (string)FindResource("未配置自动工位");
                StationInputButton.IsEnabled = false;
            }

            MachineAlarmRecord[] alarms;
            bool alarmReadFailed = false;
            try { alarms = _runtime.AlarmService.Alarms.ToArray(); }
            catch (Exception error)
            {
                alarms = new MachineAlarmRecord[0];
                alarmReadFailed = true;
                AlarmText.ToolTip = error.GetBaseException().Message;
            }
            bool codeMode = _runtime.ControlOwner == MachineControlOwner.StationFlows;
            AlarmDetailsPanel.Visibility = codeMode ? Visibility.Collapsed : Visibility.Visible;
            StationAlarmSummaryText.Text = alarmReadFailed || _controllerConnectionLost
                ? (string)FindResource("流程页.报警状态未知")
                : alarms.Length == 0 ? string.Empty : string.Format((string)FindResource("报警数量摘要"), alarms.Length, alarms.Count(a => a.RequiresReset), alarms.Count(a => !a.RequiresReset));
            StationAlarmSummaryText.Visibility = codeMode && !string.IsNullOrEmpty(StationAlarmSummaryText.Text) ? Visibility.Visible : Visibility.Collapsed;
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

            if (alarmReadFailed) AlarmText.Text = (string)FindResource("流程页.报警状态未知");
            if (_runtime.LastError != null) _lastObservedError = _runtime.LastError.GetBaseException().Message;
            LastErrorText.Visibility = string.IsNullOrEmpty(_lastObservedError) ? Visibility.Collapsed : Visibility.Visible;
            LastErrorText.Text = string.IsNullOrEmpty(_lastObservedError) ? string.Empty : ((string)FindResource("最近故障")) + _lastObservedError;
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
