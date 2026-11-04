using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Framework.Runtime.Machine;

namespace MotionControl.Host.Wpf.Operation
{
    /// <summary>当前报警及四块处理指导；页面没有复位、确认清除或设备写入入口。</summary>
    public partial class CurrentAlarmsView : UserControl
    {
        /// <summary>当前报警处理提示跟随页面语言。</summary>
        private bool _guidanceEnglish;
        /// <summary>只读导航请求。</summary>
        public event EventHandler<OperatorNavigationEventArgs> NavigationRequested;
        /// <summary>与顶部按钮共用的当前 Runtime。</summary>
        private MachineRuntime _runtime;
        /// <summary>仅独立预览传入的明确示例。</summary>
        private OperationSnapshot _preview;
        /// <summary>当前展示的完整软件状态。</summary>
        private OperationSnapshot _snapshot;
        /// <summary>从当前任务会话只读取得工位来源，供报警定位。</summary>
        private Func<IReadOnlyList<StationOverviewData>> _taskStationProvider;
        /// <summary>保持表格和滚动位置的已展示行。</summary>
        private List<AlarmDisplayRow> _rows = new List<AlarmDisplayRow>();
        /// <summary>稳定选择标识，条件更新后仍选择同一次报警。</summary>
        private string _selectedKey;
        /// <summary>替换列表期间不把临时空选择当作人员操作。</summary>
        private bool _refreshing;
        /// <summary>只在可见时刷新报警软件状态。</summary>
        private readonly DispatcherTimer _timer;
        /// <summary>初始化空状态与只读列表。</summary>
        public CurrentAlarmsView()
        {
            InitializeComponent();
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _timer.Tick += TimerTick; Loaded += ViewLoaded; Unloaded += ViewUnloaded;
            UpdateHeaders(); RefreshDisplay();
        }
        /// <summary>切换运行时并丢弃旧来源的选择，不能把旧报警视为新设备报警。</summary>
        public void SetRuntime(MachineRuntime runtime)
        { _runtime = runtime; _preview = null; _selectedKey = null; RefreshDisplay(true); }
        /// <summary>注入任务工位读取，不创建会话或执行设备动作。</summary>
        public void SetTaskStationProvider(Func<IReadOnlyList<StationOverviewData>> provider)
        { _taskStationProvider = provider; RefreshDisplay(true); }

