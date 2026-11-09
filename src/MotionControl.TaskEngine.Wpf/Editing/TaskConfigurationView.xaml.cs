using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Configuration;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Device;
using MotionControl.TaskEngine.Editing;
using MotionControl.TaskEngine.Machine;
using MotionControl.TaskEngine.Validation;
using MotionControl.TaskEngine.Wpf.Monitoring;
using MotionControl.TaskEngine.Wpf.Simulation;
using MotionControl.TaskEngine.Wpf.Views;

namespace MotionControl.TaskEngine.Wpf.Editing
{
    /// <summary>设备任务草稿的明确入口，工位安排与任务库分为两个页签。</summary>
    public partial class TaskConfigurationView : UserControl
    {
        /// <summary>请求定位现有页面；此事件不代表设备动作。</summary>
        public event EventHandler<TaskReferenceEventArgs> ReferenceRequested;
        /// <summary>请求查看当前正式 Runtime 流程；导航不会应用草稿或执行设备动作。</summary>
        public event EventHandler CurrentFlowRequested;
        /// <summary>草稿与运行文件的具体存储。</summary>
        private readonly TaskWorkspaceStore _store;
        /// <summary>正式 Host 提供的真实任务能力；预览工具未提供时明确只检查定义。</summary>
        private readonly TaskDeviceCapabilities _executionCapabilities;
        /// <summary>当前设备稳定标识。</summary>
        private readonly string _machineId;
        /// <summary>由 Host 返回应用受阻原因，空值才允许应用。</summary>
        private readonly Func<string> _getBlockedReason;
        /// <summary>由 Host 提供当前正式 Runtime 的本地化状态文字。</summary>
        private readonly Func<string> _getRuntimeStateText;
        /// <summary>由 Host 提供当前正式 Runtime 的工位名称。</summary>
        private readonly Func<IEnumerable<string>> _getRuntimeStationNames;
        /// <summary>由 Host 提供与总览一致的自动启动拦截原因。</summary>
        private readonly Func<string> _getRuntimeReadinessReason;
        /// <summary>由 Host 判断当前是否使用任务列表控制。</summary>
        private readonly Func<bool> _isTaskEngineOwner;
        /// <summary>Host 提供的当前有效已发布任务会话；不从磁盘猜测运行来源。</summary>
        private Func<TaskMonitorSession> _getCurrentTaskSession;
        /// <summary>完整设备草稿。</summary>
        private TaskWorkspaceDocument _workspace;
        /// <summary>已应用文件的快照，仅供显示版本与草稿差异。</summary>
        private TaskWorkspaceDocument _activeWorkspace;
        /// <summary>最近一次明确检查的结果；任何编辑后失效。</summary>
        private IReadOnlyList<TaskValidationIssue> _lastCheckIssues;
        /// <summary>任务库所选草稿，显示行不成为第二份数据来源。</summary>
        private TaskWorkspaceTask SelectedLibraryTask { get { return (TasksGrid.SelectedItem as TaskLibraryRow)?.Source; } }
        /// <summary>已有逻辑对象目录。</summary>
        private TaskResourceCatalog _catalog;
        /// <summary>正在编辑的任务视图。</summary>
        private TaskEditorView _editor;
        /// <summary>编辑器对应的草稿条目。</summary>
        private TaskWorkspaceTask _editingEntry;
        /// <summary>当前工位对象，表单提交时写回。</summary>
        private StationTaskConfiguration _station;
        /// <summary>构造和刷新时不记录为人员编辑。</summary>
        private bool _loading;
        /// <summary>设备清单或任务库是否存在未保存修改。</summary>
        private bool _dirty;
        /// <summary>草稿基准已失效时禁止应用，避免界面仍暗示可以覆盖。</summary>
        private bool _sourceChanged;
        /// <summary>页面当前语言。</summary>
        private string _language = "zh-CN";
        /// <summary>用于及时解释应用按钮禁用原因的页面计时器。</summary>
        private readonly DispatcherTimer _timer;
        /// <summary>工位名称输入。</summary>
        private TextBox _stationName;
        /// <summary>周期轮询间隔输入。</summary>
        private TextBox _interval;
        /// <summary>工位任务与运行方式选择。</summary>
        private ComboBox _stationTask, _mode;
        /// <summary>请求与完成变量选择。</summary>
        private ComboBox _request, _complete;
        /// <summary>是否参与运行。</summary>
        private CheckBox _enabled;
        /// <summary>仅请求模式可见的字段容器。</summary>
        private StackPanel _signals;
        /// <summary>设备显示名称。</summary>
        private TextBox _machineName;
        /// <summary>共享资源名称，每行一项。</summary>
        private TextBox _resources;
        /// <summary>可选初始化任务。</summary>
        private ComboBox _initialization;

