using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Configuration;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Validation;
using MotionControl.TaskEngine.Wpf.Monitoring;
using MotionControl.TaskEngine.Wpf.Views;

namespace MotionControl.TaskEngine.Wpf.Editing
{
    /// <summary>具体的列表步骤编辑器，原任务对象和运行会话不会被直接修改。</summary>
    public partial class TaskEditorView : UserControl
    {
        /// <summary>本次编辑独占的任务副本。</summary>
        public TaskDefinition Definition { get; private set; }
        /// <summary>尚未保存到设备草稿包的修改，同时更新可见的未保存标记。</summary>
        public bool IsDirty
        {
            get { return _isDirty; }
            private set { _isDirty = value; if (DirtyIndicator != null) DirtyIndicator.Visibility = value ? Visibility.Visible : Visibility.Collapsed; }
        }
        /// <summary>编辑副本的未保存状态。</summary>
        private bool _isDirty;
        /// <summary>保存草稿时通知所属任务配置页持久化完整草稿包。</summary>
        public event EventHandler DraftSaved;
        /// <summary>请求所属页面导出包含当前未保存任务的设备任务备份。</summary>
        public event EventHandler BackupExportRequested;
        /// <summary>返回所属任务配置页。</summary>
        public event EventHandler BackRequested;
        /// <summary>请求 Host 只读定位已有页面。</summary>
        public event EventHandler<TaskReferenceEventArgs> ReferenceRequested;
        /// <summary>编辑用的最新轴、IO、变量和示教点目录。</summary>
        private TaskResourceCatalog _catalog;
        /// <summary>按当前设备配置重新读取任务资源目录。</summary>
        private readonly Func<TaskResourceCatalog> _loadCurrentCatalog;
        /// <summary>供子任务引用校验使用的其他定义。</summary>
        private readonly List<TaskDefinition> _others;
        /// <summary>当前界面语言。</summary>
        private string _language = "zh-CN";
        /// <summary>当前选择节点及树行。</summary>
        private TaskNode _selected;
        /// <summary>当前选中树行，用于无效输入时恢复选择。</summary>
        private TreeViewItem _selectedItem;
        /// <summary>创建控件或恢复选择期间不产生编辑修改。</summary>
        private bool _building;
        /// <summary>超出当前表单能力的既有节点只读保留，不能用默认参数覆盖。</summary>
        private bool _readOnlyDetails;
        /// <summary>树行显示序号与稳定 ID 分开，用于修改动作名称后刷新行文字。</summary>
        private readonly Dictionary<string, string> _rowPrefixes = new Dictionary<string, string>();
        /// <summary>最近保存的定义快照，离开时兜底检查未被控件事件标记的改动。</summary>
        private string _savedDefinitionJson;
        /// <summary>当前具体表单字段，键名与读取方法直接对应。</summary>
        private readonly Dictionary<string, FrameworkElement> _fields = new Dictionary<string, FrameworkElement>();

