using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using MotionControl.TaskEngine.Configuration;
using MotionControl.Framework.Runtime.Configuration;

namespace MotionControl.Host.Wpf.Configuration
{
    /// <summary>当前单机工程的导出、导入预览和备份查看页面。</summary>
    public partial class EquipmentProjectView : UserControl
    {
        /// <summary>负责导入导出及文件一致性校验的单机工程服务。</summary>
        private readonly MachineProjectPackageService _service;
        /// <summary>可选的当前设备持久历史，不提供时保留未接入状态。</summary>
        private readonly ConfigurationChangeJournal _journal;
        /// <summary>实时返回禁止应用工程的状态原因；只能在 UI 线程调用。</summary>
        private readonly Func<bool, string> _getApplyBlockedReason;
        /// <summary>应用结束后锁定旧内存配置，等待正常关闭并重启。</summary>
        private readonly Action _requireRestart;
        /// <summary>申请工程应用期间与控制卡连接互斥的许可。</summary>
        private readonly Func<Task<IDisposable>> _acquireApplyGate;
        /// <summary>人员选择且已通过检查的不可变工程预览。</summary>
        private MachineProjectPreview _preview;
        /// <summary>后台文件操作期间禁止重复提交。</summary>
        private bool _busy;
        /// <summary>成功或中途状态不明后本进程不能再次修改配置。</summary>
        private bool _restartRequired;
        /// <summary>界面当前语言。</summary>
        private bool _english;
        /// <summary>单个文件的页面显示长度上限；校验和应用仍使用完整文件。</summary>
        private const int MaximumPreviewCharacters = 50000;

        /// <summary>创建页面；文件操作不连接控制器、不发送运动命令。</summary>
        public EquipmentProjectView(MachineProjectPackageService service, Func<bool, string> getApplyBlockedReason,
            Func<Task<IDisposable>> acquireApplyGate, Action requireRestart)
            : this(service, getApplyBlockedReason, acquireApplyGate, requireRestart, null) { }

        /// <summary>接入当前设备的持久记录读取及工程导入结果记录。</summary>
        public EquipmentProjectView(MachineProjectPackageService service, Func<bool, string> getApplyBlockedReason,
            Func<Task<IDisposable>> acquireApplyGate, Action requireRestart, ConfigurationChangeJournal journal)
        {
            InitializeComponent();
            _journal = journal;
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _getApplyBlockedReason = getApplyBlockedReason ?? throw new ArgumentNullException(nameof(getApplyBlockedReason));
            _acquireApplyGate = acquireApplyGate ?? throw new ArgumentNullException(nameof(acquireApplyGate));
            _requireRestart = requireRestart ?? throw new ArgumentNullException(nameof(requireRestart));
            Loaded += HistoryPage_Loaded;
            Unloaded += HistoryPage_Unloaded;
            SetLanguage("zh-CN");
            ShowStatus(T("请选择工程文件查看内容，导入不会自动启动设备。", "Select a project to review. Import does not start the equipment."), false);
        }

        /// <summary>切换页面主要文字，不更改已选择的预览或工程文件。</summary>
        internal void SetLanguage(string language)
        {
            _english = string.Equals(language, "en-US", StringComparison.OrdinalIgnoreCase);
            ExportButton.Content = T("导出当前工程", "Export current project");
            ImportButton.Content = T("选择工程并预览", "Select and review project");
            BackupsButton.Content = T("查看备份", "View backups");
            ApplyButton.Content = T("应用导入", "Apply import");
            CloseButton.Content = T("关闭软件", "Close application");
            PreviewTab.Header = T("工程预览", "Project review");
            BackupsTab.Header = T("工程备份", "Backups");
            ChangeHistoryTab.Header = T("配置变更记录", "Configuration history");
            ChangeHistoryTitle.Text = T("配置变更记录 / 只读", "Configuration history / read only");
            ChangeHistoryMessage.Text = _journal == null
                ? T("当前未接入配置变更记录服务。", "The configuration history service is not connected.")
                : HistoryScope();
            RefreshHistoryButton.Content = T("刷新记录", "Refresh records");
            ExportHistoryButton.Content = T("导出记录", "Export records");
            RefreshHistoryButton.IsEnabled = _journal != null;
            RefreshHistoryButton.ToolTip = _journal == null ? ChangeHistoryMessage.Text : T("读取最近 50 条记录", "Read the latest 50 records");
            ExportHistoryButton.ToolTip = T("尚未提供历史导出服务。", "History export is unavailable.");
            if (DetailsTabs.SelectedItem == ChangeHistoryTab && _journal != null) _ = RefreshHistoryAsync();
            StatusDetails.Header = T("查看完整原因", "Show full details");
            CurrentFileLabel.Text = T("当前配置（只读）", "Current configuration (read only)");
            IncomingFileLabel.Text = T("工程包配置（只读）", "Incoming configuration (read only)");
            HintText.Text = T("工程包含设备映射与任务配置。导入前请先保存轴、IO、任务及报警库页面中未保存的编辑。", "A project includes equipment mappings and task settings. Save unsaved axis, IO, task and alarm edits before importing.");
            BackupsHint.Text = T("列出导入前保存的工程备份路径；备份不会自动覆盖当前设备配置。", "Backups created before import are listed here; they are not applied automatically.");
            FooterText.Text = T("工程包包含已登记配置；设备自定义参数须先登记。运行记录、恢复信息、任务草稿及包外文件会保留。应用前须断开控制器，应用后请关闭并重新打开。", "Packages contain registered configuration; custom equipment parameters must be registered first. Runtime records, recovery evidence, drafts and unlisted files are preserved. Disconnect before applying and reopen afterwards.");
            if (_preview == null) PreviewSummary.Text = T("尚未选择工程文件。", "No project selected.");
            else RenderPreview();
            UpdateButtons();
        }

