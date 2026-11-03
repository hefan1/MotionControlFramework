using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MotionControl.Framework.Contracts.Preparation;
using MotionControl.Framework.Runtime.Machine;

namespace MotionControl.Host.Wpf.Operation
{
    /// <summary>两种执行入口共用的设备准备页面；只提交整机命令，不编排轴和 IO 动作。</summary>
    public partial class EquipmentPreparationView : UserControl
    {
        /// <summary>当前正式设备上下文；断线后清空。</summary>
        private MachineRuntime _runtime;
        /// <summary>按当前入口读取任务配置；不启动生产。</summary>
        private readonly Func<Task> _ensureConfiguration;
        /// <summary>进入已有监控或生产页面的导航回调。</summary>
        private readonly Action<string> _navigate;
        /// <summary>稳定的工位行，刷新时保持操作员选择。</summary>
        private readonly ObservableCollection<PreparationRow> _rows = new ObservableCollection<PreparationRow>();
        /// <summary>只读采集准备进度的界面定时器。</summary>
        private readonly DispatcherTimer _timer;
        /// <summary>当前页面是否正在装载或发起准备，阻止重复点击。</summary>
        private bool _commandInProgress;
        /// <summary>当前连接是否已成功检查准备配置。</summary>
        private bool _configurationLoaded;
        /// <summary>保留最近一次页面命令错误，直至下一次操作。</summary>
        private string _error;
        /// <summary>完整异常供维护人员展开查看，不占据操作员的主要状态区域。</summary>
        private string _diagnostic;
        /// <summary>实际设备配置目录，恢复被拦截时仅供维护人员定位原始证据。</summary>
        private string _recoveryEvidenceDirectory;

        /// <summary>创建页面，命令与导航由 Host 显式接入。</summary>
        public EquipmentPreparationView(Func<Task> ensureConfiguration, Action<string> navigate)
        {
            InitializeComponent();
            _ensureConfiguration = ensureConfiguration;
            _navigate = navigate;
            StationsGrid.ItemsSource = _rows;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _timer.Tick += RefreshTimer_Tick;
            Loaded += View_Loaded;
            Unloaded += View_Unloaded;
        }

        /// <summary>换连接时清除旧准备显示；不会从磁盘恢复已回零标志。</summary>
        public void SetRuntime(MachineRuntime runtime)
        {
            _runtime = runtime;
            _configurationLoaded = false;
            _error = null;
            _diagnostic = null;
            _rows.Clear();
            Refresh();
            if (IsLoaded) LoadConfiguration();
        }

        /// <summary>接收本次设备的真实配置路径，仅用于只读诊断，不修改恢复记录。</summary>
        public void SetRecoveryEvidenceDirectory(string directory)
        {
            _recoveryEvidenceDirectory = directory;
            Refresh();
        }

        /// <summary>切换本页文字资源；设备名称及实际诊断仍来自设备定义。</summary>
        public void SetLanguage(string language)
        {
            OperationUi.SetLanguage(this, language);
            Refresh();
        }

        /// <summary>页面进入可见区域后读取配置并开始显示进度。</summary>
        private void View_Loaded(object sender, RoutedEventArgs e) { _configurationLoaded = false; _timer.Start(); LoadConfiguration(); }
        /// <summary>隐藏页面时停止界面轮询；设备准备仍由 Runtime 管理。</summary>
        private void View_Unloaded(object sender, RoutedEventArgs e) { _timer.Stop(); }
        /// <summary>读取任务入口的现有会话；失败保留具体原因且不发送动作。</summary>
        private async void LoadConfiguration()
        {
            if (_configurationLoaded || _commandInProgress || _runtime == null) return;
            var loadingRuntime = _runtime;
            _commandInProgress = true;
            Refresh();
            try
            {
                if (_ensureConfiguration != null) await _ensureConfiguration();
                if (ReferenceEquals(loadingRuntime, _runtime))
                { _configurationLoaded = true; _error = null; _diagnostic = null; }
            }
            catch (Exception error) { if (ReferenceEquals(loadingRuntime, _runtime)) SetError(error); }
            finally
            {
                _commandInProgress = false; Refresh();
                if (!ReferenceEquals(loadingRuntime, _runtime) && IsLoaded) LoadConfiguration();
            }
        }

        /// <summary>刷新当前工位状态；不读取另一份映射或推测设备就绪。</summary>
        private void RefreshTimer_Tick(object sender, EventArgs e) { Refresh(); }

