using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Runtime.Machine;
using MotionControl.TaskEngine.Wpf.Monitoring;

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
        /// <summary>读取当前任务运行会话对象，区分同 Runtime 上先后装载的不同任务。</summary>
        private Func<object> _taskSessionIdentityProvider;
        /// <summary>只读核对任务准备范围是否已发布到当前设备连接和配置代次。</summary>
        private Func<MachineRuntime, Guid?, bool> _taskPreparationScopeProvider;
        /// <summary>从实际任务会话读取工位，不读取文件或推进任务。</summary>
        private Func<IReadOnlyList<StationOverviewData>> _taskStationProvider;
        /// <summary>当前任务会话中需要现场核查的资源摘要；不得推断为夹具实际状态。</summary>
        private Func<string> _taskSafetySummaryProvider;
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
        /// <summary>选中工位的实际身份；C# 使用工位对象，任务使用唯一提供器标识。</summary>
        private object _selectedStationKey;
        /// <summary>更新工位表时避免选择事件回写临时空状态。</summary>
        private bool _updatingStationTable;
        /// <summary>详情只消费本轮已采集快照，不在选择事件中再次采样。</summary>
        private OperationSnapshot _stationTableSnapshot;
        /// <summary>记录本轮数据所属的 Runtime 和连接，以便点击时再次核对。</summary>
        private MachineRuntime _stationTableRuntime;
        /// <summary>当前工位表是否已记录过观察上下文。</summary>
        private bool _hasStationTableContext;
        /// <summary>工位表上次观察时的 Runtime。</summary>
        private MachineRuntime _stationContextRuntime;
        /// <summary>工位表上次观察时的控制器连接会话。</summary>
        private Guid? _stationContextConnectionSession;
        /// <summary>工位表上次观察时的控制权来源。</summary>
        private MachineControlOwner? _stationContextControlOwner;
        /// <summary>上次观察的任务会话身份是否可确认。</summary>
        private bool _stationContextTaskIdentityKnown;
        /// <summary>上次观察的任务运行会话对象。</summary>
        private object _stationContextTaskIdentity;
        /// <summary>上下文变化后，要求操作员重新选定工位。</summary>
        private bool _selectionNeedsUserChoice;
        /// <summary>当前工位表所属任务身份是否已确认。</summary>
        private bool _stationTableTaskIdentityKnown;
        /// <summary>当前工位表所属任务运行会话对象。</summary>
        private object _stationTableTaskIdentity;
        /// <summary>本次采样已同时确认任务未读取且会话身份为空。</summary>
        private bool _stationTableTaskNotLoaded;
        private Guid? _stationTableSession;

        /// <summary>Host 的失联提示和 Runtime 的真实会话均可使当前观察失效。</summary>
        private bool IsObservationCurrent(MachineRuntime runtime, Guid? session)
        {
            if (_connectionStatusKnown && !_isCardConnected) return false;
            if (_preview != null) return true;
            if (runtime == null || !ReferenceEquals(runtime, _runtime)) return false;
            try { return runtime.IsConnectionSessionCurrent(session); }
            catch (Exception error)
            {
                System.Diagnostics.Trace.TraceWarning("设备总览无法确认控制器会话：" + error);
                return false;
            }
        }
        /// <summary>选择只改变本站详情，不改变控制权。</summary>
        private void StationTable_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingStationTable) return;
            var item = StationTable.SelectedItem as ListBoxItem;
            if (item != null) _selectionNeedsUserChoice = false;
            _selectedStationKey = item?.Tag;
            var station = item?.DataContext as StationOverviewData;
            var snapshot = _stationTableSnapshot;
            string key = station == null ? null : string.IsNullOrEmpty(station.Id) ? station.Name : station.Id;
            if (item == null)
            {
                OverviewSelectionFields.Visibility = Visibility.Collapsed;
                OverviewSelectionMessage.Visibility = Visibility.Visible;
                bool taskIdentityMissing = !_stationTableTaskNotLoaded && snapshot?.ControlOwner == MachineControlOwner.TaskEngine
                    && (!_stationTableTaskIdentityKnown || _stationTableTaskIdentity == null);
                SelectedStationText.Text = _stationTableTaskNotLoaded ? string.Empty : taskIdentityMissing ? T("任务会话身份待确认")
                    : _selectionNeedsUserChoice ? T("总览上下文已变化请重新选择") : string.Empty;
                SelectedFlowButton.Tag = SelectedReasonButton.Tag = SelectedInputButton.Tag = null;
                SelectedFlowButton.IsEnabled = SelectedReasonButton.IsEnabled = SelectedInputButton.IsEnabled = false;
                SelectedInputButton.Visibility = Visibility.Collapsed;
                return;
            }

            bool taskIdentityCurrent = IsTaskSessionIdentityCurrent(snapshot?.ControlOwner,
                _stationTableTaskIdentityKnown, _stationTableTaskIdentity);
            bool controlOwnerCurrent = IsRuntimeControlOwnerCurrent(_stationTableRuntime, snapshot?.ControlOwner);
            bool connectionCurrent = IsObservationCurrent(_stationTableRuntime, _stationTableSession);
            if (!taskIdentityCurrent || !controlOwnerCurrent || !connectionCurrent)
            {
                OverviewSelectionFields.Visibility = Visibility.Collapsed;
                OverviewSelectionMessage.Visibility = Visibility.Visible;
                _updatingStationTable = true;
                StationTable.SelectedIndex = -1;
                _updatingStationTable = false;
                _selectionNeedsUserChoice = true;
                _selectedStationKey = null;
                SelectedStationText.Text = snapshot?.ControlOwner == MachineControlOwner.TaskEngine && !taskIdentityCurrent
                    ? T("任务会话身份待确认") : T("总览上下文已变化请重新选择");
                SelectedFlowButton.Tag = SelectedReasonButton.Tag = SelectedInputButton.Tag = null;
                SelectedFlowButton.IsEnabled = SelectedReasonButton.IsEnabled = SelectedInputButton.IsEnabled = false;
                SelectedInputButton.Visibility = Visibility.Collapsed;
                RefreshDisplay();
                return;
            }

            _selectionNeedsUserChoice = false;
            SelectedStationText.Text = item.ToolTip as string ?? string.Empty;
            OverviewSelectionMessage.Visibility = Visibility.Collapsed;
            OverviewSelectionFields.Visibility = Visibility.Visible;
            SelectedStationNameText.Text = station.Name;
            SelectedStationStateText.Text = (item.Content as string[])?[1] ?? T("未提供");
            SelectedStationStepLabelText.Text = T(OperationUi.StepLabelKey(snapshot.State, station));
            bool stationReadFailed = station.StatusReadError != null;
            SelectedStationStepText.Text = stationReadFailed ? T("总览数据未知") : string.IsNullOrWhiteSpace(station.CurrentStep) ? T("总览未提供") : station.CurrentStep;
            SelectedStationWaitText.Text = stationReadFailed ? T("总览数据未知") : StationOperatorPresentation.DescribeWaitItems(station.WaitItems, station.HasWaitInformation, T);
            SelectedStationNextText.Text = stationReadFailed ? T("总览数据未知") : string.IsNullOrWhiteSpace(station.NextAction) ? T("未提供") : station.NextAction;
            bool unique = key != null && snapshot != null && FindStation(snapshot, key) != null
                && taskIdentityCurrent && controlOwnerCurrent && connectionCurrent;
            SelectedFlowButton.Tag = SelectedReasonButton.Tag = SelectedInputButton.Tag = key;
            SelectedFlowButton.IsEnabled = SelectedReasonButton.IsEnabled = unique;
            SelectedInputButton.IsEnabled = unique && !stationReadFailed && !string.IsNullOrWhiteSpace(station.WaitingInputName);
            SelectedInputButton.Visibility = SelectedInputButton.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        }
        /// <summary>按当前真实来源生成可滚动工位表；没有稳定身份不跨刷新猜测选择。</summary>
        private void UpdateStationTable(OperationSnapshot snapshot, bool connectionLost,
            MachineRuntime runtime, Guid? connectionSession, bool taskIdentityKnown, object taskSessionIdentity,
            bool taskNotLoaded)
        {
            _updatingStationTable = true;
            bool contextChanged = _hasStationTableContext
                && (!ReferenceEquals(runtime, _stationContextRuntime)
                    || connectionSession != _stationContextConnectionSession
                    || snapshot.ControlOwner != _stationContextControlOwner
                    || taskIdentityKnown != _stationContextTaskIdentityKnown
                    || (taskIdentityKnown && !ReferenceEquals(taskSessionIdentity, _stationContextTaskIdentity)));
            bool hadEstablishedRuntime = _stationContextRuntime != null || _stationContextConnectionSession.HasValue;
            if (contextChanged)
            {
                StationTable.Items.Clear();
                StationTable.SelectedIndex = -1;
                _selectedStationKey = null;
                _selectionNeedsUserChoice = _selectionNeedsUserChoice || hadEstablishedRuntime;
            }
            _hasStationTableContext = true;
            _stationContextRuntime = runtime;
            _stationContextConnectionSession = connectionSession;
            _stationContextControlOwner = snapshot.ControlOwner;
            _stationContextTaskIdentityKnown = taskIdentityKnown;
            _stationContextTaskIdentity = taskSessionIdentity;
            _stationTableSnapshot = snapshot;
            _stationTableTaskIdentityKnown = taskIdentityKnown;
            _stationTableTaskIdentity = taskSessionIdentity;
            _stationTableTaskNotLoaded = taskNotLoaded;
            bool taskIdentityUsable = snapshot.ControlOwner != MachineControlOwner.TaskEngine
                || (taskIdentityKnown && taskSessionIdentity != null);
            if (!taskIdentityUsable)
            {
                StationTable.Items.Clear();
                StationTable.SelectedIndex = -1;
                _selectedStationKey = null;
                _updatingStationTable = false;
                StationTable_SelectionChanged(null, null);
                return;
            }

            var flows = _preview == null && snapshot.ControlOwner == MachineControlOwner.StationFlows
                ? _runtime.StationFlows.ToArray() : new IStationFlow[0];
            for (int index = 0; index < snapshot.Stations.Count; index++)
            {
                var station = snapshot.Stations[index];
                object key = index < flows.Length ? (object)flows[index]
                    : !string.IsNullOrWhiteSpace(station.Id) && snapshot.Stations.Count(s => s.Id == station.Id) == 1 ? station.Id : null;
                string state = connectionLost || station.StatusReadError != null ? T("总览数据未知")
                    : string.IsNullOrWhiteSpace(station.StateText)
                        ? T(StationOperatorPresentation.StateKey(station.IndependentState)) : station.StateText;
                string step = connectionLost || station.StatusReadError != null ? T("总览数据未知") : string.IsNullOrWhiteSpace(station.CurrentStep) ? T("总览未提供") : station.CurrentStep;
                string wait = connectionLost || station.StatusReadError != null ? T("总览数据未知")
                    : StationOperatorPresentation.DescribeWaitItems(station.WaitItems, station.HasWaitInformation, T);
                string description = station.Name + "\n" + state + "\n" + T(OperationUi.StepLabelKey(snapshot.State, station)) + "\n" + step + "\n" + wait + "\n" + T("总览采样缺省");
                var item = key == null ? null : StationTable.Items.OfType<ListBoxItem>().FirstOrDefault(existing => Equals(existing.Tag, key));
                if (item == null)
                {
                    item = new ListViewItem { MinHeight = 44, Padding = new Thickness(8), FontSize = 14 };
                    StationTable.Items.Insert(index, item);
                }
                else if (StationTable.Items.IndexOf(item) != index)
                {
                    StationTable.Items.Remove(item);
                    StationTable.Items.Insert(index, item);
                }
                item.Tag = key;
                item.DataContext = station;
                item.ToolTip = description + "\n\n" + DescribeStationEvidence(station);
                // 质量结论放在单元格首位，完整的来源与采样说明保留在工位详情中。
                item.Content = new[] { station.Name, state, T(OperationUi.StepLabelKey(snapshot.State, station)) + "：" + step, wait,
                    T(station.StatusReadError == null ? "未提供" : "总览读取失败") };
                if (key != null && Equals(key, _selectedStationKey)) StationTable.SelectedItem = item;
            }
            while (StationTable.Items.Count > snapshot.Stations.Count) StationTable.Items.RemoveAt(StationTable.Items.Count - 1);
            if (StationTable.SelectedItem == null && StationTable.Items.Count > 0
                && !_selectionNeedsUserChoice && taskIdentityUsable)
                StationTable.SelectedIndex = 0;
            _updatingStationTable = false;
            StationTable_SelectionChanged(null, null);
        }

        /// <summary>确认定位操作仍属于总览当前显示的任务运行会话。</summary>
        private bool IsTaskSessionIdentityCurrent(MachineControlOwner? owner, bool identityKnown, object identity)
        {
            if (owner != MachineControlOwner.TaskEngine) return true;
            if (!identityKnown || identity == null || _taskSessionIdentityProvider == null) return false;
            try { return ReferenceEquals(_taskSessionIdentityProvider(), identity); }
            catch (Exception error)
            {
                System.Diagnostics.Trace.TraceWarning("设备总览无法确认任务会话身份：" + error);
                return false;
            }
        }

        /// <summary>确认本次只读工位快照和定位仍属于 Runtime 当前控制入口。</summary>
        private bool IsRuntimeControlOwnerCurrent(MachineRuntime runtime, MachineControlOwner? expectedOwner)
        {
            if (_preview != null) return _preview.ControlOwner == expectedOwner;
            if (runtime == null || !ReferenceEquals(runtime, _runtime)) return expectedOwner == null;
            try { return runtime.ControlOwner == expectedOwner; }
            catch (Exception error)
            {
                System.Diagnostics.Trace.TraceWarning("设备总览无法确认当前控制权来源：" + error);
                return false;
            }
        }

        /// <summary>只在连接、入口和已发布准备范围均未变化时返回准备是否未完成；否则返回未知。</summary>
        private bool? ReadPreparationRequired(MachineRuntime runtime, Guid? session, MachineControlOwner? owner,
            bool taskIdentityKnown, object taskIdentity)
        {
            if (_preview != null || runtime == null || !IsObservationCurrent(runtime, session)
                || (owner != MachineControlOwner.StationFlows && owner != MachineControlOwner.TaskEngine)) return null;
            try
            {
                bool task = owner == MachineControlOwner.TaskEngine;
                long preparationVersion = runtime.PreparationConfigurationVersion;
                long observationVersion = runtime.SafetyObservationVersion;
                if (task && (!IsTaskSessionIdentityCurrent(owner, taskIdentityKnown, taskIdentity)
                    || _taskPreparationScopeProvider?.Invoke(runtime, session) != true)) return null;
                bool preparing = runtime.IsPreparing || runtime.IsClearing;
                bool prepared = runtime.IsPrepared;
                if ((task && _taskPreparationScopeProvider?.Invoke(runtime, session) != true)
                    || !ReferenceEquals(runtime, _runtime) || !IsObservationCurrent(runtime, session)
                    || !IsRuntimeControlOwnerCurrent(runtime, owner)
                    || !IsTaskSessionIdentityCurrent(owner, taskIdentityKnown, taskIdentity)
                    || runtime.PreparationConfigurationVersion != preparationVersion
                    || runtime.SafetyObservationVersion != observationVersion
                    || runtime.IsPreparing || runtime.IsClearing || preparing) return null;
                return !prepared;
            }
            catch (Exception error)
            {
                System.Diagnostics.Trace.TraceWarning("设备总览无法确认准备范围：" + error);
                return null;
            }
        }
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
        /// <summary>接入当前任务运行会话身份，阻止同一设备连接上不同任务间沿用工位选择。</summary>
        public void SetTaskSessionIdentityProvider(Func<object> provider)
        { _taskSessionIdentityProvider = provider; RefreshDisplay(); }
        /// <summary>复用设备准备页的任务会话发布校验，不装载任务或读取硬件。</summary>
        public void SetTaskPreparationScopeProvider(Func<MachineRuntime, Guid?, bool> provider)
        { _taskPreparationScopeProvider = provider; RefreshDisplay(); }
        /// <summary>接入任务入口的只读工位来源；未读取任务时提供方返回空。</summary>
        public void SetTaskStationProvider(Func<IReadOnlyList<StationOverviewData>> provider)
        { _taskStationProvider = provider; RefreshDisplay(); }
        /// <summary>接入任务资源的只读摘要；读取失败时页面明确提示未知。</summary>
        public void SetTaskSafetySummaryProvider(Func<string> provider)
        { _taskSafetySummaryProvider = provider; RefreshDisplay(); }
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
        { OperationUi.SetLanguage(this, language); RefreshDisplay(); }
        /// <summary>可见时启动刷新。</summary>
        private void ViewLoaded(object sender, RoutedEventArgs e) { RefreshDisplay(); _timer.Start(); }
        /// <summary>离开时停止刷新，保留卡片与滚动位置。</summary>
        private void ViewUnloaded(object sender, RoutedEventArgs e) { _timer.Stop(); }
        /// <summary>定时仅采样内存状态，不读取卡 SDK。</summary>
        private void TimerTick(object sender, EventArgs e) { RefreshDisplay(); }

        /// <summary>宽屏并排显示工位和详情；窄屏按工位列表、详情的顺序排列。</summary>
        private void OverviewWorkspace_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            bool narrow = e.NewSize.Width < 900;
            if ((Grid.GetColumn(OverviewDetailsCard) == 0) == narrow) return;
            OverviewListColumn.Width = new GridLength(narrow ? 1 : 3, GridUnitType.Star);
            OverviewGapColumn.Width = new GridLength(narrow ? 0 : 10);
            OverviewDetailColumn.Width = narrow ? new GridLength(0) : new GridLength(2, GridUnitType.Star);
            OverviewListRow.Height = new GridLength(narrow ? 2 : 1, GridUnitType.Star);
            OverviewGapRow.Height = new GridLength(narrow ? 10 : 0);
            OverviewDetailRow.Height = narrow ? new GridLength(3, GridUnitType.Star) : new GridLength(0);
            Grid.SetColumn(OverviewDetailsCard, narrow ? 0 : 2);
            Grid.SetRow(OverviewDetailsCard, narrow ? 2 : 0);
        }
        /// <summary>展示当前工位动作、等待和报警；两种流程使用各自真实来源。</summary>
        public void RefreshDisplay()
        {
            MachineRuntime runtime = _runtime;
            Guid? session = runtime?.ConnectionSessionId;
            bool connectionLost = _preview == null && runtime != null && !IsObservationCurrent(runtime, session);
            object taskIdentityBeforeSample = null;
            bool taskIdentityBeforeKnown = false;
            bool taskOwnerBeforeSample = false;
            bool taskIdentitySamplingChanged = false;
            bool taskIdentityAfterKnown = false;
            Exception taskIdentityReadError = null;
            if (!connectionLost && _preview == null && runtime != null)
            {
                try
                {
                    taskOwnerBeforeSample = runtime.ControlOwner == MachineControlOwner.TaskEngine;
                    if (taskOwnerBeforeSample && _taskSessionIdentityProvider != null)
                    {
                        taskIdentityBeforeSample = _taskSessionIdentityProvider();
                        taskIdentityBeforeKnown = true;
                    }
                }
                catch (Exception error) { taskIdentityReadError = error; }
            }
            OperationSnapshot snapshot = connectionLost ? null : ReadCurrentSnapshot(runtime);
            string safetySummary = null;
            bool? taskLoaded = null;
            object taskSessionIdentity = null;
            bool taskIdentityKnown = snapshot == null || snapshot.ControlOwner != MachineControlOwner.TaskEngine;
            if (!connectionLost)
            {
                try { if (_preview == null && _taskSafetySummaryProvider != null) safetySummary = _taskSafetySummaryProvider(); }
                catch (Exception error) { safetySummary = T("资源状态读取失败请核查") + "：" + error.Message; }
                if (snapshot.ControlOwner == MachineControlOwner.TaskEngine && _isTaskSessionLoaded != null)
                {
                    try { taskLoaded = _isTaskSessionLoaded(); }
                    catch (Exception error) { snapshot.StationReadError = error; }
                }
                if (_preview == null && snapshot.ControlOwner == MachineControlOwner.TaskEngine)
                {
                    if (_taskSessionIdentityProvider != null)
                    {
                        try
                        {
                            taskSessionIdentity = _taskSessionIdentityProvider();
                            taskIdentityAfterKnown = true;
                        }
                        catch (Exception error) { taskIdentityReadError = taskIdentityReadError ?? error; }
                    }
                    taskIdentitySamplingChanged = taskOwnerBeforeSample && taskIdentityBeforeKnown
                        && taskIdentityAfterKnown && !ReferenceEquals(taskIdentityBeforeSample, taskSessionIdentity);
                    taskIdentityKnown = taskOwnerBeforeSample && taskIdentityBeforeKnown && taskIdentityAfterKnown
                        && taskIdentityBeforeSample != null && taskSessionIdentity != null
                        && ReferenceEquals(taskIdentityBeforeSample, taskSessionIdentity) && taskLoaded != false;
                    if (!taskIdentityKnown)
                    {
                        snapshot.Stations.Clear();
                        if (taskIdentitySamplingChanged)
                            snapshot.StationReadError = new InvalidOperationException(T("总览任务会话采样期间变化"));
                        else if (taskIdentityReadError != null)
                            snapshot.StationReadError = taskIdentityReadError;
                        safetySummary = null;
                    }
                }
            }
            if (!ReferenceEquals(runtime, _runtime)) return;
            if (_preview == null && runtime != null && (connectionLost || !IsObservationCurrent(runtime, session)))
            {
                connectionLost = true;
                snapshot = ReadUnavailableSnapshot(runtime);
                if (!ReferenceEquals(runtime, _runtime)) return;
                safetySummary = null;
                taskIdentityKnown = snapshot.ControlOwner != MachineControlOwner.TaskEngine;
                taskSessionIdentity = null;
            }
            if (_preview == null && runtime != null && !connectionLost
                && !IsRuntimeControlOwnerCurrent(runtime, snapshot.ControlOwner))
            {
                MachineControlOwner currentOwner = runtime.ControlOwner;
                snapshot.Stations.Clear();
                snapshot.ControlOwner = currentOwner;
                snapshot.State = runtime.State;
                snapshot.StationReadError = new InvalidOperationException(T("总览控制权采样期间变化"));
                safetySummary = null;
                taskLoaded = null;
                taskSessionIdentity = null;
                taskIdentityKnown = currentOwner != MachineControlOwner.TaskEngine;
            }
            _stationTableRuntime = runtime;
            _stationTableSession = connectionLost ? null : session;
            bool snapshotFailed = !snapshot.HasRuntime && !string.IsNullOrWhiteSpace(snapshot.LastError);
            bool taskOwner = snapshot.ControlOwner == MachineControlOwner.TaskEngine;
            bool showTask = taskOwner && !connectionLost;
            MachineRunState? displayState = connectionLost ? MachineRunState.Disconnected : snapshot.State;
            string recoveryReason = _preview == null ? _runtime?.RecoveryBlockReason : null;
            bool recoveryBlocked = !string.IsNullOrWhiteSpace(recoveryReason);
            bool taskNotLoaded = taskOwner && !connectionLost && !recoveryBlocked && taskLoaded == false
                && taskOwnerBeforeSample && taskIdentityBeforeKnown && taskIdentityAfterKnown
                && taskIdentityBeforeSample == null && taskSessionIdentity == null
                && taskIdentityReadError == null && snapshot.StationReadError == null;
            bool clearing = _preview == null && (_runtime?.IsClearing ?? false);
            bool activeOperation = displayState == MachineRunState.Running || displayState == MachineRunState.Preparing || displayState == MachineRunState.Pausing || (_runtime?.IsPreparing ?? false) || (_runtime?.IsClearing ?? false);
            bool recoveryHeadline = recoveryBlocked && !connectionLost && !activeOperation && displayState != MachineRunState.Alarmed && snapshot.HasRuntime;
            bool startBlocked = displayState == MachineRunState.Ready
                && !string.IsNullOrWhiteSpace(_automaticStartBlockReason);
            bool startChecking = displayState == MachineRunState.Ready && _automaticStartReadinessChecking;
            MachineStateText.Text = T(snapshotFailed ? "状态读取失败" : clearing && !connectionLost && displayState != MachineRunState.Alarmed ? "设备清料中" : recoveryHeadline ? "恢复信息未确认" : startBlocked ? "启动条件未满足"
                : startChecking ? "正在检查启动条件" : OperationUi.StateKey(displayState));
            Brush stateBrush = recoveryHeadline || startBlocked
                ? StartBlockedBrush
                : OperationUi.StateBrush(displayState);
            MachineStateText.Foreground = stateBrush;
            var border = stateBrush.Clone(); border.Opacity = 0.32; StatusBanner.BorderBrush = border;
            var background = stateBrush.Clone(); background.Opacity = 0.06; StatusBanner.Background = background;
            SourceText.Text = T(connectionLost ? "控制器状态未确认" : snapshot.IsPreview ? "离线页面示例" : "状态来源：设备运行数据");
            SampleText.Text = T("软件状态采样") + " · " + snapshot.SampledAt.ToString("yyyy-MM-dd HH:mm:ss");
            if (recoveryBlocked) safetySummary = T("恢复信息未确认") + "：" + (recoveryReason.Length > 180 ? recoveryReason.Substring(0, 180) + "…" : recoveryReason) + (string.IsNullOrWhiteSpace(safetySummary) ? string.Empty : "\n" + safetySummary);
            SafetySummaryText.ToolTip = recoveryReason;
            SafetySummaryActionButton.Content = T(recoveryBlocked ? "查看恢复诊断" : "查看工位运行");
            SafetySummaryText.Text = safetySummary ?? string.Empty;
            SafetySummaryPanel.Visibility = string.IsNullOrWhiteSpace(safetySummary) ? Visibility.Collapsed : Visibility.Visible;
            bool noNamedAxes = _axisMappingStatusKnown && _configuredAxisCount == 0;
            bool? preparationNeeded = !connectionLost && !snapshotFailed
                ? ReadPreparationRequired(runtime, session, snapshot.ControlOwner, taskIdentityKnown, taskSessionIdentity) : null;
            bool preparationRequired = preparationNeeded == true && !activeOperation;
            NextActionText.Text = connectionLost ? T("控制器断开后请核对状态") : startBlocked ? _automaticStartBlockReason
                : startChecking ? T("正在检查启动条件")
                : showTask && snapshot.Stations.Count == 0 && displayState != MachineRunState.Alarmed && displayState != MachineRunState.Paused && displayState != MachineRunState.Stopped ? T("任务入口操作提示")
                : T(_isCardConnected && noNamedAxes
                    ? "先配置设备轴映射再进行手动调试"
                    : NextAction(snapshot));
            if (clearing && !connectionLost && displayState != MachineRunState.Alarmed) NextActionText.Text = T("清料进行中操作提示");
            if (!string.IsNullOrWhiteSpace(safetySummary))
                NextActionText.Text = recoveryBlocked ? T("恢复阻断核查提示") + (connectionLost || displayState == MachineRunState.Alarmed || activeOperation ? "\n" + NextActionText.Text : string.Empty) : T("资源待核查先查看工位运行");
            // 只在当前连接和运行状态有效时提示准备缺项，不覆盖具体启动阻断或恢复诊断。
            if (preparationRequired && !noNamedAxes
                && !startBlocked && !startChecking && string.IsNullOrWhiteSpace(safetySummary)
                && (displayState == MachineRunState.ResetRequired || displayState == MachineRunState.Ready))
                NextActionText.Text = T(displayState == MachineRunState.ResetRequired ? "复位并完成设备准备后核对启动条件" : "完成设备准备后核对启动条件");
            else if (!preparationNeeded.HasValue && taskOwner && taskIdentityKnown && !activeOperation && !noNamedAxes
                && !startBlocked && !startChecking && string.IsNullOrWhiteSpace(safetySummary)
                && (displayState == MachineRunState.ResetRequired || displayState == MachineRunState.Ready))
                NextActionText.Text = T("准备范围待确认请先核对");
            if (snapshotFailed)
            {
                NextActionText.Text = T("状态读取失败核查提示")
                    + (string.IsNullOrWhiteSpace(snapshot.LastError) ? string.Empty : "\n" + snapshot.LastError.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[0]);
                NextActionText.ToolTip = snapshot.LastError;
            }
            else NextActionText.ToolTip = null;
            bool axisActionRequired = !snapshotFailed && startBlocked
                && _automaticStartBlockReason.IndexOf("逻辑轴", StringComparison.OrdinalIgnoreCase) >= 0;
            bool preparationActionAvailable = preparationRequired && !noNamedAxes;
            AutomaticStartActionButton.Content = T(preparationActionAvailable ? "设备准备" : "打开轴手动");
            AutomaticStartActionButton.Visibility = string.IsNullOrWhiteSpace(safetySummary)
                && !activeOperation && displayState != MachineRunState.Alarmed && (preparationActionAvailable || axisActionRequired)
                ? Visibility.Visible : Visibility.Collapsed;
            ConnectionStatusPanel.Visibility = snapshotFailed || _preview != null || (_isCardConnected && !noNamedAxes)
                ? Visibility.Collapsed : Visibility.Visible;
            ConnectionStateText.Text = T(_isCardConnected ? "设备轴映射未完成" : "控制卡未连接");
            ConnectionGuidanceText.Text = T(_isCardConnected ? "先配置设备轴映射再进行手动调试" : "请检查设备连接后查看工位状态");
            ConnectionDetailText.Text = _isCardConnected && noNamedAxes
                ? T("尚未命名任何设备轴说明")
                : string.IsNullOrWhiteSpace(_connectionStatusMessage)
                    ? T("打开轴映射查看连接原因")
                    : _connectionStatusMessage;
            AggregateMetrics.Visibility = Visibility.Collapsed;
            TaskRunPanel.Visibility = showTask && (snapshot.Stations.Count == 0 || snapshot.StationReadError != null)
                ? Visibility.Visible : Visibility.Collapsed;
            TaskRunStatusText.Text = connectionLost ? T("总览数据未知")
                : taskOwner && taskIdentitySamplingChanged ? T("总览任务会话采样期间变化")
                : taskOwner && !taskIdentityKnown && (taskIdentityReadError != null || snapshot.StationReadError != null)
                    ? T("工位状态读取失败") + "：" + (taskIdentityReadError ?? snapshot.StationReadError).Message
                : recoveryBlocked ? T("恢复未确认工位数据未知")
                : taskNotLoaded ? T("任务未读取请先读取")
                : taskOwner && !taskIdentityKnown ? T("任务会话身份待确认")
                : snapshot.StationReadError != null ? T("工位状态读取失败") + "：" + snapshot.StationReadError.Message
                : T(!taskLoaded.HasValue ? "任务会话状态待查看"
                    : taskLoaded.Value ? "任务已读取，暂无工位状态" : "任务未读取请先读取");
            TaskRunStatusText.ToolTip = snapshot.StationReadError?.ToString();

            EmptyText.Text = connectionLost ? T("控制器状态未确认") : snapshot.Stations.Count == 0 && !taskOwner
                ? T(!snapshot.HasRuntime ? "尚无运行时，请先检查控制卡连接"
                    : snapshot.ControlOwner == MachineControlOwner.None ? "当前未启用自动运行" : "未配置自动工位") : "";
            EmptyText.Visibility = snapshot.Stations.Count == 0 && !taskOwner ? Visibility.Visible : Visibility.Collapsed;
            StationTable.Visibility = connectionLost ? Visibility.Collapsed : Visibility.Visible;
            UpdateStationTable(snapshot, connectionLost, runtime, connectionLost ? null : session,
                taskIdentityKnown, taskSessionIdentity, taskNotLoaded);
            if (connectionLost) SelectedStationText.Text = T("总览工位步骤与输入未知");
            if (!snapshot.HasRuntime || (connectionLost && snapshot.Alarms.Count == 0))
            {
                AlertText.Text = T("未读取报警状态");
                AlertBanner.Background = UnknownAlertBackground;
                AlertBanner.BorderBrush = UnknownAlertBorder;
            }
            else if (snapshot.Alarms.Count == 0)
            {
                AlertText.Text = T(recoveryBlocked ? "当前会话无报警恢复仍未确认" : "当前无报警");
                AlertBanner.Background = recoveryBlocked ? UnknownAlertBackground : ClearAlertBackground;
                AlertBanner.BorderBrush = recoveryBlocked ? UnknownAlertBorder : ClearAlertBorder;
            }
            else
            {
                int stopAlarmCount = snapshot.Alarms.Count(alarm => alarm.RequiresReset);
                int noticeCount = snapshot.Alarms.Count - stopAlarmCount;
                string summary = string.Format(
                    T(connectionLost ? "断连会话报警计数" : "报警数量摘要"), snapshot.Alarms.Count, stopAlarmCount, noticeCount);
                var visibleAlarms = snapshot.Alarms.Take(3).ToList();
                string details = string.Join(" · ", visibleAlarms.Select(alarm => alarm.SourceName + "：" + alarm.Definition.Message));
                int hiddenCount = snapshot.Alarms.Count - visibleAlarms.Count;
                if (hiddenCount > 0)
                {
                    details += " · " + string.Format(T("另有 {0} 条报警，请打开当前报警查看"), hiddenCount);
                }

                AlertText.Text = summary + (string.IsNullOrWhiteSpace(details) ? string.Empty : "\n" + details);
                if (connectionLost) AlertText.Text += "\n" + T("断连报警状态未知");
                AlertBanner.Background = connectionLost ? UnknownAlertBackground : ActiveAlertBackground;
                AlertBanner.BorderBrush = connectionLost ? UnknownAlertBorder : ActiveAlertBorder;
            }
        }
        /// <summary>会话失效时只读取本 Runtime 保存的报警记录，不采集工位或等待输入。</summary>
        private static OperationSnapshot ReadUnavailableSnapshot(MachineRuntime runtime)
        {
            var snapshot = new OperationSnapshot { HasRuntime = true, State = MachineRunState.Disconnected,
                SampledAt = DateTime.Now, ControlOwner = runtime.ControlOwner };
            try { snapshot.Alarms = runtime.AlarmService.Alarms.ToList(); }
            catch (Exception error) { snapshot.HasRuntime = false; snapshot.LastError = error.ToString(); }
            return snapshot;
        }
        /// <summary>连接丢失后不继续读取任务工位，旧缓存不作为当前动作展示。</summary>
        private OperationSnapshot ReadCurrentSnapshot(MachineRuntime runtime)
        {
            try { return _preview ?? OperationSnapshot.Read(runtime,
                _connectionStatusKnown && !_isCardConnected ? null : _taskStationProvider); }
            catch (Exception error)
            {
                var result = new OperationSnapshot { SampledAt = DateTime.Now, LastError = error.ToString() };
                if (runtime == null || runtime.ControlOwner != MachineControlOwner.StationFlows) return result;
                result.HasRuntime = true;
                result.State = runtime.State;
                result.ControlOwner = runtime.ControlOwner;
                try { result.Alarms = runtime.AlarmService.Alarms.ToList(); }
                catch (Exception alarmError)
                {
                    // 保留原始采集错误与报警读取错误；HasRuntime=false 使现有报警条显示未知。
                    result.HasRuntime = false;
                    result.LastError = error.ToString() + "\n" + alarmError.ToString();
                }
                foreach (var flow in runtime.StationFlows) result.Stations.Add(OperationSnapshot.ReadStation(flow));
                return result;
            }
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
                    case MachineRunState.Ready: return "未启用自动工位，可进入轴手动和 IO 监控页面调试";
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
        /// <summary>仅按展示行所属设备和任务会话重新观察，并验证按钮仍指向同一工位。</summary>
        private StationOverviewData ReadNavigationStation(string key, out OperationSnapshot snapshot)
        {
            var runtime = _stationTableRuntime;
            Guid? session = _stationTableSession;
            snapshot = null;
            var displayed = _stationTableSnapshot;
            bool taskIdentityKnown = _stationTableTaskIdentityKnown;
            object taskIdentity = _stationTableTaskIdentity;
            if (displayed != null && FindStation(displayed, key) != null
                && IsObservationCurrent(runtime, session)
                && IsRuntimeControlOwnerCurrent(runtime, displayed.ControlOwner)
                && IsTaskSessionIdentityCurrent(displayed.ControlOwner, taskIdentityKnown, taskIdentity))
            {
                snapshot = ReadCurrentSnapshot(runtime);
                if (snapshot.ControlOwner == displayed.ControlOwner && IsObservationCurrent(runtime, session)
                    && IsRuntimeControlOwnerCurrent(runtime, displayed.ControlOwner)
                    && IsTaskSessionIdentityCurrent(displayed.ControlOwner, taskIdentityKnown, taskIdentity))
                {
                    var current = FindStation(snapshot, key);
                    if (current != null) return current;
                }
            }
            RefreshDisplay();
            return null;
        }

        /// <summary>导航只查看工位当前流程。</summary>
        private void OpenFlow(object sender, RoutedEventArgs e)
        {
            string key = (string)((Button)sender).Tag;
            if (ReadNavigationStation(key, out _) != null)
                NavigationRequested?.Invoke(this, new OperatorNavigationEventArgs("工位运行", key));
        }
        /// <summary>没有结构化等待证据时明确说明，不从名称推断资源持有人。</summary>
        private void OpenReason(object sender, RoutedEventArgs e)
        {
            var station = ReadNavigationStation((string)((Button)sender).Tag, out var snapshot);
            if (station == null) return;
            string reason = station.StatusReadError == null && snapshot.IsPreview && !string.IsNullOrEmpty(station.PreviewReason)
                ? station.PreviewReason
                : DescribeStationEvidence(station) + "\n\n" + T("整机状态") + "：\n" + BuildStationReason(snapshot);
            string alarmSection = string.Empty; // 未提供明确报警归属接口，不按同名推断。
            string currentStep = station.StatusReadError == null ? station.CurrentStep : T("总览数据未知");
            MessageBox.Show(station.Name + "\n" + T(OperationUi.StepLabelKey(snapshot.State, station)) + "：" + currentStep
                + "\n\n" + reason + alarmSection,
                T("工位状态与处理建议"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        /// <summary>集中显示工位结构化等待和原始说明；没有来源的建议、完成判据保持未提供。</summary>
        private string DescribeStationEvidence(StationOverviewData station)
        {
            if (station.StatusReadError != null)
                return T("总览数据未知") + "\n" + station.StatusReadError;
            string result = StationOperatorPresentation.DescribeWaitItems(station.WaitItems, station.HasWaitInformation, T);
            if (!string.IsNullOrWhiteSpace(station.Detail)) result += "\n" + station.Detail;
            result += "\n" + T("工位下一步建议") + "：" + (station.NextAction ?? T("未提供"));
            result += "\n" + T("工位完成判据") + "：" + (station.CompletionCondition ?? T("未提供"));
            if (station.StatusCapturedAtUtc.HasValue)
                result += "\n" + T("工位状态记录时间") + "：" + station.StatusCapturedAtUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            return result;
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
                    if (_preview == null)
                    {
                        bool? preparation = ReadPreparationRequired(_stationTableRuntime, _stationTableSession, snapshot.ControlOwner,
                            _stationTableTaskIdentityKnown, _stationTableTaskIdentity);
                        if (!preparation.HasValue) return T("准备范围待确认请先核对");
                        if (preparation.Value) return T("完成设备准备后核对启动条件");
                    }
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
            string key = (string)((Button)sender).Tag;
            var displayed = _stationTableSnapshot == null ? null : FindStation(_stationTableSnapshot, key);
            var station = ReadNavigationStation(key, out _);
            if (station == null || station.StatusReadError != null || displayed?.StatusReadError != null
                || string.IsNullOrWhiteSpace(station.WaitingInputName)) return;
            if (!string.Equals(station.WaitingInputName, displayed?.WaitingInputName, StringComparison.OrdinalIgnoreCase))
            {
                RefreshDisplay();
                return;
            }
            NavigationRequested?.Invoke(this, new OperatorNavigationEventArgs(
                "IO监控", station.Name, station.WaitingInputName));
        }
        /// <summary>优先按执行入口标识定位；没有标识的传统工位按名称匹配。</summary>
        private static StationOverviewData FindStation(OperationSnapshot snapshot, string key)
        {
            var ids = snapshot.Stations.Where(station => !string.IsNullOrEmpty(station.Id) && string.Equals(station.Id, key, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (ids.Length == 1) return ids[0];
            if (ids.Length > 1) return null;
            var names = snapshot.Stations.Where(station => string.Equals(station.Name, key, StringComparison.OrdinalIgnoreCase)).ToArray();
            return names.Length == 1 ? names[0] : null;
        }
        /// <summary>进入当前报警，不执行复位。</summary>
        private void Alarms_Click(object sender, RoutedEventArgs e) { NavigationRequested?.Invoke(this, new OperatorNavigationEventArgs("当前报警")); }
        /// <summary>打开真实任务运行页，不自动读取任务或启动设备。</summary>
        private void OpenSafetyDiagnostics_Click(object sender, RoutedEventArgs e)
        { NavigationRequested?.Invoke(this, new OperatorNavigationEventArgs(
            !string.IsNullOrWhiteSpace(_runtime?.RecoveryBlockReason) ? "设备准备" : "工位运行")); }
        private void OpenTaskRun_Click(object sender, RoutedEventArgs e)
        { NavigationRequested?.Invoke(this, new OperatorNavigationEventArgs("工位运行")); }
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

}
