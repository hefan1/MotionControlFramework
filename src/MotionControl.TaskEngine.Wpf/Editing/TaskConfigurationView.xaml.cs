using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Microsoft.Win32;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Configuration;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Editing;
using MotionControl.TaskEngine.Machine;
using MotionControl.TaskEngine.Validation;
using MotionControl.TaskEngine.Wpf.Monitoring;
using MotionControl.TaskEngine.Wpf.Views;

namespace MotionControl.TaskEngine.Wpf.Editing
{
    /// <summary>设备任务草稿的明确入口，工位安排与任务库分为两个页签。</summary>
    public partial class TaskConfigurationView : UserControl
    {
        /// <summary>请求定位现有页面；此事件不代表设备动作。</summary>
        public event EventHandler<TaskReferenceEventArgs> ReferenceRequested;
        /// <summary>草稿与运行文件的具体存储。</summary>
        private readonly TaskWorkspaceStore _store;
        /// <summary>当前设备稳定标识。</summary>
        private readonly string _machineId;
        /// <summary>由 Host 返回应用受阻原因，空值才允许应用。</summary>
        private readonly Func<string> _getBlockedReason;
        /// <summary>完整设备草稿。</summary>
        private TaskWorkspaceDocument _workspace;
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

        /// <summary>接入当前设备目录与 Host 应用边界，不持有运动卡。</summary>
        public TaskConfigurationView(MachineConfigurationStore store, string machineId, Func<string> getBlockedReason)
        {
            _store = new TaskWorkspaceStore(store); _machineId = machineId; _getBlockedReason = getBlockedReason ?? (() => "未提供运行会话状态，暂不能应用。");
            InitializeComponent();
            UpdateHeaders(); IssuesGrid.Visibility = Visibility.Collapsed; ConfigIssueRow.Height = new GridLength(0);
            AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler(TextChanged));
            AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler(ChoiceChanged));
            AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler(ToggleChanged)); AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler(ToggleChanged));
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) }; _timer.Tick += RefreshPermission;
            Loaded += ViewLoaded; Unloaded += ViewUnloaded;
            LoadWorkspace();
        }
        /// <summary>只翻译界面，稳定对象名称与 ID 保持原值。</summary>
        public void SetLanguage(string language)
        {
            _language = language; TaskEditingUi.SetLanguage(this, language);
            UpdateHeaders();
            if (_editor != null) _editor.SetLanguage(language);
            else if (ReadSettings()) Rebuild();
            if (_workspace != null && _editor == null) StatusText.Text = _dirty ? T("有未保存修改，运行版本未改变")
                : _workspace.SavedAt == default(DateTime) ? T("已从运行文件载入，尚无草稿") : T("草稿与现有目录已载入") + " · " + _workspace.SavedAt.ToString("yyyy-MM-dd HH:mm:ss");
            RefreshPermission(null, EventArgs.Empty);
        }
        /// <summary>读取本页语言文字。</summary>
        private string T(string value) { return TaskEditingUi.T(this, value); }
        /// <summary>完整读取草稿、目录与来源状态后再替换页面；读取失败保留本窗口的编辑内容。</summary>
        public void LoadWorkspace()
        {
            try
            {
                var loaded = _store.Load(_machineId);
                var catalog = _store.LoadCatalog(loaded);
                bool sourceChanged = _store.ComputeSignature(_machineId) != loaded.BaseSignature;

                _workspace = loaded;
                _catalog = catalog;
                _sourceChanged = sourceChanged;
                _editor = null;
                ContentHost.Content = MainTabs;
                ShowEditor(false);
                _dirty = false;
                Rebuild();
                StatusText.Text = _workspace.SavedAt == default(DateTime)
                    ? T("已从运行文件载入，尚无草稿")
                    : T("草稿与现有目录已载入") + " · " + _workspace.SavedAt.ToString("yyyy-MM-dd HH:mm:ss");
            }
            catch (Exception error) { StatusText.Text = error.ToString(); }
            RefreshPermission(null, EventArgs.Empty);
        }
        /// <summary>页面可见时更新按钮边界。</summary>
        private void ViewLoaded(object sender, RoutedEventArgs e) { RefreshPermission(sender, e); _timer.Start(); }
        /// <summary>移出页面停止轮询，草稿仍保留在页面对象中。</summary>
        private void ViewUnloaded(object sender, RoutedEventArgs e) { _timer.Stop(); }
        /// <summary>禁用时显示真实原因，不把已暂停的会话当作已结束。</summary>
        private void RefreshPermission(object sender, EventArgs e)
        {
            string reason = _getBlockedReason();
            if (_store.HasPendingApply(_machineId)) reason = T("上次应用未完成，请先恢复");
            if (_editor != null) reason = T("请先保存并返回任务配置");
            if (_sourceChanged) reason = T("来源文件已经变化，请重新载入");
            ApplyButton.IsEnabled = _workspace != null && string.IsNullOrEmpty(reason); ApplyButton.ToolTip = reason;
            ApplyReasonText.Text = reason ?? T("仅应用任务文件，不启动设备");
            RecoverButton.Visibility = _store.HasPendingApply(_machineId) ? Visibility.Visible : Visibility.Collapsed;
        }
        /// <summary>只标记当前配置表单修改，编辑器有自己的修改状态。</summary>
        private void TextChanged(object sender, TextChangedEventArgs e) { MarkDirty(); }
        /// <summary>下拉只在详情区变更时标记，选择列表行不算编辑。</summary>
        private void ChoiceChanged(object sender, SelectionChangedEventArgs e)
        { if (e.OriginalSource is DependencyObject source && (StationDetails.IsAncestorOf(source) || MachineDetails.IsAncestorOf(source))) MarkDirty(); }
        /// <summary>工位参与标记等勾选需要保存草稿。</summary>
        private void ToggleChanged(object sender, RoutedEventArgs e)
        { if (e.OriginalSource is CheckBox source && (StationDetails.IsAncestorOf(source) || MachineDetails.IsAncestorOf(source))) MarkDirty(); }
        /// <summary>直接显示未保存状态，避免旧成功提示误导。</summary>
        private void MarkDirty() { if (!_loading && _editor == null) { _dirty = true; StatusText.Text = T("有未保存修改，运行版本未改变"); ShowIssues(new TaskValidationIssue[0]); } }

        /// <summary>列本身不在可视树中，明确赋值表头以可靠切换局部语言资源。</summary>
        private void UpdateHeaders()
        {
            string[][] texts = { new[] { "参与运行", "工位名称", "执行任务", "运行方式" }, new[] { "任务名称", "任务标识", "基准版本", "文件" }, new[] { "等级", "任务标识", "说明" } };
            DataGrid[] grids = { StationsGrid, TasksGrid, IssuesGrid };
            for (int g = 0; g < grids.Length; g++) for (int i = 0; i < texts[g].Length; i++) grids[g].Columns[i].Header = T(texts[g][i]);
        }
        /// <summary>没有问题时收起空表，给工位和参数留出空间。</summary>
        private void ShowIssues(IReadOnlyList<TaskValidationIssue> issues)
        {
            IssuesGrid.ItemsSource = issues.Select(issue => new TaskEditingIssueRow(issue, _workspace?.Tasks.FirstOrDefault(task => task.Definition.TaskId == issue.TaskId)?.Definition, T(issue.Severity == TaskIssueSeverity.Error ? "错误" : "提醒"))).ToList();
            bool show = issues.Count > 0 && _editor == null;
            IssuesGrid.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            ConfigIssueRow.Height = new GridLength(show ? 110 : 0);
        }
        /// <summary>更新列表并按稳定标识恢复工位和任务选择；保存或换语言不跳回第一行。</summary>
        private void Rebuild()
        {
            if (_workspace == null) return;
            string stationId = _station?.StationId;
            string taskId = (TasksGrid.SelectedItem as TaskWorkspaceTask)?.Definition.TaskId;
            _loading = true; _station = null;
            TasksGrid.ItemsSource = _workspace.Tasks; TasksGrid.Items.Refresh();
            TasksGrid.SelectedItem = _workspace.Tasks.FirstOrDefault(item => string.Equals(item.Definition.TaskId, taskId, StringComparison.OrdinalIgnoreCase));
            StationsGrid.ItemsSource = _workspace.Machine.Stations.Select(item => new TaskStationEditRow(item,
                _workspace.Tasks.FirstOrDefault(task => string.Equals(task.Definition.TaskId, item.TaskId, StringComparison.OrdinalIgnoreCase))?.Definition.Name ?? item.TaskId, T(ModeName(item.Mode)))).ToList();
            BuildMachine();
            var selected = StationsGrid.Items.Cast<TaskStationEditRow>().FirstOrDefault(item => string.Equals(item.Source.StationId, stationId, StringComparison.OrdinalIgnoreCase));
            if (selected != null) StationsGrid.SelectedItem = selected;
            else if (StationsGrid.Items.Count > 0) StationsGrid.SelectedIndex = 0;
            _loading = false; BuildStation((StationsGrid.SelectedItem as TaskStationEditRow)?.Source);
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
            _loading = true; _station = station; StationDetails.Children.Clear();
            if (station == null) { StationDetails.Children.Add(TaskEditingUi.Label(this, "暂无工位，请新增工位")); _loading = false; return; }
            StationDetails.Children.Add(TaskEditingUi.Label(this, "工位标识")); StationDetails.Children.Add(new TextBlock { Text = station.StationId });
            _stationName = TaskEditingUi.Text(station.Name); AddStationField("工位名称", _stationName);
            _stationTask = TaskChoices(station.TaskId); AddStationField("执行任务", _stationTask);
            _mode = TaskEditingUi.Choices(this, new object[] { StationTaskMode.Once, StationTaskMode.Cyclic, StationTaskMode.OnRequest }, new[] { "单次执行", "独立循环", "请求触发" }, station.Mode); AddStationField("运行方式", _mode);
            _enabled = new CheckBox { Content = T("参与运行"), IsChecked = station.Enabled, Margin = new Thickness(0, 7, 0, 3) }; StationDetails.Children.Add(_enabled);
            _interval = TaskEditingUi.Text(station.CycleIntervalMs.ToString()); AddStationField("周期间隔(ms)", _interval);
            var variables = _catalog.Variables.Where(item => item.Value == DeviceVariableType.Boolean).Select(item => item.Key).ToList();
            _request = TaskEditingUi.Names(variables, station.RequestVariable); _complete = TaskEditingUi.Names(variables, station.CompletedVariable);
            _signals = new StackPanel(); _signals.Children.Add(TaskEditingUi.Label(this, "请求变量(bool)")); _signals.Children.Add(_request); _signals.Children.Add(TaskEditingUi.Label(this, "完成变量(bool)")); _signals.Children.Add(_complete);
            StationDetails.Children.Add(_signals); _signals.Visibility = station.Mode == StationTaskMode.OnRequest ? Visibility.Visible : Visibility.Collapsed;
            _mode.SelectionChanged += ModeChanged; _loading = false;
        }
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
            catch (Exception error) { StatusText.Text = error.Message; return false; }
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
            if (!(TasksGrid.SelectedItem is TaskWorkspaceTask source) || !ReadSettings()) return;
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
            catch (Exception error) { StatusText.Text = error.ToString(); }
        }
        /// <summary>选中任务后打开独立定义副本，重复双击空白行无作用。</summary>
        private void EditTask_Click(object sender, RoutedEventArgs e) { if (TasksGrid.SelectedItem is TaskWorkspaceTask entry && ReadSettings()) OpenEditor(entry); }
        /// <summary>编辑器占用同一工作区，已有页面跳转后可以返回原草稿。</summary>
        private void OpenEditor(TaskWorkspaceTask entry)
        {
            try
            {
                _catalog = _store.LoadCatalog(_workspace); _editingEntry = entry;
                _editor = new TaskEditorView(entry.Definition, _catalog, _workspace.Tasks.Where(item => !ReferenceEquals(item, entry)).Select(item => item.Definition));
                _editor.SetLanguage(_language); _editor.DraftSaved += EditorSaved; _editor.BackRequested += EditorBack; _editor.ReferenceRequested += EditorReference;
                _editor.LocalCopyRequested += EditorLocalCopy;
                ContentHost.Content = _editor; ShowEditor(true); RefreshPermission(null, EventArgs.Empty);
            }
            catch (Exception error) { StatusText.Text = error.ToString(); }
        }
        /// <summary>先把完整设备草稿写成功，编辑器才显示保存成功。</summary>
        private void EditorSaved(object sender, EventArgs e)
        {
            var previous = _editingEntry.Definition;
            try { _editingEntry.Definition = TaskEditorNodes.CopyTask(_editor.Definition); _store.SaveDraft(_workspace); _dirty = false; StatusText.Text = T("草稿已保存，未改变运行版本"); }
            catch { _editingEntry.Definition = previous; throw; }
        }
        /// <summary>复制完整工作区并带入当前编辑器字段，保留副本时不替换公共草稿和原任务对象。</summary>
        private void EditorLocalCopy(object sender, EventArgs e)
        {
            var editedTask = new TaskWorkspaceTask { FileName = _editingEntry.FileName, Definition = _editor.Definition };
            _editor.ShowLocalCopySaved(_store.SaveLocalCopy(_workspace, editedTask));
        }
        /// <summary>返回任务库，不重新载入而丢弃当前设备草稿。</summary>
        private void EditorBack(object sender, EventArgs e) { ContentHost.Content = MainTabs; _editor = null; ShowEditor(false); Rebuild(); MainTabs.SelectedIndex = 1;
            StatusText.Text = _dirty ? T("有未保存修改，运行版本未改变") : T("草稿与现有目录已载入"); RefreshPermission(null, EventArgs.Empty); }
        /// <summary>编辑器显示自己的工具栏和问题表，避免同屏重复两套保存检查。</summary>
        private void ShowEditor(bool visible)
        {
            ConfigToolbar.Visibility = ConfigurationStatus.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
            ShowIssues(new TaskValidationIssue[0]);
        }
        /// <summary>只传递已有对象定位请求。</summary>
        private void EditorReference(object sender, TaskReferenceEventArgs e) { ReferenceRequested?.Invoke(this, e); }
        /// <summary>列出工位、初始化和子任务调用的直接引用。</summary>
        private List<string> References(string id)
        {
            var result = _workspace.Machine.Stations.Where(item => string.Equals(item.TaskId, id, StringComparison.OrdinalIgnoreCase)).Select(item => T("工位") + ": " + item.Name).ToList();
            if (string.Equals(_workspace.Machine.InitializationTaskId, id, StringComparison.OrdinalIgnoreCase)) result.Add(T("初始化任务"));
            foreach (var task in _workspace.Tasks)
                foreach (var node in AllNodes(task.Definition.Root).OfType<CallTaskNode>().Where(item => string.Equals(item.TaskId, id, StringComparison.OrdinalIgnoreCase))) result.Add(task.Definition.Name + " / " + node.Name);
            return result;
        }
        /// <summary>沿明确子节点关系遍历，不反射读取对象。</summary>
        private static IEnumerable<TaskNode> AllNodes(TaskNode node)
        { yield return node; foreach (var child in TaskEditorNodes.Children(node)) foreach (var item in AllNodes(child)) yield return item; }
        /// <summary>引用窗口只显示关系，不改变任务。</summary>
        private void References_Click(object sender, RoutedEventArgs e)
        { if (TasksGrid.SelectedItem is TaskWorkspaceTask task && ReadSettings()) MessageBox.Show(string.Join(Environment.NewLine, References(task.Definition.TaskId)), T("查看引用")); }
        /// <summary>被引用任务不能删除；确认后只从草稿移除。</summary>
        private void RemoveTask_Click(object sender, RoutedEventArgs e)
        {
            if (!(TasksGrid.SelectedItem is TaskWorkspaceTask task) || !ReadSettings()) return;
            var references = References(task.Definition.TaskId); if (references.Count > 0) { StatusText.Text = T("任务仍被引用，不能删除") + "\n" + string.Join("\n", references); return; }
            if (MessageBox.Show(T("确认删除任务") + "：" + task.Definition.Name, T("删除任务"), MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            _workspace.Tasks.Remove(task); _dirty = true; Rebuild();
        }
        /// <summary>顶栏保存调用同一草稿入口。</summary>
        private void Save_Click(object sender, RoutedEventArgs e) { SaveDraft(); }
        /// <summary>发生共享草稿冲突时仍能保留本窗口内容，不要求先覆盖或放弃他人的草稿。</summary>
        private void LocalCopy_Click(object sender, RoutedEventArgs e)
        {
            if (_workspace == null || !ReadSettings()) return;
            try { StatusText.Text = T("草稿副本已保留，原草稿尚未保存") + "\n" + _store.SaveLocalCopy(_workspace); }
            catch (Exception error) { StatusText.Text = error.ToString(); }
        }
        /// <summary>明确放弃本窗口未保存内容后读取最新共享草稿；不会删除他人的草稿。</summary>
        private void LoadLatest_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(T("载入最新草稿会放弃本窗口未保存修改，建议先保留草稿副本"), T("载入最新草稿"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            LoadWorkspace();
        }
        /// <summary>编辑器打开时由编辑器提交字段；否则保存完整工位清单。</summary>
        private bool SaveDraft()
        {
            if (_editor != null) return _editor.SaveDraft();
            if (_workspace == null || !ReadSettings()) return false;
            try { _store.SaveDraft(_workspace); _dirty = false; StatusText.Text = T("草稿已保存，未改变运行版本"); Rebuild(); return true; }
            catch (Exception error) { StatusText.Text = error.ToString(); return false; }
        }
        /// <summary>检查使用最新硬件目录，并额外提示源文件已被外部修改。</summary>
        private void Check_Click(object sender, RoutedEventArgs e)
        {
            if (_editor != null) { StatusText.Text = T("请先保存并返回任务配置"); return; }
            if (_workspace == null || !ReadSettings()) return;
            try { ShowIssues(_store.Check(_workspace)); _sourceChanged = _store.ComputeSignature(_machineId) != _workspace.BaseSignature;
                StatusText.Text = T("检查完成，双击问题定位") + (_sourceChanged ? " · " + T("来源文件已经变化，请重新载入") : ""); RefreshPermission(null, EventArgs.Empty); }
            catch (Exception error) { StatusText.Text = error.ToString(); }
        }
        /// <summary>应用确认前后都核对会话状态，存储层还会检查文件签名。</summary>
        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            if (_workspace == null || _editor != null || !ReadSettings()) return;
            try
            {
                string blocked = _getBlockedReason(); if (!string.IsNullOrEmpty(blocked)) throw new InvalidOperationException(blocked);
                var issues = _store.Check(_workspace); ShowIssues(issues);
                if (issues.Any(item => item.Severity == TaskIssueSeverity.Error)) { StatusText.Text = T("存在错误，不能应用"); return; }
                if (MessageBox.Show(T("确认应用以下任务文件，不会启动设备") + "\n" + string.Join("\n", _workspace.Tasks.Select(item => item.Definition.Name)), T("应用到设备"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                blocked = _getBlockedReason(); if (!string.IsNullOrEmpty(blocked)) throw new InvalidOperationException(blocked);
                string warning; _workspace = _store.Apply(_workspace, out warning); _catalog = _store.LoadCatalog(_workspace); _dirty = false; _sourceChanged = false; Rebuild();
                StatusText.Text = T("运行文件已应用，下次新会话使用") + (warning == null ? "" : "\n" + warning);
            }
            catch (Exception error) { StatusText.Text = error.ToString(); }
        }
        /// <summary>明确放弃草稿后从运行文件重建，旧草稿先备份。</summary>
        private void Reload_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(T("放弃当前草稿并重新载入运行文件"), T("重新载入运行文件"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            try { _store.DiscardDraft(_machineId); _editor = null; ContentHost.Content = MainTabs; LoadWorkspace(); }
            catch (Exception error) { StatusText.Text = error.ToString(); }
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
            catch (Exception error) { StatusText.Text = error.ToString(); }
        }
        /// <summary>双击检查问题进入对应任务，清单问题保留在工位安排页。</summary>
        private void IssueOpen(object sender, RoutedEventArgs e)
        {
            if (!(IssuesGrid.SelectedItem is TaskEditingIssueRow row) || _workspace == null || !ReadSettings()) return;
            TaskValidationIssue issue = row.Issue;
            var task = _workspace.Tasks.FirstOrDefault(item => item.Definition.TaskId == issue.TaskId);
            if (task != null) { OpenEditor(task); _editor?.FocusIssue(issue); } else { MainTabs.SelectedIndex = 0; StatusText.Text = issue.Message; }
        }
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
        /// <summary>构造只读显示，不复制另一份可写配置。</summary>
        internal TaskStationEditRow(StationTaskConfiguration source, string task, string mode) { Source = source; TaskName = task; Mode = mode; }
    }
}
