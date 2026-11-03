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
using MotionControl.Framework.Contracts.Preparation;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.Framework.Runtime.Machine;
using MotionControl.Framework.Runtime.Variables;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Machine;
using MotionControl.TaskEngine.Resources;
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
        /// <summary>依据设备状态显示下一步操作。</summary>
        private readonly TextBlock _nextAction;
        /// <summary>未读取任务时的说明。</summary>
        private readonly TextBlock _emptyState;
        /// <summary>报警数据的读取状态。</summary>
        private readonly TextBlock _alarmState;
        /// <summary>供维护人员查看的资源目录。</summary>
        private readonly TextBlock _catalogDetails;
        /// <summary>上次读取的目录，用于切换语言时更新统计文字。</summary>
        private TaskResourceCatalog _catalog;
        /// <summary>供维护人员核对的任务来源。</summary>
        private readonly TextBlock _loadedSource;
        /// <summary>已读取任务的原始来源。</summary>
        private string _taskSource;
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
        /// <summary>最近成功操作的文字键，用于切换语言时重新显示。</summary>
        private string _successTextKey;
        /// <summary>成功提示对应的任务状态；任务关闭后的停止提示使用空值。</summary>
        private TaskMachineState? _successTaskState;
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
        /// <summary>仅对已终止会话提供待确认资源的现场核查入口。</summary>
        private readonly Button _recoverResourcesButton;
        /// <summary>查看当前设备已写入的产品记录，不影响运行。</summary>
        private readonly Button _productRecordsButton;
        /// <summary>避免重复打开多个产品记录窗口。</summary>
        private ProductRecordWindow _productRecordsWindow;
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
        /// <summary>连接上下文每次更换或失效时递增，阻止异步读取的旧会话挂入新连接。</summary>
        private long _machineContextVersion;
        /// <summary>与 Host 其他页面共用的设备变量存储。</summary>
        private DeviceVariableStore _variables;
        /// <summary>当前任务运行会话；创建时取得设备控制权。</summary>
        private TaskMonitorSession _session;
        /// <summary>归还任务控制权后恢复设备原准备定义，防止跳过 C# 准备条件。</summary>
        private IReadOnlyList<IStationPreparation> _equipmentPreparations = new IStationPreparation[0];
        /// <summary>当前正式设备任务监控视图。</summary>
        private TaskMonitorView _monitor;
        /// <summary>定时刷新任务状态和报警，并把任务故障反馈到 Runtime。</summary>
        private readonly DispatcherTimer _stateTimer;
        /// <summary>防止任务故障同步 Runtime 状态的后台操作重复启动。</summary>
        private bool _faultStatusSyncing;
        /// <summary>防止多个任务按钮同时操作设备。</summary>
        private bool _commandInProgress;
        /// <summary>Host 恢复记录的启动保护检查，仅查询，不改变记录。</summary>
        private readonly Func<string> _recoveryStartBlockReason;
        /// <summary>连接异常丢失后须重新建立设备上下文，不能把旧任务监控当作实时状态。</summary>
        private bool _controllerConnectionLost;
        /// <summary>当前是否已装载一个整机任务会话。</summary>
        internal bool HasTaskSession { get { return _session != null; } }

        /// <summary>返回当前会话身份，供恢复保护区分是否为同一次任务接管。</summary>
        internal object CurrentTaskSession { get { return _session; } }

        /// <summary>核对同一任务会话已完成暂停和故障处理；只读，不发设备命令。</summary>
        internal bool IsTaskSessionSettled(object session)
        {
            TaskMonitorSession current = _session;
            return current != null && ReferenceEquals(current, session)
                && current.Machine.IsPauseSettlementConfirmed;
        }

        /// <summary>任务命令正在读写或等待停稳，顶部按钮应避免并发提交。</summary>
        internal bool IsCommandInProgress { get { return _commandInProgress; } }

        /// <summary>任务入口是否已读取并处于可启动状态；实际启动仍由 Runtime 和任务执行器再次检查。</summary>
        internal bool CanStartFromHost
        {
            get { return IsControllerConnected() && _session != null && !_commandInProgress
                && _runtime != null && _runtime.State == MachineRunState.Ready && _runtime.IsPrepared
                && string.IsNullOrWhiteSpace(_runtime.RecoveryBlockReason)
                && string.IsNullOrWhiteSpace(_recoveryStartBlockReason?.Invoke())
                && _session.Machine.GetSnapshot().State == TaskMachineState.Ready; }
        }

        /// <summary>任务生产运行中才允许从顶部发起保留工步的受控暂停。</summary>
        internal bool CanPauseFromHost
        {
            get { return _session != null && !_commandInProgress
                && _runtime != null && _runtime.State == MachineRunState.Running
                && _session.Machine.GetSnapshot().State == TaskMachineState.Running; }
        }

        /// <summary>设备已就绪但任务仍等待复位时，顶部复位也应可用。</summary>
        internal bool NeedsResetFromHost
        {
            get
            {
                if (_session == null || _commandInProgress || _runtime == null || _runtime.IsPreparing) return false;
                var state = _session.Machine.GetSnapshot().State;
                return state == TaskMachineState.NeedsReset || state == TaskMachineState.Paused || state == TaskMachineState.Faulted;
            }
        }

        /// <summary>说明任务入口尚不能从顶部启动的原因；仅查询状态，不发设备命令。</summary>
        internal string GetHostStartBlockReason()
        {
            if (!string.IsNullOrWhiteSpace(_runtime?.RecoveryBlockReason))
                return "设备恢复信息未确认，不能准备或启动：" + _runtime.RecoveryBlockReason;
            string recoveryReason = _recoveryStartBlockReason?.Invoke();
            if (!string.IsNullOrWhiteSpace(recoveryReason)) return recoveryReason;
            if (_session == null) return "请先进入任务运行页读取当前任务。";
            if (_commandInProgress) return "任务命令正在执行，请稍候。";
            if (_runtime != null && !_runtime.IsPrepared) return "请先在设备准备页完成工位回零。";
            var state = _session.Machine.GetSnapshot().State;
            if (state == TaskMachineState.NeedsReset || state == TaskMachineState.Paused || state == TaskMachineState.Faulted)
                return "请先处理现场状态并按报警复位。";
            return state == TaskMachineState.Ready ? null : "任务当前不能启动，请查看任务运行状态。";
        }

        /// <summary>创建任务运行入口；不在此处连接控制卡或启动任务。</summary>
        internal TaskMonitorLauncher(MachineConfigurationStore store, string machineId, MainWindow host, Func<string> recoveryStartBlockReason = null)
        {
            _store = store; _machineId = machineId; _host = host; _recoveryStartBlockReason = recoveryStartBlockReason;
            var root = new Grid { Margin = new Thickness(14, 8, 14, 12), Background = System.Windows.Media.Brushes.Transparent };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 220 });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };

            var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            StyleToolbar(toolbar);
            _loadButton = AddCommandButton(toolbar, "任务执行读取任务", LoadTask_Click);
            _resetButton = AddCommandButton(toolbar, "任务执行复位", ResetTask_Click);
            _startButton = AddCommandButton(toolbar, "任务执行启动", StartTask_Click);
            _pauseButton = AddCommandButton(toolbar, "任务执行暂停", PauseTask_Click);
            _stopButton = AddCommandButton(toolbar, "任务执行停止", StopTask_Click);
            _recoverResourcesButton = new Button { Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 8, 0),
                Background = Brush("#FFFFFF"), BorderBrush = Brush("#C9D5E3"), BorderThickness = new Thickness(1), Visibility = Visibility.Collapsed };
            _recoverResourcesButton.Click += RecoverResources_Click; toolbar.Children.Add(_recoverResourcesButton);
            _productRecordsButton = new Button { Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 8, 0),
                Background = Brush("#FFFFFF"), BorderBrush = Brush("#C9D5E3"), BorderThickness = new Thickness(1) };
            _productRecordsButton.Click += ProductRecords_Click; toolbar.Children.Add(_productRecordsButton);
            AddButton(toolbar, "设备总览", EquipmentOverview_Click);
            AddButton(toolbar, "设备准备", EquipmentPreparation_Click);
            AddButton(toolbar, "流程状态", FlowStatus_Click);
            header.Children.Add(toolbar);
            root.Children.Add(header);

            var summary = new StackPanel();
            _runStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4), Foreground = Brush("#176BD0"), FontSize = 15, FontWeight = FontWeights.SemiBold };
            summary.Children.Add(_runStatus);
            _nextAction = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brush("#40536A"), Margin = new Thickness(0, 0, 0, 4) };
            summary.Children.Add(_nextAction);
            Grid.SetRow(summary, 1); root.Children.Add(summary);
            _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 5) };
            Grid.SetRow(_status, 2); root.Children.Add(_status);

            var monitorArea = new Grid();
            _monitorContent = new ContentControl();
            monitorArea.Children.Add(_monitorContent);
            _emptyState = new TextBlock { Margin = new Thickness(16), TextWrapping = TextWrapping.Wrap, Foreground = Brush("#596B7D"), FontSize = 13 };
            monitorArea.Children.Add(_emptyState);
            _nextStepButtons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 0, 4), Visibility = Visibility.Collapsed };
            summary.Children.Add(_nextStepButtons);
            _diagnosticText = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                MaxHeight = 120, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Visibility = Visibility.Collapsed };
            var details = new StackPanel();
            var refresh = new WrapPanel();
            AddButton(refresh, "刷新关联配置目录", RefreshCatalog_Click);
            details.Children.Add(refresh);
            _catalogDetails = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
            details.Children.Add(_catalogDetails);
            _loadedSource = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
            details.Children.Add(_loadedSource);
            details.Children.Add(_diagnosticText);
            _diagnostics = new Expander { Margin = new Thickness(0, 6, 0, 0), Content = details };
            Grid.SetRow(_diagnostics, 5); root.Children.Add(_diagnostics);
            Grid.SetRow(monitorArea, 3); root.Children.Add(monitorArea);

            var alarmPanel = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            var alarmTitle = new TextBlock { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 8, 4) };
            alarmTitle.SetResourceReference(TextBlock.TextProperty, "任务执行当前报警");
            DockPanel.SetDock(alarmTitle, Dock.Top); alarmPanel.Children.Add(alarmTitle);
            _alarmState = new TextBlock { Margin = new Thickness(0, 0, 0, 4), Foreground = Brush("#596B7D"), TextWrapping = TextWrapping.Wrap };
            DockPanel.SetDock(_alarmState, Dock.Top); alarmPanel.Children.Add(_alarmState);
            _alarmGrid = new TaskAlarmGrid(GetText); alarmPanel.Children.Add(_alarmGrid);
            Grid.SetRow(alarmPanel, 4); root.Children.Add(alarmPanel);
            Content = root;

            _stateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _stateTimer.Tick += StateTimer_Tick;
            _host.LanguageChanged += Host_LanguageChanged;
            Loaded += Launcher_Loaded;
            UpdateRunStatus();
            UpdateCommandAvailability();
            RefreshPageText();
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
            ILogicalIo logicalIo, DeviceVariableStore variables, IReadOnlyList<IStationPreparation> preparations = null)
        {
            _machineContextVersion++;
            if (_session != null) await CloseTaskSessionAsync().ConfigureAwait(true);
            _runtime = runtime; _motionCards = motionCards; _logicalIo = logicalIo; _variables = variables;
            _equipmentPreparations = preparations ?? new IStationPreparation[0];
            _controllerConnectionLost = runtime == null;
            if (runtime == null)
            {
                _stateTimer.Stop();
                _alarmGrid.ClearAlarms();
            }
            _successTextKey = null;
            _status.Text = string.Empty;
            ClearError();
            UpdateRunStatus();
            UpdateCommandAvailability();
            RefreshPageText();
        }

        /// <summary>页面进入可见区域后刷新已保存的任务资源目录。</summary>
        private void Launcher_Loaded(object sender, RoutedEventArgs e)
        {
            RefreshCatalog();
            RefreshConnectionDisplay();
        }
        /// <summary>控制器异常断开后立即隐藏旧任务画面；新上下文建立后才重新确认状态。</summary>
        internal void MarkControllerConnectionLost()
        {
            _machineContextVersion++;
            _controllerConnectionLost = true;
            RefreshConnectionDisplay();
        }
        /// <summary>依据实际连接与会话状态刷新显示，不修改任务或设备。</summary>
        private void RefreshConnectionDisplay()
        {
            bool connected = IsControllerConnected();
            _monitorContent.Visibility = connected && _session != null ? Visibility.Visible : Visibility.Collapsed;
            _alarmGrid.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
            _emptyState.Visibility = !connected || _session == null ? Visibility.Visible : Visibility.Collapsed;
            _emptyState.Text = !connected
                ? PageText("控制器未连接，任务和设备状态未确认。请重新连接后检查。", "Controller disconnected. Task and equipment states are unconfirmed; reconnect and check.")
                : PageText("任务尚未读取。读取后可在此查看工位步骤与运行状态。", "Tasks are not loaded. Load tasks to view station steps and run status.");
            UpdateRunStatus();
            UpdateCommandAvailability();
            RefreshAlarmState();
        }
        /// <summary>异常断线锁存到新设备上下文建立，避免心跳恢复就误认旧会话有效。</summary>
        private bool IsControllerConnected()
        { return !_controllerConnectionLost && _runtime != null && _motionCards != null && _motionCards.IsConnected; }
        /// <summary>重新读取其他页面已保存的配置。</summary>
        private void RefreshCatalog_Click(object sender, RoutedEventArgs e) { RefreshCatalog(); }
        /// <summary>打开实际展示设备运行状态的总览页。</summary>
        private void EquipmentOverview_Click(object sender, RoutedEventArgs e) { _host.ShowEquipmentOverview(); }
        /// <summary>进入同一 Runtime 的设备准备页面，不重建任务会话。</summary>
        private void EquipmentPreparation_Click(object sender, RoutedEventArgs e) { _host.ShowEquipmentPreparation(); }
        /// <summary>打开实际展示工位步骤的流程状态页。</summary>
        private void FlowStatus_Click(object sender, RoutedEventArgs e) { _host.ShowFlowStatus(); }

        /// <summary>非模态查看当前设备产品记录；窗口不接管运行或阻止主窗口暂停。</summary>
        private void ProductRecords_Click(object sender, RoutedEventArgs e)
        {
            if (_productRecordsWindow != null) { _productRecordsWindow.Activate(); return; }
            try
            {
                var window = new ProductRecordWindow(_store.GetMachineDirectory(_machineId), _machineId,
                    string.Equals(_host.CurrentLanguage, "en-US", StringComparison.OrdinalIgnoreCase));
                window.Owner = Window.GetWindow(this);
                window.Closed += (closedSender, closedArgs) => { if (ReferenceEquals(_productRecordsWindow, window)) _productRecordsWindow = null; };
                _productRecordsWindow = window; window.Show();
            }
            catch (Exception error)
            {
                _productRecordsWindow = null;
                MessageBox.Show(PageText("无法打开产品记录：", "Unable to open product records: ") + error.Message,
                    PageText("产品记录", "Product records"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>只对已终止的当前会话核查单个资源；不会自动复位、启动或关闭会话。</summary>
        private void RecoverResources_Click(object sender, RoutedEventArgs e)
        {
            if (_commandInProgress || _session == null || _runtime == null || !IsControllerConnected()) return;
            var session = _session;
            var runtime = _runtime;
            if (session.Machine.GetSnapshot().State != TaskMachineState.Stopped || runtime.IsPreparing) return;
            _commandInProgress = true; UpdateCommandAvailability();
            try
            {
                var dialog = new ResourceRecoveryDialog(session.TaskDevice,
                    () => ReferenceEquals(_session, session) && ReferenceEquals(_runtime, runtime)
                        && IsControllerConnected() && !runtime.IsPreparing
                        && runtime.State != MachineRunState.Running && runtime.State != MachineRunState.Pausing
                        && session.Machine.GetSnapshot().State == TaskMachineState.Stopped,
                    string.Equals(_host.CurrentLanguage, "en-US", StringComparison.OrdinalIgnoreCase));
                dialog.Owner = Window.GetWindow(this);
                dialog.ShowDialog();
            }
            catch (Exception error) { ShowError(error, "任务停止失败", "任务运行复位建议"); }
            finally { _commandInProgress = false; UpdateRunStatus(); UpdateCommandAvailability(); }
        }

        /// <summary>在后台创建真实任务设备并载入已应用的任务定义，未装载时不接管设备。</summary>
        private async void LoadTask_Click(object sender, RoutedEventArgs e)
        {
            if (_runtime == null || _motionCards == null || _logicalIo == null || _variables == null)
            {
                ShowError(new InvalidOperationException("请先连接运动控制卡，再读取当前任务。"), "任务设备未连接", "任务配置检查建议");
                return;
            }

            await RunCommandAsync(LoadTaskSessionAsync, "任务装载失败", "任务配置检查建议").ConfigureAwait(true);
        }

        /// <summary>设备准备页复用当前正式任务会话，避免额外执行器争用控制卡。</summary>
        internal async Task EnsureTaskSessionAsync()
        {
            if (_session != null) return;
            if (_commandInProgress) throw new InvalidOperationException("任务命令正在执行，请稍后再进入设备准备。");
            _commandInProgress = true;
            try { await LoadTaskSessionAsync(); }
            finally { _commandInProgress = false; UpdateCommandAvailability(); }
        }

        /// <summary>读取运行配置并建立同一设备会话；仅装配，不发送轴或 IO 命令。</summary>
        private async Task LoadTaskSessionAsync()
        {
            if (!IsControllerConnected() || _logicalIo == null || _variables == null)
                throw new InvalidOperationException("请先连接控制器，再读取设备准备配置。");
            if (_session != null) return;
            {
                var runtime = _runtime;
                var motionCards = _motionCards;
                var logicalIo = _logicalIo;
                var variables = _variables;
                long contextVersion = _machineContextVersion;
                TaskMonitorSession loaded = await Task.Run(() => HostTaskSessionLoader.Load(
                    _store, _machineId, motionCards, logicalIo, variables, runtime)).ConfigureAwait(true);

                if (contextVersion != _machineContextVersion || !ReferenceEquals(runtime, _runtime)
                    || !ReferenceEquals(motionCards, _motionCards) || !ReferenceEquals(logicalIo, _logicalIo)
                    || !ReferenceEquals(variables, _variables) || !IsControllerConnected())
                {
                    // 旧装载只归还自己取得的控制权，不走会恢复当前 Runtime 准备定义的公共关闭入口。
                    await loaded.Machine.CloseAsync().ConfigureAwait(true);
                    throw new InvalidOperationException("设备连接在任务读取期间发生变化，本次读取已取消；请在当前连接中重新读取任务。");
                }

                _session = loaded;
                _monitor = new TaskMonitorView(loaded);
                _monitor.HideSourcePath();
                _taskSource = loaded.Source;
                _monitor.ReferenceRequested += Monitor_ReferenceRequested;
                _monitor.SetLanguage(_host.CurrentLanguage);
                _monitorContent.Content = _monitor;
                _stateTimer.Start();
                ClearError();
                SetSuccessText("已读取当前任务");
                _emptyState.Visibility = Visibility.Collapsed;
                RefreshAlarms();
                RefreshPageText();
                UpdateRunStatus();
                UpdateCommandAvailability();
                _host.RefreshMachineStateDisplay();
            }
        }

        /// <summary>顶部报警复位复用任务侧条件核对，不绕过任务故障锁存。</summary>
        internal async Task ResetFromHostAsync()
        {
            if (_commandInProgress) throw new InvalidOperationException("任务命令正在执行，请等待完成。");
            _commandInProgress = true;
            try
            {
                RequireTaskSession();
                await _session.Machine.ValidateResetPrerequisitesAsync();
                if (_runtime.State != MachineRunState.Ready) await _runtime.ResetAsync(CancellationToken.None);
                await _session.Machine.ResetAsync();
            }
            finally { _commandInProgress = false; UpdateCommandAvailability(); }
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
                SetSuccessText("任务执行复位完成");
            }, "任务复位失败", "任务运行复位建议").ConfigureAwait(true);
        }

        /// <summary>先让 Runtime 建立安全运行状态和急停监控，再启动任务工位。</summary>
        private async void StartTask_Click(object sender, RoutedEventArgs e)
        {
            await RunCommandAsync(StartTaskCoreAsync, "任务启动失败", "任务运行复位建议").ConfigureAwait(true);
        }

        /// <summary>任务页和顶部启动复用同一流程，先建立运行安全监控，再启动实际任务。</summary>
        private async Task StartTaskCoreAsync()
        {
            RequireTaskSession();
            await _runtime.BeginTaskEngineRunAsync(CancellationToken.None).ConfigureAwait(true);
            try
            {
                _session.Machine.Start();
                _runtime.ConfirmTaskEngineProductionStarted();
            }
            catch (Exception startError)
            {
                await _runtime.FinishTaskEngineRunAsync(true, startError).ConfigureAwait(true);
                throw;
            }
            SetSuccessText("任务执行已启动");
        }

        /// <summary>请求所有任务停步并等待真实轴受控停稳，再更新整机状态。</summary>
        private async void PauseTask_Click(object sender, RoutedEventArgs e)
        {
            await RunCommandAsync(PauseTaskCoreAsync, "任务暂停失败", "任务运行复位建议").ConfigureAwait(true);
        }

        /// <summary>受控停轴并保留任务工步，供任务页及顶部暂停、停止共用。</summary>
        private async Task PauseTaskCoreAsync()
        {
            RequireTaskSession();
            await _session.Machine.PauseAsync().ConfigureAwait(true);
            TaskMachineSnapshot snapshot = _session.Machine.GetSnapshot();
            if (snapshot.State == TaskMachineState.Faulted)
            {
                string message = snapshot.Faults.LastOrDefault()?.Message ?? "暂停期间任务发生故障。";
                await _runtime.FinishTaskEngineRunAsync(true, new InvalidOperationException(message)).ConfigureAwait(true);
                throw new InvalidOperationException(message);
            }
            await _runtime.FinishTaskEngineRunAsync(false, null).ConfigureAwait(true);
            SetSuccessText("任务执行已暂停");
        }

        /// <summary>从顶部执行任务启动或暂停；任务页的并发保护和诊断状态共用。</summary>
        internal async Task RunFromHostAsync(bool start)
        {
            if (_commandInProgress) throw new InvalidOperationException("任务命令正在执行，请等待完成。");
            _commandInProgress = true;
            UpdateCommandAvailability();
            try
            {
                if (start) await StartTaskCoreAsync().ConfigureAwait(true);
                else await PauseTaskCoreAsync().ConfigureAwait(true);
                ClearError();
            }
            finally
            {
                _commandInProgress = false;
                UpdateRunStatus();
                UpdateCommandAvailability();
                _host.RefreshMachineStateDisplay();
            }
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
                SetSuccessText("任务执行已停止");
            }, "任务停止失败", "任务运行复位建议").ConfigureAwait(true);
        }

        /// <summary>检查会话未运行后关闭任务执行器；断卡和应用退出均复用此安全收尾。</summary>
        internal async Task CloseTaskSessionAsync()
        {
            TaskMonitorSession session = _session;
            if (session == null) return;
            TaskMachineState state = session.Machine.GetSnapshot().State;
            if (state == TaskMachineState.Running || state == TaskMachineState.Pausing
                || _runtime?.State == MachineRunState.Running || _runtime?.State == MachineRunState.Pausing || _runtime?.IsPreparing == true)
                throw new InvalidOperationException("任务仍在运行或等待停轴；先在任务运行页暂停或停止并确认安全状态。");

            _stateTimer.Stop();
            await session.Machine.CloseAsync().ConfigureAwait(true);
            _runtime?.ConfigurePreparations(_equipmentPreparations);
            _session = null;
            _monitor = null;
            _monitorContent.Content = null;
            _taskSource = null;
            _emptyState.Visibility = Visibility.Visible;
            _alarmGrid.ClearAlarms();
            UpdateRunStatus();
            UpdateCommandAvailability();
            RefreshPageText();
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
                if (_lastError != null) _status.Text = string.Empty;
                ClearError();
                _catalog = catalog;
                RefreshCatalogDetails();
            }
            catch (Exception error)
            {
                _catalog = null;
                _catalogDetails.Text = string.Empty;
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
                && (_runtime?.State == MachineRunState.Running || _runtime?.State == MachineRunState.Pausing
                    || _runtime?.State == MachineRunState.Paused))
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
            RefreshAlarmState();
        }

        /// <summary>更新任务整机和设备运行状态摘要。</summary>
        private void UpdateRunStatus(TaskMachineSnapshot snapshot = null)
        {
            if (_session != null && snapshot == null) snapshot = _session.Machine.GetSnapshot();
            UpdateSuccessFeedback(snapshot?.State, IsControllerConnected());
            if (!IsControllerConnected())
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

            if (snapshot.State == TaskMachineState.Ready
                && !string.IsNullOrWhiteSpace(_recoveryStartBlockReason?.Invoke()))
            {
                _runStatus.Text = PageText("任务暂不能启动，请查看下方处理提示。", "Task start is blocked; see the guidance below.");
                return;
            }

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
            bool connected = IsControllerConnected()
                && _logicalIo != null && _variables != null;
            var snapshot = _session?.Machine.GetSnapshot();
            TaskMachineState? taskState = snapshot?.State;
            bool hasBlockedResources = snapshot != null && snapshot.Resources.Any(item => item.State == TaskResourceState.Blocked);
            _loadButton.IsEnabled = connected && _session == null && !_commandInProgress && !_runtime.IsPreparing;
            _resetButton.IsEnabled = connected && taskState.HasValue && !_commandInProgress
                && taskState != TaskMachineState.Running && taskState != TaskMachineState.Pausing
                && _runtime.State != MachineRunState.Running && _runtime.State != MachineRunState.Pausing && !_runtime.IsPreparing;
            _startButton.IsEnabled = connected && taskState == TaskMachineState.Ready && !_commandInProgress
                && _runtime.State == MachineRunState.Ready && _runtime.IsPrepared
                && string.IsNullOrWhiteSpace(_runtime.RecoveryBlockReason)
                && string.IsNullOrWhiteSpace(_recoveryStartBlockReason?.Invoke());
            _pauseButton.IsEnabled = taskState == TaskMachineState.Running && !_commandInProgress;
            _stopButton.IsEnabled = _session != null && !_commandInProgress && !_runtime.IsPreparing;
            _recoverResourcesButton.Visibility = hasBlockedResources ? Visibility.Visible : Visibility.Collapsed;
            _recoverResourcesButton.IsEnabled = hasBlockedResources && connected && taskState == TaskMachineState.Stopped
                && !_commandInProgress && !_runtime.IsPreparing;
            UpdateNextAction(connected, taskState);
            _host.RefreshMachineStateDisplay();
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
            _successTextKey = null;
            _failureOperationKey = operationKey;
            _failureAdviceKey = adviceKey;
            _status.Foreground = Brush("#B23B3B");
            _diagnosticText.Text = error.ToString();
            _diagnostics.IsExpanded = false;
            _diagnosticText.Visibility = Visibility.Visible;
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
            UpdateCommandAvailability();
            RefreshPageText();
        }

        /// <summary>显示失败操作、原因和建议，完整诊断保留在展开区。</summary>
        private void RefreshErrorText()
        {
            string reason = _lastError.Message;
            var missingFile = _lastError as FileNotFoundException;
            if (missingFile != null && !string.IsNullOrWhiteSpace(missingFile.FileName))
                reason = string.Format(GetText("任务配置文件不存在"), Path.GetFileName(missingFile.FileName));
            _status.Text = GetText(_failureOperationKey) + "\n" + reason + "\n" + GetText(_failureAdviceKey);
        }

        /// <summary>成功后清除过期的错误详情。</summary>
        private void ClearError()
        {
            _lastError = null;
            _failureOperationKey = null;
            _failureAdviceKey = null;
            _status.Foreground = Brush("#26384D");
            _diagnosticText.Text = string.Empty;
            _diagnosticText.Visibility = Visibility.Collapsed;
            _nextStepButtons.Children.Clear();
            _nextStepButtons.Visibility = Visibility.Collapsed;
        }

        /// <summary>根据连接和任务状态说明下一步，不改变原有按钮控制条件。</summary>
        private void UpdateNextAction(bool connected, TaskMachineState? state)
        {
            string recoveryReason = connected && !_commandInProgress && _runtime.State != MachineRunState.Running
                && _runtime.State != MachineRunState.Pausing ? _recoveryStartBlockReason?.Invoke() : null;
            if (_commandInProgress)
                _nextAction.Text = PageText("正在处理，请稍候。", "Processing, please wait.");
            else if (!connected)
                _nextAction.Text = string.Empty;
            else if (!string.IsNullOrWhiteSpace(_runtime.RecoveryBlockReason))
                _nextAction.Text = PageText("设备恢复信息未确认，不能准备或启动：", "Recovery information is not verified; preparation and start are blocked: ")
                    + _runtime.RecoveryBlockReason;
            else if (state == TaskMachineState.Paused && _runtime.State == MachineRunState.Paused
                && IsSettledPauseAwaitingReset(state, _runtime.State,
                _session != null && _session.Machine.IsPauseSettlementConfirmed,
                _runtime.AlarmService.HasLatchedStopAlarm, recoveryReason))
                _nextAction.Text = PageText("暂停后输出可能保持原状态；请检查机构和输出，再复位、启动。", "Outputs may remain active while paused. Check the equipment and outputs before reset and start.");
            else if (_runtime.State != MachineRunState.Running && _runtime.State != MachineRunState.Pausing
                && !string.IsNullOrWhiteSpace(recoveryReason))
                _nextAction.Text = recoveryReason;
            else if (_runtime.IsPreparing)
                _nextAction.Text = PageText("设备回零正在执行；可在设备准备页查看进度或停止。", "Preparation is running. View progress or stop on the preparation page.");
            else if (!_runtime.IsPrepared)
                _nextAction.Text = PageText("先在设备准备页完成工位回零，再复位并启动生产。", "Prepare the stations before resetting and starting production.");
            else if (!state.HasValue)
                _nextAction.Text = _runtime.State == MachineRunState.Running || _runtime.State == MachineRunState.Pausing
                    ? PageText("设备正在运行或停轴，请先确认停稳，再读取任务。", "Wait for the equipment to stop before loading tasks.")
                    : PageText("先读取当前任务，再检查现场、复位并启动。", "Load tasks, check the equipment, then reset and start.");
            else if (state == TaskMachineState.NeedsReset)
                _nextAction.Text = PageText("请检查现场及输出状态，然后复位。", "Check the equipment and outputs, then reset.");
            else if (state == TaskMachineState.Ready)
                _nextAction.Text = _runtime.State == MachineRunState.Ready
                    ? PageText("检查现场后按启动。", "Check the equipment, then start.")
                    : PageText("设备尚未就绪，请检查报警和复位条件。", "Equipment is not ready; check alarms and reset conditions.");
            else if (state == TaskMachineState.Running)
                _nextAction.Text = PageText("需要人工介入时请先暂停，并确认机构停稳。", "Before intervention, pause and confirm motion has stopped.");
            else if (state == TaskMachineState.Pausing)
                _nextAction.Text = PageText("正在等待停轴，请等待停稳。", "Waiting for motion to stop.");
            else if (state == TaskMachineState.Paused)
                _nextAction.Text = PageText("暂停后输出可能保持原状态；请检查机构和输出，再复位、启动。", "Outputs may remain active while paused. Check the equipment and outputs before reset and start.");
            else if (state == TaskMachineState.Faulted)
                _nextAction.Text = PageText("请处理报警并检查机构和输出，再复位、启动。", "Resolve alarms and check the equipment and outputs before reset and start.");
            else
                _nextAction.Text = PageText("请检查任务状态，必要时停止并重新读取任务。", "Check task status; stop and reload tasks if needed.");
        }

        /// <summary>只对已确认收尾的正常暂停隐藏等待复位的标记提示；其他保护原因继续显示。</summary>
        private static bool IsSettledPauseAwaitingReset(TaskMachineState? state, MachineRunState runtimeState,
            bool settlementConfirmed, bool hasStopAlarm, string recoveryReason)
        {
            return state == TaskMachineState.Paused && runtimeState == MachineRunState.Paused
                && settlementConfirmed && !hasStopAlarm && recoveryReason != null
                && recoveryReason.StartsWith("上次运行尚未确认安全收尾，", StringComparison.Ordinal);
        }

        /// <summary>区分未读取、通讯中断和已读取但没有报警。</summary>
        private void RefreshAlarmState()
        {
            if (_runtime == null)
                _alarmState.Text = PageText("未连接设备，报警状态未确认。", "Equipment disconnected; alarm status is unknown.");
            else if (!IsControllerConnected())
                _alarmState.Text = PageText("控制器未连接，报警状态未确认。", "Controller disconnected; alarm status is unknown.");
            else if (_session == null)
                _alarmState.Text = PageText("任务尚未读取，请在“当前报警”页面查看设备报警。", "Tasks are not loaded; check the Current Alarms page.");
            else
                _alarmState.Text = _runtime.AlarmService.Alarms.Count == 0
                    ? PageText("当前无报警。", "No active alarms.") : string.Empty;
        }

        /// <summary>切换语言或会话时刷新页面专用说明。</summary>
        private void RefreshPageText()
        {
            _diagnostics.Header = PageText("工程诊断与配置目录", "Diagnostics and configuration");
            _recoverResourcesButton.Content = PageText("核查待确认资源", "Inspect blocked resources");
            _productRecordsButton.Content = PageText("产品记录", "Product records");
            _loadedSource.Text = string.IsNullOrWhiteSpace(_taskSource) ? string.Empty
                : PageText("已读取任务来源：", "Loaded task source: ") + _taskSource;
            if (_lastError == null && _successTextKey != null) _status.Text = SuccessText(_successTextKey);
            RefreshCatalogDetails();
            RefreshConnectionDisplay();
        }

        /// <summary>使用已读取的目录更新统计与来源路径，不再次读取设备配置。</summary>
        private void RefreshCatalogDetails()
        {
            if (_catalog == null) return;
            _catalogDetails.Text = string.Format(GetText("任务关联目录统计"), _catalog.Axes.Count, _catalog.Inputs.Count,
                _catalog.Outputs.Count, _catalog.Variables.Count, _catalog.TeachPoints.Count)
                + "\n" + _store.GetMachineDirectory(_machineId) + "\n" + string.Join("\n", _catalog.Notices);
        }

        /// <summary>记录成功操作并根据当前语言显示反馈。</summary>
        private void SetSuccessText(string key)
        {
            _successTextKey = key;
            switch (key)
            {
                case "任务执行复位完成": _successTaskState = TaskMachineState.Ready; break;
                case "任务执行已启动": _successTaskState = TaskMachineState.Running; break;
                case "任务执行已暂停": _successTaskState = TaskMachineState.Paused; break;
                case "任务执行已停止": _successTaskState = null; break;
                default: _successTaskState = _session?.Machine.GetSnapshot().State; break;
            }
            _status.Text = SuccessText(key);
        }

        /// <summary>返回成功操作在当前语言下的说明。</summary>
        private string SuccessText(string key)
        {
            return key == "已读取当前任务"
                ? PageText("已读取当前任务。", "Current tasks loaded.") : GetText(key);
        }

        /// <summary>状态变化或连接中断后移除过期成功提示，错误提示不受影响。</summary>
        private void UpdateSuccessFeedback(TaskMachineState? state, bool connected)
        {
            if (_successTextKey == null || (connected && state == _successTaskState)) return;
            _successTextKey = null;
            _status.Text = string.Empty;
        }

        /// <summary>选择当前页面语言对应的说明。</summary>
        private string PageText(string chinese, string english)
        {
            return string.Equals(_host.CurrentLanguage, "en-US", StringComparison.OrdinalIgnoreCase) ? english : chinese;
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