        /// <summary>独立示例必须明确标记，正常 Host 不自动补入任何报警。</summary>
        public void SetPreview(OperationSnapshot snapshot)
        {
            if (snapshot == null || !snapshot.IsPreview) throw new ArgumentException("预览快照必须标记 IsPreview。");
            _runtime = null; _preview = snapshot; RefreshDisplay(true);
        }
        /// <summary>切换界面文字并保留当前报警与查询条件。</summary>
        public void SetLanguage(string language)
        { _guidanceEnglish = language == "en-US"; OperationUi.SetLanguage(this, language); UpdateHeaders(); RefreshDisplay(true); }
        /// <summary>表头不继承局部资源，因此明确翻译。</summary>
        private void UpdateHeaders()
        {
            string[] labels = { "等级", "发生时间", "位置", "报警内容", "状态" };
            for (int i = 0; i < labels.Length; i++) AlarmGrid.Columns[i].Header = T(labels[i]);
        }
        /// <summary>进入页面后刷新并启动计时。</summary>
        private void ViewLoaded(object sender, RoutedEventArgs e) { RefreshDisplay(); _timer.Start(); }
        /// <summary>离开后停止轮询，选择和筛选内容继续保留。</summary>
        private void ViewUnloaded(object sender, RoutedEventArgs e) { _timer.Stop(); }
        /// <summary>轮询只读报警服务，不读取硬件。</summary>
        private void TimerTick(object sender, EventArgs e) { RefreshDisplay(); }
        /// <summary>刷新按钮不复位或清除报警。</summary>
        private void Refresh_Click(object sender, RoutedEventArgs e) { RefreshDisplay(true); }
        /// <summary>搜索按原始位置、代码、说明和补充信息匹配，不改变报警集合。</summary>
        private void SearchChanged(object sender, TextChangedEventArgs e) { if (_timer != null) RefreshDisplay(true); }
        /// <summary>按当前快照更新列表，未变化时保持已有行和滚动位置。</summary>
        public void RefreshDisplay(bool force = false)
        {
            _snapshot = _preview ?? OperationSnapshot.Read(_runtime, _taskStationProvider);
            string query = SearchBox.Text.Trim();
            var alarms = _snapshot.Alarms.Where(alarm => Matches(alarm, query)).OrderBy(alarm => alarm.Definition.Kind == MachineAlarmKind.StopMachine ? 0 : 1).ThenByDescending(alarm => alarm.FirstRaisedAtUtc).ToList();
            SourceText.Text = T(_snapshot.IsPreview ? "离线页面示例" : "状态来源：设备运行数据") + " · " + T("软件状态采样") + " " + _snapshot.SampledAt.ToString("yyyy-MM-dd HH:mm:ss");
            if (_snapshot.StationReadError != null)
                SourceText.Text += " · " + T("工位状态读取失败") + "：" + _snapshot.StationReadError.Message;
            if (!_snapshot.HasRuntime)
            {
                CountText.Text = T("未读取报警状态");
            }
            else
            {
                int stopAlarmCount = _snapshot.Alarms.Count(alarm => alarm.RequiresReset);
                int noticeCount = _snapshot.Alarms.Count - stopAlarmCount;
                CountText.Text = string.Format(
                    T("报警数量摘要"), _snapshot.Alarms.Count, stopAlarmCount, noticeCount);
            }
            EmptyText.Text = !_snapshot.HasRuntime ? T("未读取报警状态") : _snapshot.Alarms.Count == 0 ? T("当前无报警") : T("没有匹配的报警");
            EmptyText.Visibility = alarms.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (force || !_rows.Select(row => row.Alarm).SequenceEqual(alarms))
            {
                _refreshing = true;
                _rows = alarms.Select(alarm => new AlarmDisplayRow(alarm, T)).ToList();
                AlarmGrid.ItemsSource = _rows;
                AlarmGrid.SelectedItem = _rows.FirstOrDefault(row => row.Key == _selectedKey) ?? _rows.FirstOrDefault();
                _refreshing = false;
            }
            ShowDetails();
        }
        /// <summary>小范围不区分大小写查找，空搜索显示全部。</summary>
        private static bool Matches(MachineAlarmRecord alarm, string query)
        {
            return string.IsNullOrEmpty(query) || new[] { alarm.SourceName, alarm.Definition.Code, alarm.Definition.Message, alarm.Detail }
                .Any(text => text != null && text.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
        }
        /// <summary>只有人员实际选择或刷新完成后才改变详情。</summary>
        private void AlarmSelected(object sender, SelectionChangedEventArgs e) { if (!_refreshing) ShowDetails(); }
        /// <summary>显示报警处理指导；未选中报警时折叠详情区，让列表空态占满可用区域。</summary>
        private void ShowDetails()
        {
            var row = AlarmGrid.SelectedItem as AlarmDisplayRow;
            FlowButton.IsEnabled = row != null && _snapshot.Stations.Count(station => MatchesStation(station, row.Alarm.SourceName)) == 1;
            IoButton.IsEnabled = AxisButton.IsEnabled = row != null;
            if (row == null)
            {
                DetailsPanel.Visibility = Visibility.Collapsed;
                DetailsRow.MinHeight = 0;
                DetailsRow.Height = new GridLength(0);
                WhereText.Text = CauseText.Text = CheckText.Text = AfterText.Text = string.Empty;
                DiagnosticText.Clear();
                return;
            }
            DetailsPanel.Visibility = Visibility.Visible;
            DetailsRow.MinHeight = 160;
            DetailsRow.Height = new GridLength(1.6, GridUnitType.Star);
            _selectedKey = row.Key;
            var alarm = row.Alarm;
            CauseTitle.Text = T(alarm.RequiresReset ? "2 当前为什么不能运行" : "2 当前提醒条件");
            var matchingStations = _snapshot.Stations.Where(station => MatchesStation(station, alarm.SourceName)).Take(2).ToArray();
            var flow = matchingStations.Length == 1 ? matchingStations[0] : null;
            WhereText.Text = alarm.SourceName + (flow == null ? "" : "\n" + T("当前动作") + "：" + flow.CurrentStep);
            CauseText.Text = alarm.Definition.Message + "\n" + row.ConditionText;
            string catalogGuidance = alarm.Definition.CheckGuidance;
            string runtimeDetail = alarm.Detail;
            if (!string.IsNullOrWhiteSpace(catalogGuidance) && !string.IsNullOrWhiteSpace(runtimeDetail))
                CheckText.Text = catalogGuidance + "\n" + runtimeDetail;
            else
                CheckText.Text = string.IsNullOrWhiteSpace(catalogGuidance)
                    ? string.IsNullOrWhiteSpace(runtimeDetail) ? T("按报警内容检查相关机构和信号，具体处理要求由设备项目提供") : runtimeDetail
                    : catalogGuidance;

            // 报警库的固定指导不能覆盖当前条件状态，否则现场人员可能误以为可以立即复位。
            AfterText.Text = T(alarm.RequiresReset ? alarm.ConditionActive ? "条件仍有效，先处理原因，不能通过确认或刷新清除" : "停机报警待复位处理提示"
                : "非停机提醒随条件恢复自动消失，不用人工清除");
            if (!string.IsNullOrWhiteSpace(alarm.Definition.RecoveryGuidance))
                AfterText.Text += "\n" + alarm.Definition.RecoveryGuidance;
            if (alarm.RequiresReset)
                AfterText.Text += "\n" + (_guidanceEnglish
                    ? "Next: resolve the cause and verify feedback, then reset using the machine controls. Reset does not start production. If stopping failed, feedback is unknown or resources await confirmation, maintenance must inspect them before recovery. Output commands do not confirm fixture position."
                    : "下一步：处理原因并核对反馈后，再从设备控制入口复位；复位不会启动生产。若停止失败、反馈未知或资源待确认，由设备维护人员先核查处理。输出命令不能证明夹具已到位。");
            DiagnosticText.Text = T("报警代码") + "：" + alarm.Definition.Code + "\n" + T("首次发生") + "：" + row.OccurredAt
                + "\n" + T("最近变化") + "：" + alarm.LastChangedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                + "\n\n" + T("最近整机异常，不一定属于此报警") + "\n" + (_snapshot.LastError ?? T("未提供"));
        }
        /// <summary>定位当前执行入口的实际工位，不能切换控制权或启动设备。</summary>
        private void Flow_Click(object sender, RoutedEventArgs e) { Navigate("工位运行"); }
        /// <summary>仅按来源明确提供的名称或稳定标识关联工位。</summary>
        private static bool MatchesStation(StationOverviewData station, string source)
        { return string.Equals(station.Name, source, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(station.Id) && string.Equals(station.Id, source, StringComparison.OrdinalIgnoreCase)); }
        /// <summary>没有明确 IO 名称时仅打开现有监控页。</summary>
        private void Io_Click(object sender, RoutedEventArgs e) { Navigate("IO监控"); }
        /// <summary>仅打开轴手动，页面本身不点动、不使能。</summary>
        private void Axis_Click(object sender, RoutedEventArgs e) { Navigate("轴手动"); }
        /// <summary>仅使用报警记录明确提供的输入；不把工位当前等待输入当作报警原因。</summary>
        private void Navigate(string page)
        {
            if (!(AlarmGrid.SelectedItem is AlarmDisplayRow row)) return;
            NavigationRequested?.Invoke(this, new OperatorNavigationEventArgs(
                page, row.Alarm.SourceName, page == "IO监控" ? row.Alarm.RelatedInputName : null));
        }
        /// <summary>当前语言的中文资源键。</summary>
        private string T(string text) { return OperationUi.T(this, text); }
    }

