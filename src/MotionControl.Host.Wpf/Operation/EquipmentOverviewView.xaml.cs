using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Runtime.Machine;

namespace MotionControl.Host.Wpf.Operation
{
    /// <summary>设备总览只显示当前 Runtime 的状态，启停仍由主窗口负责。</summary>
    public partial class EquipmentOverviewView : UserControl
    {
        /// <summary>请求查看已有流程或报警页面。</summary>
        public event EventHandler<OperatorNavigationEventArgs> NavigationRequested;
        /// <summary>当前生产运行入口，可为空。</summary>
        private MachineRuntime _runtime;
        /// <summary>查询正式任务页面是否已读取任务；未提供时不推断会话状态。</summary>
        private Func<bool> _isTaskSessionLoaded;
        /// <summary>当前控制卡连接状态。</summary>
        private bool _isCardConnected;
        /// <summary>Host 是否已提供连接结果；未提供时不臆断独立预览状态。</summary>
        private bool _connectionStatusKnown;
        /// <summary>轴映射页提供的连接结果或失败原因。</summary>
        private string _connectionStatusMessage;
        /// <summary>当前轴数量范围内已填写设备轴名称的通道数。</summary>
        private int _configuredAxisCount;
        /// <summary>当前控制卡配置的物理轴通道总数。</summary>
        private int _configuredAxisTotal;
        /// <summary>Host 是否已提供轴映射统计。</summary>
        private bool _axisMappingStatusKnown;
        /// <summary>Runtime 读取到的自动启动拦截原因。</summary>
        private string _automaticStartBlockReason;
        /// <summary>正在读取当前自动启动条件。</summary>
        private bool _automaticStartReadinessChecking;
        /// <summary>没有报警记录时使用的清晰状态配色。</summary>
        private static readonly Brush ClearAlertBackground = new SolidColorBrush(Color.FromRgb(240, 247, 244));
        /// <summary>没有报警记录时使用的边框配色。</summary>
        private static readonly Brush ClearAlertBorder = new SolidColorBrush(Color.FromRgb(182, 220, 199));
        /// <summary>报警状态未知时使用的中性配色。</summary>
        private static readonly Brush UnknownAlertBackground = new SolidColorBrush(Color.FromRgb(243, 246, 250));
        /// <summary>报警状态未知时使用的边框配色。</summary>
        private static readonly Brush UnknownAlertBorder = new SolidColorBrush(Color.FromRgb(217, 224, 232));
        /// <summary>存在报警记录时使用的提示配色。</summary>
        private static readonly Brush ActiveAlertBackground = new SolidColorBrush(Color.FromRgb(255, 248, 233));
        /// <summary>存在报警记录时使用的边框配色。</summary>
        private static readonly Brush ActiveAlertBorder = new SolidColorBrush(Color.FromRgb(240, 211, 150));
        /// <summary>自动启动条件未满足时使用的提示色，区别于停机报警状态。</summary>
        private static readonly Brush StartBlockedBrush = new SolidColorBrush(Color.FromRgb(132, 91, 10));
        /// <summary>仅独立预览明确传入的示例，不会在正常 Host 自动补入。</summary>
        private OperationSnapshot _preview;
        /// <summary>只在本页可见时刷新软件状态。</summary>
        private readonly DispatcherTimer _timer;
        /// <summary>保持卡片与滚动上下文，不在每次刷新时重建全部控件。</summary>
        private readonly List<StationOverviewCard> _cards = new List<StationOverviewCard>();
        /// <summary>创建总览；无 Runtime 时明确显示未读取。</summary>
        public EquipmentOverviewView()
        {
            InitializeComponent();
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _timer.Tick += TimerTick; Loaded += ViewLoaded; Unloaded += ViewUnloaded;
            RefreshDisplay();
        }
        /// <summary>连接或断开时替换当前实际来源，同时清除任何预览数据。</summary>
        public void SetRuntime(MachineRuntime runtime) { _runtime = runtime; _preview = null; RefreshDisplay(); }
        /// <summary>接入任务会话状态查询，供总览区分未读取和已读取任务。</summary>
        public void SetTaskSessionStateProvider(Func<bool> provider)
        { _isTaskSessionLoaded = provider; RefreshDisplay(); }
        /// <summary>同步控制卡连接诊断，供操作员在首页直接查看并跳转设置。</summary>
        public void SetConnectionStatus(bool isConnected, string statusMessage)
        {
            _isCardConnected = isConnected;
            _connectionStatusKnown = true;
            _connectionStatusMessage = statusMessage;
            RefreshDisplay();
        }
        /// <summary>更新轴映射概况；没有设备轴名称时首页优先提示配置步骤。</summary>
        public void SetAxisMappingStatus(int configuredAxisCount, int configuredAxisTotal)
        {
            _configuredAxisTotal = Math.Max(0, configuredAxisTotal);
            _configuredAxisCount = Math.Max(0, Math.Min(configuredAxisCount, _configuredAxisTotal));
            _axisMappingStatusKnown = _configuredAxisTotal > 0;
            RefreshDisplay();
        }
        /// <summary>同步自动启动就绪检查结果，让总览和顶部启动按钮显示相同的拦截原因。</summary>
        public void SetAutomaticStartReadiness(string blockReason, bool checking)
        {
            _automaticStartBlockReason = blockReason;
            _automaticStartReadinessChecking = checking;
            RefreshDisplay();
        }
        /// <summary>独立预览必须明确标记，禁止把普通快照伪装成生产来源。</summary>
        public void SetPreview(OperationSnapshot snapshot)
        {
            if (snapshot == null || !snapshot.IsPreview) throw new ArgumentException("预览快照必须标记 IsPreview。");
            _runtime = null; _preview = snapshot; RefreshDisplay();
        }
        /// <summary>切换本页语言后重建文字，不修改设备数据。</summary>
        public void SetLanguage(string language)
        { OperationUi.SetLanguage(this, language); _cards.Clear(); StationCards.Items.Clear(); RefreshDisplay(); }
        /// <summary>可见时启动刷新。</summary>
        private void ViewLoaded(object sender, RoutedEventArgs e) { RefreshDisplay(); _timer.Start(); }
        /// <summary>离开时停止刷新，保留卡片与滚动位置。</summary>
        private void ViewUnloaded(object sender, RoutedEventArgs e) { _timer.Stop(); }
        /// <summary>定时仅采样内存状态，不读取卡 SDK。</summary>
        private void TimerTick(object sender, EventArgs e) { RefreshDisplay(); }
        /// <summary>按当前设备状态与来源更新总览；多工位时只展示卡片级统计，避免误读汇总数。</summary>
        public void RefreshDisplay()
        {
            OperationSnapshot snapshot = _preview ?? OperationSnapshot.Read(_runtime);
            bool connectionLost = _preview == null && _connectionStatusKnown && !_isCardConnected && snapshot.HasRuntime;
            bool taskOwner = snapshot.ControlOwner == MachineControlOwner.TaskEngine;
            bool showTask = taskOwner && !connectionLost;
            MachineRunState? displayState = connectionLost ? MachineRunState.Disconnected : snapshot.State;
            bool startBlocked = displayState == MachineRunState.Ready
                && !string.IsNullOrWhiteSpace(_automaticStartBlockReason);
            bool startChecking = displayState == MachineRunState.Ready && _automaticStartReadinessChecking;
            MachineStateText.Text = T(startBlocked ? "启动条件未满足"
                : startChecking ? "正在检查启动条件" : OperationUi.StateKey(displayState));
            Brush stateBrush = startBlocked
                ? StartBlockedBrush
                : OperationUi.StateBrush(displayState);
            MachineStateText.Foreground = stateBrush;
            var border = stateBrush.Clone(); border.Opacity = 0.32; StatusBanner.BorderBrush = border;
            var background = stateBrush.Clone(); background.Opacity = 0.06; StatusBanner.Background = background;
            SourceText.Text = T(connectionLost ? "控制器状态未确认" : snapshot.IsPreview ? "离线页面示例" : "状态来源：设备运行数据");
            SampleText.Text = T("软件状态采样") + " · " + snapshot.SampledAt.ToString("yyyy-MM-dd HH:mm:ss");
            bool noNamedAxes = _axisMappingStatusKnown && _configuredAxisCount == 0;
            NextActionText.Text = connectionLost ? T("控制器断开后请核对状态") : startBlocked ? _automaticStartBlockReason
                : startChecking ? T("正在检查启动条件")
                : showTask && displayState != MachineRunState.Alarmed && displayState != MachineRunState.Paused && displayState != MachineRunState.Stopped ? T("任务入口操作提示")
                : T(_isCardConnected && noNamedAxes
                    ? "先配置设备轴映射再进行手动调试"
                    : NextAction(snapshot));
            bool preparationRequired = _runtime != null && !_runtime.IsPreparing && !_runtime.IsPrepared
                && displayState != MachineRunState.Running && displayState != MachineRunState.Pausing;
            bool axisActionRequired = startBlocked
                && _automaticStartBlockReason.IndexOf("逻辑轴", StringComparison.OrdinalIgnoreCase) >= 0;
            AutomaticStartActionButton.Content = T(preparationRequired ? "设备准备" : "打开轴手动");
            AutomaticStartActionButton.Visibility = preparationRequired || axisActionRequired ? Visibility.Visible : Visibility.Collapsed;
            ConnectionStatusPanel.Visibility = _preview != null || (_isCardConnected && !noNamedAxes)
                ? Visibility.Collapsed : Visibility.Visible;
            ConnectionStateText.Text = T(_isCardConnected ? "设备轴映射未完成" : "控制卡未连接");
            ConnectionDetailText.Text = _isCardConnected && noNamedAxes
                ? T("尚未命名任何设备轴说明")
                : string.IsNullOrWhiteSpace(_connectionStatusMessage)
                    ? T("打开轴映射查看连接原因")
                    : _connectionStatusMessage;
            bool showStationSummary = snapshot.Stations.Count <= 1;
            AggregateMetrics.Visibility = showStationSummary && !taskOwner ? Visibility.Visible : Visibility.Collapsed;
            TaskRunPanel.Visibility = showTask ? Visibility.Visible : Visibility.Collapsed;
            TaskRunStatusText.Text = T(_isTaskSessionLoaded == null ? "任务会话状态待查看"
                : _isTaskSessionLoaded() ? "任务已读取请查看运行状态" : "任务未读取请先读取");
            StationCards.Visibility = connectionLost ? Visibility.Collapsed : Visibility.Visible;
            CompletedText.Text = snapshot.Stations.Count > 1 ? T("按工位查看")
                : snapshot.CompletedCount.HasValue ? snapshot.CompletedCount.Value.ToString(CultureInfo.InvariantCulture) + " " + T("次")
                : T("未接入循环统计");
            CycleText.Text = snapshot.Stations.Count > 1 ? T("按工位查看")
                : snapshot.CycleSeconds.HasValue ? snapshot.CycleSeconds.Value.ToString("0.0", CultureInfo.InvariantCulture) + " " + T("秒")
                : T("未完成");
            EmptyText.Text = snapshot.Stations.Count == 0 && !taskOwner
                ? T(!snapshot.HasRuntime ? "尚无运行时，请先检查控制卡连接"
                    : snapshot.ControlOwner == MachineControlOwner.None ? "当前未启用自动运行" : "未配置自动工位") : "";
            EmptyText.Visibility = snapshot.Stations.Count == 0 && !taskOwner ? Visibility.Visible : Visibility.Collapsed;
            bool changed = _cards.Count != snapshot.Stations.Count || _cards.Where((card, index) => card.StationName != snapshot.Stations[index].Name).Any();
            if (changed)
            {
                _cards.Clear(); StationCards.Items.Clear();
                foreach (var station in snapshot.Stations)
                {
                    var card = new StationOverviewCard(this, station.Name, OpenFlow, OpenReason, OpenStationInput);
                    _cards.Add(card); StationCards.Items.Add(card.Container);
                }
            }
            for (int index = 0; index < _cards.Count; index++)
                _cards[index].Update(snapshot, snapshot.Stations[index], startBlocked, startChecking);
            if (!snapshot.HasRuntime || (connectionLost && snapshot.Alarms.Count == 0))
            {
                AlertText.Text = T("未读取报警状态");
                AlertBanner.Background = UnknownAlertBackground;
                AlertBanner.BorderBrush = UnknownAlertBorder;
            }
            else if (snapshot.Alarms.Count == 0)
            {
                AlertText.Text = T("当前无报警");
                AlertBanner.Background = ClearAlertBackground;
                AlertBanner.BorderBrush = ClearAlertBorder;
            }
            else
            {
                int stopAlarmCount = snapshot.Alarms.Count(alarm => alarm.RequiresReset);
                int noticeCount = snapshot.Alarms.Count - stopAlarmCount;
                string summary = string.Format(
                    T("报警数量摘要"), snapshot.Alarms.Count, stopAlarmCount, noticeCount);
                var visibleAlarms = snapshot.Alarms.Take(3).ToList();
                string details = string.Join(" · ", visibleAlarms.Select(alarm => alarm.SourceName + "：" + alarm.Definition.Message));
                int hiddenCount = snapshot.Alarms.Count - visibleAlarms.Count;
                if (hiddenCount > 0)
                {
                    details += " · " + string.Format(T("另有 {0} 条报警，请打开当前报警查看"), hiddenCount);
                }

                AlertText.Text = summary + (string.IsNullOrWhiteSpace(details) ? string.Empty : "\n" + details);
                if (connectionLost) AlertText.Text += "\n" + T("控制器断开报警可能不完整");
                AlertBanner.Background = ActiveAlertBackground;
                AlertBanner.BorderBrush = ActiveAlertBorder;
            }
        }
        /// <summary>按窗口宽度排列工位卡片；四个及以上工位可使用四列，窄窗仍在工位区滚动。</summary>
        private void CardsSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.NewSize.Width < 100) return;
            int maximumColumns = _cards.Count >= 4 ? 4 : 3;
            int columns = Math.Max(1, Math.Min(maximumColumns, (int)(e.NewSize.Width / 290)));
            double width = Math.Max(250, e.NewSize.Width / columns - 12);
            foreach (var card in _cards) if (Math.Abs(card.Container.Width - width) > 0.5) card.Container.Width = width;
        }
        /// <summary>依据整机状态和自动工位配置提示可执行的下一步，不从动作文字猜测复位或回零结果。</summary>
        private static string NextAction(OperationSnapshot snapshot)
        {
            if (snapshot.State == MachineRunState.Preparing) return "正在准备设备，完成后等待启动；需要中断时按停止";
            if (!snapshot.HasRuntime) return "连接后查看就绪条件，启停使用顶部按钮";
            if (snapshot.Stations.Count == 0)
            {
                switch (snapshot.State)
                {
                    case MachineRunState.Paused: return "设备已暂停，确认现场状态后按整机复位";
                    case MachineRunState.Stopped: return "生产已停止；如需继续旧工步，请从顶部报警复位核查";
                    case MachineRunState.Alarmed: return "先查看报警并处理原因，条件恢复后按整机复位";
                    case MachineRunState.Ready: return "未启用自动工位，可进入轴手动和 IO 手动页面调试";
                    case MachineRunState.ResetRequired: return "检查急停与轴状态，整机复位后可进行手动调试";
                }
            }
            switch (snapshot.State)
            {
                case MachineRunState.Running: return "设备自动运行中；需要人工介入时请先暂停";
                case MachineRunState.Paused: return "保留当前步骤，确认现场后按设备规则复位再启动";
                case MachineRunState.Stopped: return "生产已停止；如需继续旧工步，请从顶部报警复位核查";
                case MachineRunState.Alarmed: return "先查看报警并处理原因，条件恢复后复位再启动";
                case MachineRunState.Ready: return "设备已就绪，使用顶部启动按钮";
                default: return "检查连接与就绪条件，按设备规则复位后启动";
            }
        }
        /// <summary>导航只查看工位当前流程。</summary>
        private void OpenFlow(object sender, RoutedEventArgs e)
        { NavigationRequested?.Invoke(this, new OperatorNavigationEventArgs("流程状态", (string)((Button)sender).Tag)); }
        /// <summary>没有结构化等待证据时明确说明，不从名称推断资源持有人。</summary>
        private void OpenReason(object sender, RoutedEventArgs e)
        {
            string name = (string)((Button)sender).Tag;
            var snapshot = _preview ?? OperationSnapshot.Read(_runtime);
            var station = snapshot.Stations.FirstOrDefault(item => item.Name == name);
            if (station == null) return;
            string reason = snapshot.IsPreview && !string.IsNullOrEmpty(station.PreviewReason)
                ? station.PreviewReason
                : BuildStationReason(snapshot);
            string alarmDetails = string.Join("\n", snapshot.Alarms.Select(alarm =>
                alarm.SourceName + " / " + alarm.Definition.Message + "：" + alarm.Detail));
            string alarmSection = string.IsNullOrWhiteSpace(alarmDetails)
                ? string.Empty
                : "\n\n" + T("当前报警") + "：\n" + alarmDetails;
            MessageBox.Show(name + "\n" + T(OperationUi.StepLabelKey(snapshot.State)) + "：" + station.CurrentStep
                + "\n\n" + reason + alarmSection,
                T("工位状态与处理建议"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        /// <summary>把整机状态、启动拦截和报警整理成该工位可执行的现场处理建议。</summary>
        private string BuildStationReason(OperationSnapshot snapshot)
        {
            switch (snapshot.State)
            {
                case MachineRunState.ResetRequired:
                    return snapshot.Alarms.Any(alarm => alarm.RequiresReset)
                        ? T("存在停机报警时的复位建议")
                        : T("等待整机复位时的处理建议");
                case MachineRunState.Ready:
                    if (_automaticStartReadinessChecking) return T("正在检查启动条件时的建议");
                    if (!string.IsNullOrWhiteSpace(_automaticStartBlockReason))
                        return T("启动条件未满足时的建议") + "\n" + _automaticStartBlockReason;
                    return T("整机就绪时的处理建议");
                case MachineRunState.Running:
                    return T("整机运行时的处理建议");
                case MachineRunState.Pausing:
                case MachineRunState.Paused:
                    return T("整机暂停时的处理建议");
                case MachineRunState.Stopped:
                    return T("生产已停止；如需继续旧工步，请从顶部报警复位核查");
                case MachineRunState.Alarmed:
                    return snapshot.Alarms.Count == 0
                        ? T("整机报警但没有报警记录时的处理建议")
                        : T("整机报警时的处理建议");
                default:
                    return T("无法读取整机状态时的处理建议");
            }
        }
        /// <summary>从工位卡片按其明确报告的等待 DI 打开 IO 监控并定位同名输入。</summary>
        private void OpenStationInput(object sender, RoutedEventArgs e)
        {
            string name = (string)((Button)sender).Tag;
            var snapshot = _preview ?? OperationSnapshot.Read(_runtime);
            var station = snapshot.Stations.FirstOrDefault(item => item.Name == name);
            if (station == null || string.IsNullOrWhiteSpace(station.WaitingInputName)) return;
            NavigationRequested?.Invoke(this, new OperatorNavigationEventArgs(
                "IO监控", station.Name, station.WaitingInputName));
        }
        /// <summary>进入当前报警，不执行复位。</summary>
        private void Alarms_Click(object sender, RoutedEventArgs e) { NavigationRequested?.Invoke(this, new OperatorNavigationEventArgs("当前报警")); }
        /// <summary>打开真实任务运行页，不自动读取任务或启动设备。</summary>
        private void OpenTaskRun_Click(object sender, RoutedEventArgs e)
        { NavigationRequested?.Invoke(this, new OperatorNavigationEventArgs("任务接入状态")); }
        /// <summary>从启动拦截提示进入设备准备；其他轴问题仍使用原有轴手动页面。</summary>
        private void OpenAxisManual_Click(object sender, RoutedEventArgs e)
        { NavigationRequested?.Invoke(this, new OperatorNavigationEventArgs(
            _runtime != null && !_runtime.IsPrepared ? "设备准备" : "轴手动")); }
        /// <summary>跳转到轴映射页查看控制卡连接参数和失败原因，不执行连接或运动。</summary>
        private void ConnectionSettings_Click(object sender, RoutedEventArgs e)
        { NavigationRequested?.Invoke(this, new OperatorNavigationEventArgs("轴映射配置")); }
        /// <summary>中文原文对应当前语言资源。</summary>
        internal string T(string text) { return OperationUi.T(this, text); }
    }

    /// <summary>一个工位的固定卡片控件，刷新只更新文字。</summary>
    internal sealed class StationOverviewCard
    {
        /// <summary>启动条件检查或拦截时使用的提示色。</summary>
        private static readonly Brush StartReadinessBrush = new SolidColorBrush(Color.FromRgb(132, 91, 10));

        /// <summary>稳定工位名称用于保持卡片顺序。</summary>
        internal string StationName { get; private set; }
        /// <summary>卡片外边框。</summary>
        internal Border Container { get; private set; }
        /// <summary>所属页面提供语言文字。</summary>
        private readonly EquipmentOverviewView _owner;
        /// <summary>当前整机约束下的状态文字。</summary>
        private readonly TextBlock _state;
        /// <summary>当前流程动作原文。</summary>
        private readonly TextBlock _step;
        /// <summary>根据整机状态说明当前步骤是待执行、正在执行还是暂停保留。</summary>
        private readonly TextBlock _stepLabel;
        /// <summary>提示操作员从流程与信号页面确认下一步。</summary>
        private readonly TextBlock _note;
        /// <summary>仅在多个工位时展示各自独立的循环统计。</summary>
        private readonly TextBlock _metrics;
        /// <summary>工位明确等待 DI 时提供直接核对入口。</summary>
        private readonly Button _inputButton;
        /// <summary>按 PDF 的标题、状态、动作和操作区构造卡片。</summary>
        internal StationOverviewCard(EquipmentOverviewView owner, string name, RoutedEventHandler flow,
            RoutedEventHandler reason, RoutedEventHandler input)
        {
            _owner = owner; StationName = name;
            var panel = new StackPanel { Margin = new Thickness(15) };
            panel.Children.Add(new TextBlock { Text = name, FontSize = 20, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = name });
            _state = new TextBlock { Margin = new Thickness(0, 10, 0, 10), FontWeight = FontWeights.SemiBold }; panel.Children.Add(_state);
            _metrics = new TextBlock { Margin = new Thickness(0, 0, 0, 10), Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap };
            panel.Children.Add(_metrics);
            _stepLabel = new TextBlock { Foreground = Brushes.SlateGray };
            panel.Children.Add(_stepLabel);
            _step = new TextBlock { TextWrapping = TextWrapping.Wrap, MinHeight = 58, Margin = new Thickness(0, 7, 0, 5), FontSize = 15 }; panel.Children.Add(_step);
            _note = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brushes.SlateGray, MinHeight = 34 }; panel.Children.Add(_note);
            var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            var view = new Button { Content = owner.T("查看流程"), Tag = name }; view.Click += flow; actions.Children.Add(view);
            var why = new Button { Content = owner.T("查看原因"), Tag = name }; why.Click += reason; actions.Children.Add(why); panel.Children.Add(actions);
            _inputButton = new Button { Content = owner.T("核对输入"), Tag = name, Visibility = Visibility.Collapsed };
            _inputButton.SetValue(FrameworkElement.ToolTipProperty, owner.T("从当前等待步骤定位对应输入"));
            _inputButton.Click += input; actions.Children.Add(_inputButton);
            Container = new Border { Width = 315, Margin = new Thickness(0, 0, 12, 12), Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(217, 224, 232)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = panel };
        }
        /// <summary>同步工位步骤与整机状态，并说明循环数属于当前 Runtime 周期。</summary>
        internal void Update(OperationSnapshot snapshot, StationOverviewData station, bool startBlocked, bool startChecking)
        {
            string stateKey = startBlocked ? "启动条件未满足"
                : startChecking ? "正在检查启动条件"
                : OperationUi.StateKey(snapshot.State);
            _state.Text = _owner.T("整机状态") + " · " + _owner.T(stateKey);
            _state.Foreground = startBlocked || startChecking
                ? StartReadinessBrush
                : OperationUi.StateBrush(snapshot.State);
            _stepLabel.Text = _owner.T(OperationUi.StepLabelKey(snapshot.State));
            _step.Text = string.IsNullOrEmpty(station.CurrentStep) ? _owner.T("未提供当前步骤") : station.CurrentStep;
            bool waitingInput = !string.IsNullOrWhiteSpace(station.WaitingInputName);
            _note.Text = waitingInput
                ? _owner.T("当前等待输入") + "：" + station.WaitingInputName
                : _owner.T("查看流程确认当前步骤；等待传感器时到 IO 监控的 DI 页按信号名核对");
            _inputButton.Visibility = waitingInput ? Visibility.Visible : Visibility.Collapsed;
            bool showCardStatistics = snapshot.Stations.Count > 1;
            _metrics.Visibility = showCardStatistics ? Visibility.Visible : Visibility.Collapsed;
            if (!station.CompletedCycles.HasValue)
            {
                _metrics.Text = _owner.T("未接入循环统计");
                return;
            }

            string cycleDescription;
            if (station.LastCycleSeconds.HasValue)
            {
                cycleDescription = _owner.T("最近周期") + " "
                    + station.LastCycleSeconds.Value.ToString("0.0", CultureInfo.InvariantCulture)
                    + " " + _owner.T("秒");
            }
            else if (station.CompletedCycles.Value > 0)
            {
                cycleDescription = _owner.T("最近周期未计时");
            }
            else
            {
                cycleDescription = _owner.T("尚无完整周期");
            }

            _metrics.Text = _owner.T("本次运行") + " "
                + station.CompletedCycles.Value.ToString(CultureInfo.InvariantCulture)
                + " " + _owner.T("次") + " · " + cycleDescription;
        }
    }
}
