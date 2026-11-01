using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MotionControl.TaskEngine.Execution;
using MotionControl.TaskEngine.Machine;
using MotionControl.TaskEngine.Wpf.Monitoring;
using MotionControl.TaskEngine.Wpf.Simulation;

namespace MotionControl.TaskEngine.Wpf.Views
{
    /// <summary>只读运行监控页；界面刷新只读取快照，不推进设备动作。</summary>
    public partial class TaskMonitorView : UserControl
    {
        /// <summary>提供当前任务定义、设备端口和整机状态的监控会话。</summary>
        private readonly TaskMonitorSession _session;
        /// <summary>稳定的工位行集合。</summary>
        private readonly ObservableCollection<TaskStationRow> _stations = new ObservableCollection<TaskStationRow>();
        /// <summary>当前工位定义树，换工位时才重建。</summary>
        private IReadOnlyList<TaskStepRow> _steps = new TaskStepRow[0];
        /// <summary>用于关联资源和工位的最新整机快照。</summary>
        private TaskMachineSnapshot _snapshot;
        /// <summary>页面显示时刷新，卸载后停止。</summary>
        private readonly DispatcherTimer _timer;
        /// <summary>区分跟随选择与用户手动选择。</summary>
        private bool _following;
        /// <summary>最近一次步骤选择是否由现场人员输入触发。</summary>
        private bool _operatorSelectionPending;
        /// <summary>故障定位后，首次恢复运行时自动回到当前工步。</summary>
        private bool _followAfterFaultRecovery;
        /// <summary>标记代码正在更新跟随复选框，避免覆盖操作员的恢复选择。</summary>
        private bool _updatingFollowBox;
        /// <summary>当前显示详情的稳定步骤行。</summary>
        private TaskStepRow _selected;
        /// <summary>防止刷新失败后继续把旧数据称为当前状态。</summary>
        private bool _refreshFailed;
        /// <summary>请求 Host 定位关联配置；示例模式不跳转现场同名对象。</summary>
        public event EventHandler<TaskReferenceEventArgs> ReferenceRequested;

        /// <summary>创建监控页；构造和定时刷新均不启动任务。</summary>
        public TaskMonitorView(TaskMonitorSession session)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            InitializeComponent();
            RefreshStationColumnHeaders();
            StationsGrid.ItemsSource = _stations;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _timer.Tick += RefreshTick;
            Loaded += View_Loaded;
            Unloaded += View_Unloaded;
        }

        /// <summary>页面进入可见区域后开始采样。</summary>
        private void View_Loaded(object sender, RoutedEventArgs e) { Refresh(); _timer.Start(); }
        /// <summary>移出界面后停止采样，后台任务仍由所属窗口管理。</summary>
        private void View_Unloaded(object sender, RoutedEventArgs e) { _timer.Stop(); }
        /// <summary>界面线程每次读取一份快照。</summary>
        private void RefreshTick(object sender, EventArgs e) { Refresh(); }

        /// <summary>刷新工位和步骤；失败时明确标记陈旧数据，保留原始错误。</summary>
        public void Refresh()
        {
            try
            {
                _snapshot = _session.Machine.GetSnapshot();
                var stations = new List<StationTaskSnapshot>();
                if (_snapshot.Initialization != null) stations.Add(_snapshot.Initialization);
                stations.AddRange(_snapshot.Stations);
                foreach (var station in stations)
                {
                    var row = _stations.FirstOrDefault(item => item.Id == station.StationId);
                    if (row == null) { row = new TaskStationRow(station.StationId); row.Update(station, _snapshot.State, T); _stations.Add(row); }
                    else row.Update(station, _snapshot.State, T);
                }
                if (StationsGrid.SelectedItem == null && _stations.Count > 0) StationsGrid.SelectedIndex = 0;
                var source = _session.IsExample ? T("内置示例来源") : _session.Source;
                SourceText.Text = string.Format(T("流程数据来源格式"), source);
                RefreshStatus.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                _refreshFailed = false;
                if (_followAfterFaultRecovery && _snapshot.State == TaskMachineState.Running)
                {
                    _followAfterFaultRecovery = false;
                    SetFollowExecution(true);
                }
                RefreshCurrentCauseButton();
                RefreshSteps();
            }
            catch (Exception error)
            {
                _refreshFailed = true;
                LocateCurrentCauseButton.IsEnabled = false;
                RefreshStatus.Text = T("刷新失败，以下为旧数据");
                DetailSummary.Text = error.Message;
                System.Diagnostics.Trace.TraceError("任务监控刷新失败：" + error);
                _timer.Stop();
            }
        }