        /// <summary>接入设备目录、Host 应用边界和正式 Runtime 状态，不持有运动卡。</summary>
        public TaskConfigurationView(
            MachineConfigurationStore store,
            string machineId,
            Func<string> getBlockedReason,
            Func<string> getRuntimeStateText = null,
            Func<IEnumerable<string>> getRuntimeStationNames = null,
            Func<string> getRuntimeReadinessReason = null,
            Func<bool> isTaskEngineOwner = null,
            TaskDeviceCapabilities executionCapabilities = null)
        {
            _executionCapabilities = executionCapabilities;
            _store = new TaskWorkspaceStore(store, executionCapabilities); _machineId = machineId; _getBlockedReason = getBlockedReason ?? (() => "未提供运行会话状态，暂不能应用。");
            _getRuntimeStateText = getRuntimeStateText;
            _getRuntimeStationNames = getRuntimeStationNames;
            _getRuntimeReadinessReason = getRuntimeReadinessReason;
            _isTaskEngineOwner = isTaskEngineOwner;
            InitializeComponent();
            UpdateHeaders(); IssuesGrid.Visibility = Visibility.Collapsed; ConfigIssueRow.Height = new GridLength(0);
            AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler(TextChanged));
            AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler(ChoiceChanged));
            AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler(ToggleChanged)); AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler(ToggleChanged));
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) }; _timer.Tick += RefreshPermission;
            Loaded += ViewLoaded; Unloaded += ViewUnloaded;
            LoadWorkspace();
        }
        /// <summary>接入当前有效任务会话的只读查询，不加载配置或控制设备。</summary>
        public void SetCurrentTaskSessionProvider(Func<TaskMonitorSession> provider)
        {
            _getCurrentTaskSession = provider;
            RefreshPermission(null, EventArgs.Empty);
        }

        /// <summary>只翻译界面，稳定对象名称与 ID 保持原值。</summary>
        public void SetLanguage(string language)
        {
            _language = language; TaskEditingUi.SetLanguage(this, language);
            UpdateHeaders();
            if (_editor != null) _editor.SetLanguage(language);
            else if (ReadSettings()) Rebuild();
            if (_workspace != null && _editor == null) SetStatusText(_dirty ? T("有未保存修改，运行版本未改变")
                : _workspace.SavedAt == default(DateTime) ? T("已从运行文件载入，尚无草稿") : T("草稿与现有目录已载入") + " · " + _workspace.SavedAt.ToString("yyyy-MM-dd HH:mm:ss"));
            RefreshPermission(null, EventArgs.Empty);
        }
        /// <summary>读取本页语言文字。</summary>
        private string T(string value) { return TaskEditingUi.T(this, value); }
        /// <summary>更新普通状态并清除已过期的错误详情。</summary>
        private void SetStatusText(string text)
        {
            StatusText.Text = text ?? string.Empty;
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x17, 0x6B, 0xD0));
            if (_sourceReadFailed && _sourceReadError != null)
            {
                ErrorDiagnosticsText.Text = new InvalidOperationException(T("任务来源无法读取，请先恢复读取"), _sourceReadError).ToString();
                ErrorDiagnosticsExpander.Visibility = Visibility.Visible;
            }
            else
            {
                ErrorDiagnosticsText.Clear();
                ErrorDiagnosticsExpander.IsExpanded = false;
                ErrorDiagnosticsExpander.Visibility = Visibility.Collapsed;
            }
        }
        /// <summary>主动操作的结果需要可见；输入时的普通状态更新不改变人员滚动位置。</summary>
        private void SetActionStatusText(string text)
        {
            SetStatusText(text);
            RevealStatusText();
        }
        /// <summary>回到消息首行，并将消息带入状态区视口。</summary>
        private void RevealStatusText()
        {
            StatusMessageScroll.ScrollToTop();
            ConfigurationStatusScroll.UpdateLayout();
            StatusText.BringIntoView();
        }
        /// <summary>缺少轴参数文件时提示操作员去映射页处理；完整异常留在默认收起的详情里。</summary>
        private void ShowError(Exception error)
        {
            string message = error.Message;
            var missingFile = error as FileNotFoundException;
            string missingPath = missingFile?.FileName?.Replace(
                Path.AltDirectorySeparatorChar,
                Path.DirectorySeparatorChar);
            string axisParametersSuffix = Path.Combine("HardwareConfig", "AxisParameters.json");
            if (missingPath != null && missingPath.EndsWith(axisParametersSuffix, StringComparison.OrdinalIgnoreCase))
                message = T("缺少轴参数配置操作指引");

            StatusText.Text = message;
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xB4, 0x23, 0x18));
            ErrorDiagnosticsText.Text = error.ToString();
            ErrorDiagnosticsExpander.IsExpanded = false;
            ErrorDiagnosticsExpander.Visibility = Visibility.Visible;
            RevealStatusText();
        }
        /// <summary>完整读取草稿、目录与来源状态后再替换页面；读取失败保留本窗口的编辑内容。</summary>
        public void LoadWorkspace()
        {
            try
            {
                var loaded = _store.Load(_machineId);
                var catalog = _store.LoadCatalog(loaded);
                var active = _store.Load(_machineId, false);
                bool sourceChanged = _store.ComputeSignature(_machineId) != loaded.BaseSignature;
                string preparationSignature = _store.ComputePreparationSignature(_machineId);

                _workspace = loaded;
                _catalog = catalog;
                _activeWorkspace = active;
                _lastCheckIssues = null;
                _sourceChanged = sourceChanged;
                _observedPreparationSignature = preparationSignature;
                _sourceReadFailed = false;
                _sourceReadError = null;
                _editor = null;
                ContentHost.Content = MainTabs;
                ShowEditor(false);
                _dirty = false;
                Rebuild();
                SetStatusText(_workspace.SavedAt == default(DateTime)
                    ? T("已从运行文件载入，尚无草稿")
                    : T("草稿与现有目录已载入") + " · " + _workspace.SavedAt.ToString("yyyy-MM-dd HH:mm:ss"));
            }
            catch (Exception error) { ShowError(error); }
            RefreshPermission(null, EventArgs.Empty);
        }
        /// <summary>页面可见时更新按钮边界。</summary>
        private void ViewLoaded(object sender, RoutedEventArgs e) { RefreshPermission(sender, e); _timer.Start(); }
        /// <summary>移出页面停止轮询，草稿仍保留在页面对象中。</summary>
        private void ViewUnloaded(object sender, RoutedEventArgs e) { _timer.Stop(); }
        /// <summary>宽屏并列工位与草稿表单，窄屏上下排列并保留足够的可滚动高度。</summary>
        private void StationWorkspace_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            bool narrow = e.NewSize.Width < 1000;
            if ((Grid.GetColumn(StationSettingsCard) == 0) == narrow) return;
            StationWorkspaceScroll.VerticalScrollBarVisibility = narrow ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
            NarrowStationIdentity.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
            StationWorkspace.Height = narrow ? 760 : double.NaN;
            StationListColumn.Width = new GridLength(narrow ? 1 : 1.6, GridUnitType.Star);
            StationGapColumn.Width = new GridLength(narrow ? 0 : 10);
            StationSettingsColumn.Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            StationListRow.Height = narrow ? new GridLength(300) : new GridLength(1, GridUnitType.Star);
            StationGapRow.Height = new GridLength(narrow ? 8 : 0);
            StationSettingsRow.Height = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            Grid.SetColumn(StationSettingsCard, narrow ? 0 : 2);
            Grid.SetRow(StationSettingsCard, narrow ? 2 : 0);
        }

        /// <summary>即时更新草稿状态，不改变当前会话的只读来源。</summary>
        private void RefreshDraftSummaryCard()
        {
            DraftSummaryText.Text = T(_workspace == null ? "编辑状态未知" : _dirty ? "有未保存编辑" : "无待保存编辑")
                + (_workspace != null && _sourceChanged ? " · " + T("草稿基准待核查") : string.Empty);
        }

        /// <summary>从 Host 查询当前有效会话；查询失败时保留原因并使旧身份失效。</summary>
        private TaskSessionMetadata ReadCurrentSessionMetadata(out string sessionError)
        {
            sessionError = null;
            if (_getCurrentTaskSession == null) return null;
            try { return _getCurrentTaskSession()?.PublishedMetadata; }
            catch (Exception error) { sessionError = error.GetBaseException().Message; return null; }
        }

        /// <summary>刷新三张来源卡片，供编辑状态变化时使用。</summary>
        private void RefreshSummaryCards()
        {
            var metadata = ReadCurrentSessionMetadata(out string sessionError);
            DisplaySummaryCards(metadata, sessionError);
        }

        /// <summary>分别展示草稿、文件快照和当前有效会话，不用文件版本代替运行会话。</summary>
        private void DisplaySummaryCards(TaskSessionMetadata metadata, string sessionError)
        {
            RefreshDraftSummaryCard();
            FileSummaryText.Text = _activeWorkspace?.Tasks == null ? T("文件快照未提供")
                : string.Format(T("已载入文件快照数量格式"), _activeWorkspace.Tasks.Count);
            bool english = _language == "en-US";
            SessionSummaryText.Text = sessionError != null
                ? (english ? "Task session read failed: " : "任务会话信息读取失败：") + sessionError
                : metadata != null ? metadata.FormatSummary(english)
                : _getCurrentTaskSession == null ? T("会话信息未提供")
                : english ? "No task session is currently confirmed." : "当前没有可确认的任务会话。";
            SessionSummaryText.ToolTip = metadata == null ? SessionSummaryText.Text : metadata.FormatDetails(english);
        }

        /// <summary>显示应用禁用原因，并在来源变化时提供保留草稿或重新载入入口。</summary>
        private void RefreshPermission(object sender, EventArgs e)
        {
            RefreshPreparationSource();
            RefreshRuntimeContext();
            bool english = _language == "en-US";
            TaskSessionMetadata metadata = ReadCurrentSessionMetadata(out string sessionError);
            string sourceSummary = metadata == null
                ? TaskSourceSummary.Build(_workspace, _activeWorkspace, _dirty, _sourceChanged, english)
                : TaskSourceSummary.BuildWithSession(_workspace, _activeWorkspace, _dirty, _sourceChanged, english, metadata);
            if (metadata == null && _getCurrentTaskSession != null)
                sourceSummary += sessionError == null
                    ? (english ? "\nNo task session is currently confirmed." : "\n当前没有可确认的任务会话。")
                    : (english ? "\nTask session read failed: " : "\n任务会话信息读取失败：") + sessionError;
            SourceSummaryText.Text = sourceSummary;
            DisplaySummaryCards(metadata, sessionError);
            _editor?.SetSourceSummary(sourceSummary);
            string reason = _getBlockedReason();
            bool pending;
            string pendingError = null;
            try { pending = _store.HasPendingApply(_machineId); }
            catch (Exception error) { pending = true; pendingError = error.Message; }
            if (_workspace == null) reason = T("任务配置尚未成功加载，请先处理上方错误提示");
            if (_editor != null) reason = T("请先保存并返回任务配置");
            if (_sourceChanged) reason = T(_sourceReadFailed ? "任务来源无法读取，请先恢复读取" : "来源文件已经变化，请先同步草稿或重新载入运行文件");
            if (pending) reason = pendingError == null ? T("上次应用未完成，请先恢复")
                : (_language == "en-US" ? "Cannot verify task recovery marker: " : "无法确认任务恢复标记：") + pendingError;
            ApplyButton.IsEnabled = _workspace != null && string.IsNullOrEmpty(reason); ApplyButton.ToolTip = reason;
            RebaseDraftButton.Visibility = _workspace != null && _sourceChanged ? Visibility.Visible : Visibility.Collapsed;
            RebaseDraftButton.IsEnabled = _workspace != null && _editor == null && !pending && !_sourceReadFailed;
            ReloadRuntimeButton.Visibility = _workspace != null && _sourceChanged ? Visibility.Visible : Visibility.Collapsed;
            ReloadRuntimeButton.IsEnabled = !_sourceReadFailed;
            RebaseDraftButton.ToolTip = T(_sourceReadFailed ? "任务来源无法读取，请先恢复读取" : "保留草稿并同步来源提示");
            ReloadRuntimeButton.ToolTip = T(_sourceReadFailed ? "任务来源无法读取，请先恢复读取" : "重新载入运行文件提示");
            SaveAndCheckButton.IsEnabled = _workspace != null;
            BackupRestoreButton.IsEnabled = _workspace != null;
            ContentHost.IsEnabled = _workspace != null;
            ApplyReasonText.Text = reason ?? T("仅更新任务文件，不启动设备");
            RecoverButton.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
        }
        /// <summary>展示正式 Runtime 状态、启动拦截原因和工位清单，避免把当前草稿误认为正在运行。</summary>
        private void RefreshRuntimeContext()
        {
            if (_getRuntimeStateText == null || _getRuntimeStationNames == null)
            {
                RuntimeContextPanel.Visibility = Visibility.Collapsed;
                return;
            }

            RuntimeContextPanel.Visibility = Visibility.Visible;
            string stateText = _getRuntimeStateText();
            RuntimeStateText.Text = T("正式设备当前状态") + "："
                + (string.IsNullOrWhiteSpace(stateText) ? T("没有连接运行时") : T(stateText));
            string readinessReason = _getRuntimeReadinessReason == null ? null : _getRuntimeReadinessReason();
            RuntimeReadinessText.Text = string.IsNullOrWhiteSpace(readinessReason)
                ? string.Empty
                : T("启动拦截原因") + "：" + readinessReason;
            RuntimeReadinessText.Visibility = string.IsNullOrWhiteSpace(readinessReason)
                ? Visibility.Collapsed
                : Visibility.Visible;
            bool taskEngineOwner = _isTaskEngineOwner != null && _isTaskEngineOwner();
            string[] stationNames = (taskEngineOwner ? Enumerable.Empty<string>() : _getRuntimeStationNames() ?? Enumerable.Empty<string>())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToArray();
            RuntimeStationsText.Text = taskEngineOwner ? T("任务运行工位查看指引")
                : T("设备流程工位") + "：" + (stationNames.Length == 0 ? T("没有自动工位") : string.Join("、", stationNames));
            RuntimeStationsText.ToolTip = taskEngineOwner ? RuntimeStationsText.Text : T("运行工位说明");

            StationTaskConfiguration[] draftStations = _workspace?.Machine?.Stations?
                .Where(station => station != null)
                .ToArray() ?? new StationTaskConfiguration[0];
            TaskWorkspaceTask[] draftTasks = _workspace?.Tasks?
                .Where(item => item != null)
                .ToArray() ?? new TaskWorkspaceTask[0];
            int enabledDraftStations = draftStations.Count(station => station.Enabled);
            var draftAssignments = new List<string>();
            foreach (StationTaskConfiguration station in draftStations)
            {
                string stationName = string.IsNullOrWhiteSpace(station.Name) ? station.StationId : station.Name;
                TaskWorkspaceTask assignedTask = draftTasks.FirstOrDefault(item => item.Definition != null
                    && string.Equals(item.Definition.TaskId, station.TaskId, StringComparison.OrdinalIgnoreCase));
                string taskName = assignedTask?.Definition?.Name;
                if (string.IsNullOrWhiteSpace(taskName))
                {
                    taskName = string.IsNullOrWhiteSpace(station.TaskId)
                        ? T("任务未分配")
                        : station.TaskId + "（" + T("任务定义未找到") + "）";
                }

                draftAssignments.Add(stationName + " → " + taskName + "（"
                    + T(station.Enabled ? "工位参与运行" : "工位已停用") + "）");
            }

            TaskDraftStationsText.Text = T("任务草稿工位") + "（"
                + string.Format(T("草稿工位参与数量"), enabledDraftStations, draftStations.Length) + "）："
                + (draftAssignments.Count == 0 ? T("草稿没有工位") : string.Join("；", draftAssignments));
            TaskDraftStationsText.ToolTip = TaskDraftStationsText.Text;
            TaskDraftBoundaryText.Text = T("草稿工位运行边界");
            TaskDraftStationsText.Visibility = _workspace == null ? Visibility.Collapsed : Visibility.Visible;
            TaskDraftBoundaryText.Visibility = _workspace == null ? Visibility.Collapsed : Visibility.Visible;
            CurrentFlowButton.Content = T(taskEngineOwner ? "查看任务运行" : "查看当前流程状态");
            CurrentFlowButton.IsEnabled = taskEngineOwner || stationNames.Length > 0;
            CurrentFlowButton.ToolTip = taskEngineOwner ? T("查看任务运行")
                : stationNames.Length > 0 ? T("查看当前流程状态") : T("没有自动工位");
        }
        /// <summary>只读跳转到 Host 当前流程页，不更改草稿或运行状态。</summary>
        private void CurrentFlow_Click(object sender, RoutedEventArgs e)
        {
            CurrentFlowRequested?.Invoke(this, EventArgs.Empty);
        }
        /// <summary>只标记当前配置表单修改；搜索和只读诊断不修改草稿。</summary>
        private void TextChanged(object sender, TextChangedEventArgs e)
        {
            if (e.OriginalSource is DependencyObject source && (StationDetails.IsAncestorOf(source) || MachineDetails.IsAncestorOf(source))) MarkDirty();
        }
        /// <summary>下拉只在详情区变更时标记，选择列表行不算编辑。</summary>
        private void ChoiceChanged(object sender, SelectionChangedEventArgs e)
        { if (e.OriginalSource is DependencyObject source && (StationDetails.IsAncestorOf(source) || MachineDetails.IsAncestorOf(source))) MarkDirty(); }
        /// <summary>工位参与标记等勾选需要保存草稿。</summary>
        private void ToggleChanged(object sender, RoutedEventArgs e)
        { if (e.OriginalSource is CheckBox source && (StationDetails.IsAncestorOf(source) || MachineDetails.IsAncestorOf(source))) MarkDirty(); }
        /// <summary>直接显示未保存状态，避免旧成功提示误导。</summary>
        private void MarkDirty()
        {
            if (_loading || _editor != null || _workspace == null) return;
            bool refreshRows = !_dirty || _lastCheckIssues != null;
            _dirty = true; _lastCheckIssues = null;
            RefreshSummaryCards();
            SetStatusText(T("有未保存修改，运行版本未改变")); ShowIssues(new TaskValidationIssue[0]);
            if (refreshRows) RefreshLibraryRows();
        }

        /// <summary>列本身不在可视树中，明确赋值表头以可靠切换局部语言资源。</summary>
        private void UpdateHeaders()
        {
            string[][] texts = { new[] { "参与运行", "工位名称", "执行任务", "运行方式", "文件快照版本", "操作" }, new[] { "任务名称", "任务标识", "文件快照版本", "草稿状态", "文件" }, new[] { "等级", "任务", "动作步骤", "字段", "说明" } };
            DataGrid[] grids = { StationsGrid, TasksGrid, IssuesGrid };
            for (int g = 0; g < grids.Length; g++) for (int i = 0; i < texts[g].Length; i++) grids[g].Columns[i].Header = T(texts[g][i]);
        }
        /// <summary>没有问题时收起空表，给工位和参数留出空间。</summary>
        private void ShowIssues(IReadOnlyList<TaskValidationIssue> issues)
        {
            IssuesGrid.ItemsSource = issues.Select(issue => new TaskEditingIssueRow(issue, _workspace?.Tasks.FirstOrDefault(task => task.Definition.TaskId == issue.TaskId)?.Definition, T(issue.Severity == TaskIssueSeverity.Error ? "错误" : "提醒"), T)).ToList();
            bool show = issues.Count > 0 && _editor == null;
            IssuesGrid.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            ConfigIssueRow.Height = new GridLength(show ? 140 : 0);
        }
        /// <summary>更新列表并按稳定标识恢复工位和任务选择；保存或换语言不跳回第一行。</summary>
        private void Rebuild()
        {
            if (_workspace == null) return;
            _lastCheckIssues = null;
            string stationId = _station?.StationId;
            _loading = true; _station = null;
            RefreshLibraryRows();
            StationsGrid.ItemsSource = _workspace.Machine.Stations.Select(item => new TaskStationEditRow(item,
                _workspace.Tasks.FirstOrDefault(task => string.Equals(task.Definition.TaskId, item.TaskId, StringComparison.OrdinalIgnoreCase))?.Definition.Name ?? item.TaskId, T(ModeName(item.Mode)), AppliedVersion(item.TaskId))).ToList();
            BuildMachine();
            var selected = StationsGrid.Items.Cast<TaskStationEditRow>().FirstOrDefault(item => string.Equals(item.Source.StationId, stationId, StringComparison.OrdinalIgnoreCase));
            if (selected != null) StationsGrid.SelectedItem = selected;
            else if (StationsGrid.Items.Count > 0) StationsGrid.SelectedIndex = 0;
            _loading = false; BuildStation((StationsGrid.SelectedItem as TaskStationEditRow)?.Source);
        }
        /// <summary>筛选隐藏选择后保留真实TaskId，不把首个匹配任务改成编辑目标。</summary>
        private string _librarySelectionId;
        /// <summary>列表重绑期间忽略自动产生的选择通知。</summary>
        private bool _refreshingLibraryFilter;
        /// <summary>只记录实际选中的任务身份，不写入工作区。</summary>
        private void LibrarySelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_refreshingLibraryFilter && SelectedLibraryTask != null) _librarySelectionId = SelectedLibraryTask.Definition.TaskId;
            if (!_refreshingLibraryFilter) { RefreshLibraryActionAvailability(); RefreshTaskDeletePermission(); }
        }
        /// <summary>搜索只改变可见任务，删除按钮仍按所选任务的真实引用刷新。</summary>
        private void LibrarySearchChanged(object sender, TextChangedEventArgs e) { if (_workspace != null) RefreshLibraryRows(); }
        /// <summary>只清查询文本，不修改任务定义或保存状态。</summary>
        private void ClearLibrarySearch_Click(object sender, RoutedEventArgs e) { LibrarySearchBox.Clear(); }
        /// <summary>按稳定标识刷新只读版本行并核对删除条件，保留选择，不改变草稿。</summary>
        private void RefreshLibraryRows()
        {
            if (_workspace == null) return;
            string id = SelectedLibraryTask?.Definition.TaskId ?? _librarySelectionId;
            string query = LibrarySearchBox.Text.Trim();
            var rows = _workspace.Tasks.Where(task => query.Length == 0
                || (task.Definition.Name ?? string.Empty).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                || (task.Definition.TaskId ?? string.Empty).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(task => new TaskLibraryRow(task, AppliedVersion(task.Definition.TaskId), DraftState(task))).ToList();
            _refreshingLibraryFilter = true;
            try
            {
                TasksGrid.ItemsSource = rows;
                TasksGrid.SelectedItem = rows.FirstOrDefault(row => string.Equals(row.Definition.TaskId, id, StringComparison.OrdinalIgnoreCase));
            }
            finally { _refreshingLibraryFilter = false; }
            _librarySelectionId = id;
            LibraryMatchText.Text = string.Format(T("任务筛选数量格式"), rows.Count, _workspace.Tasks.Count);
            LibraryEmptyText.Text = _workspace.Tasks.Count == 0 ? T("任务库为空") : rows.Count == 0 ? T("没有匹配任务") : string.Empty;
            ClearLibrarySearchButton.IsEnabled = LibrarySearchBox.Text.Length > 0;
            RefreshLibraryActionAvailability();
            RefreshTaskDeletePermission();
        }
        /// <summary>没有实际选中目标时，不开放复制、编辑或查看引用。</summary>
        private void RefreshLibraryActionAvailability()
        {
            bool selected = SelectedLibraryTask != null;
            foreach (Button button in new[] { CopyTaskButton, EditTaskButton, ReferencesButton })
            {
                button.IsEnabled = selected;
                button.ToolTip = selected ? null : T("请先选择任务");
            }
        }
        /// <summary>显示已应用文件修订号，新任务没有已应用版本。</summary>
        private string AppliedVersion(string taskId)
        {
            var active = _activeWorkspace?.Tasks.FirstOrDefault(task => string.Equals(task.Definition.TaskId, taskId, StringComparison.OrdinalIgnoreCase));
            return active == null ? T("未应用") : "R" + active.Definition.Revision;
        }
        /// <summary>明确区分草稿差异与最近检查结果，不把保存当成应用。</summary>
        private string DraftState(TaskWorkspaceTask task)
        {
            if (_lastCheckIssues != null)
            {
                var taskIssues = _lastCheckIssues.Where(issue => string.Equals(issue.TaskId, task.Definition.TaskId, StringComparison.OrdinalIgnoreCase)).ToList();
                return T(taskIssues.Any(issue => issue.Severity == TaskIssueSeverity.Error) ? "任务检查有错误"
                    : taskIssues.Count > 0 ? "任务检查有提醒" : "任务检查通过");
            }
            var active = _activeWorkspace?.Tasks.FirstOrDefault(item => string.Equals(item.FileName, task.FileName, StringComparison.OrdinalIgnoreCase));
            bool changed = active == null || TaskDefinitionJson.Serialize(active.Definition) != TaskDefinitionJson.Serialize(task.Definition);
            return T(!changed ? "无修改" : _dirty ? "未保存修改" : "草稿待检查");
        }
        /// <summary>设备设置使用现有任务标识和明确的一行一资源。</summary>
        private void BuildMachine()
        {
            MachineDetails.Children.Clear();
            _machineName = TaskEditingUi.Text(_workspace.Machine.Name); AddMachine("设备名称", _machineName);
            _initialization = TaskChoices(_workspace.Machine.InitializationTaskId); AddMachine("初始化任务(可选)", _initialization);
            _resources = TaskEditingUi.Text(string.Join(Environment.NewLine, _workspace.Machine.NamedResources ?? new List<string>())); _resources.AcceptsReturn = true; _resources.MinHeight = 65;
            AddMachine("共享资源(每行一个名称)", _resources); MachineDetails.Children.Add(TaskEditingUi.Label(this, "不填写Axis:或Output:前缀，轴和输出由目录提供"));
        }
        /// <summary>设备字段按固定顺序排列。</summary>
        private void AddMachine(string name, FrameworkElement control) { MachineDetails.Children.Add(TaskEditingUi.Label(this, name)); MachineDetails.Children.Add(control); }
        /// <summary>工位选择变化前提交原表单，错误数值保持原选择。</summary>
        private void StationSelected(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            if (!ReadSettings()) { _loading = true; StationsGrid.SelectedItem = StationsGrid.Items.Cast<TaskStationEditRow>().FirstOrDefault(item => ReferenceEquals(item.Source, _station)); _loading = false; return; }
            BuildStation((StationsGrid.SelectedItem as TaskStationEditRow)?.Source);
        }
        /// <summary>工位的请求字段仅在请求触发方式下显示。</summary>
        private void BuildStation(StationTaskConfiguration station)
        {
            _loading = true; _station = station; _stationName = null; StationDetails.Children.Clear();
            if (station == null) { StationDetails.Children.Add(TaskEditingUi.Label(this, "暂无工位，请新增工位")); UpdateStationIdentity(); _loading = false; return; }
            StationDetails.Children.Add(TaskEditingUi.Label(this, "工位标识")); StationDetails.Children.Add(new TextBlock { Text = station.StationId });
            _stationName = TaskEditingUi.Text(station.Name); AddStationField("工位名称", _stationName);
            _stationName.TextChanged += StationNameChanged;
            _stationTask = TaskChoices(station.TaskId); AddStationField("执行任务", _stationTask);
            _mode = TaskEditingUi.Choices(this, new object[] { StationTaskMode.Once, StationTaskMode.Cyclic, StationTaskMode.OnRequest }, new[] { "单次执行", "独立循环", "请求触发" }, station.Mode); AddStationField("运行方式", _mode);
            _enabled = new CheckBox { Content = T("参与运行"), IsChecked = station.Enabled, Margin = new Thickness(0, 7, 0, 3) }; StationDetails.Children.Add(_enabled);
            _interval = TaskEditingUi.Text(station.CycleIntervalMs.ToString()); AddStationField("周期间隔(ms)", _interval);
            var variables = _catalog.Variables.Where(item => item.Value == DeviceVariableType.Boolean).Select(item => item.Key).ToList();
            _request = TaskEditingUi.Names(variables, station.RequestVariable); _complete = TaskEditingUi.Names(variables, station.CompletedVariable);
            _signals = new StackPanel(); _signals.Children.Add(TaskEditingUi.Label(this, "请求变量(bool)")); _signals.Children.Add(_request); _signals.Children.Add(TaskEditingUi.Label(this, "完成变量(bool)")); _signals.Children.Add(_complete);
            StationDetails.Children.Add(_signals); _signals.Visibility = station.Mode == StationTaskMode.OnRequest ? Visibility.Visible : Visibility.Collapsed;
            _mode.SelectionChanged += ModeChanged; UpdateStationIdentity(); _loading = false;
        }
        /// <summary>窄屏固定显示当前真实工位的名称与稳定标识，随表单输入同步。</summary>
        private void UpdateStationIdentity()
        {
            string name = _stationName?.Text ?? _station?.Name;
            NarrowStationIdentityText.Text = _station == null ? T("未选择工位")
                : T("正在编辑工位") + "：" + (string.IsNullOrWhiteSpace(name) ? T("工位名称未填写") : name)
                    + " · " + _station.StationId;
        }
        /// <summary>输入名称只改变表单和身份提示，不提交配置。</summary>
        private void StationNameChanged(object sender, TextChangedEventArgs e) { UpdateStationIdentity(); }
        /// <summary>工位字段只负责显示和输入。</summary>
        private void AddStationField(string text, FrameworkElement control) { StationDetails.Children.Add(TaskEditingUi.Label(this, text)); StationDetails.Children.Add(control); }
        /// <summary>模式切换保留未提交输入，保存时清除不适用的握手引用。</summary>
        private void ModeChanged(object sender, SelectionChangedEventArgs e) { _signals.Visibility = (StationTaskMode)_mode.SelectedValue == StationTaskMode.OnRequest ? Visibility.Visible : Visibility.Collapsed; }
        /// <summary>显示任务名称，同时稳定保存 TaskId。</summary>
        private ComboBox TaskChoices(string id)
        {
            var box = new ComboBox { MinHeight = 30, SelectedValuePath = "Tag" }; box.Items.Add(new ComboBoxItem { Content = "", Tag = "" });
            foreach (var task in _workspace.Tasks)
                box.Items.Add(new ComboBoxItem { Content = task.Definition.Name + " [" + task.Definition.TaskId + "]",
                    Tag = string.Equals(task.Definition.TaskId, id, StringComparison.OrdinalIgnoreCase) ? id : task.Definition.TaskId });
            if (!string.IsNullOrEmpty(id) && !_workspace.Tasks.Any(task => string.Equals(task.Definition.TaskId, id, StringComparison.OrdinalIgnoreCase))) box.Items.Add(new ComboBoxItem { Content = id + " (?)", Tag = id });
            box.SelectedValue = id ?? ""; return box;
        }
        /// <summary>读取设备和工位字段，整数失败时不继续切换或保存。</summary>
        private bool ReadSettings()
        {
            if (_workspace == null || _machineName == null) return true;
            try
            {
                int interval = _station == null ? 20 : TaskEditingUi.Integer(_interval, "周期间隔(ms)");
                _workspace.Machine.Name = _machineName.Text;
                _workspace.Machine.InitializationTaskId = string.IsNullOrEmpty(_initialization.SelectedValue as string) ? null : (string)_initialization.SelectedValue;
                _workspace.Machine.NamedResources = _resources.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(value => value.Trim()).ToList();
                if (_station != null)
                {
                    _station.Name = _stationName.Text; _station.TaskId = _stationTask.SelectedValue as string ?? ""; _station.Mode = (StationTaskMode)_mode.SelectedValue;
                    _station.Enabled = _enabled.IsChecked == true; _station.CycleIntervalMs = interval;
                    _station.RequestVariable = _station.Mode == StationTaskMode.OnRequest ? _request.SelectedItem as string : null;
                    _station.CompletedVariable = _station.Mode == StationTaskMode.OnRequest ? _complete.SelectedItem as string : null;
                }
                return true;
            }
            catch (Exception error) { ShowError(error); return false; }
        }
        /// <summary>新增稳定标识工位，默认不参与运行，避免误加后自动开工。</summary>
        private void AddStation_Click(object sender, RoutedEventArgs e)
        { if (!ReadSettings() || _workspace == null) return; _workspace.Machine.Stations.Add(new StationTaskConfiguration { StationId = "Station_" + Guid.NewGuid().ToString("N").Substring(0, 8), Name = T("新工位"), TaskId = "", Enabled = false, Mode = StationTaskMode.Once }); _dirty = true; Rebuild(); StationsGrid.SelectedIndex = StationsGrid.Items.Count - 1; }
        /// <summary>移除仅改变草稿清单，需要明确确认。</summary>
        private void RemoveStation_Click(object sender, RoutedEventArgs e)
        { if (_station == null || MessageBox.Show(T("确认移除工位") + "：" + _station.Name, T("移除工位"), MessageBoxButton.YesNo) != MessageBoxResult.Yes) return; _workspace.Machine.Stations.Remove(_station); _dirty = true; Rebuild(); }
        /// <summary>新增任务有明确顺序根和无硬件延时，之后进入具体编辑。</summary>
        private void NewTask_Click(object sender, RoutedEventArgs e)
        {
            if (_workspace == null || !ReadSettings()) return; string id = "Task_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var root = (SequenceNode)TaskEditorNodes.New("Sequence"); root.Children.Add(TaskEditorNodes.New("Delay"));
            var entry = new TaskWorkspaceTask { FileName = id + ".json", Definition = new TaskDefinition { TaskId = id, Name = T("新任务"), Root = root } };
            _workspace.Tasks.Add(entry); _dirty = true; OpenEditor(entry);
        }
        /// <summary>复制完整任务但更新任务与内部节点 ID，子任务目标仍为原引用。</summary>
        private void CopyTask_Click(object sender, RoutedEventArgs e)
        {
            var source = SelectedLibraryTask;
            if (source == null || !ReadSettings()) return;
            var copy = TaskEditorNodes.CopyTask(source.Definition); copy.TaskId = "Task_" + Guid.NewGuid().ToString("N").Substring(0, 8); copy.Name += " " + T("副本"); copy.Revision = 1; TaskEditorNodes.RenewIds(copy.Root);
            var entry = new TaskWorkspaceTask { FileName = copy.TaskId + ".json", Definition = copy }; _workspace.Tasks.Add(entry); _dirty = true; OpenEditor(entry);
        }
        /// <summary>只导入受限 JSON 任务，不加载程序集或执行脚本。</summary>
        private void ImportTask_Click(object sender, RoutedEventArgs e)
        {
            if (_workspace == null || !ReadSettings()) return; var picker = new OpenFileDialog { Filter = "Task JSON|*.json", Multiselect = false };
            if (picker.ShowDialog() != true) return;
            try
            {
                if (new FileInfo(picker.FileName).Length > 4 * 1024 * 1024) throw new InvalidDataException("任务文件超过 4MB，拒绝导入。");
                TaskDefinition task = TaskDefinitionJson.Deserialize(File.ReadAllText(picker.FileName));
                var nodes = AllNodes(task.Root).ToList();
                if (string.IsNullOrWhiteSpace(task.TaskId) || task.TaskId != task.TaskId.Trim() || !(task.Root is SequenceNode)
                    || nodes.Any(node => string.IsNullOrWhiteSpace(node.Id) || node.Id != node.Id.Trim())
                    || nodes.Select(node => node.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != nodes.Count)
                    throw new InvalidDataException("导入任务的标识、顺序根或节点 ID 无效，请先修正源文件。");
                if (_workspace.Tasks.Any(item => string.Equals(item.Definition.TaskId, task.TaskId, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("任务 ID 已存在，请使用复制任务建立独立任务。");
                var entry = new TaskWorkspaceTask { FileName = "Task_" + Guid.NewGuid().ToString("N") + ".json", Definition = task }; _workspace.Tasks.Add(entry); _dirty = true; OpenEditor(entry);
            }
            catch (Exception error) { ShowError(error); }
        }
        /// <summary>选中任务后打开独立定义副本，重复双击空白行无作用。</summary>
        private void EditTask_Click(object sender, RoutedEventArgs e) { if (SelectedLibraryTask != null && ReadSettings()) OpenEditor(SelectedLibraryTask); }
        /// <summary>工位行内直接进入其任务，仍先提交当前有效配置字段。</summary>
        private void EditStationTask_Click(object sender, RoutedEventArgs e)
        {
            if (!ReadSettings() || !(((Button)sender).Tag is StationTaskConfiguration station)) return;
            OpenTaskById(station.TaskId);
        }
        /// <summary>按稳定标识进入已有任务，供工位行和独立四页预览共用。</summary>
        public bool OpenTaskById(string taskId)
        {
            if (_workspace == null || !ReadSettings()) return false;
            if (_editor != null)
            {
                if (string.Equals(_editingEntry.Definition.TaskId, taskId, StringComparison.OrdinalIgnoreCase)) return true;
                if (!_editor.ConfirmLeave()) return false;
            }
            var task = _workspace.Tasks.FirstOrDefault(item => string.Equals(item.Definition.TaskId, taskId, StringComparison.OrdinalIgnoreCase));
            if (task == null) { SetActionStatusText(T("未选择或引用失效")); return false; }
            OpenEditor(task); return _editor != null;
        }
        /// <summary>外部预览导航返回配置页时，仍先让人员决定如何处理未保存任务。</summary>
        public bool ReturnToConfiguration()
        {
            if (_editor == null) return true;
            if (!_editor.ConfirmLeave()) return false;
            EditorBack(this, EventArgs.Empty); return true;
        }
        /// <summary>编辑器占用同一工作区，已有页面跳转后可以返回原草稿。</summary>
        private void OpenEditor(TaskWorkspaceTask entry)
        {
            try
            {
                _catalog = _store.LoadCatalog(_workspace); _editingEntry = entry;
                _editor = new TaskEditorView(entry.Definition, _catalog,
                    () => _store.LoadCatalog(_workspace),
                    _workspace.Tasks.Where(item => !ReferenceEquals(item, entry)).Select(item => item.Definition));
                _editor.SetLanguage(_language); _editor.DraftSaved += EditorSaved; _editor.BackRequested += EditorBack; _editor.ReferenceRequested += EditorReference;
                _editor.BackupExportRequested += EditorExportBackup;
                ContentHost.Content = _editor; ShowEditor(true); RefreshPermission(null, EventArgs.Empty);
            }
            catch (Exception error) { ShowError(error); }
        }
        /// <summary>先把完整设备草稿写成功，编辑器才显示保存成功。</summary>
        private void EditorSaved(object sender, EventArgs e)
        {
            var previous = _editingEntry.Definition;
            try { _editingEntry.Definition = TaskEditorNodes.CopyTask(_editor.Definition); _store.SaveDraft(_workspace); _dirty = false; _lastCheckIssues = null; SetStatusText(T("草稿已保存，未改变运行版本")); }
            catch { _editingEntry.Definition = previous; throw; }
        }
        /// <summary>导出完整设备工作区并带入编辑器未保存的任务字段。</summary>
        private void EditorExportBackup(object sender, EventArgs e)
        {
            var editedTask = new TaskWorkspaceTask { FileName = _editingEntry.FileName, Definition = _editor.Definition };
            ExportWorkspaceBackup(editedTask);
        }
        /// <summary>返回任务库；没有保存任务修改时保留仍然有效的整机检查结果。</summary>
        private void EditorBack(object sender, EventArgs e)
        {
            IReadOnlyList<TaskValidationIssue> previousCheckIssues = _lastCheckIssues;

            // 返回目标是刚编辑的真实任务；即使被当前筛选隐藏，也不回退到进入前的旧选择。
            _librarySelectionId = _editingEntry?.Definition.TaskId;
            _refreshingLibraryFilter = true;
            try { TasksGrid.SelectedItem = null; }
            finally { _refreshingLibraryFilter = false; }
            ContentHost.Content = MainTabs;
            _editor = null;
            ShowEditor(false);
            Rebuild();

            if (previousCheckIssues != null)
            {
                _lastCheckIssues = previousCheckIssues;
                ShowIssues(_lastCheckIssues);
                RefreshLibraryRows();
            }

            MainTabs.SelectedIndex = 1;
            TasksGrid.SelectedItem = TasksGrid.Items.Cast<TaskLibraryRow>()
                .FirstOrDefault(row => ReferenceEquals(row.Source, _editingEntry));
            SetStatusText(_dirty ? T("有未保存修改，运行版本未改变") : T("草稿与现有目录已载入"));
            RefreshPermission(null, EventArgs.Empty);
        }
        /// <summary>编辑器显示自己的工具栏和问题表，避免同屏重复两套保存检查。</summary>
        private void ShowEditor(bool visible)
        {
            ConfigToolbar.Visibility = ConfigurationStatus.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
            ShowIssues(new TaskValidationIssue[0]);
        }
        /// <summary>只传递已有对象定位请求。</summary>
        private void EditorReference(object sender, TaskReferenceEventArgs e) { ReferenceRequested?.Invoke(this, e); }
        /// <summary>查看引用与删除保护使用同一来源，包含独立准备项及具体文件字段。</summary>
        private List<string> References(string id)
        {
            return _store.FindTaskReferences(_workspace, id).Select(ReferenceText).ToList();
        }
        /// <summary>沿明确子节点关系遍历，不反射读取对象。</summary>
        private static IEnumerable<TaskNode> AllNodes(TaskNode node)
        { yield return node; foreach (var child in TaskEditorNodes.Children(node)) foreach (var item in AllNodes(child)) yield return item; }
        /// <summary>引用窗口只显示关系，不改变任务。</summary>
        private void References_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedLibraryTask == null || !ReadSettings()) return;
            try { MessageBox.Show(string.Join(Environment.NewLine, References(SelectedLibraryTask.Definition.TaskId)), T("查看引用")); }
            catch (Exception error) { ShowError(error); }
        }
        /// <summary>被引用任务不能删除；确认后只从草稿移除。</summary>
        private void RemoveTask_Click(object sender, RoutedEventArgs e)
        {
            var task = SelectedLibraryTask;
            if (task == null || !ReadSettings()) return;
            try
            {
                var references = References(task.Definition.TaskId);
                if (references.Count > 0) { SetActionStatusText(T("任务仍被引用，不能删除") + "\n" + string.Join("\n", references)); return; }
                if (MessageBox.Show(T("确认删除任务") + "：" + task.Definition.Name, T("删除任务"), MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                // 确认窗口停留期间准备文件可能被外部修改，删除草稿前再读一次真实引用。
                references = References(task.Definition.TaskId);
                if (references.Count > 0) { SetActionStatusText(T("任务仍被引用，不能删除") + "\n" + string.Join("\n", references)); return; }
                _workspace.Tasks.Remove(task); _dirty = true; Rebuild();
            }
            catch (Exception error) { RefreshTaskDeletePermission(); ShowError(error); }
        }
        /// <summary>一次完成草稿保存和完整检查；检查发现问题时仍保留草稿供修改。</summary>
        private void SaveAndCheck_Click(object sender, RoutedEventArgs e)
        {
            if (!SaveDraft() || _editor != null) return;
            Check_Click(sender, e);
            if (_lastCheckIssues != null) SetStatusText(T("草稿已保存") + "；" + StatusText.Text);
            RevealStatusText();
        }
        /// <summary>打开低频维护操作，不把备份和草稿恢复按钮放在日常工具栏。</summary>
        private void BackupRestore_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new TaskBackupActionsWindow(_language) { Owner = Window.GetWindow(this) };
            if (dialog.ShowDialog() != true) return;

            if (dialog.SelectedAction == TaskBackupAction.Export) ExportWorkspaceBackup(null);
            else if (dialog.SelectedAction == TaskBackupAction.Restore) RestoreWorkspaceBackup();
            else if (dialog.SelectedAction == TaskBackupAction.LoadLatestDraft) LoadLatestDraft();
            else if (dialog.SelectedAction == TaskBackupAction.ReloadRuntimeFiles) ReloadRuntimeFiles();
        }
        /// <summary>从来源变更提示旁直接进入安全重载流程；重载前仍会确认并保留草稿副本。</summary>
        private void ReloadRuntimeFiles_Click(object sender, RoutedEventArgs e)
        {
            ReloadRuntimeFiles();
        }
        /// <summary>先保留完整草稿副本，再把草稿绑定到当前来源并立即检查引用。</summary>
        private void RebaseDraft_Click(object sender, RoutedEventArgs e)
        {
            if (_workspace == null || _editor != null || !CanResolveTaskSources() || !ReadSettings()) return;
            if (MessageBox.Show(T("保留草稿并同步来源确认说明"), T("保留草稿并同步来源"),
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            if (!CanResolveTaskSources()) return;

            try
            {
                _store.SaveDraft(_workspace);
                _dirty = false;
                string backupPath = _store.RebaseDraftToCurrentSources(_workspace);
                _activeWorkspace = _store.Load(_machineId, false);
                _catalog = _store.LoadCatalog(_workspace);
                string preparationSignature;
                _sourceChanged = ComputeSourceSignatureForAction(out preparationSignature) != _workspace.BaseSignature;
                Rebuild();

                _lastCheckIssues = _store.Check(_workspace);
                ShowIssues(_lastCheckIssues);
                RefreshLibraryRows();
                _sourceChanged = ComputeSourceSignatureForAction(out preparationSignature) != _workspace.BaseSignature;
                _observedPreparationSignature = preparationSignature;
                _sourceReadFailed = false;
                _sourceReadError = null;
                bool hasErrors = _lastCheckIssues.Any(issue => issue.Severity == TaskIssueSeverity.Error);
                SetActionStatusText(T("草稿来源已同步") + "；" + T(hasErrors ? "检查发现问题" : _lastCheckIssues.Count > 0 ? "检查有提醒" : "检查通过")
                    + (_sourceChanged ? "；" + T("来源文件再次变化，请重新检查") : "") + "\n"
                    + T("同步前草稿备份") + "：" + backupPath);
                RefreshPermission(null, EventArgs.Empty);
            }
            catch (Exception error) { RefreshSourceAfterActionFailure(); ShowError(error); RefreshPermission(null, EventArgs.Empty); }
        }
        /// <summary>把当前页面内容导出到人员选择的 JSON 文件，可选带入尚未保存的任务。</summary>
        private void ExportWorkspaceBackup(TaskWorkspaceTask editedTask)
        {
            if (_workspace == null || !ReadSettings()) return;
            var dialog = new SaveFileDialog
            {
                Title = T("请选择任务配置备份保存位置"),
                Filter = T("任务配置备份文件过滤器"),
                DefaultExt = ".json",
                AddExtension = true,
                OverwritePrompt = true,
                FileName = "TaskBackup_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".json"
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

            try
            {
                _store.ExportBackup(_workspace, dialog.FileName, editedTask);
                SetActionStatusText(T("任务配置备份已导出") + "\n" + dialog.FileName);
                if (editedTask != null) _editor?.ShowBackupExported(dialog.FileName);
            }
            catch (Exception error) { ShowError(error); }
        }
        /// <summary>确认后把所选备份恢复为草稿，自动检查且不改写运行任务文件。</summary>
        private void RestoreWorkspaceBackup()
        {
            var dialog = new OpenFileDialog
            {
                Title = T("请选择要恢复的任务配置备份"),
                Filter = T("任务配置备份文件过滤器"),
                CheckFileExists = true,
                Multiselect = false
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            if (MessageBox.Show(T("恢复备份确认说明") + "\n" + dialog.FileName, T("从备份恢复为草稿"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            if (_workspace == null || !ReadSettings()) return;

            try
            {
                string currentCopy = _store.SaveLocalCopy(_workspace);
                _store.ImportBackupAsDraft(_machineId, dialog.FileName);
                TaskWorkspaceDocument previousWorkspace = _workspace;
                LoadWorkspace();
                if (_workspace == null || ReferenceEquals(_workspace, previousWorkspace)) return;

                Check_Click(this, new RoutedEventArgs());
                if (_lastCheckIssues != null)
                {
                    bool hasErrors = _lastCheckIssues.Any(item => item.Severity == TaskIssueSeverity.Error);
                    SetActionStatusText(T(hasErrors ? "备份已恢复为草稿，检查发现问题"
                        : _lastCheckIssues.Count > 0 ? "备份已恢复为草稿，检查有提醒" : "备份已恢复为草稿，检查通过")
                        + "\n" + T("恢复前草稿已自动保留") + "：" + currentCopy);
                }
            }
            catch (Exception error) { ShowError(error); }
        }
        /// <summary>读取其他窗口刚保存的草稿前，自动保留本窗口当前内容。</summary>
        private void LoadLatestDraft()
        {
            if (MessageBox.Show(T("读取最新草稿确认说明"), T("重新读取最新草稿"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            try
            {
                string currentCopy = null;
                if (_workspace != null)
                {
                    if (!ReadSettings()) return;
                    currentCopy = _store.SaveLocalCopy(_workspace);
                }
                TaskWorkspaceDocument previousWorkspace = _workspace;
                LoadWorkspace();
                if (currentCopy != null && !ReferenceEquals(_workspace, previousWorkspace))
                    SetActionStatusText(T("草稿已重新读取，当前内容已保留") + "\n" + currentCopy);
            }
            catch (Exception error) { ShowError(error); }
        }
        /// <summary>编辑器打开时由编辑器提交字段；否则保存完整工位清单。</summary>
        private bool SaveDraft()
        {
            if (_editor != null) return _editor.SaveDraft();
            if (_workspace == null || !ReadSettings()) return false;
            try { _store.SaveDraft(_workspace); _dirty = false; SetStatusText(T("草稿已保存，未改变运行版本")); Rebuild(); return true; }
            catch (Exception error) { ShowError(error); return false; }
        }
        /// <summary>使用最新目录与准备配置检查；检查过程中来源再次变化时不保留过期的成功结论。</summary>
        private void Check_Click(object sender, RoutedEventArgs e)
        {
            if (_editor != null) { SetStatusText(T("请先保存并返回任务配置")); return; }
            if (_workspace == null || !ReadSettings()) return;
            try
            {
                string preparationSignature;
                string before = ComputeSourceSignatureForAction(out preparationSignature);
                var issues = _store.Check(_workspace);
                string after = ComputeSourceSignatureForAction(out preparationSignature);
                _observedPreparationSignature = preparationSignature;
                _sourceReadFailed = false;
                _sourceReadError = null;
                _sourceChanged = after != _workspace.BaseSignature;
                _lastCheckIssues = before == after ? issues : null;
                ShowIssues(_lastCheckIssues ?? new TaskValidationIssue[0]);
                RefreshLibraryRows();
                SetStatusText(_lastCheckIssues == null ? T("来源文件再次变化，请重新检查")
                    : T("检查完成，双击问题定位") + " · " + CapabilityCheckScope()
                        + (_sourceChanged ? " · " + T("来源文件已经变化，请重新载入") : ""));
                RefreshPermission(null, EventArgs.Empty);
            }
            catch (Exception error)
            {
                RefreshSourceAfterActionFailure();
                ShowError(error);
                RefreshPermission(null, EventArgs.Empty);
            }
        }
        /// <summary>说明检查覆盖的范围；后端能力通过不等于现场准备或动作已完成。</summary>
        private string CapabilityCheckScope()
        {
            bool english = string.Equals(_language, "en-US", StringComparison.OrdinalIgnoreCase);
            if (_executionCapabilities == null)
                return english ? "Definition check only; backend capabilities not supplied." : "仅核对定义；未提供当前后端能力。";
            return (english ? "Includes backend capability checks: " : "已纳入后端能力检查：")
                + _executionCapabilities.BackendName
                + (english ? "; loading and preparation remain required." : "；仍需重新装载和设备准备。");
        }

        /// <summary>更新正式任务文件前后都核对会话状态；存储层复核能力、引用及来源签名。</summary>
        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            if (_workspace == null || _editor != null || !ReadSettings()) return;
            try
            {
                string blocked = _getBlockedReason(); if (!string.IsNullOrEmpty(blocked)) throw new InvalidOperationException(blocked);
                var issues = _store.Check(_workspace); ShowIssues(issues);
                if (issues.Any(item => item.Severity == TaskIssueSeverity.Error)) { SetActionStatusText(T("存在错误，不能应用")); return; }
                if (MessageBox.Show(T("确认更新以下任务文件") + "\n" + string.Join("\n", _workspace.Tasks.Select(item => item.Definition.Name)), T("更新任务文件"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                blocked = _getBlockedReason(); if (!string.IsNullOrEmpty(blocked)) throw new InvalidOperationException(blocked);
                string warning; _workspace = _store.Apply(_workspace, out warning); _activeWorkspace = _store.Load(_machineId, false); _catalog = _store.LoadCatalog(_workspace); _dirty = false; _sourceChanged = false; _lastCheckIssues = null; Rebuild();
                SetActionStatusText(T("任务文件已更新") + (warning == null ? "" : "\n" + warning));
            }
            catch (Exception error) { ShowError(error); }
        }
        /// <summary>明确放弃草稿并重新读取运行任务文件，执行前保留当前页面内容。</summary>
        private void ReloadRuntimeFiles()
        {
            if (!CanResolveTaskSources()) return;
            if (MessageBox.Show(T("放弃草稿并读取运行版本确认说明"), T("放弃草稿并读取运行版本"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            if (!CanResolveTaskSources()) return;
            try
            {
                string currentCopy = null;
                if (_workspace != null)
                {
                    if (!ReadSettings()) return;
                    currentCopy = _store.SaveLocalCopy(_workspace);
                }
                _store.DiscardDraft(_machineId);
                _editor = null;
                ContentHost.Content = MainTabs;
                TaskWorkspaceDocument previousWorkspace = _workspace;
                LoadWorkspace();
                if (currentCopy != null && !ReferenceEquals(_workspace, previousWorkspace))
                    SetActionStatusText(T("已读取运行版本，草稿内容已保留") + "\n" + currentCopy);
            }
            catch (Exception error) { ShowError(error); }
        }
        /// <summary>恢复上次半应用前同样要求没有活动会话。</summary>
        private void Recover_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string blocked = _getBlockedReason(); if (!string.IsNullOrEmpty(blocked)) throw new InvalidOperationException(blocked);
                if (MessageBox.Show(T("确认恢复上次应用前的任务文件"), T("恢复上次应用"), MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                blocked = _getBlockedReason(); if (!string.IsNullOrEmpty(blocked)) throw new InvalidOperationException(blocked);
                _store.RecoverPendingApply(_machineId); LoadWorkspace();
            }
            catch (Exception error) { ShowError(error); }
        }
        /// <summary>双击检查问题进入对应任务，清单问题保留在工位安排页。</summary>
        private void IssueOpen(object sender, RoutedEventArgs e)
        {
            if (!(IssuesGrid.SelectedItem is TaskEditingIssueRow row) || _workspace == null || !ReadSettings()) return;
            TaskValidationIssue issue = row.Issue;
            var task = _workspace.Tasks.FirstOrDefault(item => item.Definition.TaskId == issue.TaskId);
            if (task != null) { OpenEditor(task); _editor?.FocusIssue(issue); } else { MainTabs.SelectedIndex = 0; SetStatusText(issue.Message); }
        }
        /// <summary>任务清单或当前编辑器存在尚未保存的内容时为真。</summary>
        public bool HasUnsavedChanges { get { return _dirty || (_editor != null && _editor.IsDirty); } }

        /// <summary>Host 关闭时允许保存、放弃或取消；不能因关闭丢失人员编辑。</summary>
        public bool ConfirmClose()
        {
            if (_editor != null && !_editor.ConfirmLeave()) return false;
            if (!_dirty) return true;
            var answer = MessageBox.Show(T("是否保存设备任务草稿"), T("未保存修改"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            return answer == MessageBoxResult.No || answer == MessageBoxResult.Yes && SaveDraft();
        }
        /// <summary>运行方式使用现场易懂文字。</summary>
        private static string ModeName(StationTaskMode mode)
        { return mode == StationTaskMode.Once ? "单次执行" : mode == StationTaskMode.Cyclic ? "独立循环" : "请求触发"; }
    }
    /// <summary>工位表只读显示行，编辑值继续存于原清单模型。</summary>
    internal sealed class TaskStationEditRow
    {
        /// <summary>工位配置原对象。</summary>
        public StationTaskConfiguration Source { get; private set; }
        /// <summary>任务的可读显示名称。</summary>
        public string TaskName { get; private set; }
        /// <summary>翻译后的运行方式。</summary>
        public string Mode { get; private set; }
        /// <summary>已应用任务文件版本，不冒充正在执行的会话版本。</summary>
        public string AppliedVersion { get; private set; }
        /// <summary>构造只读显示，不复制另一份可写配置。</summary>
        internal TaskStationEditRow(StationTaskConfiguration source, string task, string mode, string version) { Source = source; TaskName = task; Mode = mode; AppliedVersion = version; }
    }
}
