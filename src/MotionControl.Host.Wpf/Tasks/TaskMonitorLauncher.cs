using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.IO;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.Framework.Runtime.Machine;
using MotionControl.Framework.Runtime.Variables;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Machine;
using MotionControl.TaskEngine.Wpf.Simulation;
using MotionControl.TaskEngine.Wpf.Views;

namespace MotionControl.Host.Wpf.Tasks
{
    /// <summary>在正式 Host 中加载任务、运行整机任务并显示现场步骤与报警。</summary>
    internal sealed class TaskMonitorLauncher : UserControl
    {
        /// <summary>复用当前设备配置服务。</summary>
        private readonly MachineConfigurationStore _store;
        /// <summary>当前设备标识。</summary>
        private readonly string _machineId;
        /// <summary>负责导航到已有配置和监控页面的主窗口。</summary>
        private readonly MainWindow _host;
        /// <summary>页面操作状态与目录读取结果。</summary>
        private readonly TextBlock _status;
        /// <summary>运行入口的当前状态说明。</summary>
        private readonly TextBlock _runStatus;
        /// <summary>默认收起的完整错误详情，供维护人员定位配置和异常来源。</summary>
        private readonly Expander _diagnostics;
        /// <summary>可复制的原始异常内容。</summary>
        private readonly TextBox _diagnosticText;
        /// <summary>最近一次失败，用于语言切换后重新生成操作提示。</summary>
        private Exception _lastError;
        /// <summary>最近一次失败所属的操作资源键。</summary>
        private string _failureOperationKey;
        /// <summary>最近一次失败后的建议资源键。</summary>
        private string _failureAdviceKey;
        /// <summary>当前诊断对应的配置页快捷入口。</summary>
        private readonly WrapPanel _nextStepButtons;
        /// <summary>装载当前已应用的任务、复位、启动、暂停和停止控制。</summary>
        private readonly Button _loadButton;
        /// <summary>复位按钮。</summary>
        private readonly Button _resetButton;
        /// <summary>启动按钮。</summary>
        private readonly Button _startButton;
        /// <summary>暂停按钮。</summary>
        private readonly Button _pauseButton;
        /// <summary>停止并关闭当前任务会话按钮。</summary>
        private readonly Button _stopButton;
        /// <summary>显示当前任务步骤监控页。</summary>
        private readonly ContentControl _monitorContent;
        /// <summary>显示 Runtime 当前报警，保持任务页内可见。</summary>
        private readonly TaskAlarmGrid _alarmGrid;
        /// <summary>连接后提供任务设备组合所需的当前 Runtime。</summary>
        private MachineRuntime _runtime;
        /// <summary>连接后的运动卡组。</summary>
        private MotionCardGroup _motionCards;
        /// <summary>连接后的逻辑 IO。</summary>
        private ILogicalIo _logicalIo;
        /// <summary>与 Host 其他页面共用的设备变量存储。</summary>
        private DeviceVariableStore _variables;
        /// <summary>当前任务运行会话；创建时取得设备控制权。</summary>
        private TaskMonitorSession _session;
        /// <summary>当前正式设备任务监控视图。</summary>
        private TaskMonitorView _monitor;
        /// <summary>定时刷新任务状态和报警，并把任务故障反馈到 Runtime。</summary>
        private readonly DispatcherTimer _stateTimer;
        /// <summary>防止任务故障同步 Runtime 状态的后台操作重复启动。</summary>
        private bool _faultStatusSyncing;
        /// <summary>防止多个任务按钮同时操作设备。</summary>
        private bool _commandInProgress;
        /// <summary>当前是否已装载一个整机任务会话。</summary>
        internal bool HasTaskSession { get { return _session != null; } }

