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
        /// <summary>当前列表是否为断连后的留存记录。</summary>
        private bool _rowsRetained;
        /// <summary>本次显示所依据的连接周期；点击导航时重新核对。</summary>
        private Guid? _displaySession;
        /// <summary>有 Runtime 但连接周期无法确认时，不能把软件记录当成现场状态。</summary>
        private bool _connectionUnconfirmed;
        /// <summary>记录最近布局，避免轮询时反复改变工作区。</summary>
        private bool? _layoutNarrow, _layoutHasDetails;
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
        /// <summary>根据窗口宽度和选中报警调整列表与指导的位置。</summary>
        private void AlarmWorkspace_SizeChanged(object sender, SizeChangedEventArgs e) { UpdateAlarmLayout(); }
        /// <summary>宽屏并列、窄屏上下；没有报警详情时列表使用完整工作区。</summary>
        private void UpdateAlarmLayout()
        {
            bool narrow = AlarmWorkspace.ActualWidth < 1100;
            bool hasDetails = DetailsPanel.Visibility == Visibility.Visible;
            if (_layoutNarrow == narrow && _layoutHasDetails == hasDetails) return;
            _layoutNarrow = narrow; _layoutHasDetails = hasDetails;
            AlarmListColumn.Width = new GridLength(hasDetails && !narrow ? 1.2 : 1, GridUnitType.Star);
            AlarmGapColumn.Width = new GridLength(hasDetails && !narrow ? 10 : 0);
            AlarmDetailsColumn.Width = hasDetails && !narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            AlarmListRow.Height = hasDetails && narrow ? new GridLength(130) : new GridLength(1, GridUnitType.Star);
            AlarmListRow.MinHeight = hasDetails && narrow ? 100 : 0;
            AlarmGapRow.Height = new GridLength(hasDetails && narrow ? 8 : 0);
            AlarmDetailsRow.Height = hasDetails && narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            AlarmDetailsRow.MinHeight = hasDetails && narrow ? 160 : 0;
            Grid.SetColumn(DetailsPanel, hasDetails && !narrow ? 2 : 0);
            Grid.SetRow(DetailsPanel, hasDetails && narrow ? 2 : 0);
        }
        /// <summary>处理指导超出视口时明确提示可以滚动，查看入口始终保留在底部。</summary>
        private void AlarmDetailsScroll_Changed(object sender, ScrollChangedEventArgs e)
        {
            if (AlarmScrollHint != null)
                AlarmScrollHint.Visibility = e.ExtentHeight > e.ViewportHeight + 0.5 ? Visibility.Visible : Visibility.Collapsed;
        }
        /// <summary>轮询只读报警服务，不读取硬件。</summary>
        private void TimerTick(object sender, EventArgs e) { RefreshDisplay(); }
        /// <summary>刷新按钮不复位或清除报警。</summary>
        private void Refresh_Click(object sender, RoutedEventArgs e) { RefreshDisplay(true); }
        /// <summary>搜索按原始位置、代码、说明和补充信息匹配，不改变报警集合。</summary>
        private void SearchChanged(object sender, TextChangedEventArgs e) { if (_timer != null) RefreshDisplay(true); }
        /// <summary>仅清除查询文字，不复位报警或改变导航资格。</summary>
        private void ClearFilter_Click(object sender, RoutedEventArgs e) { SearchBox.Clear(); }
        /// <summary>按当前快照更新列表，未变化时保持已有行和滚动位置。</summary>
        public void RefreshDisplay(bool force = false)
        {
            try
            {
                Guid? session = _runtime?.ConnectionSessionId;
                _snapshot = _preview ?? OperationSnapshot.Read(_runtime, _taskStationProvider);
                bool current = _preview != null ? _snapshot.State != MachineRunState.Disconnected
                    : _runtime != null && _runtime.IsConnectionSessionCurrent(session);
                _connectionUnconfirmed = _snapshot.HasRuntime && !current;
                _displaySession = _preview == null && current ? session : null;
            }
            catch (Exception error)
            {
                // 读取失败不能显示零报警，保留完整诊断并禁用旧记录导航。
                CountText.Text = T("未读取报警状态");
                FilterCountText.Text = T("报警筛选数量未知");
                ClearFilterButton.IsEnabled = SearchBox.Text.Length > 0;
                SourceText.ToolTip = error.ToString();
                SourceText.Text = (_guidanceEnglish ? "Alarm/state read failed: " : "报警/状态读取失败：") + error.Message;
                if (!string.IsNullOrWhiteSpace(_runtime?.RecoveryBlockReason))
                    SourceText.Text += "\n" + RecoveryGuidance() + _runtime.RecoveryBlockReason;
                EmptyText.Text = _guidanceEnglish ? "Alarm status unknown; maintenance must investigate." : "报警状态未知，请设备维护人员排查。";
                EmptyText.Visibility = Visibility.Visible;
                AlarmGrid.ItemsSource = null;
                _rows.Clear();
                _displaySession = null;
                _connectionUnconfirmed = true;
                DetailsPanel.Visibility = Visibility.Collapsed;
                UpdateAlarmLayout();
                FlowButton.IsEnabled = IoButton.IsEnabled = AxisButton.IsEnabled = false;
                return;
            }
            SourceText.ToolTip = null;
            string query = SearchBox.Text.Trim();
            ClearFilterButton.IsEnabled = SearchBox.Text.Length > 0;
            var alarms = _snapshot.Alarms.Where(alarm => Matches(alarm, query)).OrderBy(alarm => alarm.Definition.Kind == MachineAlarmKind.StopMachine ? 0 : 1).ThenByDescending(alarm => alarm.FirstRaisedAtUtc).ToList();
            SourceText.Text = !_snapshot.HasRuntime && !_snapshot.IsPreview ? T("报警来源未提供")
                : T(_snapshot.IsPreview ? "离线页面示例" : "状态来源：设备运行数据") + " · " + T("软件状态采样") + " "
                    + _snapshot.SampledAt.ToString("yyyy-MM-dd HH:mm:ss") + " · " + T("报警为软件记录，不代表实时输入");
            if (!_connectionUnconfirmed && _snapshot.StationReadError != null)
                SourceText.Text += " · " + T("工位状态读取失败") + "：" + _snapshot.StationReadError.Message;
            if (!_snapshot.HasRuntime)
            {
                CountText.Text = T("未读取报警状态");
            }
            else if (_connectionUnconfirmed)
            {
                CountText.Text = string.Format(T("留存报警记录计数"), _snapshot.Alarms.Count, _snapshot.Alarms.Count(alarm => alarm.RequiresReset), _snapshot.Alarms.Count(alarm => !alarm.RequiresReset));
            }
            else
            {
                int stopAlarmCount = _snapshot.Alarms.Count(alarm => alarm.RequiresReset);
                int noticeCount = _snapshot.Alarms.Count - stopAlarmCount;
                CountText.Text = string.Format(
                    T("报警记录数量摘要"), _snapshot.Alarms.Count, stopAlarmCount, noticeCount);
            }
            FilterCountText.Text = !_snapshot.HasRuntime || _connectionUnconfirmed
                ? T("报警筛选数量未知") : string.Format(T("报警筛选数量格式"), alarms.Count, _snapshot.Alarms.Count);
            EmptyText.Text = !_snapshot.HasRuntime ? T("未读取报警状态") : _snapshot.Alarms.Count == 0 ? T("报警服务当前无记录") : T("没有匹配的报警");
            string recoveryReason = _preview == null ? _runtime?.RecoveryBlockReason : null;
            if (!string.IsNullOrWhiteSpace(recoveryReason))
            {
                SourceText.Text += "\n" + RecoveryGuidance() + recoveryReason;
                if (_snapshot.HasRuntime && _snapshot.Alarms.Count == 0)
                    EmptyText.Text = _guidanceEnglish ? "No alarms recorded by the software service; equipment recovery remains unconfirmed." : "软件报警服务未记录报警，设备恢复信息仍未确认";
            }
            if (_connectionUnconfirmed)
                SourceText.Text += "\n" + T("连接未确认报警状态未知");
            if (_connectionUnconfirmed && alarms.Count == 0)
                EmptyText.Text = string.IsNullOrWhiteSpace(recoveryReason) ? T("连接未确认报警状态未知")
                    : EmptyText.Text + "\n" + T("连接未确认报警状态未知");
            EmptyText.Visibility = alarms.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            bool retained = _connectionUnconfirmed;
            if (force || _rowsRetained != retained || !_rows.Select(row => row.Alarm).SequenceEqual(alarms))
            {
                _refreshing = true;
                _rows = alarms.Select(alarm => new AlarmDisplayRow(alarm, T, retained)).ToList();
                _rowsRetained = retained;
                AlarmGrid.ItemsSource = _rows;
                AlarmGrid.SelectedItem = _rows.FirstOrDefault(row => row.Key == _selectedKey) ?? _rows.FirstOrDefault();
                _refreshing = false;
            }
            ShowDetails();
        }
        /// <summary>读取失败和正常采样均保留恢复责任提示，不能用报警复位代替现场确认。</summary>
        private string RecoveryGuidance()
        {
            return _guidanceEnglish
                ? "Equipment recovery remains unconfirmed. Maintenance must verify the site, retained states and recovery records, and follow the equipment procedures. Alarm reset does not confirm recovery or start production. "
                : "设备恢复信息仍未确认。由设备维护人员核查现场、保持状态与恢复记录，并按设备规程处理；报警复位不代表恢复确认，也不会启动生产。";
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
            bool retained = _connectionUnconfirmed;
            FlowButton.IsEnabled = row != null && !retained;
            IoButton.IsEnabled = AxisButton.IsEnabled = row != null && !retained;
            if (row == null)
            {
                DetailsPanel.Visibility = Visibility.Collapsed;
                UpdateAlarmLayout();
                WhereText.Text = CauseText.Text = CheckText.Text = AfterText.Text = string.Empty;
                DiagnosticText.Clear();
                return;
            }
            DetailsPanel.Visibility = Visibility.Visible;
            UpdateAlarmLayout();
            _selectedKey = row.Key;
            var alarm = row.Alarm;
            CauseTitle.Text = T("2 报警记录状态");
            WhereText.Text = alarm.SourceName;
            CauseText.Text = alarm.Definition.Message + "\n" + row.ConditionText;
            string catalogGuidance = alarm.Definition.CheckGuidance;
            string runtimeDetail = alarm.Detail;
            if (!string.IsNullOrWhiteSpace(catalogGuidance) && !string.IsNullOrWhiteSpace(runtimeDetail))
                CheckText.Text = catalogGuidance + "\n" + runtimeDetail;
            else
                CheckText.Text = string.IsNullOrWhiteSpace(catalogGuidance)
                    ? string.IsNullOrWhiteSpace(runtimeDetail) ? T("按报警内容检查相关机构和信号，具体处理要求由设备项目提供") : runtimeDetail
                    : catalogGuidance;
            CheckText.Text = T("以下为报警记录信息") + "\n" + T("记录的关联输入") + "："
                + (string.IsNullOrWhiteSpace(alarm.RelatedInputName) ? T("未提供") : alarm.RelatedInputName) + "\n" + CheckText.Text;

            // 报警库的固定指导不能覆盖当前条件状态，否则现场人员可能误以为可以立即复位。
            AfterText.Text = retained ? T("断连后核查现场及报警记录") : T(alarm.RequiresReset ? alarm.ConditionActive ? "条件仍有效，先处理原因，不能通过确认或刷新清除" : "停机报警待复位处理提示"
                : "非停机提醒随条件恢复自动消失，不用人工清除");
            if (!string.IsNullOrWhiteSpace(alarm.Definition.RecoveryGuidance))
                AfterText.Text += "\n" + T("报警记录的恢复指导") + "：" + alarm.Definition.RecoveryGuidance;
            if (alarm.RequiresReset && !retained)
                AfterText.Text += "\n" + (_guidanceEnglish
                    ? "Next: resolve the cause and verify feedback, then reset using the machine controls. Reset does not start production. If stopping failed, feedback is unknown or resources await confirmation, maintenance must inspect them before recovery. Output commands do not confirm fixture position."
                    : "下一步：处理原因并核对反馈后，再从设备控制入口复位；复位不会启动生产。若停止失败、反馈未知或资源待确认，由设备维护人员先核查处理。输出命令不能证明夹具已到位。");
            DiagnosticText.Text = T("报警代码") + "：" + alarm.Definition.Code + "\n" + T("首次发生") + "：" + row.OccurredAt
                + "\n" + T("最近变化") + "：" + alarm.LastChangedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                + "\n" + T("记录的关联输入") + "：" + (string.IsNullOrWhiteSpace(alarm.RelatedInputName) ? T("未提供") : alarm.RelatedInputName)
                + "\n\n" + T("最近整机异常，不一定属于此报警") + "\n" + (_snapshot.LastError ?? T("未提供"));
        }
        /// <summary>定位当前执行入口的实际工位，不能切换控制权或启动设备。</summary>
        private void Flow_Click(object sender, RoutedEventArgs e) { Navigate("工位运行"); }
        /// <summary>没有明确 IO 名称时仅打开现有监控页。</summary>
        private void Io_Click(object sender, RoutedEventArgs e) { Navigate("IO监控"); }
        /// <summary>仅打开轴手动，页面本身不点动、不使能。</summary>
        private void Axis_Click(object sender, RoutedEventArgs e) { Navigate("轴手动"); }
        /// <summary>点击时复核连接周期，只打开页面；报警记录没有可验证的当前工位或输入归属。</summary>
        private void Navigate(string page)
        {
            if (!(AlarmGrid.SelectedItem is AlarmDisplayRow) || _connectionUnconfirmed) return;
            if (_preview == null)
            {
                try { if (_runtime == null || !_runtime.IsConnectionSessionCurrent(_displaySession)) { RefreshDisplay(true); return; } }
                catch { RefreshDisplay(true); return; }
            }
            else if (_snapshot.State == MachineRunState.Disconnected) return;
            NavigationRequested?.Invoke(this, new OperatorNavigationEventArgs(page));
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
        internal AlarmDisplayRow(MachineAlarmRecord alarm, Func<string, string> translate, bool retained)
        {
            Alarm = alarm; Key = alarm.SourceName + "\u001f" + alarm.Definition.Code + "\u001f" + alarm.FirstRaisedAtUtc.Ticks;
            KindText = translate(alarm.RequiresReset ? "停机" : "非停机");
            OccurredAt = alarm.FirstRaisedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            ConditionText = translate(retained ? "留存记录，现场状态未知" : alarm.RequiresReset ? alarm.ConditionActive ? "记录条件有效" : "记录等待复位" : "记录非停机提醒");
        }
    }
}
