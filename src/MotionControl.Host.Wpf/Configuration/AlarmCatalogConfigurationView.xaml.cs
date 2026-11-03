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

        /// <summary>本页编辑的报警副本；保存成功前不改写设备配置。</summary>
        public ObservableCollection<MachineAlarmCatalogEntry> Alarms { get; private set; }

        /// <summary>当前选择的报警记录，供双向列表绑定使用。</summary>
        public MachineAlarmCatalogEntry SelectedAlarm { get; private set; }

        /// <summary>阻止切换选择和载入字段时误标记草稿已修改。</summary>
        private bool _loadingFields;

        /// <summary>记录是否存在尚未保存的编辑。</summary>
        private bool _dirty;

        /// <summary>载入当前设备报警库；读取失败时保留错误提示并允许在确认后修复。</summary>
        public AlarmCatalogConfigurationView(MachineAlarmCatalogStore store, string machineId)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            if (string.IsNullOrWhiteSpace(machineId)) throw new ArgumentException("设备编号不能为空。", nameof(machineId));
            _machineId = machineId;
            InitializeComponent();
            DataContext = this;
            LoadAlarms();
        }

        /// <summary>从磁盘读取报警定义副本，保留配置损坏或缺失的具体诊断。</summary>
        private void LoadAlarms()
        {
            try
            {
                MachineAlarmCatalogDocument document = _store.Load(_machineId);
                Alarms = new ObservableCollection<MachineAlarmCatalogEntry>(document.Alarms.Select(alarm => alarm.Copy()));
                DataContext = null;
                DataContext = this;
                StatusText.Text = T("报警库读取完成");
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

        /// <summary>先提交当前选中项的编辑，再通过设备配置存储原子保存整个报警库。</summary>
        private void SaveAlarms_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                CommitSelectedAlarm();
                var document = new MachineAlarmCatalogDocument
                {
                    SchemaVersion = 1,
                    Alarms = Alarms.Select(alarm => alarm.Copy()).ToList()
                };
                string backupPath = _store.Save(_machineId, document);
                _dirty = false;
                AlarmGrid.Items.Refresh();
                SetStatus(string.IsNullOrWhiteSpace(backupPath)
                    ? T("报警库保存成功")
                    : T("报警库保存成功，已备份") + " " + backupPath);
            }
            catch (Exception error)
            {
                StatusText.Text = T("报警库保存失败") + "：" + error.Message;
                StatusText.Foreground = Brush("#B54747");
            }
        }

        /// <summary>切换报警时先保留旧行输入，再显示新选中报警字段。</summary>
        private void AlarmGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingFields) return;
            CommitSelectedAlarm();
            SelectedAlarm = AlarmGrid.SelectedItem as MachineAlarmCatalogEntry;
            DeleteButton.IsEnabled = SelectedAlarm != null;
            ShowSelectedAlarm();
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
            if (!_dirty) return true;
            MessageBoxResult result = MessageBox.Show(
                T("报警库未保存修改"),
                T("报警库未保存"),
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);
            if (result == MessageBoxResult.No) return true;
            if (result == MessageBoxResult.Cancel) return false;
            SaveAlarms_Click(this, new RoutedEventArgs());
            return !_dirty;
        }

        /// <summary>显示报警目录草稿尚未写入文件。</summary>
        private void MarkDirty()
        {
            _dirty = true;
            SetStatus("报警库有未保存修改");
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
