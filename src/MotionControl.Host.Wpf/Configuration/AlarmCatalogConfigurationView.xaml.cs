using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Framework.Runtime.Configuration;

namespace MotionControl.Host.Wpf.Configuration
{
    /// <summary>编辑当前设备报警库；每条定义有稳定代码、停机类别和操作指导。</summary>
    public partial class AlarmCatalogConfigurationView : UserControl
    {
        /// <summary>当前设备报警目录的读写入口。</summary>
        private readonly MachineAlarmCatalogStore _store;

        /// <summary>当前编辑的设备编号。</summary>
        private readonly string _machineId;
        /// <summary>设备工程变更记录服务，未接入时明确提示而不影响保存结果。</summary>
        private ConfigurationChangeJournal _changeJournal;
        /// <summary>尚未被人员确认的记录故障，关闭页面前需再次提示。</summary>
        private string _pendingHistoryWarning;

        /// <summary>本页编辑的报警副本；保存成功前不改写设备配置。</summary>
        public ObservableCollection<MachineAlarmCatalogEntry> Alarms { get; private set; }

        /// <summary>当前选择的报警记录，供双向列表绑定使用。</summary>
        public MachineAlarmCatalogEntry SelectedAlarm { get; private set; }

        /// <summary>阻止切换选择和载入字段时误标记草稿已修改。</summary>
        private bool _loadingFields;

        /// <summary>记录是否存在尚未保存的编辑。</summary>
        private bool _dirty;
        /// <summary>区分真实空目录和读取失败后的空显示集合。</summary>
        private bool _catalogLoaded;

        /// <summary>载入当前设备报警库；读取失败时保留错误提示并允许在确认后修复。</summary>
        public AlarmCatalogConfigurationView(MachineAlarmCatalogStore store, string machineId)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            if (string.IsNullOrWhiteSpace(machineId)) throw new ArgumentException("设备编号不能为空。", nameof(machineId));
            _machineId = machineId;
            InitializeComponent();
            DataContext = this;
            LoadAlarms();
            Search_TextChanged(this, null);
        }