        /// <summary>复制定义后建立列表，并保留重新读取当前设备资源目录的入口；构造不运行任何步骤。</summary>
        public TaskEditorView(TaskDefinition definition, TaskResourceCatalog catalog,
            Func<TaskResourceCatalog> loadCurrentCatalog, IEnumerable<TaskDefinition> others)
        {
            Definition = TaskEditorNodes.CopyTask(definition);
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _loadCurrentCatalog = loadCurrentCatalog ?? throw new ArgumentNullException(nameof(loadCurrentCatalog));
            _others = others.ToList();
            InitializeComponent(); UpdateHeaders(); IssuesGrid.Visibility = Visibility.Collapsed; _building = true; TaskNameBox.Text = Definition.Name; _building = false;
            TaskVersionText.Text = T("草稿基准版本") + " R" + Definition.Revision;
            AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler(ContentChanged));
            AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler(ChoiceChanged));
            AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler(ToggleChanged));
            AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler(ToggleChanged));
            AddHandler(Button.ClickEvent, new RoutedEventHandler(DetailAction));
            RebuildTree(Definition.Root.Id);
            _savedDefinitionJson = TaskDefinitionJson.Serialize(Definition);
        }
        /// <summary>切换语言前提交有效字段；无效输入仍原样保留，避免语言切换丢数据。</summary>
        public void SetLanguage(string language)
        {
            _language = language; TaskEditingUi.SetLanguage(this, language);
            UpdateHeaders();
            TaskVersionText.Text = T("草稿基准版本") + " R" + Definition.Revision;
            if (TryRead()) { RebuildTree(_selected?.Id); }
        }
        /// <summary>按中文界面原文查资源。</summary>
        private string T(string text) { return TaskEditingUi.T(this, text); }
        /// <summary>用户修改输入时标记未保存，构造和选中列表不算编辑。</summary>
        private void ContentChanged(object sender, TextChangedEventArgs e) { if (!_building) IsDirty = true; }
        /// <summary>只把人员正在操作的下拉变化作为修改，延后显示绑定值不算编辑。</summary>
        private void ChoiceChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_building && e.OriginalSource is ComboBox box && Details.IsAncestorOf(box)
                && (box.IsKeyboardFocusWithin || box.IsMouseOver)) IsDirty = true;
        }
        /// <summary>详情中的勾选改动属于草稿修改。</summary>
        private void ToggleChanged(object sender, RoutedEventArgs e)
        { if (!_building && e.OriginalSource is CheckBox box && (box.IsKeyboardFocusWithin || box.IsMouseOver)) IsDirty = true; }

        /// <summary>新增或移除子条件、联锁行也属于编辑，查看关联对象不算修改。</summary>
        private void DetailAction(object sender, RoutedEventArgs e)
        {
            var button = e.OriginalSource as Button;
            if (_building || button == null || !Details.IsAncestorOf(button) || button.Tag is TaskReferenceEventArgs) return;
            string text = button.Content as string;
            if (text != T("查看轴手动") && text != T("查看轴映射")) IsDirty = true;
        }

        /// <summary>DataGridColumn 不继承局部资源，表头明确翻译。</summary>
        private void UpdateHeaders()
        { string[] labels = { "等级", "步骤", "字段", "说明" }; for (int i = 0; i < labels.Length; i++) IssuesGrid.Columns[i].Header = T(labels[i]); }
        /// <summary>读取原选择的表单后再切换到新节点；无效数值阻止切换。</summary>
        private void SelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (_building || !(e.NewValue is TreeViewItem item)) return;
            if (!TryRead()) { _building = true; if (_selectedItem != null) _selectedItem.IsSelected = true; _building = false; return; }
            _selectedItem = item; _selected = (TaskNode)item.Tag; BuildDetails();
        }
        /// <summary>从定义重新建立层次，按稳定 ID 恢复选中行。</summary>
        private void RebuildTree(string selectedId)
        {
            _building = true; StepsTree.Items.Clear(); _rowPrefixes.Clear(); _selectedItem = null;
            AddTree(Definition.Root, StepsTree.Items, "", selectedId);
            if (_selectedItem == null) _selectedItem = (TreeViewItem)StepsTree.Items[0];
            _selectedItem.IsSelected = true; _selected = (TaskNode)_selectedItem.Tag;
            _building = false; BuildDetails();
        }
        /// <summary>每个树行直接关联原节点，层次序号只是阅读标记。</summary>
        private void AddTree(TaskNode node, ItemCollection owner, string number, string selectedId)
        {
            var item = new TreeViewItem { Header = CreateStepHeader(node, number), Tag = node, ToolTip = node.Id, IsExpanded = true, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            owner.Add(item); if (node.Id == selectedId) _selectedItem = item;
            _rowPrefixes[node.Id] = number;
            int count = 0;
            foreach (TaskNode child in TaskEditorNodes.Children(node))
            {
                count++; string prefix = (count * 10).ToString();
                if (node is ConditionBranchNode branch) prefix = ReferenceEquals(child, branch.WhenTrue) ? T("成立时") : T("不成立时");
                AddTree(child, item.Items, prefix, selectedId);
            }
        }
        /// <summary>以步号、动作和类型三栏显示树行；稳定 ID 放悬停提示，不挤占操作员视线。</summary>
        private FrameworkElement CreateStepHeader(TaskNode node, string number)
        {
            var grid = new Grid { Margin = new Thickness(2, 0, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(45) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            string[] texts = { number, node.Name, T(TaskEditorNodes.KindName(node.Kind)) };
            for (int i = 0; i < texts.Length; i++)
            {
                var text = new TextBlock { Text = texts[i], ToolTip = texts[i], Margin = new Thickness(4, 9, 4, 9), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(text, i); grid.Children.Add(text);
            }
            return new Border { BorderBrush = System.Windows.Media.Brushes.LightGray, BorderThickness = new Thickness(0, 0, 0, 0.5), Child = grid };
        }
        /// <summary>公用字段后按明确节点类型创建专属表单。</summary>
        private void BuildDetails()
        {
            _building = true; _readOnlyDetails = false; Details.Children.Clear(); _fields.Clear(); _motionRows.Clear(); _motionTargetsGrid = null;
            var siblings = TaskEditorNodes.ParentList(Definition.Root, _selected);
            int selectedIndex = siblings == null ? -1 : siblings.IndexOf(_selected);
            CopyButton.IsEnabled = DeleteButton.IsEnabled = siblings != null;
            UpButton.IsEnabled = selectedIndex > 0; DownButton.IsEnabled = siblings != null && selectedIndex >= 0 && selectedIndex + 1 < siblings.Count;
            try
            {
            Details.Children.Add(new TextBlock { Text = T(TaskEditorNodes.KindName(_selected.Kind)), FontSize = 17, FontWeight = FontWeights.SemiBold });
            AddField("Name", "动作说明", TaskEditingUi.Text(_selected.Name));
            AddField("Timeout", "步骤超时(ms)", TaskEditingUi.Text(_selected.TimeoutMs.ToString()));
            if (_selected is MoveAxisNode || _selected is MoveAxisGroupNode || _selected is AxisCommandNode) BuildMotion();
            else if (_selected is WaitInputNode || _selected is SetOutputNode || _selected is SetVariableNode || _selected is DelayNode) BuildSignals();
            else BuildFlow();
                if (_selected is CoordinatedMoveNode) { _fields["Name"].IsEnabled = false; _fields["Timeout"].IsEnabled = false; _readOnlyDetails = true; }
            }
            catch (Exception error)
            {
                _readOnlyDetails = true; Details.Children.Clear(); _fields.Clear();
                Details.Children.Add(new TextBlock { Text = _selected.Name + "\n" + T("当前步骤暂时只读，原定义保留") + "\n" + error.Message, TextWrapping = TextWrapping.Wrap });
                StatusText.Text = error.ToString();
            }
            finally { _building = false; }
        }
        /// <summary>具体字段保留明确名称；返回标签供按动作类型显示或隐藏。</summary>
        private TextBlock AddField(string key, string label, FrameworkElement control)
        {
            var text = TaskEditingUi.Label(this, label);
            _fields.Add(key, control); Details.Children.Add(text); Details.Children.Add(control); return text;
        }
        /// <summary>读取选中节点的具体字段，不自动执行或校验运动状态。</summary>
        private void ReadDetails()
        {
            if (_selected == null || _readOnlyDetails) { Definition.Name = TaskNameBox.Text; return; }
            string name = ((TextBox)_fields["Name"]).Text;
            int timeout = TaskEditingUi.Integer((TextBox)_fields["Timeout"], "步骤超时(ms)");
            if (_selected is MoveAxisNode || _selected is MoveAxisGroupNode || _selected is AxisCommandNode) ReadMotion();
            else if (_selected is WaitInputNode || _selected is SetOutputNode || _selected is SetVariableNode || _selected is DelayNode) ReadSignals();
            else ReadFlow();
            _selected.Name = name; _selected.TimeoutMs = timeout; Definition.Name = TaskNameBox.Text;
            if (_selectedItem != null && _rowPrefixes.ContainsKey(_selected.Id))
                _selectedItem.Header = CreateStepHeader(_selected, _rowPrefixes[_selected.Id]);
        }
        /// <summary>解析失败时在原页保留输入，不用旧值悄悄保存。</summary>
        private bool TryRead()
        {
            try { ReadDetails(); return true; }
            catch (Exception error) { StatusText.Text = error.Message; return false; }
        }
        /// <summary>新增普通步骤或并行分支；分支根和范围根保持明确结构。</summary>
        private void Add_Click(object sender, RoutedEventArgs e)
        {
            if (!TryRead()) return;
            List<TaskNode> list = _selected is SequenceNode sequence ? sequence.Children : _selected is ResourceScopeNode scope && scope.Body is SequenceNode body ? body.Children : null;
            TaskNode added;
            if (_selected is ParallelGroupNode parallel) { added = TaskEditorNodes.New("Sequence"); parallel.Branches.Add(added); }
            else
            {
                var picker = new TaskStepTypeDialog(_language) { Owner = Window.GetWindow(this) };
                if (picker.ShowDialog() != true) return;
                added = TaskEditorNodes.New(picker.SelectedKind);
                if (list != null) list.Add(added);
                else
                {
                    list = TaskEditorNodes.ParentList(Definition.Root, _selected);
                    if (list == null || _selected is ConditionBranchNode) { StatusText.Text = T("请先选择顺序段或分支内部步骤"); return; }
                    list.Insert(list.IndexOf(_selected) + 1, added);
                }
            }
            IsDirty = true; RebuildTree(added.Id);
        }
        /// <summary>上移只在同一实际父列表内改变顺序。</summary>
        private void Up_Click(object sender, RoutedEventArgs e) { Move(-1); }
        /// <summary>下移不跨越分支或资源范围边界。</summary>
        private void Down_Click(object sender, RoutedEventArgs e) { Move(1); }
        /// <summary>保持所有节点 ID 不变地移动当前步骤。</summary>
        private void Move(int offset)
        {
            if (!TryRead()) return; var list = TaskEditorNodes.ParentList(Definition.Root, _selected);
            if (list == null) return; int index = list.IndexOf(_selected), next = index + offset;
            if (next < 0 || next >= list.Count) return; list.RemoveAt(index); list.Insert(next, _selected); IsDirty = true; RebuildTree(_selected.Id);
        }
        /// <summary>复制当前步骤及内部步骤，全部生成新 ID。</summary>
        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            if (!TryRead()) return; var list = TaskEditorNodes.ParentList(Definition.Root, _selected); if (list == null) return;
            TaskNode copy = TaskEditorNodes.CopyNode(_selected); list.Insert(list.IndexOf(_selected) + 1, copy); IsDirty = true; RebuildTree(copy.Id);
        }
        /// <summary>删除需要明确确认；结构根不允许当作普通步骤移除。</summary>
        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (!TryRead()) return; var list = TaskEditorNodes.ParentList(Definition.Root, _selected); if (list == null) { StatusText.Text = T("结构根不能删除"); return; }
            if (MessageBox.Show(T("删除此步骤及全部子步骤") + "：" + _selected.Name, T("删除"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            list.Remove(_selected); IsDirty = true; RebuildTree(Definition.Root.Id);
        }
        /// <summary>将任务副本提交给所属页面保存完整草稿；失败时仍保持未保存状态。</summary>
        private void Save_Click(object sender, RoutedEventArgs e) { SaveDraft(); }
        /// <summary>保存只写草稿，不更新仿真任务文件或设备。</summary>
        public bool SaveDraft()
        {
            if (!TryRead()) return false;
            try { if (DraftSaved == null) throw new InvalidOperationException("未配置草稿保存入口。"); DraftSaved.Invoke(this, EventArgs.Empty); IsDirty = false;
                _savedDefinitionJson = TaskDefinitionJson.Serialize(Definition); StatusText.Text = T("草稿已保存，未改变运行版本"); RebuildTree(_selected.Id); return true; }
            catch (Exception error) { StatusText.Text = error.ToString(); return false; }
        }
        /// <summary>把当前任务编辑内容交给所属页面导出为设备任务备份。</summary>
        private void ExportBackup_Click(object sender, RoutedEventArgs e)
        {
            if (!TryRead()) return;
            try
            {
                if (BackupExportRequested == null) throw new InvalidOperationException("未配置任务备份导出入口。");
                BackupExportRequested.Invoke(this, EventArgs.Empty);
            }
            catch (Exception error) { StatusText.Text = error.ToString(); }
        }
        /// <summary>显示任务备份的实际路径，未保存标记仍保留。</summary>
        public void ShowBackupExported(string path)
        { StatusText.Text = T("任务配置备份已导出") + "\n" + path; }
        /// <summary>读取最新设备资源目录后检查当前任务，不持久化任务定义。</summary>
        private void Check_Click(object sender, RoutedEventArgs e)
        {
            if (!TryRead()) return;
            try
            {
                _catalog = _loadCurrentCatalog();
                if (_catalog == null) throw new InvalidOperationException("当前设备没有可用的任务资源目录。");

                // 先把已输入值读入任务定义，再用新目录重建当前步骤的下拉选项。
                BuildDetails();
                var issues = new TaskDefinitionValidator().Validate(Definition, _catalog, _others);
                IssuesGrid.ItemsSource = issues.Select(issue => new TaskEditingIssueRow(issue, issue.TaskId == Definition.TaskId ? Definition : _others.FirstOrDefault(task => task.TaskId == issue.TaskId), T(issue.Severity == TaskIssueSeverity.Error ? "错误" : "提醒"))).ToList();
                IssuesGrid.Visibility = issues.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                IssuesRow.Height = new GridLength(issues.Count > 0 ? 110 : 0);
                StatusText.Text = T("检查结果") + "：" + issues.Count + " · " + Definition.TaskId;
            }
            catch (Exception error)
            {
                IssuesGrid.ItemsSource = null;
                IssuesGrid.Visibility = Visibility.Collapsed;
                IssuesRow.Height = new GridLength(0);
                StatusText.Text = T("任务检查失败") + "：" + error.Message;
            }
        }
        /// <summary>点击错误行按稳定节点 ID 定位，不依赖显示步号。</summary>
        private void IssueSelected(object sender, SelectionChangedEventArgs e)
        { if (IssuesGrid.SelectedItem is TaskEditingIssueRow row) { if (row.Issue.TaskId == Definition.TaskId) FocusIssue(row.Issue); else StatusText.Text = row.TaskName + " / " + row.Field + "：" + row.Message; } }
        /// <summary>供任务配置页定位清单检查发现的任务节点。</summary>
        public void FocusNode(string nodeId) { if (TryRead()) RebuildTree(nodeId); }
        /// <summary>定位步骤后在顶部完整显示问题，长消息不会被表格截断。</summary>
        public void FocusIssue(TaskValidationIssue issue) { FocusNode(issue.NodeId); StatusText.Text = issue.Field + "：" + issue.Message; }
        /// <summary>关闭或返回时仅对未保存内容确认一次。</summary>
        public bool ConfirmLeave()
        {
            if (!IsDirty) IsDirty = !TryRead() || TaskDefinitionJson.Serialize(Definition) != _savedDefinitionJson;
            if (!IsDirty) return true;
            var answer = MessageBox.Show(T("是否保存当前任务草稿"), T("未保存修改"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            return answer == MessageBoxResult.No || answer == MessageBoxResult.Yes && SaveDraft();
        }
        /// <summary>返回后由所属页面继续显示任务库，保留设备级草稿。</summary>
        private void Back_Click(object sender, RoutedEventArgs e) { if (ConfirmLeave()) BackRequested?.Invoke(this, EventArgs.Empty); }
        /// <summary>所有引用按钮都只发送定位事件。</summary>
        private void Reference_Click(object sender, RoutedEventArgs e)
        {
            if (!(((Button)sender).Tag is TaskReferenceEventArgs reference)) return;
            string name = reference.Name;
            if (name.StartsWith("@")) name = ((ComboBox)_fields[name.Substring(1)]).SelectedItem as string ?? "";
            if (string.IsNullOrWhiteSpace(name)) { StatusText.Text = T("未选择或引用失效"); return; }
            ReferenceRequested?.Invoke(this, new TaskReferenceEventArgs(reference.Page, name, reference.Direction));
        }
        /// <summary>创建带明确对象的只读关联入口。</summary>
        private void AddReference(string page, string name, string direction = null)
        {
            var button = TaskEditingUi.Button(this, page, Reference_Click); button.Tag = new TaskReferenceEventArgs(page, name, direction); Details.Children.Add(button);
        }
    }
}
