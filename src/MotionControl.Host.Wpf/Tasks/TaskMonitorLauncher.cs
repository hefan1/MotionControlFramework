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
    internal sealed partial class TaskMonitorLauncher : UserControl
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
        /// <summary>当前目录是否来自已装载任务会话的冻结快照。</summary>
        private bool _catalogFromLoadedSession;
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
        /// <summary>保存任务关联目录读取失败，设备上下文更新时不能误清除该配置问题。</summary>
        private Exception _catalogReadError;
        /// <summary>最近成功操作的文字键，用于切换语言时重新显示。</summary>
        private string _successTextKey;
        /// <summary>成功提示对应的任务状态；任务关闭后的停止提示使用空值。</summary>
        private TaskMachineState? _successTaskState;
        /// <summary>当前诊断对应的配置页快捷入口。</summary>
        private readonly WrapPanel _nextStepButtons;
        /// <summary>尚未读取会话时提供明确的设备流程装载入口；装载本身不启动设备。</summary>
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
        /// <summary>仅导航至统一当前报警页，不改变设备状态。</summary>
        private readonly Button _currentAlarmsButton;
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
        /// <summary>已发布任务实际安装的准备配置代次；变化后不能再当作该会话的准备范围。</summary>
        private long _publishedPreparationVersion = -1;
        /// <summary>任务关闭期间不再将其作为可提交准备的会话。</summary>
        private bool _closingTaskSession;
        /// <summary>同一会话的 Host 收尾任务，供并发关闭入口共同等待。</summary>
        private Task _sessionCloseTask;
        private bool _sessionClosePreservesBlocked;
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

        /// <summary>确认页面查看的是当前 Runtime 已发布的任务设备，而非装载中的定义。</summary>
        internal bool HasPublishedPreparationSession(MachineRuntime runtime, Guid? session)
        {
            var device = _session?.TaskDevice as MotionControl.TaskEngine.MachineDevice.MachineTaskDevice;
            return !_closingTaskSession && IsControllerConnected() && runtime != null
                && ReferenceEquals(runtime, _runtime) && runtime.IsConnectionSessionCurrent(session)
                && device?.BoundConnectionSessionId == session
                && runtime.PreparationConfigurationVersion == _publishedPreparationVersion;
        }

        /// <summary>返回当前会话身份，供恢复保护区分是否为同一次任务接管。</summary>
        internal object CurrentTaskSession { get { return _session; } }

        /// <summary>仅返回仍绑定当前连接与准备配置的已发布任务会话，供只读页面展示。</summary>
        internal TaskMonitorSession CurrentPublishedTaskSession
        {
            get
            {
                var session = _session;
                var runtime = _runtime;
                return session != null && session.PublishedMetadata != null && runtime != null
                    && runtime.ControlOwner == MachineControlOwner.TaskEngine && runtime.IsTaskEngineControlClaimed
                    && HasPublishedPreparationSession(runtime, runtime.ConnectionSessionId) ? session : null;
            }
        }

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
                && !_runtime.ProductionStopReviewRequired
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

        /// <summary>运行或暂停的同一任务会话可由顶部停止生产，不直接销毁会话。</summary>
        internal bool CanStopFromHost
        {
            get
            {
                if (_session == null || _commandInProgress || _runtime == null) return false;
                var state = _session.Machine.GetSnapshot().State;
                return (_runtime.State == MachineRunState.Running && state == TaskMachineState.Running)
                    || (_runtime.State == MachineRunState.Paused && state == TaskMachineState.Paused);
            }
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
            if (_runtime?.ProductionStopReviewRequired == true) return "生产已停止，旧工步尚未明确核查。";
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
            var engineeringToolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            _loadButton = AddButton(toolbar, "任务执行读取任务", LoadTask_Click);
            // 日常启停使用外壳顶部统一入口；任务会话的直接命令保留在工程区供维护。
            _resetButton = AddButton(engineeringToolbar, "任务执行复位", ResetTask_Click);
            _startButton = AddButton(engineeringToolbar, "任务执行启动", StartTask_Click);
            _pauseButton = AddButton(engineeringToolbar, "任务执行暂停", PauseTask_Click);
            _stopButton = AddButton(engineeringToolbar, "任务执行停止", StopTask_Click);
            _recoverResourcesButton = new Button { Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 8, 0),
                Background = Brush("#FFFFFF"), BorderBrush = Brush("#C9D5E3"), BorderThickness = new Thickness(1), Visibility = Visibility.Collapsed };
            _recoverResourcesButton.Click += RecoverResources_Click; toolbar.Children.Add(_recoverResourcesButton);
            _productRecordsButton = new Button { Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 8, 0),
                Background = Brush("#FFFFFF"), BorderBrush = Brush("#C9D5E3"), BorderThickness = new Thickness(1) };
            _productRecordsButton.Click += ProductRecords_Click; engineeringToolbar.Children.Add(_productRecordsButton);
            AddButton(toolbar, "设备总览", EquipmentOverview_Click);
            AddButton(toolbar, "设备准备", EquipmentPreparation_Click);
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
            details.Children.Add(engineeringToolbar);
            var refresh = new WrapPanel();
            AddButton(refresh, "刷新关联配置目录", RefreshCatalog_Click);
            details.Children.Add(refresh);
            _catalogDetails = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
            details.Children.Add(_catalogDetails);
            _loadedSource = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
            details.Children.Add(_loadedSource);
            details.Children.Add(_diagnosticText);
            // 任务库较大时诊断区保持有界，完整显示内容可在区内滚动。
            var diagnosticsScroll = new ScrollViewer { Content = details, MaxHeight = 200,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            _diagnostics = new Expander { Margin = new Thickness(0, 6, 0, 0), Content = diagnosticsScroll };
            Grid.SetRow(_diagnostics, 5); root.Children.Add(_diagnostics);
            Grid.SetRow(monitorArea, 3); root.Children.Add(monitorArea);

            var alarmPanel = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            _alarmState = new TextBlock { Margin = new Thickness(0, 0, 0, 4), Foreground = Brush("#596B7D"), TextWrapping = TextWrapping.Wrap };
            DockPanel.SetDock(_alarmState, Dock.Top); alarmPanel.Children.Add(_alarmState);
            _currentAlarmsButton = new Button { Padding = new Thickness(8, 3, 8, 3), HorizontalAlignment = HorizontalAlignment.Left };
            _currentAlarmsButton.Click += CurrentAlarms_Click;
            alarmPanel.Children.Add(_currentAlarmsButton);
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

        /// <summary>创建任务页面按钮并接入 Host 当前语言资源，返回按钮以便更新命令状态。</summary>
        private static Button AddButton(Panel panel, string key, RoutedEventHandler handler)
        {
            var button = new Button { Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 8, 0),
                Background = Brush("#FFFFFF"), BorderBrush = Brush("#C9D5E3"), BorderThickness = new Thickness(1) };
            button.SetResourceReference(ContentControl.ContentProperty, key);
            button.Click += handler; panel.Children.Add(button); return button;
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
            if (_session != null || _retryUnpublishedCleanup != null) await CloseTaskSessionAsync().ConfigureAwait(true);
            _runtime = runtime; _motionCards = motionCards; _logicalIo = logicalIo; _variables = variables;
            _equipmentPreparations = preparations ?? new IStationPreparation[0];
            _controllerConnectionLost = runtime == null;
            if (runtime == null)
            {
                _stateTimer.Stop();
            }
            _successTextKey = null;
            _status.Text = string.Empty;
            ClearError();
            if (_catalogReadError != null)
                ShowError(_catalogReadError, "任务目录读取失败", "任务配置检查建议");
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
            _runtime?.InvalidateConnectionSessionOnLoss();
            RefreshConnectionDisplay();
        }
        /// <summary>依据实际连接与会话状态刷新显示，不修改任务或设备。</summary>
        private void RefreshConnectionDisplay()
        {
            bool connected = IsControllerConnected();
            _monitorContent.Visibility = connected && _session != null ? Visibility.Visible : Visibility.Collapsed;
            _emptyState.Visibility = !connected || _session == null ? Visibility.Visible : Visibility.Collapsed;
            _emptyState.Text = !connected
                ? PageText("控制器未连接，任务和设备状态未确认。请重新连接后检查。", "Controller disconnected. Task and equipment states are unconfirmed; reconnect and check.")
                : !string.IsNullOrWhiteSpace(_runtime?.RecoveryBlockReason)
                    ? PageText("任务尚未读取；设备恢复信息待确认，请先核查现场和设备准备页的恢复诊断。", "Tasks are not loaded; recovery requires inspection. Check the equipment and recovery diagnostics on the preparation page first.")
                    : PageText("任务尚未读取。读取后可在此查看工位步骤与运行状态。", "Tasks are not loaded. Load tasks to view station steps and run status.");
            UpdateRunStatus();
            UpdateCommandAvailability();
            RefreshAlarmState();
            RefreshSessionSource();
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
            // 固定本次连接身份，即使断开后很快重连也不能复用原窗口的核查结果。
            Guid? connectionSession = runtime.ConnectionSessionId;
            if (session.Machine.GetSnapshot().State != TaskMachineState.Stopped || runtime.IsPreparing) return;
            _commandInProgress = true; UpdateCommandAvailability();
            try
            {
                var dialog = new ResourceRecoveryDialog(session.TaskDevice,
                    () => ReferenceEquals(_session, session) && ReferenceEquals(_runtime, runtime)
                        && runtime.IsConnectionSessionCurrent(connectionSession)
                        && HasPublishedPreparationSession(runtime, connectionSession)
                        && IsControllerConnected() && !runtime.IsPreparing
                        && runtime.State != MachineRunState.Running && runtime.State != MachineRunState.Pausing
                        && session.Machine.GetSnapshot().State == TaskMachineState.Stopped,
                    string.Equals(_host.CurrentLanguage, "en-US", StringComparison.OrdinalIgnoreCase));
                dialog.Owner = Window.GetWindow(this);
                dialog.ShowDialog();
            }
            catch (Exception error) { ShowResourceRecoveryOpenError(error); }
            finally { _commandInProgress = false; UpdateRunStatus(); UpdateCommandAvailability(); }
        }

        /// <summary>区分核查窗口打开失败和任务停止失败，保留隔离并提示重新核查。</summary>
        private void ShowResourceRecoveryOpenError(Exception error)
        {
            ShowError(error, "资源核查窗口打开失败", null);
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
            if (!IsControllerConnected())
                throw new InvalidOperationException("控制器连接未确认，请重新连接后读取设备准备配置。");
            if (_session != null) return;
            if (_commandInProgress) throw new InvalidOperationException("任务命令正在执行，请稍后再进入设备准备。");
            _commandInProgress = true;
            try { await LoadTaskSessionAsync(); }
            finally { _commandInProgress = false; UpdateCommandAvailability(); }
        }

        /// <summary>读取运行配置并建立同一设备会话；仅装配，不发送轴或 IO 命令。</summary>
        private async Task LoadTaskSessionAsync()
        {
            await RetryUnpublishedCleanupAsync();
            if (!IsControllerConnected() || _logicalIo == null || _variables == null)
                throw new InvalidOperationException("请先连接控制器，再读取设备准备配置。");
            if (_session != null) return;
            {
                var runtime = _runtime;
                var motionCards = _motionCards;
                var logicalIo = _logicalIo;
                var variables = _variables;
                var equipmentPreparations = _equipmentPreparations;
                Guid? sessionId = runtime.ConnectionSessionId;
                if (!runtime.IsConnectionSessionCurrent(sessionId))
                    throw new InvalidOperationException("当前控制器连接会话未确认，请重新连接后读取任务。");
                long contextVersion = _machineContextVersion;
                TaskMonitorSession loaded = await Task.Run(() => HostTaskSessionLoader.Load(
                    _store, _machineId, motionCards, logicalIo, variables, runtime, sessionId.Value)).ConfigureAwait(true);

                // 准备配置会通知外部订阅者，安装前及发布前均检查最初的上下文。
                void CheckContext()
                {
                    if (contextVersion != _machineContextVersion || !ReferenceEquals(runtime, _runtime)
                        || !ReferenceEquals(motionCards, _motionCards) || !ReferenceEquals(logicalIo, _logicalIo)
                        || !ReferenceEquals(variables, _variables) || !IsControllerConnected()
                        || !runtime.IsConnectionSessionCurrent(sessionId))
                        throw new InvalidOperationException("设备连接在任务读取期间发生变化，本次读取已取消；请在当前连接中重新读取任务。");
                }
                long installedVersion = 0;
                try
                {
                    CheckContext();
                    var preparations = await Task.Run(() => HostTaskSessionLoader.LoadPreparations(_store, _machineId, loaded));
                    CheckContext();
                    runtime.TryConfigurePreparations(preparations, null, out installedVersion);
                    CheckContext();
                    if (runtime.PreparationConfigurationVersion != installedVersion)
                        throw new InvalidOperationException("任务准备配置在读取期间发生变化，请核对当前配置后重新读取。");
                    // 仅在连接与准备定义均核对成功后生成本次会话身份。
                    loaded.Publish(_machineId);
                }
                catch (Exception loadError)
                {
                    _retryUnpublishedCleanup = async () =>
                    {
                        // 只撤回此次安装的定义；新上下文重设过配置时版本检查直接跳过。
                        if (installedVersion != 0)
                            runtime.TryConfigurePreparations(equipmentPreparations, installedVersion, out _);
                        await loaded.Machine.CloseAsync();
                    };
                    try { await RetryUnpublishedCleanupAsync(); }
                    catch (Exception cleanupError)
                    {
                        throw new AggregateException("任务装载未发布且收尾未完成；请重试关闭或读取。", loadError, cleanupError);
                    }
                    throw;
                }

                _publishedPreparationVersion = installedVersion;
                _session = loaded;
                _catalog = loaded.TaskDevice.Catalog;
                _catalogFromLoadedSession = true;
                _monitor = new TaskMonitorView(loaded);
                _monitor.SetSessionValidityProvider(() => ReferenceEquals(CurrentPublishedTaskSession, loaded));
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

        /// <summary>顶部报警复位使用与任务页面相同的校验和复位顺序，不绕过任务故障锁存。</summary>
        internal async Task ResetFromHostAsync(bool confirmedStoppedProduction)
        {
            if (_commandInProgress) throw new InvalidOperationException("任务命令正在执行，请等待完成。");
            _commandInProgress = true;
            try { await ResetTaskCoreAsync(confirmedStoppedProduction).ConfigureAwait(true); }
            finally { _commandInProgress = false; UpdateCommandAvailability(); }
        }

        /// <summary>任务页面复位复用顶部按钮的同一执行过程，保留原有成功提示。</summary>
        private async void ResetTask_Click(object sender, RoutedEventArgs e)
        {
            await RunCommandAsync(async () =>
            {
                await ResetTaskCoreAsync(false).ConfigureAwait(true);
                SetSuccessText("任务执行复位完成");
            }, "任务复位失败", "任务运行复位建议").ConfigureAwait(true);
        }

        /// <summary>复位前后确认当前任务与连接未切换；只按原会话执行轴及报警准入。</summary>
        private async Task ResetTaskCoreAsync(bool confirmedStoppedProduction)
        {
            RequireTaskSession();
            var session = _session;
            var runtime = _runtime;
            var device = session.TaskDevice as MotionControl.TaskEngine.MachineDevice.MachineTaskDevice;
            long contextVersion = _machineContextVersion;
            EnsureCurrentTaskCommandContext(session, runtime, device, contextVersion);
            await session.Machine.ValidateResetPrerequisitesAsync().ConfigureAwait(true);
            EnsureCurrentTaskCommandContext(session, runtime, device, contextVersion);
            if (runtime.State != MachineRunState.Ready)
                await runtime.ResetAsync(CancellationToken.None, confirmedStoppedProduction).ConfigureAwait(true);
            EnsureCurrentTaskCommandContext(session, runtime, device, contextVersion);
            await session.Machine.ResetAsync().ConfigureAwait(true);
            EnsureCurrentTaskCommandContext(session, runtime, device, contextVersion);
        }

        /// <summary>先让 Runtime 建立安全运行状态和急停监控，再启动任务工位。</summary>
        private async void StartTask_Click(object sender, RoutedEventArgs e)
        {
            await RunCommandAsync(StartTaskCoreAsync, "任务启动失败", "任务运行复位建议").ConfigureAwait(true);
        }

        /// <summary>任务页和顶部启动复用同一流程，确认仍是原连接和任务会话后发放运行许可。</summary>
        private async Task StartTaskCoreAsync()
        {
            RequireTaskSession();
            var session = _session;
            var runtime = _runtime;
            var device = session.TaskDevice as MotionControl.TaskEngine.MachineDevice.MachineTaskDevice;
            long contextVersion = _machineContextVersion;
            EnsureCurrentTaskCommandContext(session, runtime, device, contextVersion);
            Guid connection = device.BoundConnectionSessionId.Value;
            await runtime.BeginTaskEngineRunAsync(connection, device, CancellationToken.None).ConfigureAwait(true);
            try
            {
                EnsureCurrentTaskCommandContext(session, runtime, device, contextVersion);
                session.Machine.Start();
                runtime.ConfirmTaskEngineProductionStarted();
            }
            catch (Exception startError)
            {
                try { await runtime.FinishTaskEngineRunAsync(connection, device, true, startError).ConfigureAwait(true); }
                catch (Exception finishError)
                {
                    throw new AggregateException("任务启动失败，原连接的运行状态也未能确认收尾。", startError, finishError);
                }
                throw;
            }
            SetSuccessText("任务执行已启动");
        }

        /// <summary>请求所有任务停步并等待真实轴受控停稳，再更新整机状态。</summary>
        private async void PauseTask_Click(object sender, RoutedEventArgs e)
        {
            await RunCommandAsync(PauseTaskCoreAsync, "任务暂停失败", "任务运行复位建议").ConfigureAwait(true);
        }

        /// <summary>受控停轴后只更新原运行时；停止失败时必须锁存故障，不能保留虚假的运行状态。</summary>
        private async Task PauseTaskCoreAsync()
        {
            RequireTaskSession();
            var session = _session;
            var runtime = _runtime;
            var device = session.TaskDevice as MotionControl.TaskEngine.MachineDevice.MachineTaskDevice;
            long contextVersion = _machineContextVersion;
            EnsureCurrentTaskCommandContext(session, runtime, device, contextVersion);
            Guid connection = device.BoundConnectionSessionId.Value;
            try
            {
                await session.Machine.PauseAsync().ConfigureAwait(true);
                EnsureCurrentTaskCommandContext(session, runtime, device, contextVersion);
                TaskMachineSnapshot snapshot = session.Machine.GetSnapshot();
                if (snapshot.State != TaskMachineState.Paused)
                {
                    string reason = snapshot.Faults.LastOrDefault()?.Message ?? "任务未确认暂停及全部动作收尾。";
                    throw new InvalidOperationException(reason);
                }
                await runtime.FinishTaskEngineRunAsync(connection, device, false, null).ConfigureAwait(true);
            }
            catch (Exception pauseError)
            {
                try { await runtime.FinishTaskEngineRunAsync(connection, device, true, pauseError).ConfigureAwait(true); }
                catch (Exception finishError)
                {
                    throw new AggregateException("任务暂停失败，原连接的报警状态也未能确认收尾。", pauseError, finishError);
                }
                throw;
            }
            SetSuccessText("任务执行已暂停");
        }

        /// <summary>从顶部执行任务启动或暂停；任务页的并发保护和诊断状态共用。</summary>
        internal async Task RunFromHostAsync(bool start)
        {
            if (_commandInProgress) throw new InvalidOperationException("任务命令正在执行，请等待完成。");
            ClearError();
            _commandInProgress = true;
            UpdateCommandAvailability();
            try
            {
                if (start) await StartTaskCoreAsync().ConfigureAwait(true);
                else await PauseTaskCoreAsync().ConfigureAwait(true);
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
                await StopTaskSessionCoreAsync().ConfigureAwait(true);
                SetSuccessText("任务执行已停止");
            }, "任务停止失败", "任务运行复位建议").ConfigureAwait(true);
        }

        /// <summary>应用退出时沿用任务页的终止及收尾，不跳过设备控制权和资源检查。</summary>
        internal async Task StopTaskSessionForShutdownAsync()
        {
            if (_session == null) return;
            if (_commandInProgress) throw new InvalidOperationException("任务命令正在执行，请等待结束后重试关闭。");
            _commandInProgress = true;
            UpdateCommandAvailability();
            try { await StopTaskSessionCoreAsync(true).ConfigureAwait(true); }
            finally
            {
                _commandInProgress = false;
                UpdateRunStatus();
                UpdateCommandAvailability();
                _host.RefreshMachineStateDisplay();
            }
        }

        /// <summary>终止本次任务并等待收尾，不能让旧会话的完成结果改写新设备运行状态。</summary>
        private async Task StopTaskSessionCoreAsync(bool allowBlockedHandoff = false)
        {
            TaskMonitorSession session = _session;
            if (session == null) return;
            var runtime = _runtime;
            var device = session.TaskDevice as MotionControl.TaskEngine.MachineDevice.MachineTaskDevice;
            long contextVersion = _machineContextVersion;
            // 即使连接刚丢失，也先尝试结束本次会话的任务；只有后续状态提交需要有效连接。
            await WaitForShutdownSettlementAsync(session.Machine.AbortAsync(), "任务终止").ConfigureAwait(true);
            EnsureCurrentTaskCommandContext(session, runtime, device, contextVersion);
            Guid connection = device.BoundConnectionSessionId.Value;
            if (runtime.State == MachineRunState.Running || runtime.State == MachineRunState.Pausing)
                await runtime.FinishTaskEngineRunAsync(connection, device, false, null).ConfigureAwait(true);
            bool preserveBlocked = allowBlockedHandoff && session.TaskDevice.ReadResources()
                .Any(resource => resource.State == TaskResourceState.Blocked);
            EnsureCurrentTaskCommandContext(session, runtime, device, contextVersion);
            await CloseTaskSessionAsync(preserveBlocked).ConfigureAwait(true);
        }

        /// <summary>等待在途操作有界结束；超时只提示未确认，后台任务和资源保护继续保留。</summary>
        private static async Task WaitForShutdownSettlementAsync(Task work, string operation)
        {
            if (await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(15))).ConfigureAwait(true) != work)
            {
                // 继续观察迟到异常；不能把超时理解为任务或轴已停止。
                _ = work.ContinueWith(task => { var ignored = task.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                throw new TimeoutException(operation + "尚未完成，设备状态待确认；请检查任务和轴状态后重试。");
            }
            await work.ConfigureAwait(true);
        }

        /// <summary>未发布任务若无法撤回定义或释放控制权，保留清理工作供关闭或重新读取时重试。</summary>
        private Func<Task> _retryUnpublishedCleanup;
        /// <summary>当前未发布会话的清理任务，防止重复关闭同一执行器。</summary>
        private Task _unpublishedCleanupInProgress;

        /// <summary>清理未发布的任务设备；仅在恢复或移除原定义并安全关闭后丢弃其引用。</summary>
        private async Task RetryUnpublishedCleanupAsync()
        {
            var cleanup = _retryUnpublishedCleanup;
            if (cleanup == null) return;
            if (_unpublishedCleanupInProgress != null) { await _unpublishedCleanupInProgress; return; }
            var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _unpublishedCleanupInProgress = completed.Task;
            try
            {
                await cleanup();
                _retryUnpublishedCleanup = null;
                completed.TrySetResult(true);
            }
            catch (Exception error)
            {
                completed.TrySetException(error);
                _ = completed.Task.Exception;
                throw;
            }
            finally { _unpublishedCleanupInProgress = null; }
        }

        /// <summary>检查会话未运行后关闭任务执行器；断卡和应用退出均复用此安全收尾。</summary>
        internal Task CloseTaskSessionAsync(bool preserveBlockedForShutdown = false)
        {
            if (_sessionCloseTask != null && !_sessionCloseTask.IsCompleted)
            {
                if (_sessionClosePreservesBlocked != preserveBlockedForShutdown)
                    throw new InvalidOperationException("另一种任务关闭交接正在执行，请等待完成后重试。");
                return _sessionCloseTask;
            }
            _sessionClosePreservesBlocked = preserveBlockedForShutdown;
            return _sessionCloseTask = CloseTaskSessionCoreAsync(preserveBlockedForShutdown);
        }

        /// <summary>一次关闭只恢复一次准备配置，并保留失败后的会话与诊断。</summary>
        private async Task CloseTaskSessionCoreAsync(bool preserveBlockedForShutdown)
        {
            await RetryUnpublishedCleanupAsync();
            TaskMonitorSession session = _session;
            if (session == null) return;
            TaskMachineState state = session.Machine.GetSnapshot().State;
            if (state == TaskMachineState.Running || state == TaskMachineState.Pausing
                || _runtime?.State == MachineRunState.Running || _runtime?.State == MachineRunState.Pausing || _runtime?.IsPreparing == true)
                throw new InvalidOperationException("任务仍在运行或等待停轴；先在任务运行页暂停或停止并确认安全状态。");

            _stateTimer.Stop();
            _closingTaskSession = true;
            // 关闭期间可能长时间等待，立即撤销旧身份，不依赖已经停止的计时器。
            RefreshSessionSource();
            _monitor?.Refresh();
            try
            {
                Task close = preserveBlockedForShutdown ? session.Machine.CloseForShutdownAsync() : session.Machine.CloseAsync();
                // 超时后只观察执行器结果，禁止迟到续体清除仍需现场核查的会话。
                await WaitForShutdownSettlementAsync(close, "任务会话关闭").ConfigureAwait(true);
            }
            catch
            {
                // 关闭失败或超时后会话可能仍在收尾；保持准备范围不可提交，直至重试关闭成功。
                _stateTimer.Start();
                throw;
            }
            if (!ReferenceEquals(_session, session))
                throw new InvalidOperationException("任务会话在关闭期间发生变化，未清理当前会话。");
            Exception restoreError = null;
            try
            {
                // 关闭前若已有其他安装代次，保留它，不用旧设备定义覆盖。
                if (_runtime != null && !_runtime.IsShutdownInProgress
                    && !_runtime.TryConfigurePreparations(_equipmentPreparations, _publishedPreparationVersion, out _))
                    throw new InvalidOperationException("准备配置已变化，未覆盖当前配置。");
            }
            catch (Exception error) { restoreError = error; }
            _session = null;
            _publishedPreparationVersion = -1;
            _closingTaskSession = false;
            _monitor = null;
            _monitorContent.Content = null;
            _taskSource = null;
            RefreshCatalog();
            _emptyState.Visibility = Visibility.Visible;
            UpdateRunStatus();
            UpdateCommandAvailability();
            RefreshPageText();
            _host.RefreshMachineStateDisplay();
            if (restoreError != null)
                throw new InvalidOperationException("任务会话已关闭，但设备准备配置未能恢复；请核对当前配置。", restoreError);
        }

        /// <summary>读取当前会话冻结目录，或在没有会话时读取当前保存配置。</summary>
        private void RefreshCatalog()
        {
            try
            {
                TaskResourceCatalog catalog;
                if (_session != null)
                {
                    catalog = _session.TaskDevice.Catalog;
                    if (catalog == null)
                        throw new InvalidOperationException("已加载任务会话未提供冻结资源目录，不能改用磁盘目录代替。");
                    _catalogFromLoadedSession = true;
                }
                else
                {
                    var namedResources = new MotionControl.TaskEngine.Configuration.TaskMachineConfigurationStore(_store)
                        .LoadNamedResources(_machineId);
                    catalog = new TaskResourceCatalogLoader(_store).Load(_machineId, namedResources);
                    _catalogFromLoadedSession = false;
                }
                if (_lastError != null) _status.Text = string.Empty;
                _catalogReadError = null;
                ClearError();
                _catalog = catalog;
                RefreshCatalogDetails();
            }
            catch (Exception error)
            {
                _catalog = null;
                _catalogFromLoadedSession = false;
                _catalogReadError = error;
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
            RefreshSessionSource();
            RefreshAlarms();
            TaskMachineSnapshot snapshot = _session.Machine.GetSnapshot();
            if (!_faultStatusSyncing && snapshot.State == TaskMachineState.Faulted
                && (_runtime?.State == MachineRunState.Running || _runtime?.State == MachineRunState.Pausing
                    || _runtime?.State == MachineRunState.Paused || _runtime?.State == MachineRunState.Stopped))
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

        /// <summary>刷新设备级报警摘要，不对报警做复位或清除。</summary>
        private void RefreshAlarms()
        {
            // 仅展示设备级摘要，避免按不可靠名称把报警挂到工位。
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
                    : !string.IsNullOrWhiteSpace(_runtime.RecoveryBlockReason)
                        ? PageText("设备已连接 · 恢复信息待确认，任务尚未读取。", "Equipment connected · recovery requires inspection; tasks are not loaded.")
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
            _loadButton.Visibility = _session == null ? Visibility.Visible : Visibility.Collapsed;
            _resetButton.IsEnabled = connected && taskState.HasValue && !_commandInProgress && !_runtime.ProductionStopReviewRequired
                && taskState != TaskMachineState.Running && taskState != TaskMachineState.Pausing
                && _runtime.State != MachineRunState.Running && _runtime.State != MachineRunState.Pausing && !_runtime.IsPreparing;
            _startButton.IsEnabled = connected && taskState == TaskMachineState.Ready && !_commandInProgress
                && _runtime.State == MachineRunState.Ready && _runtime.IsPrepared && !_runtime.ProductionStopReviewRequired
                && string.IsNullOrWhiteSpace(_runtime.RecoveryBlockReason)
                && string.IsNullOrWhiteSpace(_recoveryStartBlockReason?.Invoke());
            _pauseButton.IsEnabled = taskState == TaskMachineState.Running && !_commandInProgress && !_runtime.ProductionStopReviewRequired;
            _stopButton.IsEnabled = _session != null && !_commandInProgress && !_runtime.IsPreparing
                && (!_runtime.ProductionStopReviewRequired || _runtime.State == MachineRunState.Stopped);
            _recoverResourcesButton.Visibility = hasBlockedResources ? Visibility.Visible : Visibility.Collapsed;
            _recoverResourcesButton.IsEnabled = hasBlockedResources && connected && taskState == TaskMachineState.Stopped
                && !_commandInProgress && !_runtime.IsPreparing;
            UpdateNextAction(connected, taskState, hasBlockedResources);
            _host.RefreshMachineStateDisplay();
        }

        /// <summary>统一串行执行页面命令并在失败时保留完整诊断。</summary>
        private async Task RunCommandAsync(Func<Task> command, string operationKey, string adviceKey)
        {
            if (_commandInProgress) return;
            ClearError();
            _commandInProgress = true;
            UpdateCommandAvailability();
            try
            {
                await command().ConfigureAwait(true);
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

        /// <summary>核对异步命令仍属于发起时的设备、连接和任务；失败时不操作新的会话。</summary>
        private void EnsureCurrentTaskCommandContext(TaskMonitorSession session, MachineRuntime runtime,
            MotionControl.TaskEngine.MachineDevice.MachineTaskDevice device, long contextVersion)
        {
            if (session == null || runtime == null || device?.BoundConnectionSessionId == null
                || _machineContextVersion != contextVersion
                || !ReferenceEquals(_session, session) || !ReferenceEquals(_runtime, runtime)
                || !ReferenceEquals(session.TaskDevice, device) || !IsControllerConnected()
                || !runtime.IsConnectionSessionCurrent(device.BoundConnectionSessionId))
                throw new InvalidOperationException("任务操作期间设备连接或任务会话已变化，不能用旧结果更新当前设备；请确认停稳并重新读取任务。");
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
            string operation = _failureOperationKey == "资源核查窗口打开失败"
                ? PageText("资源核查窗口打开失败", "Unable to open resource inspection")
                : GetText(_failureOperationKey);
            string advice = _failureOperationKey == "任务停止失败"
                ? PageText("请核查任务资源及现场状态；处理完成后重试停止并关闭。保持输出不会自动切换。",
                    "Inspect task resources and the equipment. Retry stop and close after inspection; holding outputs will not be switched automatically.")
                : _failureOperationKey == "资源核查窗口打开失败"
                    ? PageText("资源仍待确认，请检查连接及诊断信息后重试核查；保持输出不会自动切换。",
                        "Resources remain pending inspection. Check the connection and diagnostics, then retry inspection; holding outputs will not be switched automatically.")
                    : GetText(_failureAdviceKey);
            _status.Text = operation + "\n" + reason + "\n" + advice;
        }

        /// <summary>清除旧错误详情；有错误提示时同步清空对应可见文字。</summary>
        private void ClearError()
        {
            bool wasShowingError = _lastError != null;
            _lastError = null;
            _failureOperationKey = null;
            _failureAdviceKey = null;
            if (wasShowingError)
                _status.Text = _successTextKey == null ? string.Empty : SuccessText(_successTextKey);
            _status.Foreground = Brush("#26384D");
            _diagnosticText.Text = string.Empty;
            _diagnosticText.Visibility = Visibility.Collapsed;
            _nextStepButtons.Children.Clear();
            _nextStepButtons.Visibility = Visibility.Collapsed;
        }

        /// <summary>根据连接和任务状态说明下一步，不改变原有按钮控制条件。</summary>
        private void UpdateNextAction(bool connected, TaskMachineState? state, bool hasBlockedResources)
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
            else if (hasBlockedResources)
                _nextAction.Text = GetBlockedResourceGuidance(state,
                    string.Equals(_host.CurrentLanguage, "en-US", StringComparison.OrdinalIgnoreCase));
            else if (_runtime.ProductionStopReviewRequired)
                _nextAction.Text = PageText("生产已请求停止，旧工步和输出仍保留；需要继续旧工步时，请从顶部报警复位核查。", "Production stop requested; steps and outputs are retained. Use the top reset to review before resuming.");
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

        /// <summary>待确认资源优先于回零提示；不会暗示保持输出可自动关闭。</summary>
        private static string GetBlockedResourceGuidance(TaskMachineState? state, bool english)
        {
            if (state == TaskMachineState.Stopped)
                return english
                    ? "Resources need inspection. Select Inspect blocked resources; active holding outputs will not be switched automatically."
                    : "任务资源待确认，请选择“核查待确认资源”检查现场；夹紧、吸附等保持输出不会自动关闭。";
            return english
                ? "Resources need inspection. Stop the task and confirm motion has stopped before inspecting them."
                : "任务资源待确认，请先停止任务并确认机构停稳，再核查待确认资源。";
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
            if (_runtime == null || !IsControllerConnected())
                _alarmState.Text = PageText("控制器未连接，报警状态未知。", "Controller disconnected; alarm status is unknown.");
            else
            {
                try
                {
                    var alarms = _runtime.AlarmService.Alarms.Where(item => item.ConditionActive || item.RequiresReset).ToArray();
                    int pending = alarms.Count(item => item.RequiresReset && !item.ConditionActive);
                    _alarmState.Text = PageText("设备级报警：", "Equipment alarms: ") + alarms.Length
                        + (pending > 0 ? PageText("；待复位：", "; awaiting reset: ") + pending : "")
                        + PageText("。工位关联与处理详情请查看当前报警页；零报警不代表可以启动。", ". See Current alarms for associations and guidance; zero alarms does not establish readiness.");
                }
                catch (Exception error)
                {
                    _alarmState.Text = PageText("报警读取失败，状态未知：", "Alarm read failed; status unknown: ") + error.GetBaseException().Message;
                }
            }
        }

        /// <summary>打开只读报警页面，不复位、清除或执行设备操作。</summary>
        private void CurrentAlarms_Click(object sender, RoutedEventArgs e)
        { _host.OpenTaskReference("当前报警", null); }

        /// <summary>切换语言或会话时刷新页面专用说明。</summary>
        private void RefreshPageText()
        {
            _currentAlarmsButton.Content = PageText("查看当前报警", "View current alarms");
            _stopButton.Content = PageText("终止任务并关闭会话", "Terminate task and close session");
            _stopButton.ToolTip = PageText("等待动作退出后尝试关闭会话；失败或超时仍保留会话与保护。", "Wait for actions to exit, then attempt to close the session; failure or timeout retains the session and protections.");
            _diagnostics.Header = PageText("工程操作与诊断", "Engineering operations and diagnostics");
            _loadButton.Content = PageText("读取设备流程", "Load equipment flow");
            _loadButton.ToolTip = PageText("读取已应用的工位与步骤，不执行复位、回零或启动。",
                "Load applied stations and steps without resetting, homing, or starting the equipment.");
            _recoverResourcesButton.Content = PageText("核查待确认资源", "Inspect blocked resources");
            _productRecordsButton.Content = PageText("产品记录", "Product records");
            RefreshSessionSource();
            if (_lastError == null && _successTextKey != null) _status.Text = SuccessText(_successTextKey);
            RefreshCatalogDetails();
            RefreshConnectionDisplay();
        }

        /// <summary>只展示当前连接内已发布的会话来源；失效时不沿用旧任务身份。</summary>
        private void RefreshSessionSource()
        {
            if (_loadedSource == null) return;
            var metadata = CurrentPublishedTaskSession?.PublishedMetadata;
            _loadedSource.Text = metadata == null
                ? PageText("当前没有可确认的任务会话。", "No task session is currently confirmed.")
                : metadata.FormatDetails(string.Equals(_host.CurrentLanguage, "en-US", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>按实际来源展示目录统计；已装载会话固定显示其冻结目录。</summary>
        private void RefreshCatalogDetails()
        {
            if (_catalog == null) return;
            string sourcePath = _catalogFromLoadedSession
                ? (string.IsNullOrWhiteSpace(_taskSource) ? PageText("会话来源未提供", "Session source not provided") : _taskSource)
                : _store.GetMachineDirectory(_machineId);
            string sourceDescription = _catalogFromLoadedSession
                ? PageText("目录来源：已加载会话的冻结快照；磁盘后续修改不会改变本次会话。",
                    "Catalog source: frozen snapshot of the loaded session; later disk changes do not alter this session.")
                : PageText("目录来源：当前保存配置；尚未装入任务会话，不代表运行时目录。",
                    "Catalog source: current saved configuration; no task session is loaded, so this is not the runtime catalog.");
            _catalogDetails.Text = string.Format(GetText("任务关联目录统计"), _catalog.Axes.Count, _catalog.Inputs.Count,
                _catalog.Outputs.Count, _catalog.Variables.Count, _catalog.TeachPoints.Count)
                + "\n" + sourceDescription + "\n" + sourcePath + "\n" + string.Join("\n", _catalog.Notices);
        }

        /// <summary>记录成功操作并根据当前语言显示反馈。</summary>
        private void SetSuccessText(string key)
        {
            if (_lastError != null) return;
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