        /// <summary>接入同一设备的变更记录；不同设备的记录不能混写。</summary>
        public void SetChangeJournal(ConfigurationChangeJournal journal)
        {
            if (journal != null && !string.Equals(journal.MachineId, _machineId, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("变更记录设备与报警库不一致。", nameof(journal));
            _changeJournal = journal;
        }

        /// <summary>从磁盘读取报警定义副本，保留配置损坏或缺失的具体诊断。</summary>
        private void LoadAlarms()
        {
            _catalogLoaded = false;
            try
            {
                MachineAlarmCatalogDocument document = _store.Load(_machineId);
                Alarms = new ObservableCollection<MachineAlarmCatalogEntry>(document.Alarms.Select(alarm => alarm.Copy()));
                _catalogLoaded = true;
                DataContext = null;
                DataContext = this;
                StatusText.SetResourceReference(TextBlock.TextProperty, "报警库读取完成");
                SourceText.Text = _machineId + " / " + MachineAlarmCatalogStore.RelativeFilePath;
                StatusText.Foreground = Brush("#176BD0");
            }
            catch (Exception error)
            {
                Alarms = new ObservableCollection<MachineAlarmCatalogEntry>();
                DataContext = null;
                DataContext = this;
                StatusText.Text = T("报警库读取失败") + "：" + error.Message;
                StatusText.Foreground = Brush("#B54747");
            }
            SelectedAlarm = null;
            ShowSelectedAlarm();
            _dirty = false;
        }

        /// <summary>新增一条停机报警定义，不会触发设备报警。</summary>
        private void AddStopAlarm_Click(object sender, RoutedEventArgs e)
        {
            AddAlarm(MachineAlarmKind.StopMachine);
        }

        /// <summary>新增一条非停机报警定义，不会触发设备报警。</summary>
        private void AddNonStopAlarm_Click(object sender, RoutedEventArgs e)
        {
            AddAlarm(MachineAlarmKind.NonStop);
        }

        /// <summary>建立不重复的临时代码并选择新记录供操作人员填写。</summary>
        private void AddAlarm(MachineAlarmKind kind)
        {
            string prefix = kind == MachineAlarmKind.StopMachine ? "NEW_STOP_" : "NEW_INFO_";
            int suffix = 1;
            string code;
            do { code = prefix + suffix.ToString("D3"); suffix++; }
            while (Alarms.Any(existingAlarm => string.Equals(existingAlarm.Code, code, StringComparison.OrdinalIgnoreCase)));

            var alarm = new MachineAlarmCatalogEntry
            {
                Code = code,
                Message = T("请填写报警说明"),
                Kind = kind,
                CheckGuidance = string.Empty,
                RecoveryGuidance = string.Empty
            };
            Alarms.Add(alarm);
            AlarmGrid.SelectedItem = alarm;
            AlarmGrid.ScrollIntoView(alarm);
            MarkDirty();
        }

        /// <summary>移除选中的定义；页面会提示已有任务代码引用也需要同步处理。</summary>
        private void DeleteAlarm_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedAlarm == null) return;
            MessageBoxResult result = MessageBox.Show(
                T("删除报警定义后，引用此代码的任务草稿检查会失败。是否继续？"),
                T("确认删除报警定义"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            Alarms.Remove(SelectedAlarm);
            AlarmGrid.SelectedItem = null;
            SelectedAlarm = null;
            DeleteButton.IsEnabled = false;
            ShowSelectedAlarm();
            MarkDirty();
        }

        /// <summary>分别报告报警库保存结果和变更记录结果；记录故障不撤销已保存文件。</summary>
        private void SaveAlarms_Click(object sender, RoutedEventArgs e)
        {
            MachineAlarmCatalogDocument document;
            try
            {
                CommitSelectedAlarm();
                document = new MachineAlarmCatalogDocument
                {
                    SchemaVersion = 1,
                    Alarms = Alarms.Select(alarm => alarm.Copy()).ToList()
                };
            }
            catch (Exception error)
            {
                StatusText.Text = T("报警库保存失败") + "：" + error.Message;
                StatusText.Foreground = Brush("#B54747");
                return;
            }

            string operationId = Guid.NewGuid().ToString("N");
            try { document.Validate(); }
            catch (Exception error)
            {
                RememberHistoryError(RecordAlarmChange(ConfigurationChangeStage.Check, ConfigurationChangeOutcome.Failed,
                    DateTimeOffset.UtcNow, operationId, error.Message));
                StatusText.Text = T("报警库保存失败") + "：" + error.Message;
                StatusText.Foreground = Brush("#B54747");
                return;
            }

            string backupPath;
            try { backupPath = _store.Save(_machineId, document); }
            catch (Exception error)
            {
                RememberHistoryError(RecordAlarmChange(ConfigurationChangeStage.SaveFiles, ConfigurationChangeOutcome.Failed,
                    DateTimeOffset.UtcNow, operationId, error.Message));
                StatusText.Text = T("报警库保存失败") + "：" + error.Message;
                StatusText.Foreground = Brush("#B54747");
                return;
            }
            RememberHistoryError(RecordAlarmChange(ConfigurationChangeStage.SaveFiles, ConfigurationChangeOutcome.Succeeded,
                DateTimeOffset.UtcNow, operationId, null));
            _catalogLoaded = true;
            _dirty = false;
            AlarmGrid.Items.Refresh();
            RefreshLocalizedText();
            SetStatus(string.IsNullOrWhiteSpace(backupPath)
                ? T("报警库保存成功")
                : T("报警库保存成功，已备份") + " " + backupPath);
        }

        /// <summary>记录实际校验或文件保存的终态，未知人员和版本保持空值。</summary>
        private string RecordAlarmChange(ConfigurationChangeStage stage, ConfigurationChangeOutcome outcome,
            DateTimeOffset completedUtc, string operationId, string detail)
        {
            if (_changeJournal == null) return T("配置变更记录未接入");
            try
            {
                return _changeJournal.TryAppend(new ConfigurationChangeRecord {
                    RecordId = Guid.NewGuid(), MachineId = _machineId, TimeUtc = completedUtc,
                    Stage = stage, Outcome = outcome, OperationId = operationId,
                    Source = stage == ConfigurationChangeStage.Check ? "MachineAlarmCatalogDocument.Validate" : "MachineAlarmCatalogStore.Save", ObjectType = "报警库",
                    ObjectId = MachineAlarmCatalogStore.RelativeFilePath, Detail = detail });
            }
            catch (Exception error) { return error.Message; }
        }

        /// <summary>记录故障独立保留，后续状态刷新不能把它当作已保存。</summary>
        private void RememberHistoryError(string error)
        {
            if (string.IsNullOrWhiteSpace(error)) return;
            string warning = T("配置变更记录未保存") + error;
            if (string.IsNullOrEmpty(_pendingHistoryWarning)) _pendingHistoryWarning = warning;
            else if (!_pendingHistoryWarning.Contains(warning)) _pendingHistoryWarning += "\n" + warning;
            ChangeHistoryWarningText.Text = _pendingHistoryWarning;
            ChangeHistoryWarningScroll.Visibility = Visibility.Visible;
        }

        /// <summary>人员明确关闭记录故障提示，不改变文件和历史。</summary>
        private void DismissHistoryWarning_Click(object sender, RoutedEventArgs e)
        {
            _pendingHistoryWarning = null;
            ChangeHistoryWarningText.Text = string.Empty;
            ChangeHistoryWarningScroll.Visibility = Visibility.Collapsed;
        }
        /// <summary>切换报警时先保留旧行输入，再显示新选中报警字段。</summary>
        private void AlarmGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingFields) return;
            CommitSelectedAlarm();
            SelectedAlarm = AlarmGrid.SelectedItem as MachineAlarmCatalogEntry;
            DeleteButton.IsEnabled = SelectedAlarm != null;
            ShowSelectedAlarm();
            Search_TextChanged(this, null);
        }