        /// <summary>选择目标路径并在后台导出已保存的工程配置。</summary>
        private async void Export_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || _restartRequired) return;
            var picker = new SaveFileDialog { Filter = T("设备工程包|*.mcpkg.json|JSON 文件|*.json|所有文件|*.*", "Equipment project|*.mcpkg.json|JSON files|*.json|All files|*.*"),
                DefaultExt = ".mcpkg.json", AddExtension = true, FileName = "Machine01.mcpkg.json" };
            if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
            _busy = true; UpdateButtons();
            var host = Window.GetWindow(this) as MainWindow;
            host?.SetEquipmentProjectNavigationBusy(true);
            try
            {
                await Task.Run(() => _service.Export(picker.FileName));
                ShowStatus(T("工程已导出：", "Project exported: ") + picker.FileName, false);
            }
            catch (Exception error) { ShowStatus(T("导出失败：", "Export failed: ") + error.Message, true); }
            finally { host?.SetEquipmentProjectNavigationBusy(false); _busy = false; UpdateButtons(); }
        }

        /// <summary>只读取并验证所选工程，预览阶段不覆盖当前配置。</summary>
        private async void Import_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || _restartRequired) return;
            var picker = new OpenFileDialog { Filter = T("设备工程包|*.mcpkg.json|JSON 文件|*.json|所有文件|*.*", "Equipment project|*.mcpkg.json|JSON files|*.json|All files|*.*"), Multiselect = false };
            if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
            _preview = null; _busy = true; UpdateButtons();
            try
            {
                _preview = await Task.Run(() => _service.Preview(picker.FileName));
                RenderPreview(); DetailsTabs.SelectedItem = PreviewTab;
                ShowStatus(T("预览完成，请核对内容与差异后再应用。", "Review the content and differences before applying."), false);
            }
            catch (Exception error)
            {
                PreviewText.Text = string.Empty;
                PreviewFilePicker.ItemsSource = null; CurrentFileText.Text = string.Empty; IncomingFileText.Text = string.Empty;
                PreviewSummary.Text = T("未取得有效预览。", "No valid preview available.");
                ShowStatus(T("工程检查失败：", "Project validation failed: ") + error.Message, true);
            }
            finally { _busy = false; UpdateButtons(); }
        }

        /// <summary>显示服务返回的设备信息、文件、变更和警告，不向文件写入内容。</summary>
        private void RenderPreview()
        {
            if (_preview == null) return;
            PreviewSummary.Text = T("候选工程（尚未应用）：", "Candidate project (not applied): ") + _preview.EquipmentName
                + T("   运行方式：", "   Execution: ") + ExecutionName(_preview.ExecutionOwner)
                + T("   控制器：", "   Controller: ") + ControllerName(_preview.ControllerBackend)
                + T("   包的导出来源：", "   Package source: ") + _preview.SourceMachineId
                + T("   应用目标：", "   Apply target: ") + _preview.TargetMachineId;
            var text = new StringBuilder();
            text.AppendLine(TaskCapabilitySummary()).AppendLine();
            AppendList(text, T("工程包提供的文件", "Files supplied by the package"), _preview.Files);
            AppendList(text, T("应用后保留的现有文件（未由工程包提供）", "Existing files retained after apply (not supplied by the package)"), _preview.RetainedFiles);
            AppendList(text, T("与当前配置的差异", "Changes to current configuration"), _preview.Changes);
            AppendList(text, T("需要核对", "Items to review"), _preview.Warnings);
            PreviewText.Text = text.ToString();
            string selected = PreviewFilePicker.SelectedItem as string;
            PreviewFilePicker.ItemsSource = _preview.MergedFiles;
            if (selected != null && _preview.MergedFiles.Contains(selected, StringComparer.OrdinalIgnoreCase)) PreviewFilePicker.SelectedItem = selected;
            else if (_preview.MergedFiles.Count > 0) PreviewFilePicker.SelectedIndex = 0;
        }

        /// <summary>只展示本机代码声明的能力；离线预检通过不代表连接、回零或现场条件已满足。</summary>
        private string TaskCapabilitySummary()
        {
            var capabilities = _service.TaskCapabilities;
            if (capabilities == null)
                return T("检查范围：已检查配置定义，未提供当前后端的任务能力，不能确认可执行。",
                    "Validation scope: configuration definitions checked; task backend capabilities were not provided, so executability is unverified.");
            string supported = T("已接入", "available");
            string unsupported = T("未接入", "unavailable");
            return T("本机任务执行能力：多轴分别定位", "Installed task capabilities: independent axis group positioning ")
                + (capabilities.SupportsAxisGroupMove ? supported : unsupported)
                + T("；资源范围进入/退出条件", "; resource entry/exit conditions ")
                + (capabilities.SupportsInterlockConditions ? supported : unsupported)
                + T("。该结果仅说明代码能力，设备连接、回零和现场条件仍须重新核查。",
                    ". This describes code capabilities only. Connection, homing and site conditions still require verification.");
        }

        /// <summary>选择文件时对照预览时冻结的现有配置与工程包内容，不读取或写入运行状态。</summary>
        private void PreviewFile_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_preview == null || !(PreviewFilePicker.SelectedItem is string path)) return;
            try
            {
                CurrentFileText.Text = DisplayFile(_preview.GetCurrentFileContent(path));
                IncomingFileText.Text = _preview.Files.Contains(path, StringComparer.OrdinalIgnoreCase)
                    ? DisplayFile(_preview.GetFileContent(path))
                    : T("工程包未提供此文件；应用后保留左侧现有配置。它不属于本次导入的新配置。",
                        "The package does not supply this file. The existing configuration on the left will be retained; it is not newly imported configuration.");
            }
            catch (Exception error) { ShowStatus(T("无法显示配置预览：", "Cannot display configuration preview: ") + error.Message, true); }
        }

        /// <summary>限制显示长度，避免大型文件阻塞界面；不改变预检和应用的原始快照。</summary>
        private string DisplayFile(string content)
        {
            if (content == null) return T("当前设备没有该文件。", "This file is not present on the current equipment.");
            if (content.Length <= MaximumPreviewCharacters) return content;
            return content.Substring(0, MaximumPreviewCharacters) + T("\n… 仅显示前 50000 字，完整内容仍参与检查和应用。", "\n… Showing the first 50000 characters; the complete file is still validated and applied.");
        }

        /// <summary>向预览追加只读列表；没有内容时也明确显示。</summary>
        private void AppendList(StringBuilder text, string title, System.Collections.Generic.IEnumerable<string> rows)
        {
            text.AppendLine(title + "：");
            string[] values = rows == null ? new string[0] : rows.ToArray();
            if (values.Length == 0) text.AppendLine(T("  无", "  None"));
            foreach (string value in values) text.AppendLine("  • " + value);
            text.AppendLine();
        }

        /// <summary>只读列出本设备导入前的备份，不自动还原配置。</summary>
        private async void Backups_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || _restartRequired) return;
            _busy = true; UpdateButtons();
            try
            {
                var backups = await Task.Run(() => _service.ListBackups());
                BackupsList.ItemsSource = backups;
                DetailsTabs.SelectedItem = BackupsTab;
                ShowStatus(backups.Count == 0 ? T("暂无工程备份。", "No project backups found.")
                    : T("工程备份数量：", "Project backups: ") + backups.Count, false);
            }
            catch (Exception error) { ShowStatus(T("读取备份失败：", "Could not read backups: ") + error.Message, true); }
            finally { _busy = false; UpdateButtons(); }
        }

        /// <summary>在控制器已断开且状态可信时，经确认备份并应用所预览的工程。</summary>
        private async void Apply_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || _restartRequired || _preview == null) return;
            string blocked = _getApplyBlockedReason(false);
            if (!string.IsNullOrWhiteSpace(blocked)) { ShowStatus(blocked, true); return; }
            var answer = MessageBox.Show(Window.GetWindow(this),
                T("请先确认预览及轴、IO、任务和报警库中未保存的编辑。应用将备份当前工程，并要求关闭软件后重新打开。继续吗？",
                  "Review the project and save any axis, IO, task and alarm edits. The current project will be backed up and the application must be reopened. Continue?"),
                T("应用设备工程", "Apply equipment project"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
            blocked = _getApplyBlockedReason(false);
            if (!string.IsNullOrWhiteSpace(blocked)) { ShowStatus(blocked, true); return; }
            await ApplyConfirmedAsync();
        }

        /// <summary>确认后获取同一工程排他门，再次核查许可，仅对真正执行的 Apply 记录结果。</summary>
        private async Task ApplyConfirmedAsync()
        {
            if (_busy || _restartRequired || _preview == null) return;
            Window owner = Window.GetWindow(this);
            _busy = true; UpdateButtons();
            IDisposable gate = null;
            try
            {
                if (owner != null) owner.IsEnabled = false;
                gate = await _acquireApplyGate();
                string blocked = _getApplyBlockedReason(false);
                if (!string.IsNullOrWhiteSpace(blocked)) throw new InvalidOperationException(blocked);
                await ApplyAndReportAsync(_preview);
            }
            catch (Exception error)
            {
                ShowStatus(T("工程操作失败：", "Project operation failed: ") + error.Message, true);
            }
            finally
            {
                gate?.Dispose();
                if (owner != null) owner.IsEnabled = true;
                _busy = false; UpdateButtons();
            }
        }

        /// <summary>要求关闭重启，旧内存中的配置、流程和连接对象不能继续使用。</summary>
        private void EnterRestartRequired()
        {
            _restartRequired = true; _preview = null;
            _requireRestart();
        }

        /// <summary>通过正常窗口关闭路径退出；不结束进程或跳过安全收尾。</summary>
        private void Close_Click(object sender, RoutedEventArgs e) { Window.GetWindow(this)?.Close(); }

        /// <summary>按当前状态控制可重复执行的页面操作。</summary>
        private void UpdateButtons()
        {
            if (ApplyButton == null) return;
            ExportButton.IsEnabled = !_busy && !_restartRequired;
            ImportButton.IsEnabled = !_busy && !_restartRequired;
            BackupsButton.IsEnabled = !_busy && !_restartRequired;
            ApplyButton.IsEnabled = !_busy && !_restartRequired && _preview != null;
            CloseButton.Visibility = _restartRequired ? Visibility.Visible : Visibility.Collapsed;
            CloseButton.IsEnabled = !_busy;
        }

        /// <summary>显示成功信息或带原因的失败信息。</summary>
        private void ShowStatus(string text, bool error)
        {
            bool hasDetails = error && (text.Length > 220 || text.IndexOf('\n') >= 0);
            int firstLine = text.IndexOf('\n');
            int summaryLength = firstLine >= 0 ? Math.Min(firstLine, 220) : Math.Min(text.Length, 220);
            StatusText.Text = hasDetails ? text.Substring(0, summaryLength).TrimEnd('\r') + "…" : text;
            StatusText.Foreground = error ? Brushes.Firebrick : new SolidColorBrush(Color.FromRgb(23, 107, 208));
            StatusDetails.Visibility = hasDetails ? Visibility.Visible : Visibility.Collapsed;
            StatusDetails.IsExpanded = hasDetails;
            StatusDetailsText.Text = hasDetails ? text : string.Empty;
        }

        /// <summary>把工程内的执行入口转换为工程师易读名称。</summary>
        private string ExecutionName(string value)
        {
            if (string.Equals(value, "StationFlows", StringComparison.OrdinalIgnoreCase)) return T("C# 工位流程", "C# station flows");
            if (string.Equals(value, "TaskEngine", StringComparison.OrdinalIgnoreCase)) return T("任务列表", "Task list");
            if (string.Equals(value, "None", StringComparison.OrdinalIgnoreCase)) return T("手动", "Manual");
            return value;
        }

        /// <summary>将已知后端名称显示为工程师可读的连接类型。</summary>
        private string ControllerName(string value)
        {
            if (string.Equals(value, "Virtual", StringComparison.OrdinalIgnoreCase)) return T("软件控制器", "Software controller");
            if (string.Equals(value, "ZMotion", StringComparison.OrdinalIgnoreCase)) return T("正运动", "ZMotion");
            return value;
        }

        /// <summary>选择当前语言的界面文案。</summary>
        private string T(string chinese, string english) { return _english ? english : chinese; }
    }
}