        /// <summary>切换工位时恢复自动跟随，并显示该工位当前活动步骤。</summary>
        private void Station_Selected(object sender, SelectionChangedEventArgs e)
        {
            if (!(StationsGrid.SelectedItem is TaskStationRow station)) return;
            _steps = TaskStepTree.Build(station.Snapshot.TaskId, _session.Library);
            _selected = null;
            StepsTree.ItemsSource = _steps;
            SetFollowExecution(true);
            StepsTitle.Text = station.Name + " · " + T("运行步骤");
            RefreshSteps();
            if (_selected == null && _steps.Count > 0) SelectFollowing(_steps[0]);
        }

        /// <summary>应用整机状态、循环待执行提示并选择当前活动末端；多个并行步骤仍同时显示状态。</summary>
        private void RefreshSteps()
        {
            if (!(StationsGrid.SelectedItem is TaskStationRow station)) return;
            TaskStepTree.Update(_steps, station.Snapshot.Run, _snapshot.State, T);
            if (FollowBox.IsChecked == true)
            {
                var flat = TaskStepTree.Flatten(_steps).ToList();
                var active = flat.Where(row => row.Snapshot != null &&
                    (row.Snapshot.State == TaskNodeState.Running || row.Snapshot.State == TaskNodeState.Waiting
                        || row.Snapshot.State == TaskNodeState.Moving)).ToList();
                var target = flat.FirstOrDefault(row => row.Snapshot?.State == TaskNodeState.Failed)
                    ?? active.FirstOrDefault(row => !active.Any(child => child.Parent == row));
                if (target != null && target != _selected) SelectFollowing(target);
            }
            if (station.Snapshot.Mode == StationTaskMode.Cyclic
                && station.Snapshot.State == StationTaskState.Executing
                && station.Snapshot.CompletedCycles > 0 && station.Snapshot.Run != null)
            {
                foreach (var pending in TaskStepTree.Flatten(_steps).Where(row => row.Snapshot == null && !row.NotSelected))
                    pending.Update(null, false, pending.StateText, T("本周期尚未进入本步骤"), pending.StateColor);
            }
            ShowDetails(_selected);
        }

        /// <summary>代码选择不会关闭跟随；人工查看另一行时才关闭跟随。</summary>
        private void SelectFollowing(TaskStepRow row)
        {
            _following = true;
            try
            {
                for (var parent = row.Parent; parent != null; parent = parent.Parent) parent.IsExpanded = true;
                foreach (var item in TaskStepTree.Flatten(_steps)) item.IsSelected = item == row;
                _selected = row;
                ShowDetails(row);
            }
            finally { _following = false; }
        }