        /// <summary>创建任务运行入口；不在此处连接控制卡或启动任务。</summary>
        internal TaskMonitorLauncher(MachineConfigurationStore store, string machineId, MainWindow host)
        {
            _store = store; _machineId = machineId; _host = host;
            var root = new Grid { Margin = new Thickness(14, 8, 14, 12), Background = System.Windows.Media.Brushes.Transparent };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 220 });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            var note = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 9), FontSize = 13 };
            note.SetResourceReference(TextBlock.TextProperty, "任务执行页面说明");
            header.Children.Add(note);

            var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            StyleToolbar(toolbar);
            _loadButton = AddCommandButton(toolbar, "任务执行读取任务", LoadTask_Click);
            _resetButton = AddCommandButton(toolbar, "任务执行复位", ResetTask_Click);
            _startButton = AddCommandButton(toolbar, "任务执行启动", StartTask_Click);
            _pauseButton = AddCommandButton(toolbar, "任务执行暂停", PauseTask_Click);
            _stopButton = AddCommandButton(toolbar, "任务执行停止", StopTask_Click);
            AddButton(toolbar, "设备总览", EquipmentOverview_Click);
            AddButton(toolbar, "流程状态", FlowStatus_Click);
            AddButton(toolbar, "刷新关联配置目录", RefreshCatalog_Click);
            header.Children.Add(toolbar);
            root.Children.Add(header);

            _runStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6), Foreground = Brush("#176BD0") };
            Grid.SetRow(_runStatus, 1); root.Children.Add(_runStatus);
            _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 5) };
            Grid.SetRow(_status, 2); root.Children.Add(_status);

            var monitorArea = new Grid();
            _monitorContent = new ContentControl();
            monitorArea.Children.Add(_monitorContent);
            _nextStepButtons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(8), Visibility = Visibility.Collapsed };
            monitorArea.Children.Add(_nextStepButtons);
            _diagnosticText = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                MaxHeight = 200, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
            _diagnostics = new Expander { Margin = new Thickness(8), HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Bottom, Visibility = Visibility.Collapsed, Content = _diagnosticText };
            _diagnostics.SetResourceReference(HeaderedContentControl.HeaderProperty, "任务关联诊断详情");
            monitorArea.Children.Add(_diagnostics);
            Grid.SetRow(monitorArea, 3); root.Children.Add(monitorArea);

            var alarmPanel = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            var alarmTitle = new TextBlock { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 8, 4) };
            alarmTitle.SetResourceReference(TextBlock.TextProperty, "任务执行当前报警");
            DockPanel.SetDock(alarmTitle, Dock.Top); alarmPanel.Children.Add(alarmTitle);
            _alarmGrid = new TaskAlarmGrid(GetText); alarmPanel.Children.Add(_alarmGrid);
            Grid.SetRow(alarmPanel, 4); root.Children.Add(alarmPanel);
            Content = root;

            _stateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _stateTimer.Tick += StateTimer_Tick;
            _host.LanguageChanged += Host_LanguageChanged;
            Loaded += Launcher_Loaded;
            UpdateRunStatus();
            UpdateCommandAvailability();
        }

        /// <summary>创建任务运行命令按钮并接入 Host 当前语言资源。</summary>
        private static Button AddCommandButton(Panel panel, string key, RoutedEventHandler handler)
        {
            var button = new Button { Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 8, 0),
                Background = Brush("#FFFFFF"), BorderBrush = Brush("#C9D5E3"), BorderThickness = new Thickness(1) };
            button.SetResourceReference(ContentControl.ContentProperty, key);
            button.Click += handler; panel.Children.Add(button); return button;
        }

        /// <summary>用中文资源键创建导航按钮，语言切换自动刷新。</summary>
        private void AddButton(Panel panel, string key, RoutedEventHandler handler)
        {
            var button = new Button { Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 8, 0),
                Background = Brush("#FFFFFF"), BorderBrush = Brush("#C9D5E3"), BorderThickness = new Thickness(1) };
            button.SetResourceReference(ContentControl.ContentProperty, key); button.Click += handler; panel.Children.Add(button);
        }

        /// <summary>设置与 Host 其他任务页一致的浅色工具栏。</summary>
        private static void StyleToolbar(Panel panel)
        {
            panel.Background = Brush("#F7F9FC");
        }

        /// <summary>提供连接后的设备实例；断开前必须先安全结束任务会话。</summary>
        internal async Task SetMachineContextAsync(MachineRuntime runtime, MotionCardGroup motionCards,
            ILogicalIo logicalIo, DeviceVariableStore variables)
        {
            if (_session != null) await CloseTaskSessionAsync().ConfigureAwait(true);
            _runtime = runtime; _motionCards = motionCards; _logicalIo = logicalIo; _variables = variables;
            if (runtime == null)
            {
                _stateTimer.Stop();
                _alarmGrid.ClearAlarms();
            }
            ClearError();
            UpdateRunStatus();
            UpdateCommandAvailability();
        }

        /// <summary>页面进入可见区域后刷新已保存的任务资源目录。</summary>
        private void Launcher_Loaded(object sender, RoutedEventArgs e) { RefreshCatalog(); }
        /// <summary>重新读取其他页面已保存的配置。</summary>
        private void RefreshCatalog_Click(object sender, RoutedEventArgs e) { RefreshCatalog(); }
        /// <summary>打开实际展示设备运行状态的总览页。</summary>
        private void EquipmentOverview_Click(object sender, RoutedEventArgs e) { _host.ShowEquipmentOverview(); }
        /// <summary>打开实际展示工位步骤的流程状态页。</summary>
        private void FlowStatus_Click(object sender, RoutedEventArgs e) { _host.ShowFlowStatus(); }

        /// <summary>在后台创建真实任务设备并载入已应用的任务定义，未装载时不接管设备。</summary>
        private async void LoadTask_Click(object sender, RoutedEventArgs e)
        {
            if (_runtime == null || _motionCards == null || _logicalIo == null || _variables == null)
            {
                ShowError(new InvalidOperationException("请先连接运动控制卡，再读取当前任务。"), "任务设备未连接", "任务配置检查建议");
                return;
            }

            await RunCommandAsync(async () =>
            {
                var runtime = _runtime;
                TaskMonitorSession loaded = await Task.Run(() => HostTaskSessionLoader.Load(
                    _store, _machineId, _motionCards, _logicalIo, _variables, runtime)).ConfigureAwait(true);

                _session = loaded;
                _monitor = new TaskMonitorView(loaded);
                _monitor.ReferenceRequested += Monitor_ReferenceRequested;
                _monitor.SetLanguage(_host.CurrentLanguage);
                _monitorContent.Content = _monitor;
                _stateTimer.Start();
                ClearError();
                _status.Text = string.Format(GetText("任务执行已装载"), loaded.Source);
                UpdateRunStatus();
                UpdateCommandAvailability();
                _host.RefreshMachineStateDisplay();
            }, "任务装载失败", "任务配置检查建议").ConfigureAwait(true);
        }

        /// <summary>复位运动卡、Runtime 报警与任务整机状态；复位后仍需操作员按启动。</summary>
        private async void ResetTask_Click(object sender, RoutedEventArgs e)
        {
            await RunCommandAsync(async () =>
            {
                RequireTaskSession();
                await _session.Machine.ValidateResetPrerequisitesAsync().ConfigureAwait(true);
                if (_runtime.State != MachineRunState.Ready)
                    await _runtime.ResetAsync(CancellationToken.None).ConfigureAwait(true);
                await _session.Machine.ResetAsync().ConfigureAwait(true);
                _status.Text = GetText("任务执行复位完成");
            }, "任务复位失败", "任务运行复位建议").ConfigureAwait(true);
        }

        /// <summary>先让 Runtime 建立安全运行状态和急停监控，再启动任务工位。</summary>
        private async void StartTask_Click(object sender, RoutedEventArgs e)
        {
            await RunCommandAsync(async () =>
            {
                RequireTaskSession();
                await _runtime.BeginTaskEngineRunAsync(CancellationToken.None).ConfigureAwait(true);
                try { _session.Machine.Start(); }
                catch (Exception startError)
                {
                    await _runtime.FinishTaskEngineRunAsync(true, startError).ConfigureAwait(true);
                    throw;
                }
                _status.Text = GetText("任务执行已启动");
            }, "任务启动失败", "任务运行复位建议").ConfigureAwait(true);
        }

        /// <summary>请求所有任务停步并等待真实轴受控停稳，再更新整机状态。</summary>
        private async void PauseTask_Click(object sender, RoutedEventArgs e)
        {
            await RunCommandAsync(async () =>
            {
                RequireTaskSession();
                await _session.Machine.PauseAsync().ConfigureAwait(true);
                await _runtime.FinishTaskEngineRunAsync(false, null).ConfigureAwait(true);
                _status.Text = GetText("任务执行已暂停");
            }, "任务暂停失败", "任务运行复位建议").ConfigureAwait(true);
        }

        /// <summary>取消并等待全部任务动作收尾，然后关闭会话并归还设备控制权。</summary>
        private async void StopTask_Click(object sender, RoutedEventArgs e)
        {
            await RunCommandAsync(async () =>
            {
                RequireTaskSession();
                await _session.Machine.AbortAsync().ConfigureAwait(true);
                if (_runtime.State == MachineRunState.Running || _runtime.State == MachineRunState.Pausing)
                    await _runtime.FinishTaskEngineRunAsync(false, null).ConfigureAwait(true);
                await CloseTaskSessionAsync().ConfigureAwait(true);
                _status.Text = GetText("任务执行已停止");
            }, "任务停止失败", "任务运行复位建议").ConfigureAwait(true);
        }

        /// <summary>检查会话未运行后关闭任务执行器；断卡和应用退出均复用此安全收尾。</summary>
        internal async Task CloseTaskSessionAsync()
        {
            TaskMonitorSession session = _session;
            if (session == null) return;
            TaskMachineState state = session.Machine.GetSnapshot().State;
            if (state == TaskMachineState.Running || state == TaskMachineState.Pausing
                || _runtime?.State == MachineRunState.Running || _runtime?.State == MachineRunState.Pausing)
                throw new InvalidOperationException("任务仍在运行或等待停轴；先在任务运行页暂停或停止并确认安全状态。");

            _stateTimer.Stop();
            await session.Machine.CloseAsync().ConfigureAwait(true);
            _session = null;
            _monitor = null;
            _monitorContent.Content = null;
            _alarmGrid.ClearAlarms();
            UpdateRunStatus();
            UpdateCommandAvailability();
            _host.RefreshMachineStateDisplay();
        }

        /// <summary>显示最新目录数量，不缓存为第二份映射。</summary>
        private void RefreshCatalog()
        {
            try
            {
                var namedResources = new MotionControl.TaskEngine.Configuration.TaskMachineConfigurationStore(_store)
                    .LoadNamedResources(_machineId);
                var catalog = new TaskResourceCatalogLoader(_store).Load(_machineId, namedResources);
                ClearError();
                _status.Text = string.Format(GetText("任务关联目录统计"), catalog.Axes.Count, catalog.Inputs.Count,
                    catalog.Outputs.Count, catalog.Variables.Count, catalog.TeachPoints.Count)
                    + "\n" + _store.GetMachineDirectory(_machineId) + "\n" + string.Join("\n", catalog.Notices);
            }
            catch (Exception error)
            {
                ShowError(error, "任务目录读取失败", "任务配置检查建议");
            }
        }

        /// <summary>将任务步骤的关联对象交给 Host 已有配置和监控页面定位。</summary>
        private void Monitor_ReferenceRequested(object sender, TaskReferenceEventArgs e)
        {
            _host.OpenTaskReference(e.Page, e.Name, e.Direction);
        }

        /// <summary>在可见状态下刷新任务快照和 Runtime 当前报警。</summary>
        private async void StateTimer_Tick(object sender, EventArgs e)
        {
            if (_session == null) return;
            _monitor?.Refresh();
            RefreshAlarms();
            TaskMachineSnapshot snapshot = _session.Machine.GetSnapshot();
            if (!_faultStatusSyncing && snapshot.State == TaskMachineState.Faulted
                && (_runtime?.State == MachineRunState.Running || _runtime?.State == MachineRunState.Pausing))
            {
                _faultStatusSyncing = true;
                try
                {
                    string message = snapshot.Faults.LastOrDefault()?.Message ?? "任务执行异常暂停。";
                    await _runtime.FinishTaskEngineRunAsync(true, new InvalidOperationException(message)).ConfigureAwait(true);
                }
                catch (Exception error)
                {
                    System.Diagnostics.Trace.TraceError("任务故障同步到 Runtime 失败：" + error);
                }
                finally { _faultStatusSyncing = false; }
            }
            UpdateRunStatus(snapshot);
            UpdateCommandAvailability();
        }

        /// <summary>按 Runtime 当前报警快照刷新本页列表，不对报警做复位或清除。</summary>
        private void RefreshAlarms()
        {
            var alarms = _runtime?.AlarmService.Alarms ?? new MachineAlarmRecord[0];
            _alarmGrid.ShowAlarms(alarms);
        }

        /// <summary>更新任务整机和设备运行状态摘要。</summary>
        private void UpdateRunStatus(TaskMachineSnapshot snapshot = null)
        {
            if (_motionCards != null && !_motionCards.IsConnected)
            {
                _runStatus.Text = GetText("任务执行等待连接");
                return;
            }
            if (_session == null)
            {
                _runStatus.Text = _runtime == null
                    ? GetText("任务执行等待连接")
                    : string.Format(GetText("任务执行设备已连接"), GetControlOwnerText(_runtime.ControlOwner));
                return;
            }

            snapshot = snapshot ?? _session.Machine.GetSnapshot();
            _runStatus.Text = string.Format(GetText("任务执行运行状态"),
                _monitor?.T(snapshot.State.ToString()) ?? snapshot.State.ToString(),
                GetText(MainWindow.GetMachineStateResourceKey(_runtime?.State ?? MachineRunState.Disconnected)));
        }

        /// <summary>将当前控制入口转换为操作员可读的本地化名称。</summary>
        private string GetControlOwnerText(MachineControlOwner owner)
        {
            switch (owner)
            {
                case MachineControlOwner.StationFlows: return GetText("控制入口工位流程");
                case MachineControlOwner.TaskEngine: return GetText("控制入口任务列表");
                default: return GetText("控制入口无");
            }
        }

        /// <summary>按任务整机状态启用命令，避免从其他页面并发启动或复位。</summary>
        private void UpdateCommandAvailability()
        {
            bool connected = _runtime != null && _motionCards != null && _motionCards.IsConnected
                && _logicalIo != null && _variables != null;
            TaskMachineState? taskState = _session == null ? (TaskMachineState?)null : _session.Machine.GetSnapshot().State;
            _loadButton.IsEnabled = connected && _session == null && !_commandInProgress;
            _resetButton.IsEnabled = connected && taskState.HasValue && !_commandInProgress
                && taskState != TaskMachineState.Running && taskState != TaskMachineState.Pausing
                && _runtime.State != MachineRunState.Running && _runtime.State != MachineRunState.Pausing;
            _startButton.IsEnabled = connected && taskState == TaskMachineState.Ready && !_commandInProgress
                && _runtime.State == MachineRunState.Ready;
            _pauseButton.IsEnabled = taskState == TaskMachineState.Running && !_commandInProgress;
            _stopButton.IsEnabled = _session != null && !_commandInProgress;
        }

        /// <summary>统一串行执行页面命令并在失败时保留完整诊断。</summary>
        private async Task RunCommandAsync(Func<Task> command, string operationKey, string adviceKey)
        {
            if (_commandInProgress) return;
            _commandInProgress = true;
            UpdateCommandAvailability();
            try
            {
                await command().ConfigureAwait(true);
                ClearError();
            }
            catch (Exception error)
            {
                ShowError(error, operationKey, adviceKey);
            }
            finally
            {
                _commandInProgress = false;
                UpdateRunStatus();
                UpdateCommandAvailability();
            }
        }

        /// <summary>确认当前连接上下文和任务会话均有效。</summary>
        private void RequireTaskSession()
        {
            if (_session == null || _runtime == null)
                throw new InvalidOperationException("请先连接运动控制卡并读取任务配置。");
        }

        /// <summary>向操作人员说明异常和下一步，并保留可复制的完整堆栈。</summary>
        private void ShowError(Exception error, string operationKey, string adviceKey)
        {
            _lastError = error;
            _failureOperationKey = operationKey;
            _failureAdviceKey = adviceKey;
            _diagnosticText.Text = error.ToString();
            _diagnostics.IsExpanded = false;
            _diagnostics.Visibility = Visibility.Visible;
            UpdateNextStepButton(error);
            RefreshErrorText();
        }

        /// <summary>对已知缺失配置提供直达入口，不生成默认映射或自动保存。</summary>
        private void UpdateNextStepButton(Exception error)
        {
            _nextStepButtons.Children.Clear();
            _nextStepButtons.Visibility = Visibility.Collapsed;
            var missingFile = error as FileNotFoundException;
            if (missingFile == null || string.IsNullOrWhiteSpace(missingFile.FileName)) return;

            string fileName = Path.GetFileName(missingFile.FileName);
            string page;
            string buttonResource;
            if (string.Equals(fileName, "AxisParameters.json", StringComparison.OrdinalIgnoreCase))
            { page = "轴映射配置"; buttonResource = "打开轴映射配置"; }
            else if (string.Equals(fileName, "IoMapping.json", StringComparison.OrdinalIgnoreCase))
            { page = "IO映射"; buttonResource = "打开IO映射配置"; }
            else if (string.Equals(fileName, "TaskMachine.json", StringComparison.OrdinalIgnoreCase))
            { page = "任务配置"; buttonResource = "打开任务配置"; }
            else if (string.Equals(fileName, "DeviceVariables.json", StringComparison.OrdinalIgnoreCase))
            { page = "设备变量"; buttonResource = "打开设备变量"; }
            else return;

            AddButton(_nextStepButtons, buttonResource, (sender, args) => _host.ShowConfigurationPage(page));
            _nextStepButtons.Visibility = Visibility.Visible;
        }

        /// <summary>切换语言后更新页面文字和当前步骤，不重载或更改任务配置。</summary>
        private void Host_LanguageChanged(object sender, EventArgs e)
        {
            _monitor?.SetLanguage(_host.CurrentLanguage);
            _alarmGrid.UpdateHeaders();
            if (_lastError != null) RefreshErrorText();
            UpdateRunStatus();
        }

        /// <summary>显示失败操作、文件路径、原因和维护人员下一步建议。</summary>
        private void RefreshErrorText()
        {
            string reason = _lastError.Message;
            var missingFile = _lastError as FileNotFoundException;
            if (missingFile != null && !string.IsNullOrWhiteSpace(missingFile.FileName))
                reason = string.Format(GetText("任务配置文件不存在"), missingFile.FileName);
            _status.Text = GetText(_failureOperationKey) + "\n" + reason + "\n"
                + string.Format(GetText("任务配置设备目录"), _store.GetMachineDirectory(_machineId))
                + "\n" + GetText(_failureAdviceKey);
        }

        /// <summary>成功后清除过期的错误详情。</summary>
        private void ClearError()
        {
            _lastError = null;
            _failureOperationKey = null;
            _failureAdviceKey = null;
            _diagnosticText.Text = string.Empty;
            _diagnostics.IsExpanded = false;
            _diagnostics.Visibility = Visibility.Collapsed;
            _nextStepButtons.Children.Clear();
            _nextStepButtons.Visibility = Visibility.Collapsed;
        }

        /// <summary>按当前 Host 语言读取界面文字。</summary>
        private string GetText(string key)
        {
            return _host.TryFindResource(key) as string ?? key;
        }

        /// <summary>创建明确颜色的工具栏和数据表画刷。</summary>
        private static System.Windows.Media.Brush Brush(string color)
        {
            return (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(color);
        }

    }
}
