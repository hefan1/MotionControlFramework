using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
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
        /// <summary>所属窗口的独立内存会话。</summary>
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
                SourceText.Text = T("内存仿真来源") + "：" + _session.Source;
                RefreshStatus.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                _refreshFailed = false;
                RefreshSteps();
            }
            catch (Exception error)
            {
                _refreshFailed = true;
                RefreshStatus.Text = T("刷新失败，以下为旧数据");
                DetailSummary.Text = error.Message;
                System.Diagnostics.Trace.TraceError("任务监控刷新失败：" + error);
                _timer.Stop();
            }
        }

        /// <summary>选中工位时建立一次步骤树，随后保持选择与展开状态。</summary>
        private void Station_Selected(object sender, SelectionChangedEventArgs e)
        {
            if (!(StationsGrid.SelectedItem is TaskStationRow station)) return;
            _steps = TaskStepTree.Build(station.Snapshot.TaskId, _session.Library);
            _selected = null;
            StepsTree.ItemsSource = _steps;
            StepsTitle.Text = station.Name + " · " + T("运行步骤");
            RefreshSteps();
            if (_selected == null && _steps.Count > 0) SelectFollowing(_steps[0]);
        }

        /// <summary>应用整机状态并选择当前活动末端；多个并行步骤仍同时显示状态。</summary>
        private void RefreshSteps()
        {
            if (!(StationsGrid.SelectedItem is TaskStationRow station)) return;
            TaskStepTree.Update(_steps, station.Snapshot.Run, _snapshot.State, T);
            if (FollowBox.IsChecked == true)
            {
                var flat = TaskStepTree.Flatten(_steps).ToList();
                var active = flat.Where(row => row.Snapshot != null &&
                    (row.Snapshot.State == TaskNodeState.Running || row.Snapshot.State == TaskNodeState.Waiting)).ToList();
                var target = flat.FirstOrDefault(row => row.Snapshot?.State == TaskNodeState.Failed)
                    ?? active.FirstOrDefault(row => !active.Any(child => child.Parent == row));
                if (target != null && target != _selected) SelectFollowing(target);
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

        /// <summary>保留操作员选择，避免后台运行不断抢回正在查看的步骤。</summary>
        private void Step_Selected(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            _selected = e.NewValue as TaskStepRow;
            if (!_following) FollowBox.IsChecked = false;
            ShowDetails(_selected);
        }

        /// <summary>按故障的工位和调用编号定位，外部故障没有步骤时直接显示原因。</summary>
        private void LocateFault_Click(object sender, RoutedEventArgs e)
        {
            if (_snapshot == null || _refreshFailed) return;
            var fault = _snapshot.Faults.FirstOrDefault();
            if (fault == null) { DetailNotice.Text = T("当前无停机故障"); return; }
            var station = _stations.FirstOrDefault(item => item.Id == fault.Source);
            if (station != null) StationsGrid.SelectedItem = station;
            FollowBox.IsChecked = false;
            var step = fault.Error == null ? null : TaskStepTree.Flatten(_steps).FirstOrDefault(item => item.Snapshot?.ExecutionId == fault.Error.ExecutionId);
            if (step != null) SelectFollowing(step);
            DetailNotice.Text = fault.Message + "\n" + T("故障恢复提示");
        }

        /// <summary>按中文资源键读取文字，方便开发者从页面搜索到代码。</summary>
        public string T(string key) { return TryFindResource("任务监控." + key) as string ?? key; }

        /// <summary>切换静态标签与动态状态文字；设备自定义名称保留原文。</summary>
        public void SetLanguage(string language)
        {
            var dictionaries = Resources.MergedDictionaries;
            dictionaries[1] = new ResourceDictionary { Source = new Uri("/MotionControl.TaskEngine.Wpf;component/Localization/Strings."
                + (language == "en-US" ? "en-US" : "zh-CN") + ".xaml", UriKind.Relative) };
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