        /// <summary>按键盘方向键时标记接下来的步骤切换来自现场人员。</summary>
        private void StepsTree_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Up || e.Key == Key.Down || e.Key == Key.Home || e.Key == Key.End ||
                e.Key == Key.PageUp || e.Key == Key.PageDown)
                MarkOperatorSelection();
        }

        /// <summary>点击步骤行时标记接下来的步骤切换来自现场人员。</summary>
        private void StepsTree_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || !IsStepRow(e.OriginalSource as DependencyObject)) return;
            MarkOperatorSelection();
        }

        /// <summary>在本次输入处理完成后清除标记，避免误认后续后台选择。</summary>
        private void MarkOperatorSelection()
        {
            _operatorSelectionPending = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => _operatorSelectionPending = false));
        }

        /// <summary>沿界面元素树向上查找被点击的步骤行。</summary>
        private bool IsStepRow(DependencyObject element)
        {
            while (element != null && element != StepsTree)
            {
                if (element is TreeViewItem) return true;
                element = LogicalTreeHelper.GetParent(element) ??
                    (element is Visual ? VisualTreeHelper.GetParent(element) : null);
            }
            return false;
        }

        /// <summary>保留操作员选择，避免后台运行不断抢回正在查看的步骤。</summary>
        private void Step_Selected(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            _selected = e.NewValue as TaskStepRow;
            if (!_following && _operatorSelectionPending) FollowBox.IsChecked = false;
            ShowDetails(_selected);
        }

        /// <summary>操作员手动切换跟随时，保留其选择并取消待恢复的自动跟随。</summary>
        private void FollowBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_updatingFollowBox) return;
            _followAfterFaultRecovery = false;
        }

        /// <summary>更新跟随复选框而不误认为这是操作员的手动选择。</summary>
        private void SetFollowExecution(bool enabled)
        {
            _updatingFollowBox = true;
            try { FollowBox.IsChecked = enabled; }
            finally { _updatingFollowBox = false; }
        }

        /// <summary>有停机故障时定位故障步骤；无故障时定位当前等待步骤。</summary>
        private void LocateCurrentCause_Click(object sender, RoutedEventArgs e)
        {
            if (_snapshot == null || _refreshFailed) return;
            var fault = _snapshot.Faults.FirstOrDefault();
            if (fault != null)
            {
                var faultStation = _stations.FirstOrDefault(item => item.Id == fault.Source);
                if (faultStation != null) StationsGrid.SelectedItem = faultStation;
                SetFollowExecution(false);
                var faultStep = fault.Error == null ? null : TaskStepTree.Flatten(_steps).FirstOrDefault(item => item.Snapshot?.ExecutionId == fault.Error.ExecutionId);
                if (faultStep != null) SelectFollowing(faultStep);
                _followAfterFaultRecovery = faultStation != null;
                DetailNotice.Text = fault.Message + "\n" + T("故障恢复提示");
                return;
            }

            _followAfterFaultRecovery = false;
            var waitingStation = FindWaitingStation();
            if (waitingStation == null)
            {
                DetailNotice.Text = T("当前无等待或停机故障");
                return;
            }

            StationsGrid.SelectedItem = waitingStation;
            SetFollowExecution(false);
            var waitingNode = FindWaitingLeaf(waitingStation.Snapshot.Run);
            var waitingStep = waitingNode == null ? null : TaskStepTree.Flatten(_steps)
                .FirstOrDefault(item => item.Snapshot?.ExecutionId == waitingNode.ExecutionId);
            if (waitingStep != null) SelectFollowing(waitingStep);
            else DetailNotice.Text = waitingStation.Detail;
        }

        /// <summary>按当前选择优先查找等待工位，避免跳到无关工位。</summary>
        private TaskStationRow FindWaitingStation()
        {
            var selected = StationsGrid.SelectedItem as TaskStationRow;
            if (HasActionableWaitingStep(selected)) return selected;
            return _stations.FirstOrDefault(HasActionableWaitingStep)
                ?? (HasActiveWaitingStep(selected) ? selected : _stations.FirstOrDefault(HasActiveWaitingStep));
        }

        /// <summary>判断工位是否存在等待中的执行节点。</summary>
        private bool HasActiveWaitingStep(TaskStationRow station)
        {
            return station?.Snapshot?.Run?.Nodes?.Any(node => node.State == TaskNodeState.Waiting) == true;
        }

        /// <summary>普通工艺延时属于正常节拍；定位原因时优先查看需要排查的等待条件。</summary>
        private bool HasActionableWaitingStep(TaskStationRow station)
        {
            var waiting = FindWaitingLeaf(station?.Snapshot?.Run);
            return waiting != null && !IsNormalProcessDelay(waiting);
        }

        /// <summary>识别无需操作员介入的节拍延时。</summary>
        private bool IsNormalProcessDelay(TaskNodeSnapshot node)
        {
            return node?.Detail?.StartsWith("等待工艺延时", StringComparison.Ordinal) == true;
        }

        /// <summary>优先返回最深层等待节点，使步骤树定位到具体等待动作。</summary>
        private TaskNodeSnapshot FindWaitingLeaf(TaskRunSnapshot run)
        {
            var nodes = run?.Nodes;
            if (nodes == null) return null;
            var waitingLeaves = nodes.Where(node => node.State == TaskNodeState.Waiting
                && !nodes.Any(child => string.Equals(child.ParentExecutionId, node.ExecutionId, StringComparison.OrdinalIgnoreCase)
                    && (child.State == TaskNodeState.Running || child.State == TaskNodeState.Waiting || child.State == TaskNodeState.Moving))).ToList();
            return waitingLeaves.FirstOrDefault(node => !IsNormalProcessDelay(node))
                ?? waitingLeaves.FirstOrDefault()
                ?? nodes.FirstOrDefault(node => node.State == TaskNodeState.Waiting && !IsNormalProcessDelay(node))
                ?? nodes.FirstOrDefault(node => node.State == TaskNodeState.Waiting);
        }

        /// <summary>只有存在故障或等待步骤时才启用原因定位入口。</summary>
        private void RefreshCurrentCauseButton()
        {
            LocateCurrentCauseButton.IsEnabled = _snapshot.Faults.Any() || _stations.Any(HasActiveWaitingStep);
        }

        /// <summary>按中文资源键读取文字，方便开发者从页面搜索到代码。</summary>
        public string T(string key) { return TryFindResource("任务监控." + key) as string ?? key; }

        /// <summary>显式写入本地化工位表头，确保 DataGridColumn 脱离可视树时仍显示资源文字。</summary>
        private void RefreshStationColumnHeaders()
        {
            string[] keys = { "任务名称", "方式", "状态", "当前动作", "等待或异常说明", "完成次数" };
            for (int index = 0; index < keys.Length && index < StationsGrid.Columns.Count; index++)
                StationsGrid.Columns[index].Header = T(keys[index]);
        }

        /// <summary>切换静态标签与动态状态文字；设备自定义名称保留原文。</summary>
        public void SetLanguage(string language)
        {
            var dictionaries = Resources.MergedDictionaries;
            dictionaries[1] = new ResourceDictionary { Source = new Uri("/MotionControl.TaskEngine.Wpf;component/Localization/Strings."
                + (language == "en-US" ? "en-US" : "zh-CN") + ".xaml", UriKind.Relative) };
            RefreshStationColumnHeaders();
            if (StationsGrid.SelectedItem is TaskStationRow station)
                StepsTitle.Text = station.Name + " · " + T("运行步骤");
            Refresh();
        }
    }

    /// <summary>关联入口只携带已有页面键和对象名，不携带硬件控制委托。</summary>
    public sealed class TaskReferenceEventArgs : EventArgs
    {
        /// <summary>既有导航中文资源键。</summary>
        public string Page { get; }
        /// <summary>当前步骤引用的逻辑名称。</summary>
        public string Name { get; }
        /// <summary>IO 输入或输出方向；轴和变量引用为空。</summary>
        public string Direction { get; }
        /// <summary>创建明确的只读导航请求。</summary>
        public TaskReferenceEventArgs(string page, string name, string direction = null)
        { Page = page; Name = name; Direction = direction; }
    }
}
