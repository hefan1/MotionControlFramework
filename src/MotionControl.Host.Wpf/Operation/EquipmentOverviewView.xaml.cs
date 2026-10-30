using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
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
        /// <summary>当前控制卡连接状态。</summary>
        private bool _isCardConnected;
        /// <summary>轴映射页提供的连接结果或失败原因。</summary>
        private string _connectionStatusMessage;
        /// <summary>当前轴数量范围内已填写设备轴名称的通道数。</summary>
        private int _configuredAxisCount;
        /// <summary>当前控制卡配置的物理轴通道总数。</summary>
        private int _configuredAxisTotal;
        /// <summary>Host 是否已提供轴映射统计。</summary>
        private bool _axisMappingStatusKnown;
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
        /// <summary>同步控制卡连接诊断，供操作员在首页直接查看并跳转设置。</summary>
        public void SetConnectionStatus(bool isConnected, string statusMessage)
        {
            _isCardConnected = isConnected;
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
        /// <summary>按已确认来源显示状态；没有计数和独立工位状态时不补默认值。</summary>
        public void RefreshDisplay()
        {
            OperationSnapshot snapshot = _preview ?? OperationSnapshot.Read(_runtime);
            MachineStateText.Text = T(OperationUi.StateKey(snapshot.State));
            MachineStateText.Foreground = OperationUi.StateBrush(snapshot.State);
            var border = OperationUi.StateBrush(snapshot.State).Clone(); border.Opacity = 0.32; StatusBanner.BorderBrush = border;
            var background = OperationUi.StateBrush(snapshot.State).Clone(); background.Opacity = 0.06; StatusBanner.Background = background;
            SourceText.Text = T(snapshot.IsPreview ? "离线页面示例" : "来源：传统C#工位Runtime");
            SampleText.Text = T("软件状态采样") + " · " + snapshot.SampledAt.ToString("yyyy-MM-dd HH:mm:ss");
            bool noNamedAxes = _axisMappingStatusKnown && _configuredAxisCount == 0;
            NextActionText.Text = T(_isCardConnected && noNamedAxes
                ? "先配置设备轴映射再进行手动调试"
                : NextAction(snapshot));
            ConnectionStatusPanel.Visibility = _preview != null || (_isCardConnected && !noNamedAxes)
                ? Visibility.Collapsed : Visibility.Visible;
            ConnectionStateText.Text = T(_isCardConnected ? "设备轴映射未完成" : "控制卡未连接");
            ConnectionDetailText.Text = _isCardConnected && noNamedAxes
                ? T("尚未命名任何设备轴说明")
                : string.IsNullOrWhiteSpace(_connectionStatusMessage)
                    ? T("打开轴映射查看连接原因")
                    : _connectionStatusMessage;
            CompletedText.Text = snapshot.Stations.Count > 1 ? T("按工位查看")
                : snapshot.CompletedCount.HasValue ? snapshot.CompletedCount.Value.ToString(CultureInfo.InvariantCulture) + " " + T("次")
                : T("未接入循环统计");
            CycleText.Text = snapshot.Stations.Count > 1 ? T("按工位查看")
                : snapshot.CycleSeconds.HasValue ? snapshot.CycleSeconds.Value.ToString("0.0", CultureInfo.InvariantCulture) + " " + T("秒")
                : T("未完成");
            EmptyText.Text = snapshot.Stations.Count == 0 ? T(snapshot.HasRuntime ? "未配置自动工位" : "尚无运行时，请先检查控制卡连接") : "";
            EmptyText.Visibility = snapshot.Stations.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            bool changed = _cards.Count != snapshot.Stations.Count || _cards.Where((card, index) => card.StationName != snapshot.Stations[index].Name).Any();
            if (changed)
            {
                _cards.Clear(); StationCards.Items.Clear();
                foreach (var station in snapshot.Stations)
                {
                    var card = new StationOverviewCard(this, station.Name, OpenFlow, OpenReason);
                    _cards.Add(card); StationCards.Items.Add(card.Container);
                }
            }
            for (int index = 0; index < _cards.Count; index++) _cards[index].Update(snapshot, snapshot.Stations[index]);
            if (!snapshot.HasRuntime)
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
                AlertText.Text = string.Join(" · ", snapshot.Alarms.Take(3).Select(alarm => alarm.SourceName + "：" + alarm.Definition.Message));
                AlertBanner.Background = ActiveAlertBackground;
                AlertBanner.BorderBrush = ActiveAlertBorder;
            }
        }
        /// <summary>内容区按一到三列排列卡片，窗口缩小时滚动仍只发生在工位区。</summary>
        private void CardsSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.NewSize.Width < 100) return;
            int columns = Math.Max(1, Math.Min(3, (int)(e.NewSize.Width / 300)));
            double width = Math.Max(250, e.NewSize.Width / columns - 12);
            foreach (var card in _cards) if (Math.Abs(card.Container.Width - width) > 0.5) card.Container.Width = width;
        }
        /// <summary>依据整机状态和自动工位配置提示可执行的下一步，不从动作文字猜测复位或回零结果。</summary>
        private static string NextAction(OperationSnapshot snapshot)
        {
            if (!snapshot.HasRuntime) return "连接后查看就绪条件，启停使用顶部按钮";
            if (snapshot.Stations.Count == 0)
            {
                switch (snapshot.State)
                {
                    case MachineRunState.Paused: return "设备已暂停，确认现场状态后按整机复位";
                    case MachineRunState.Alarmed: return "先查看报警并处理原因，条件恢复后按整机复位";
                    case MachineRunState.Ready: return "未启用自动工位，可进入轴手动和 IO 手动页面调试";
                    case MachineRunState.ResetRequired: return "检查急停与轴状态，整机复位后可进行手动调试";
                }
            }
            switch (snapshot.State)
            {
                case MachineRunState.Running: return "设备自动运行中；需要人工介入时请先暂停";
                case MachineRunState.Paused: return "保留当前步骤，确认现场后按设备规则复位再启动";
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
            string reason = snapshot.IsPreview && !string.IsNullOrEmpty(station.PreviewReason) ? station.PreviewReason : T("请在流程状态核对当前步骤；等待输入时到 IO 监控的 DI 页按信号名检查；如有报警，先处理后再复位");
            string alarms = string.Join("\n", snapshot.Alarms.Where(alarm => alarm.SourceName == name).Select(alarm => alarm.Definition.Message + "：" + alarm.Detail));
            MessageBox.Show(name + "\n" + station.CurrentStep + "\n\n" + reason + "\n" + alarms, T("当前动作与原因"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        /// <summary>进入当前报警，不执行复位。</summary>
        private void Alarms_Click(object sender, RoutedEventArgs e) { NavigationRequested?.Invoke(this, new OperatorNavigationEventArgs("当前报警")); }
        /// <summary>跳转到轴映射页查看控制卡连接参数和失败原因，不执行连接或运动。</summary>
        private void ConnectionSettings_Click(object sender, RoutedEventArgs e)
        { NavigationRequested?.Invoke(this, new OperatorNavigationEventArgs("轴映射配置")); }
        /// <summary>中文原文对应当前语言资源。</summary>
        internal string T(string text) { return OperationUi.T(this, text); }
    }

    /// <summary>一个工位的固定卡片控件，刷新只更新文字。</summary>
    internal sealed class StationOverviewCard
    {
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
        /// <summary>提示操作员从流程与信号页面确认下一步。</summary>
        private readonly TextBlock _note;
        /// <summary>仅在多个工位时展示各自独立的循环统计。</summary>
        private readonly TextBlock _metrics;
        /// <summary>按 PDF 的标题、状态、动作和操作区构造卡片。</summary>
        internal StationOverviewCard(EquipmentOverviewView owner, string name, RoutedEventHandler flow, RoutedEventHandler reason)
        {
            _owner = owner; StationName = name;
            var panel = new StackPanel { Margin = new Thickness(15) };
            panel.Children.Add(new TextBlock { Text = name, FontSize = 20, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = name });
            _state = new TextBlock { Margin = new Thickness(0, 10, 0, 10), FontWeight = FontWeights.SemiBold }; panel.Children.Add(_state);
            _metrics = new TextBlock { Margin = new Thickness(0, 0, 0, 10), Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap };
            panel.Children.Add(_metrics);
            panel.Children.Add(new TextBlock { Text = owner.T("当前动作"), Foreground = Brushes.SlateGray });
            _step = new TextBlock { TextWrapping = TextWrapping.Wrap, MinHeight = 58, Margin = new Thickness(0, 7, 0, 5), FontSize = 15 }; panel.Children.Add(_step);
            _note = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brushes.SlateGray, MinHeight = 34 }; panel.Children.Add(_note);
            var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            var view = new Button { Content = owner.T("查看流程"), Tag = name }; view.Click += flow; actions.Children.Add(view);
            var why = new Button { Content = owner.T("查看原因"), Tag = name }; why.Click += reason; actions.Children.Add(why); panel.Children.Add(actions);
            Container = new Border { Width = 315, Margin = new Thickness(0, 0, 12, 12), Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(217, 224, 232)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = panel };
        }
        /// <summary>暂停或报警覆盖整机运行标签，当前动作继续保留。</summary>
        internal void Update(OperationSnapshot snapshot, StationOverviewData station)
        {
            _state.Text = _owner.T("整机状态") + " · " + _owner.T(OperationUi.StateKey(snapshot.State));
            _state.Foreground = OperationUi.StateBrush(snapshot.State);
            _step.Text = string.IsNullOrEmpty(station.CurrentStep) ? _owner.T("未提供当前动作") : station.CurrentStep;
            _note.Text = _owner.T("查看流程确认当前步骤；等待传感器时到 IO 监控的 DI 页按信号名核对");
            bool showCardStatistics = snapshot.Stations.Count > 1;
            _metrics.Visibility = showCardStatistics ? Visibility.Visible : Visibility.Collapsed;
            _metrics.Text = station.CompletedCycles.HasValue
                ? station.CompletedCycles.Value.ToString(CultureInfo.InvariantCulture) + " " + _owner.T("次") + " · "
                    + (station.LastCycleSeconds.HasValue
                        ? _owner.T("最近周期") + " " + station.LastCycleSeconds.Value.ToString("0.0", CultureInfo.InvariantCulture) + " " + _owner.T("秒")
                        : _owner.T("尚无完整周期"))
                : _owner.T("未接入循环统计");
        }
    }
}