    /// <summary>报警表的可读行，原始记录不被页面修改。</summary>
    public sealed class AlarmDisplayRow
    {
        /// <summary>当前服务提供的不可变报警记录。</summary>
        public MachineAlarmRecord Alarm { get; private set; }
        /// <summary>同一次报警的稳定选择键。</summary>
        public string Key { get; private set; }
        /// <summary>停机或提醒文字。</summary>
        public string KindText { get; private set; }
        /// <summary>包含年月日的本地发生时间。</summary>
        public string OccurredAt { get; private set; }
        /// <summary>条件有效、待复位或非停机提醒状态。</summary>
        public string ConditionText { get; private set; }
        /// <summary>将明确信息翻译为当前界面文字，不作报警复位。</summary>
        internal AlarmDisplayRow(MachineAlarmRecord alarm, Func<string, string> translate)
        {
            Alarm = alarm; Key = alarm.SourceName + "\u001f" + alarm.Definition.Code + "\u001f" + alarm.FirstRaisedAtUtc.Ticks;
            KindText = translate(alarm.RequiresReset ? "停机" : "非停机");
            OccurredAt = alarm.FirstRaisedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            ConditionText = translate(alarm.RequiresReset ? alarm.ConditionActive ? "条件仍有效" : "报警等待复位" : "非停机提醒");
        }
    }
}