        /// <summary>将 Runtime 快照转换为简单的工位、状态和动作显示。</summary>
        private void Refresh()
        {
            if (SummaryText == null) return;
            StationColumn.Header = T("工位"); StateColumn.Header = T("准备状态"); ActionColumn.Header = T("当前动作");
            bool connected = _runtime != null && _runtime.State != MachineRunState.Disconnected && _runtime.MotionCards.IsConnected;
            var snapshots = connected ? _runtime.PreparationStations : null;
            if (snapshots != null)
            {
                foreach (var old in _rows.Where(row => !snapshots.Any(item => item.StationId == row.Id)).ToList()) _rows.Remove(old);
                foreach (var snapshot in snapshots)
                {
                    var row = _rows.FirstOrDefault(item => item.Id == snapshot.StationId);
                    if (row == null) { row = new PreparationRow(snapshot.StationId); _rows.Add(row); }
                    row.Update(snapshot, T(StateText(snapshot.State)));
                }
            }
            else _rows.Clear();
            bool preparing = _runtime != null && _runtime.IsPreparing;
            bool recoveryBlocked = _runtime != null && !string.IsNullOrWhiteSpace(_runtime.RecoveryBlockReason);
            int completed = snapshots == null ? 0 : snapshots.Count(item => item.IsPrepared);
            SummaryText.Text = !connected ? T("连接设备后查看准备状态")
                : preparing ? T("正在准备设备")
                : recoveryBlocked ? T("设备恢复信息未确认")
                : _rows.Count == 0 ? T("当前未配置工位回零")
                : completed == _rows.Count ? T("配置工位已准备完成")
                : string.Format(T("已准备 {0} / {1} 个工位"), completed, _rows.Count);
            GuidanceText.Text = preparing ? T("按设备定义的顺序执行，完成后等待启动；需要中断时按停止回零。")
                : recoveryBlocked ? T("请联系维护人员核对现场及恢复记录，确认前不能回零或启动。")
                : _runtime?.IsTaskEngineControlClaimed == true
                    ? T("回零前请确认相关轴已使能、工位与物料状态安全。如需使能，请先暂停并确认轴停稳，在生产流程停止并关闭任务，再到轴手动操作。")
                    : T("回零前请确认相关轴已使能、工位与物料状态安全；回零完成后再核对其他启动条件。");
            var failed = snapshots?.FirstOrDefault(item => item.State == StationPreparationState.Failed);
            ErrorText.Text = recoveryBlocked ? _runtime.RecoveryBlockReason
                : string.IsNullOrWhiteSpace(_error) ? string.Empty
                : failed == null || _error.Contains(failed.StationName) ? _error : failed.StationName + "：" + _error;
            string recoveryDiagnostic = recoveryBlocked ? BuildRecoveryDiagnostic() : null;
            DiagnosticText.Text = recoveryDiagnostic ?? _diagnostic ?? string.Empty;
            DiagnosticExpander.Visibility = string.IsNullOrWhiteSpace(DiagnosticText.Text) ? Visibility.Collapsed : Visibility.Visible;
            bool canPrepare = connected && _configurationLoaded && !preparing && !recoveryBlocked && !_commandInProgress
                && (_runtime.State == MachineRunState.Ready || _runtime.State == MachineRunState.ResetRequired || _runtime.State == MachineRunState.Paused);
            PrepareAllButton.IsEnabled = canPrepare && _rows.Count > 0;
            PrepareAllButton.Opacity = PrepareAllButton.IsEnabled ? 1.0 : 0.5;
            PrepareSelectedButton.IsEnabled = canPrepare && StationsGrid.SelectedItem != null;
            StopPreparationButton.IsEnabled = preparing;
            var selected = StationsGrid.SelectedItem as PreparationRow;
            DetailText.Text = selected == null ? T("选择工位可查看当前动作和处理说明；相关前置工位会按依赖顺序一起准备。")
                : selected.Name + " · " + selected.StateText + "\n" + selected.Action
                    + (string.IsNullOrWhiteSpace(selected.Detail) ? string.Empty : "\n" + SimplifyError(selected.Detail));
        }

        /// <summary>展示实际证据路径和处理边界，不推断现场状态、不提供解除门禁操作。</summary>
        private string BuildRecoveryDiagnostic()
        {
            string result = T("恢复诊断.现场核查");
            if (!string.IsNullOrWhiteSpace(_recoveryEvidenceDirectory))
            {
                string runtimeDirectory = System.IO.Path.Combine(_recoveryEvidenceDirectory, "Runtime");
                result += "\n" + T("恢复诊断.记录位置") + "\n"
                    + System.IO.Path.Combine(runtimeDirectory, "RecoverySnapshot.json") + "\n"
                    + System.IO.Path.Combine(runtimeDirectory, "AutomaticRun.pending");
            }
            if (!string.IsNullOrWhiteSpace(_diagnostic)) result += "\n\n" + _diagnostic;
            return result;
        }

        /// <summary>把通用准备状态映射为现场用语。</summary>
        private static string StateText(StationPreparationState state)
        {
            switch (state)
            {
                case StationPreparationState.Waiting: return "等待前置工位";
                case StationPreparationState.Preparing: return "回零中";
                case StationPreparationState.Ready: return "已准备";
                case StationPreparationState.Stopped: return "回零已停止";
                case StationPreparationState.Failed: return "需要处理";
                default: return "未准备";
            }
        }