        /// <summary>将表单文字与类型选择变化标记为未保存。</summary>
        private void AlarmField_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_loadingFields && SelectedAlarm != null) MarkDirty();
        }

        /// <summary>将报警类型变化标记为未保存。</summary>
        private void KindBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loadingFields && SelectedAlarm != null) MarkDirty();
        }

        /// <summary>把当前选中报警属性显示到表单；不改变已保存配置。</summary>
        private void ShowSelectedAlarm()
        {
            _loadingFields = true;
            try
            {
                bool hasAlarm = SelectedAlarm != null;
                CodeBox.IsEnabled = MessageTextBox.IsEnabled = KindBox.IsEnabled = hasAlarm;
                CheckGuidanceBox.IsEnabled = RecoveryGuidanceBox.IsEnabled = hasAlarm;
                CodeBox.Text = SelectedAlarm?.Code ?? string.Empty;
                MessageTextBox.Text = SelectedAlarm?.Message ?? string.Empty;
                CheckGuidanceBox.Text = SelectedAlarm?.CheckGuidance ?? string.Empty;
                RecoveryGuidanceBox.Text = SelectedAlarm?.RecoveryGuidance ?? string.Empty;
                KindBox.SelectedValue = SelectedAlarm?.Kind == MachineAlarmKind.NonStop ? "NonStop" : "StopMachine";
            }
            finally
            {
                _loadingFields = false;
            }
        }

        /// <summary>把表单内容提交到内存报警副本，格式和重复代码由保存校验给出明确错误。</summary>
        private void CommitSelectedAlarm()
        {
            if (SelectedAlarm == null) return;
            SelectedAlarm.Code = CodeBox.Text;
            SelectedAlarm.Message = MessageTextBox.Text;
            SelectedAlarm.CheckGuidance = CheckGuidanceBox.Text;
            SelectedAlarm.RecoveryGuidance = RecoveryGuidanceBox.Text;
            string kind = KindBox.SelectedValue as string;
            if (string.Equals(kind, "NonStop", StringComparison.Ordinal)) SelectedAlarm.Kind = MachineAlarmKind.NonStop;
            else if (string.Equals(kind, "StopMachine", StringComparison.Ordinal)) SelectedAlarm.Kind = MachineAlarmKind.StopMachine;
            AlarmGrid.Items.Refresh();
        }

        /// <summary>显示保存状态并使用一致的蓝色正常提示。</summary>
        private void SetStatus(string text)
        {
            StatusText.Text = T(text);
            StatusText.Foreground = Brush("#176BD0");
        }

        /// <summary>当前报警库存在尚未保存的编辑时为真。</summary>
        public bool HasUnsavedChanges { get { return _dirty; } }

        /// <summary>关闭 Host 前处理尚未保存的报警目录修改。</summary>
        public bool ConfirmClose()
        {
            bool savedDuringClose = false;
            if (_dirty)
            {
                MessageBoxResult result = MessageBox.Show(
                    T("报警库未保存修改"), T("报警库未保存"), MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
                if (result == MessageBoxResult.Cancel) return false;
                if (result == MessageBoxResult.Yes)
                {
                    SaveAlarms_Click(this, new RoutedEventArgs());
                    if (_dirty)
                    {
                        string failure = StatusText.Text + (string.IsNullOrEmpty(_pendingHistoryWarning) ? string.Empty : "\n" + _pendingHistoryWarning);
                        MessageBox.Show(Window.GetWindow(this), failure, T("报警库保存失败"), MessageBoxButton.OK, MessageBoxImage.Error);
                        return false;
                    }
                    savedDuringClose = true;
                }
            }
            if (!string.IsNullOrEmpty(_pendingHistoryWarning))
            {
                string warning = (savedDuringClose ? T("报警库文件已保存") + "\n" : string.Empty) + _pendingHistoryWarning;
                MessageBox.Show(Window.GetWindow(this), warning, T("配置变更记录提醒"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return true;
        }

        /// <summary>显示报警目录草稿尚未写入文件。</summary>
        private void MarkDirty()
        {
            _dirty = true;
            SetStatus("报警库有未保存修改");
            Search_TextChanged(this, null);
        }

        /// <summary>只读搜索内存草稿，不提交表单或刷新编辑选择。</summary>
        private void Search_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (SearchResults == null || Alarms == null) return;
            string query = (SearchBox.Text ?? string.Empty).Trim();
            var entries = Alarms.Select(alarm => alarm == SelectedAlarm
                ? new MachineAlarmCatalogEntry { Code = CodeBox.Text, Message = MessageTextBox.Text,
                    CheckGuidance = CheckGuidanceBox.Text, RecoveryGuidance = RecoveryGuidanceBox.Text }
                : alarm.Copy()).Where(alarm => string.IsNullOrEmpty(query)
                    || (alarm.Code ?? string.Empty).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                    || (alarm.Message ?? string.Empty).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            SearchResults.ItemsSource = entries;
            RefreshLocalizedText();
        }

        /// <summary>清除查询，保留编辑字段及脏状态。</summary>
        private void ClearSearch_Click(object sender, RoutedEventArgs e)
        {
            SearchBox.Text = string.Empty;
            Search_TextChanged(sender, null);
        }

        /// <summary>显示真实指导与缺项，未配置时不推断恢复动作。</summary>
        private void SearchResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            RefreshSearchGuidance();
        }
        /// <summary>切换语言时重绘搜索计数及选中指导，不重建结果或改变编辑内容。</summary>
        public void RefreshLocalizedText()
        {
            if (SearchResults == null || Alarms == null) return;
            SearchCountText.Text = string.Format(T("报警库搜索数量"), SearchResults.Items.Count, Alarms.Count);
            RefreshSearchGuidance();
            CatalogEmptyText.Text = !_catalogLoaded ? T("报警库目录未知") : Alarms.Count == 0 ? T("报警库空目录") : string.Empty;
            SearchEmptyText.Text = _catalogLoaded && Alarms.Count > 0 && SearchResults.Items.Count == 0 ? T("报警库搜索无匹配") : string.Empty;
        }

        /// <summary>只展示选中搜索副本包含的实际指导，缺少时明确未配置。</summary>
        private void RefreshSearchGuidance()
        {
            if (SearchGuidanceText == null) return;
            var alarm = SearchResults.SelectedItem as MachineAlarmCatalogEntry;
            SearchGuidanceText.Text = alarm == null ? T("报警库选择指导")
                : alarm.Code + " — " + alarm.Message + "\n" + T("报警库检查指导标签")
                    + (string.IsNullOrWhiteSpace(alarm.CheckGuidance) ? T("报警库指导未配置") : alarm.CheckGuidance)
                    + "\n" + T("报警库恢复指导标签")
                    + (string.IsNullOrWhiteSpace(alarm.RecoveryGuidance) ? T("报警库指导未配置") : alarm.RecoveryGuidance);
        }

        /// <summary>读取 Host 当前语言资源；缺少翻译时显示传入的中文原文。</summary>
        private string T(string key)
        {
            return TryFindResource(key) as string ?? key;
        }

        /// <summary>按十六进制颜色创建页面内使用的文字和控件颜色。</summary>
        private static SolidColorBrush Brush(string color)
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        }
    }
}