        /// <summary>仅更新选中工位说明。</summary>
        private void StationSelection_Changed(object sender, SelectionChangedEventArgs e) { Refresh(); }
        /// <summary>按框架依赖关系准备所有已配置工位。</summary>
        private async void PrepareAll_Click(object sender, RoutedEventArgs e) { await PrepareAsync(null); }
        /// <summary>准备选中工位及其前置工位。</summary>
        private async void PrepareSelected_Click(object sender, RoutedEventArgs e)
        { var row = StationsGrid.SelectedItem as PreparationRow; if (row != null) await PrepareAsync(row.Id); }

        /// <summary>提交独立准备命令，成功后停留本页，不自动进入生产。</summary>
        private async Task PrepareAsync(string stationId)
        {
            if (_runtime == null || _commandInProgress) return;
            _commandInProgress = true; _error = null; _diagnostic = null; DiagnosticExpander.IsExpanded = false; Refresh();
            try { await _runtime.PrepareAsync(stationId, CancellationToken.None); }
            catch (OperationCanceledException) { _error = T("回零已停止，请检查当前机构状态后重新回零。"); }
            catch (Exception error) { SetError(error); }
            finally { _commandInProgress = false; Refresh(); }
        }

        /// <summary>请求统一受控停止，等待 Runtime 确认动作退出。</summary>
        private async void StopPreparation_Click(object sender, RoutedEventArgs e)
        {
            if (_runtime == null) return;
            try { await _runtime.StopPreparationAsync(CancellationToken.None); }
            catch (Exception error) { SetError(error); }
            Refresh();
        }
        /// <summary>保留原始异常；摘要去除执行器编号但保留具体现场条件及读数。</summary>
        private void SetError(Exception error)
        {
            _error = SimplifyError(error.Message);
            _diagnostic = error.ToString();
        }

        /// <summary>去掉任务引擎的路径包装，不能从文案推断或替换实际设备条件。</summary>
        private static string SimplifyError(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return string.Empty;
            // 执行器当前使用全角冒号，兼容半角及字段间空格变化；只匹配完整的三段内部路径。
            string result = Regex.Replace(message, @"任务\s+[^/\r\n]+?\s*/\s*节点\s+[^/\r\n]+?\s*/\s*调用\s+[^:：\r\n]+[:：]\s*", string.Empty);
            result = Regex.Replace(result, @"准备任务失败：[^。\r\n]+。(?=\S)", string.Empty);
            const string prefix = "工位准备未完成：";
            if (result.StartsWith(prefix, StringComparison.Ordinal)) result = result.Substring(prefix.Length);
            return string.IsNullOrWhiteSpace(result) ? message : result.Trim();
        }
        /// <summary>查看生产流程，不发送启动指令。</summary>
        private void Production_Click(object sender, RoutedEventArgs e) { _navigate?.Invoke("生产流程"); }
        /// <summary>查看现有 IO 监控页面，不修改输入或输出。</summary>
        private void IoMonitor_Click(object sender, RoutedEventArgs e) { _navigate?.Invoke("IO监控"); }
        /// <summary>查看现有轴状态页面，不执行手动动作。</summary>
        private void AxisMonitor_Click(object sender, RoutedEventArgs e) { _navigate?.Invoke("轴监控"); }
        /// <summary>打开现有轴手动页面；不自动切换伺服或关闭任务会话。</summary>
        private void AxisManual_Click(object sender, RoutedEventArgs e) { _navigate?.Invoke("轴手动"); }
        /// <summary>读取操作页本地化文本。</summary>
        private string T(string value) { return OperationUi.T(this, value); }
    }

    /// <summary>用于保持工位选择和按行更新的只读展示数据。</summary>
    internal sealed class PreparationRow : INotifyPropertyChanged
    {
        /// <summary>稳定工位标识。</summary>
        public string Id { get; }
        /// <summary>设备提供的工位名称。</summary>
        public string Name { get; private set; }
        /// <summary>现场可读状态。</summary>
        public string StateText { get; private set; }
        /// <summary>当前准备动作。</summary>
        public string Action { get; private set; }
        /// <summary>具体阻塞或错误说明。</summary>
        public string Detail { get; private set; }
        /// <summary>辅助状态文字的颜色。</summary>
        public Brush StateBrush { get; private set; }
        /// <summary>通知 WPF 更新当前行。</summary>
        public event PropertyChangedEventHandler PropertyChanged;
        /// <summary>创建一条固定工位行。</summary>
        internal PreparationRow(string id) { Id = id; }
        /// <summary>复制不可变快照并刷新绑定，不改变工位顺序和选择。</summary>
        internal void Update(StationPreparationSnapshot snapshot, string stateText)
        {
            Name = snapshot.StationName; StateText = stateText; Action = snapshot.Action; Detail = snapshot.Detail;
            StateBrush = snapshot.State == StationPreparationState.Failed ? Brushes.Firebrick
                : snapshot.IsPrepared ? Brushes.SeaGreen : snapshot.State == StationPreparationState.Preparing ? Brushes.RoyalBlue : Brushes.SlateGray;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        }
    }
}
