using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using MotionControl.Adapter.MotionCard.ZMotion;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Framework.Contracts.Equipment;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.IO;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Contracts.Preparation;
using MotionControl.Framework.Runtime.Alarms;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.Framework.Runtime.Machine;
using MotionControl.Framework.Runtime.Safety;
using MotionControl.Framework.Runtime.Variables;
using MotionControl.Framework.Wpf.Configuration;
using MotionControl.Framework.Wpf.ManualDebug;
using MotionControl.Framework.Wpf.Monitoring;
using MotionControl.Framework.Wpf.Variables;
using MotionControl.Host.Wpf.Recovery;

namespace MotionControl.Host.Wpf
{
    /// <summary>Host 应用启动入口，负责创建配置服务、页面和控制卡连接。</summary>
    public partial class App : Application
    {
        /// <summary>当前 Host 示例所使用设备的稳定编号。</summary>
        private const string MachineId = "Machine01";

        /// <summary>限制同一 Windows 登录会话只运行一个 Host，避免多个实例争用控制卡。</summary>
        private const string SingleInstanceMutexName = @"Local\MotionControl.Host.Wpf.Machine01";

        /// <summary>恢复已有主窗口时使用的 Win32 显示方式。</summary>
        private const int RestoreWindowCommand = 9;

        /// <summary>当前 Host 持有的单实例互斥锁。</summary>
        private Mutex _singleInstanceMutex;

        /// <summary>标记当前进程是否创建并持有单实例互斥锁。</summary>
        private bool _ownsSingleInstanceMutex;

        /// <summary>由 Host 创建并在应用退出时释放的运动卡连接服务。</summary>
        private IMotionCardHostConnection _cardConnection;

        /// <summary>同步软件控制器心跳状态，不对其他厂商连接主动发起探测。</summary>
        private DispatcherTimer _softwareConnectionTimer;

        /// <summary>最近一次已显示的软件控制器会话状态。</summary>
        private bool _lastSoftwareConnectionStatus;

        /// <summary>当前 Host 使用的轴映射页面，用于在应用启动时恢复控制卡连接。</summary>
        private AxisMappingConfigurationView _axisMappingPage;

        /// <summary>当前 Host 使用的 IO 映射页面，按设备配置目录读写 DI/DO 映射。</summary>
        private IoMappingConfigurationView _ioMappingPage;

        /// <summary>当前 Host 使用的安全联锁页面，独立读写急停 DI 设置。</summary>
        private SafetyInterlockConfigurationView _safetyInterlockPage;

        /// <summary>当前设备报警目录的配置读写入口。</summary>
        private MachineAlarmCatalogStore _alarmCatalogStore;

        /// <summary>当前设备可维护报警目录的 Host 页面。</summary>
        private Configuration.AlarmCatalogConfigurationView _alarmCatalogPage;

        /// <summary>当前设备配置读写服务，也为示教位置提供持久化。</summary>
        private MachineConfigurationStore _configurationStore;

        /// <summary>当前单机的全局设备变量运行表，供页面和工艺流程共用。</summary>
        private DeviceVariableStore _deviceVariableStore;

        /// <summary>协调单机变量、工位步骤和应用生命周期的恢复处理。</summary>
        private MachineRecoveryCoordinator _machineRecoveryCoordinator;
        /// <summary>当前设备报警历史写入器，随控制器连接创建并在断开时收尾。</summary>
        private AlarmHistoryJournal _alarmHistoryJournal;
        /// <summary>未确认写入完成的旧会话；断卡或重连期间保留在内存中以便重试。</summary>
        private readonly List<AlarmHistoryJournal> _unfinishedAlarmHistories = new List<AlarmHistoryJournal>();
        /// <summary>避免断卡与重连重复并发收尾同一个历史写入器。</summary>
        private readonly HashSet<AlarmHistoryJournal> _closingAlarmHistories = new HashSet<AlarmHistoryJournal>();
        /// <summary>应用退出时由退出路径统一等待并报告历史收尾结果。</summary>
        private bool _exitingApplication;

        /// <summary>启动时发现的恢复限制或恢复文件问题，供主窗口显示。</summary>
        private string _recoveryWarningMessage;

        /// <summary>由当前连接卡创建的设备运行时，供自动示例流程与手动调试页共同使用。</summary>
        private MachineRuntime _machineRuntime;

        /// <summary>运行时正在使用的软件急停 DI 状态读取器。</summary>
        private LogicalIoEmergencyStopStatus _machineSafetyStatus;

        /// <summary>当前已映射逻辑轴对应的手动调试页面。</summary>
        private ManualMotionDebugView _manualMotionDebugPage;

        /// <summary>当前设备已映射 DI/DO 对应的 IO 手动页面。</summary>
        private ManualIoDebugView _manualIoDebugPage;

        /// <summary>当前控制卡 DI/DO 物理通道对应的 IO 监控页面。</summary>
        private IoMonitorView _ioMonitorPage;

        /// <summary>当前设备已映射逻辑轴对应的只读轴状态页面。</summary>
        private AxisMonitorView _axisMonitorPage;

        /// <summary>与轴手动、IO 手动共用当前 Runtime 的工位状态页面。</summary>
        private FlowStatusView _flowStatusPage;

        /// <summary>显示和维护当前单机变量定义及运行值的监控页面。</summary>
        private DeviceVariableView _deviceVariablePage;

        /// <summary>当前 Host 主窗口，用于显示控制卡的统一连接状态。</summary>
        private MainWindow _mainWindow;
        /// <summary>现有配置与任务仿真工作台的组合入口。</summary>
        private Tasks.TaskMonitorLauncher _taskMonitorLauncher;
        /// <summary>本机任务草稿与列表编辑页面。</summary>
        private MotionControl.TaskEngine.Wpf.Editing.TaskConfigurationView _taskConfigurationPage;
        /// <summary>异步收尾重入关闭事件时不重复询问同一批草稿。</summary>
        private bool _taskEditsCloseConfirmed;

        /// <summary>创建当前单机的 JSON 配置存储，并接入轴映射和 IO 映射页面。</summary>
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            string productionRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MotionControlFramework", "Configurations");
            string simulationRoot;
            try
            {
                simulationRoot = SimulationConfigurationRoot.Parse(e.Args, productionRoot);
            }
            catch (Exception error)
            {
                MessageBox.Show(error.Message, "测试配置启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
                return;
            }

            // 四页离线预览先于控制卡、现场配置和恢复服务创建。
            if (e.Args.Contains("--ui-workspace-preview"))
            {
                MainWindow = new Operation.FourWorkspacesPreviewWindow();
                MainWindow.Show(); return;
            }

            // 独立编辑预览使用单独数据目录，不创建正常 Host 的控制卡服务。
            if (e.Args.Contains("--task-editing-preview"))
            {
                MainWindow = new MotionControl.TaskEngine.Wpf.Simulation.TaskEditingPreviewWindow();
                MainWindow.Show(); return;
            }

            // 独立任务仿真入口在任何控制卡服务和现场恢复服务创建前返回。
            if (e.Args.Contains("--task-simulation"))
            {
                MainWindow = new MotionControl.TaskEngine.Wpf.Views.TaskSimulationWindow(
                    MotionControl.TaskEngine.Wpf.Simulation.TaskMonitorExample.Create());
                MainWindow.Show();
                return;
            }

            _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out _ownsSingleInstanceMutex);
            if (!_ownsSingleInstanceMutex)
            {
                bool existingWindowActivated = TryActivateExistingInstance();
                if (!existingWindowActivated)
                {
                    MessageBox.Show(
                        "运动控制上位机已经运行，但未能自动切换到现有窗口。请从任务栏切换到已打开的程序。",
                        "程序已在运行",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }

                Shutdown();
                return;
            }

            string configurationRoot = simulationRoot ?? productionRoot;
            _configurationStore = new MachineConfigurationStore(configurationRoot);
            _alarmCatalogStore = new MachineAlarmCatalogStore(_configurationStore);
            try
            {
                _alarmCatalogStore.LoadOrCreate(
                    MachineId,
                    CreateDefaultAlarmCatalog(HostEquipmentSelection.ResolveEquipmentDefinition(_configurationStore, MachineId).CreateDefaultAlarms()));
            }
            catch (Exception error)
            {
                MessageBox.Show(
                    "设备报警库无法读取，自动工位报警配置不会加载；可在设置的报警库页面修复。\n" + error.Message,
                    "设备报警库",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            _deviceVariableStore = CreateDeviceVariableStore();
            var recoveryStore = new MachineRecoveryStore(_configurationStore, MachineId);
            _machineRecoveryCoordinator = new MachineRecoveryCoordinator(
                recoveryStore,
                _deviceVariableStore,
                MachineId);
            _machineRecoveryCoordinator.Load(out _recoveryWarningMessage);
            _cardConnection = CreateMotionCardConnection();
            _axisMappingPage = new AxisMappingConfigurationView(
                _configurationStore,
                MachineId,
                _cardConnection);
            _axisMappingPage.SetHardwareConfigurationReadOnly(_cardConnection is VirtualAxisMappingCardConnection);
            _axisMappingPage.PrepareCardDisconnectAsync = DisconnectMachineBeforeCardAsync;
            _axisMappingPage.ApplyConnectedConfigurationAsync = ApplyConnectedConfigurationAsync;
            _axisMappingPage.RequiresDisconnectedForAxisReconnect = true;
            _axisMappingPage.CardConnectionChanged += AxisMappingPage_CardConnectionChanged;
            _axisMappingPage.AxisMappingApplied += AxisMappingPage_AxisMappingApplied;
            _ioMappingPage = new IoMappingConfigurationView(_configurationStore, MachineId, _cardConnection);
            _ioMappingPage.ApplyConnectedConfigurationAsync = ApplyConnectedConfigurationAsync;
            _ioMappingPage.IoMappingApplied += IoMappingPage_IoMappingApplied;
            _ioMappingPage.FeedbackAssociationsChanged += IoMappingPage_FeedbackAssociationsChanged;
            _safetyInterlockPage = new SafetyInterlockConfigurationView(_configurationStore, MachineId);
            _safetyInterlockPage.ConfigurationSaved += SafetyInterlockPage_ConfigurationSaved;
            _alarmCatalogPage = new Configuration.AlarmCatalogConfigurationView(_alarmCatalogStore, MachineId);
            _deviceVariablePage = new DeviceVariableView(
                _deviceVariableStore,
                _configurationStore,
                MachineId);
            _mainWindow = new MainWindow();
            if (simulationRoot != null) _mainWindow.SetSimulationConfiguration(simulationRoot);
            _mainWindow.Loaded += MainWindow_Loaded;
            _mainWindow.Closing += MainWindow_Closing;
            _mainWindow.LanguageChanged += MainWindow_LanguageChanged;
            _mainWindow.SetAxisMappingConfigurationPage(_axisMappingPage);
            _mainWindow.SetIoMappingConfigurationPage(_ioMappingPage);
            _mainWindow.SetSafetyInterlockConfigurationPage(_safetyInterlockPage);
            _mainWindow.SetAlarmCatalogConfigurationPage(_alarmCatalogPage);
            _mainWindow.SetDeviceVariablePage(_deviceVariablePage);
            _taskMonitorLauncher = new Tasks.TaskMonitorLauncher(_configurationStore, MachineId, _mainWindow);
            _mainWindow.SetTaskMonitorPage(_taskMonitorLauncher);
            _taskConfigurationPage = new MotionControl.TaskEngine.Wpf.Editing.TaskConfigurationView(
                _configurationStore,
                MachineId,
                GetTaskApplyBlockedReason,
                GetTaskRuntimeStateText,
                GetTaskRuntimeStationNames,
                GetTaskRuntimeReadinessReason,
                IsTaskEngineOwner);
            _taskConfigurationPage.ReferenceRequested += TaskEditorReferenceRequested;
            _taskConfigurationPage.CurrentFlowRequested += (sender, args) =>
            {
                if (IsTaskEngineOwner()) _mainWindow.ShowTaskRun();
                else _mainWindow.ShowFlowStatus();
            };
            _mainWindow.SetTaskConfigurationPage(_taskConfigurationPage);
            RefreshIoMonitorPage();
            RefreshAxisMonitorPage(null);

            MainWindow = _mainWindow;
            _mainWindow.Show();
            ShowRecoveryWarningIfNeeded();
        }

        /// <summary>查找同一会话的 Host 主窗口，将其恢复并切到前台。</summary>
        private bool TryActivateExistingInstance()
        {
            int currentProcessId = Process.GetCurrentProcess().Id;
            DateTime retryUntil = DateTime.UtcNow.AddSeconds(2);

            do
            {
                Process[] runningProcesses = Process.GetProcessesByName("MotionControl.Host.Wpf");
                foreach (Process runningProcess in runningProcesses)
                {
                    using (runningProcess)
                    {
                        if (runningProcess.Id == currentProcessId)
                        {
                            continue;
                        }

                        try
                        {
                            runningProcess.Refresh();
                            IntPtr windowHandle = runningProcess.MainWindowHandle;
                            if (windowHandle == IntPtr.Zero)
                            {
                                continue;
                            }

                            ShowWindowAsync(windowHandle, RestoreWindowCommand);
                            if (SetForegroundWindow(windowHandle))
                            {
                                return true;
                            }
                        }
                        catch (InvalidOperationException error)
                        {
                            Trace.TraceWarning("读取已有 Host 窗口失败：" + error.Message);
                        }
                        catch (System.ComponentModel.Win32Exception error)
                        {
                            Trace.TraceWarning("切换到已有 Host 窗口失败：" + error.Message);
                        }
                    }
                }

                if (DateTime.UtcNow < retryUntil)
                {
                    Thread.Sleep(100);
                }
            }
            while (DateTime.UtcNow < retryUntil);

            return false;
        }

        /// <summary>通过 Windows 窗口句柄恢复已有 Host 主窗口。</summary>
        [DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(IntPtr windowHandle, int command);

        /// <summary>请求 Windows 将已有 Host 主窗口切换到前台。</summary>
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr windowHandle);

        /// <summary>在 Host 组合入口选择本次运行使用的运动卡连接实现。</summary>
        private IMotionCardHostConnection CreateMotionCardConnection()
        {
            return HostMotionCardBackendSelection.Create(_configurationStore, MachineId);
        }

        /// <summary>复制首次部署的变量模板，并加载校验后的单机变量定义。</summary>
        private DeviceVariableStore CreateDeviceVariableStore()
        {
            string templateDirectory = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "EquipmentTemplates");
            _configurationStore.CopyMissingJsonFilesFromTemplate(MachineId, templateDirectory);

            DeviceVariableConfigurationDocument document = _configurationStore.LoadJson<DeviceVariableConfigurationDocument>(
                MachineId,
                "Parameters/DeviceVariables.json");
            document.Validate();
            return new DeviceVariableStore(document.Variables);
        }

        /// <summary>在主窗口显示恢复文件失效或部分变量无法恢复的原因。</summary>
        private void ShowRecoveryWarningIfNeeded()
        {
            if (string.IsNullOrWhiteSpace(_recoveryWarningMessage))
            {
                return;
            }

            MessageBox.Show(
                _recoveryWarningMessage,
                "设备恢复状态",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            _recoveryWarningMessage = null;
        }

        /// <summary>主窗口显示后按已保存的控制卡配置自动连接，不阻塞窗口创建。</summary>
        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (_cardConnection is VirtualAxisMappingCardConnection)
            {
                _lastSoftwareConnectionStatus = _cardConnection.IsConnected;
                _mainWindow.SetMotionCardConnectionStatus(_lastSoftwareConnectionStatus, null, true);
                _softwareConnectionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _softwareConnectionTimer.Tick += SoftwareConnectionTimer_Tick;
                _softwareConnectionTimer.Start();
            }
            await _axisMappingPage.InitializeSavedCardConnectionAsync();
        }

        /// <summary>软件控制器心跳中断后及时显示断线，保留旧运行时并禁止误认状态。</summary>
        private void SoftwareConnectionTimer_Tick(object sender, EventArgs e)
        {
            bool connected = _cardConnection != null && _cardConnection.IsConnected;
            if (connected == _lastSoftwareConnectionStatus || _mainWindow == null) return;
            _lastSoftwareConnectionStatus = connected;
            if (!connected)
            {
                string reason = _mainWindow.TryFindResource("软件控制器通讯中断") as string
                    ?? "软件控制器通讯已中断，轴和输出状态未确认。";
                _axisMappingPage.ReportCardConnectionLost(reason);
                _flowStatusPage?.MarkControllerConnectionLost();
                _taskMonitorLauncher?.MarkControllerConnectionLost();
                _mainWindow.SetMotionCardConnectionStatus(false, reason, true);
            }
        }

        /// <summary>Host 语言切换后刷新轴映射页和 IO 映射页中由代码生成的文字。</summary>
        private void MainWindow_LanguageChanged(object sender, EventArgs e)
        {
            _taskConfigurationPage?.SetLanguage(_mainWindow.CurrentLanguage);
            if (_axisMappingPage != null)
            {
                _axisMappingPage.RefreshLocalizedText();
            }

            if (_ioMappingPage != null)
            {
                _ioMappingPage.RefreshLocalizedText();
            }

            if (_safetyInterlockPage != null)
            {
                _safetyInterlockPage.RefreshLocalizedText();
            }

            if (_ioMonitorPage != null)
            {
                _ioMonitorPage.RefreshLocalizedText();
            }

            if (_axisMonitorPage != null)
            {
                _axisMonitorPage.RefreshLocalizedText();
            }

            if (_flowStatusPage != null)
            {
                _flowStatusPage.RefreshLocalizedText();
            }

            if (_deviceVariablePage != null)
            {
                _deviceVariablePage.RefreshLocalizedText();
            }
        }

        /// <summary>在线下发经过整机命令锁；需要重连的轴映射必须先断卡，避免旧 Runtime 持有旧卡。</summary>
        private async Task ApplyConnectedConfigurationAsync(Func<Task> change, bool requiresReconnect, CancellationToken cancellationToken)
        {
            if (requiresReconnect)
                throw new InvalidOperationException("此轴映射更改需要重新连接；请先停止设备并断开控制卡，再保存配置。");
            MachineRuntime runtime = _machineRuntime;
            if (runtime == null)
                throw new InvalidOperationException("设备运行时尚未就绪，不能在线应用控制卡配置。");
            await runtime.RunConnectedConfigurationChangeAsync(change, cancellationToken).ConfigureAwait(true);
        }

        /// <summary>轴映射页断卡前先通过 Runtime 的同一命令锁检查并断开整机。</summary>
        private async Task DisconnectMachineBeforeCardAsync(CancellationToken cancellationToken)
        {
            MachineRuntime runtime = _machineRuntime;
            if (_taskMonitorLauncher != null)
                await _taskMonitorLauncher.CloseTaskSessionAsync().ConfigureAwait(true);
            if (runtime != null && runtime.State != MachineRunState.Disconnected)
                await runtime.DisconnectAsync(cancellationToken).ConfigureAwait(true);
        }

        /// <summary>连接状态变化后同步整机运行时、连接状态和映射对应的手动调试页。</summary>
        private async void AxisMappingPage_CardConnectionChanged(object sender, EventArgs e)
        {
            if (_mainWindow == null)
            {
                return;
            }

            if (_safetyInterlockPage != null)
            {
                _safetyInterlockPage.RefreshDigitalInputNames();
            }

            bool softwareController = _cardConnection is VirtualAxisMappingCardConnection;
            if (softwareController) _lastSoftwareConnectionStatus = _cardConnection.IsConnected;
            _mainWindow.SetMotionCardConnectionStatus(
                _cardConnection.IsConnected,
                _axisMappingPage.CardConnectionStatusMessage,
                softwareController);
            if (!_cardConnection.IsConnected)
            {
                _taskMonitorLauncher?.MarkControllerConnectionLost();
                if (_taskMonitorLauncher != null)
                {
                    try
                    {
                        await _taskMonitorLauncher.SetMachineContextAsync(null, null, null, null);
                    }
                    catch (Exception taskSessionError)
                    {
                        Trace.TraceError("控制卡断开后关闭任务会话失败：" + taskSessionError);
                    }
                }

                if (_machineRuntime != null)
                {
                    _machineRuntime.StatusChanged -= MachineRuntime_RecoveryStatusChanged;
                    try
                    {
                        await _machineRuntime.StopControlInputMonitoringAsync();
                    }
                    catch (Exception monitorError)
                    {
                        Trace.TraceWarning("控制卡断开后停止现场按钮监控失败：" + monitorError.Message);
                    }
                }

                _machineRuntime = null;
                CloseAlarmHistory();
                _machineSafetyStatus = null;
                _manualMotionDebugPage = null;
                _manualIoDebugPage = null;
                _flowStatusPage = null;
                _mainWindow.SetMachineRuntime(null);
                _deviceVariablePage.SetMachineRuntime(null);
                _mainWindow.SetFlowStatusPage(null);
                _mainWindow.SetManualMotionDebugPage(null);
                _mainWindow.SetManualIoDebugPage(null);
                RefreshAxisMonitorPage(null);
                RefreshIoMonitorPage();
                return;
            }

            string recoveryMessage = string.Empty;
            MachineRuntime createdRuntime = null;
            try
            {
                IMotionCard connectedCard = _cardConnection.ConnectedMotionCard;
                if (connectedCard == null)
                {
                    throw new InvalidOperationException("控制卡连接状态有效，但没有可供手动调试使用的卡对象。");
                }

                ILogicalIo connectedLogicalIo = _cardConnection.ConnectedLogicalIo;
                if (connectedLogicalIo == null)
                {
                    throw new InvalidOperationException("控制卡已连接，但没有可供设备运行时使用的逻辑 IO 服务。");
                }

                var motionCards = new MotionCardGroup(new IMotionCard[] { connectedCard });
                SafetyInterlockConfigurationDocument controlInputConfiguration;
                _machineSafetyStatus = CreateSafetyStatus(connectedLogicalIo, out controlInputConfiguration);
                var alarmService = new MachineAlarmService();
                CloseAlarmHistory();
                _alarmHistoryJournal = new AlarmHistoryJournal(alarmService, _configurationStore, MachineId);
                _mainWindow.SetAlarmHistory(_alarmHistoryJournal);
                IStationFlow[] stationFlows;
                IReadOnlyList<IStationPreparation> preparations;
                try
                {
                    stationFlows = CreateEquipmentStationFlows(
                        motionCards, connectedLogicalIo, alarmService, out preparations);
                }
                catch (Exception error)
                {
                    stationFlows = new IStationFlow[0];
                    preparations = new IStationPreparation[0];
                    MessageBox.Show(
                        "设备工位配置无法启用，自动流程已关闭；轴手动和 IO 手动仍可使用。\n"
                        + error.Message,
                        "设备工位配置",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }

                _machineRecoveryCoordinator.TryRestoreStationFlows(stationFlows, out recoveryMessage);

                createdRuntime = new MachineRuntime(
                    motionCards,
                    stationFlows,
                    alarmService,
                    _machineSafetyStatus,
                    controlInputIo: connectedLogicalIo,
                    startInputName: controlInputConfiguration.StartInputName,
                    pauseInputName: controlInputConfiguration.PauseInputName,
                    resetInputName: controlInputConfiguration.ResetInputName,
                    controlOwner: HostEquipmentSelection.ResolveExecutionOwner(_configurationStore, MachineId));
                createdRuntime.RequestInitializationRecovery(
                    _machineRecoveryCoordinator.HasCompletedInitializationCheckpoint);
                // 恢复数据不可信时保留原文件，并阻止自动准备和生产；手动排查仍可用。
                createdRuntime.SetRecoveryBlockReason(_machineRecoveryCoordinator.RecoveryBlockReason);
                createdRuntime.SetBeforeAutomaticRun(_machineRecoveryCoordinator.InvalidateBeforeAutomaticRun);
                createdRuntime.SetPreparationRecordCallbacks(_machineRecoveryCoordinator.MarkBeforePreparation,
                    _machineRecoveryCoordinator.SaveAfterSafePreparation);
                createdRuntime.ConfigurePreparations(preparations);
                await createdRuntime.ConnectAsync(CancellationToken.None);

                _machineRuntime = createdRuntime;
                _machineRuntime.StatusChanged += MachineRuntime_RecoveryStatusChanged;
                _deviceVariablePage.SetMachineRuntime(createdRuntime);
                _flowStatusPage = new FlowStatusView(createdRuntime, () => _taskMonitorLauncher != null && _taskMonitorLauncher.HasTaskSession);
                _mainWindow.SetFlowStatusPage(_flowStatusPage);
                RefreshManualDebugPages(createdRuntime, motionCards, connectedLogicalIo);
                await _taskMonitorLauncher.SetMachineContextAsync(
                    createdRuntime, motionCards, connectedLogicalIo, _deviceVariableStore, preparations);
                // 任务上下文先就位，再让可见的设备准备页读取对应入口，避免误用上一会话。
                _mainWindow.SetMachineRuntime(createdRuntime);
            }
            catch (Exception error)
            {
                if (createdRuntime != null)
                {
                    try
                    {
                        await createdRuntime.StopControlInputMonitoringAsync();
                    }
                    catch (Exception monitorError)
                    {
                        Trace.TraceWarning("运行时初始化失败后停止现场按钮监控失败：" + monitorError.Message);
                    }
                }

                _machineRuntime = null;
                CloseAlarmHistory();
                _machineSafetyStatus = null;
                _manualMotionDebugPage = null;
                _manualIoDebugPage = null;
                _flowStatusPage = null;
                _mainWindow.SetMachineRuntime(null);
                _deviceVariablePage.SetMachineRuntime(null);
                _mainWindow.SetFlowStatusPage(null);
                _mainWindow.SetManualMotionDebugPage(null);
                _mainWindow.SetManualIoDebugPage(null);
                RefreshAxisMonitorPage(null);
                RefreshIoMonitorPage();
                MessageBox.Show(
                    "控制卡已连接，但创建运行时或调试页面失败：" + error.Message,
                    "设备运行时",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (!string.IsNullOrWhiteSpace(recoveryMessage))
            {
                MessageBox.Show(
                    recoveryMessage,
                    "设备流程恢复",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        /// <summary>暂停后保存工位步骤与变量；自动运行开始时先写入不可续跑标记。</summary>
        private void MachineRuntime_RecoveryStatusChanged(object sender, EventArgs e)
        {
            MachineRuntime runtime = sender as MachineRuntime;
            if (runtime == null || _machineRecoveryCoordinator == null)
            {
                return;
            }

            if (runtime.State == MachineRunState.Paused)
            {
                try
                {
                    _machineRecoveryCoordinator.SavePausedCheckpoint(runtime);
                }
                catch (Exception error)
                {
                    ShowRecoverySaveWarning(error);
                }
            }
        }

        /// <summary>关闭前阻止设备仍在运行或报警处理中，并保存当前变量与可续跑状态。</summary>
        private void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            MachineRunState? runtimeState = _machineRuntime == null
                ? (MachineRunState?)null
                : _machineRuntime.State;
            if (runtimeState == MachineRunState.Running
                || runtimeState == MachineRunState.Preparing
                || runtimeState == MachineRunState.Pausing
                || runtimeState == MachineRunState.Alarmed)
            {
                e.Cancel = true;
                MessageBox.Show(
                    "设备流程仍在运行、暂停处理中或报警处理中。请先让流程安全停止并完成复位，再关闭软件。",
                    "暂不能关闭软件",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (!_taskEditsCloseConfirmed)
            {
                if (_taskConfigurationPage != null && !_taskConfigurationPage.ConfirmClose()) { e.Cancel = true; return; }
                if (_alarmCatalogPage != null && !_alarmCatalogPage.ConfirmClose()) { e.Cancel = true; return; }
                _taskEditsCloseConfirmed = true;
            }
            try
            {
                bool savePausedFlow = runtimeState == MachineRunState.Paused;
                _machineRecoveryCoordinator.SaveForShutdown(_machineRuntime, savePausedFlow);
            }
            catch (Exception error)
            {
                e.Cancel = true;
                _taskEditsCloseConfirmed = false;
                MessageBox.Show(
                    "设备恢复状态保存失败，软件暂不关闭。请处理存储问题后重试。\n" + error.Message,
                    "恢复状态保存失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }
        }

        /// <summary>更新任务文件前要求控制卡会话断开，不替人员自动断卡。</summary>
        private string GetTaskApplyBlockedReason()
        {
            if (_cardConnection != null && (_cardConnection.IsConnected || _cardConnection.ConnectedMotionCard != null))
                return _mainWindow.TryFindResource("任务配置先断开会话") as string ?? "当前控制卡会话仍连接。本阶段请先在轴映射页断开会话，再应用任务文件；草稿可以随时保存。";
            return null;
        }

        /// <summary>返回与顶部状态栏一致的正式 Runtime 状态；没有运行时则返回空值。</summary>
        private string GetTaskRuntimeStateText()
        {
            if (_machineRuntime == null || _mainWindow == null)
            {
                return null;
            }

            return _mainWindow.GetTaskConfigurationRuntimeStateText();
        }

        /// <summary>返回正式 Runtime 当前的启动拦截原因，供任务配置页显示处理方向。</summary>
        private string GetTaskRuntimeReadinessReason()
        {
            return _machineRuntime == null || _mainWindow == null
                ? null
                : _mainWindow.GetTaskConfigurationReadinessReason();
        }

        /// <summary>复制正式 Host 当前 Runtime 的工位名称供任务配置页只读对照。</summary>
        private IEnumerable<string> GetTaskRuntimeStationNames()
        {
            return _machineRuntime == null || _machineRuntime.ControlOwner != MachineControlOwner.StationFlows
                ? Enumerable.Empty<string>()
                : _machineRuntime.StationFlows.Select(flow => flow.StationName).ToArray();
        }

        /// <summary>识别设备当前采用的任务列表控制入口。</summary>
        private bool IsTaskEngineOwner()
        {
            return _machineRuntime != null && _machineRuntime.ControlOwner == MachineControlOwner.TaskEngine;
        }

        /// <summary>保留编辑页对象并导航，返回任务配置时仍有原任务与草稿。</summary>
        private void TaskEditorReferenceRequested(object sender, MotionControl.TaskEngine.Wpf.Views.TaskReferenceEventArgs e)
        { _mainWindow.OpenTaskReference(e.Page, e.Name, e.Direction); }

        /// <summary>把恢复文件保存错误显示到 WPF 界面线程，不隐藏数据写入失败。</summary>
        private void ShowRecoverySaveWarning(Exception error)
        {
            Action showWarning = () => MessageBox.Show(
                "设备已暂停，但恢复状态未能保存；关闭软件前会再次尝试。\n" + error.Message,
                "恢复状态保存失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            if (Dispatcher.CheckAccess())
            {
                showWarning();
            }
            else
            {
                Dispatcher.BeginInvoke(showWarning);
            }
        }

        /// <summary>按设备配置选择设备定义，并创建本设备的工位流程。</summary>
        private IStationFlow[] CreateEquipmentStationFlows(
            MotionCardGroup motionCards,
            ILogicalIo logicalIo,
            IMachineAlarmService alarmService,
            out IReadOnlyList<IStationPreparation> preparations)
        {
            IEquipmentDefinition equipment = HostEquipmentSelection.ResolveEquipmentDefinition(_configurationStore, MachineId);
            var context = new HostEquipmentContext(
                _configurationStore,
                MachineId,
                _alarmCatalogStore.Load(MachineId),
                _cardConnection.AxisHardwareDefinitions,
                _cardConnection.DigitalInputs,
                _cardConnection.DigitalOutputs,
                motionCards,
                logicalIo,
                alarmService);
            IStationFlow[] flows = equipment.CreateStationFlows(context).ToArray();
            var provider = equipment as IEquipmentPreparationProvider;
            preparations = provider == null ? new IStationPreparation[0] : provider.CreatePreparations(context, flows);
            return flows;
        }

        /// <summary>为首次运行的设备目录按设备定义预置报警，框架不内置任何设备的报警代码。</summary>
        private static MachineAlarmCatalogDocument CreateDefaultAlarmCatalog(
            IReadOnlyList<MachineAlarmDefinition> definitions)
        {
            var document = new MachineAlarmCatalogDocument();
            foreach (MachineAlarmDefinition definition in definitions ?? new MachineAlarmDefinition[0])
            {
                document.Alarms.Add(new MachineAlarmCatalogEntry
                {
                    Code = definition.Code,
                    Message = definition.Message,
                    Kind = definition.Kind,
                    CheckGuidance = definition.CheckGuidance,
                    RecoveryGuidance = definition.RecoveryGuidance
                });
            }

            return document;
        }

        /// <summary>使用当前运行时和映射重建轴手动、IO 手动及 IO 监控页，不创建或连接新的 Runtime。</summary>
        private void RefreshManualDebugPages(
            MachineRuntime machineRuntime,
            MotionCardGroup motionCards,
            ILogicalIo connectedLogicalIo)
        {
            ManualMotionDebugView manualPage = new ManualMotionDebugView(
                machineRuntime,
                motionCards,
                _cardConnection.AxisHardwareDefinitions,
                connectedLogicalIo,
                _cardConnection.DigitalInputs,
                _cardConnection.DigitalOutputs,
                _configurationStore,
                MachineId);
            manualPage.AxisMappingRequested += (sender, args) => _mainWindow.ShowConfigurationPage("轴映射配置");
            ManualIoDebugView ioManualPage = new ManualIoDebugView(
                machineRuntime,
                connectedLogicalIo,
                _cardConnection.DigitalInputs.Select(point => point.Name),
                _cardConnection.DigitalOutputs.Select(point => point.Name));
            ioManualPage.IoMappingRequested += (sender, args) => _mainWindow.ShowConfigurationPage("IO映射");

            _manualMotionDebugPage = manualPage;
            _manualIoDebugPage = ioManualPage;
            _mainWindow.SetManualMotionDebugPage(manualPage);
            _mainWindow.SetManualIoDebugPage(ioManualPage);
            RefreshAxisMonitorPage(motionCards);
            RefreshIoMonitorPage();
        }

        /// <summary>按当前轴映射和连接状态创建只读监控页，不执行任何运动或伺服操作。</summary>
        private void RefreshAxisMonitorPage(MotionCardGroup motionCards)
        {
            if (_mainWindow == null || _cardConnection == null)
            {
                return;
            }

            bool isCardConnected = _cardConnection.IsConnected
                && motionCards != null
                && motionCards.IsConnected;
            _axisMonitorPage = new AxisMonitorView(
                isCardConnected,
                isCardConnected ? motionCards : null,
                _cardConnection.AxisHardwareDefinitions);
            _mainWindow.SetAxisMonitorPage(_axisMonitorPage);
        }

        /// <summary>按当前连接对象和 IO 映射刷新监控页，不连接控制卡或发送 IO 命令。</summary>
        private void RefreshIoMonitorPage()
        {
            if (_mainWindow == null || _cardConnection == null)
            {
                return;
            }

            IMotionCard connectedCard = _cardConnection.ConnectedMotionCard;
            bool isCardConnected = _cardConnection.IsConnected && connectedCard != null;
            IoFeedbackAssociationConfiguration feedbackConfiguration = null;
            string feedbackError = null;
            try
            {
                feedbackConfiguration = new IoFeedbackAssociationStore(_configurationStore, MachineId).Load();
            }
            catch (Exception error)
            {
                feedbackError = error.Message;
            }
            _ioMonitorPage = new IoMonitorView(
                isCardConnected,
                _machineRuntime,
                isCardConnected ? _cardConnection as IMotionCardIoCapabilityProvider : null,
                isCardConnected ? connectedCard as IPhysicalDigitalIoMonitor : null,
                _cardConnection.DigitalInputs,
                _cardConnection.DigitalOutputs,
                isCardConnected ? _cardConnection.ConnectedLogicalIo : null,
                feedbackConfiguration,
                feedbackError);
            _mainWindow.SetIoMonitorPage(_ioMonitorPage);
        }

        /// <summary>关联配置保存后只刷新 IO 监控，保留当前控制卡连接。</summary>
        private void IoMappingPage_FeedbackAssociationsChanged(object sender, EventArgs e)
        {
            RefreshIoMonitorPage();
        }

        /// <summary>IO 映射热更新后刷新安全 DI 选项、手动页和 IO 监控页，保留现有控制卡与 Runtime。</summary>
        private void IoMappingPage_IoMappingApplied(object sender, EventArgs e)
        {
            if (_safetyInterlockPage != null)
            {
                _safetyInterlockPage.RefreshDigitalInputNames();
            }

            if (_mainWindow == null || !_cardConnection.IsConnected || _machineRuntime == null)
            {
                return;
            }

            try
            {
                IMotionCard connectedCard = _cardConnection.ConnectedMotionCard;
                ILogicalIo connectedLogicalIo = _cardConnection.ConnectedLogicalIo;
                if (connectedCard == null || connectedLogicalIo == null)
                {
                    throw new InvalidOperationException("当前控制卡连接缺少手动调试所需的轴或逻辑 IO 对象。");
                }

                string emergencyStopInputName = _safetyInterlockPage == null
                    ? string.Empty
                    : _safetyInterlockPage.EmergencyStopInputName;
                if (_machineSafetyStatus != null)
                {
                    _machineSafetyStatus.UpdateEmergencyStopInputName(emergencyStopInputName);
                }

                var motionCards = new MotionCardGroup(new[] { connectedCard });
                RefreshManualDebugPages(_machineRuntime, motionCards, connectedLogicalIo);
            }
            catch (Exception error)
            {
                MessageBox.Show(
                    "IO 映射已应用，但刷新手动调试页面失败：" + error.Message,
                    "IO 映射",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        /// <summary>轴映射在线应用后更新调试页显示，并保留当前控制卡和运行时对象。</summary>
        private void AxisMappingPage_AxisMappingApplied(object sender, EventArgs e)
        {
            if (_mainWindow == null || !_cardConnection.IsConnected || _machineRuntime == null)
            {
                return;
            }

            try
            {
                IMotionCard connectedCard = _cardConnection.ConnectedMotionCard;
                ILogicalIo connectedLogicalIo = _cardConnection.ConnectedLogicalIo;
                if (connectedCard == null || connectedLogicalIo == null)
                {
                    throw new InvalidOperationException("当前控制卡连接缺少手动调试所需的轴或逻辑 IO 对象。");
                }

                var motionCards = new MotionCardGroup(new[] { connectedCard });
                RefreshManualDebugPages(_machineRuntime, motionCards, connectedLogicalIo);
            }
            catch (Exception error)
            {
                MessageBox.Show(
                    "轴映射已在线应用，但刷新手动调试页面失败：" + error.Message,
                    "轴映射",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        /// <summary>读取独立保存的急停与现场按钮 DI；留空急停时保持软件监控关闭。</summary>
        private LogicalIoEmergencyStopStatus CreateSafetyStatus(
            ILogicalIo logicalIo,
            out SafetyInterlockConfigurationDocument controlInputConfiguration)
        {
            const string safetyInterlockFilePath = "HardwareConfig/SafetyInterlock.json";
            string fullPath = Path.Combine(
                _configurationStore.GetMachineDirectory(MachineId),
                safetyInterlockFilePath.Replace('/', Path.DirectorySeparatorChar));
            var configuration = new SafetyInterlockConfigurationDocument();

            if (File.Exists(fullPath))
            {
                configuration = _configurationStore.LoadJson<SafetyInterlockConfigurationDocument>(
                    MachineId,
                    safetyInterlockFilePath);
                if (configuration == null || configuration.SchemaVersion != 1)
                {
                    throw new InvalidOperationException(
                        "SafetyInterlock.json 格式无效，只支持 schemaVersion 为 1。");
                }
            }

            controlInputConfiguration = configuration;

            string emergencyStopInputName = configuration.EmergencyStopInputName ?? string.Empty;
            if (string.IsNullOrWhiteSpace(emergencyStopInputName))
            {
                return new LogicalIoEmergencyStopStatus(logicalIo, string.Empty);
            }

            if (!_cardConnection.DigitalInputs.Any(point => string.Equals(
                point.Name,
                emergencyStopInputName,
                StringComparison.OrdinalIgnoreCase)))
            {
                System.Diagnostics.Trace.TraceWarning(
                    "安全联锁配置中的急停输入 '" + emergencyStopInputName
                    + "' 没有对应的 DI 映射，运行时会将已配置的安全输入标记为不可读取并闭锁动作。");
            }

            return new LogicalIoEmergencyStopStatus(logicalIo, emergencyStopInputName);
        }

        /// <summary>安全配置保存后立即更新运行时正在使用的急停和整机按钮 DI。</summary>
        private void SafetyInterlockPage_ConfigurationSaved(object sender, EventArgs e)
        {
            if (_machineSafetyStatus != null)
            {
                _machineSafetyStatus.UpdateEmergencyStopInputName(
                    _safetyInterlockPage.EmergencyStopInputName);
                if (_machineRuntime != null)
                {
                    _machineRuntime.UpdateControlInputNames(
                        _safetyInterlockPage.StartInputName,
                        _safetyInterlockPage.PauseInputName,
                        _safetyInterlockPage.ResetInputName);
                }

                return;
            }

            if (_cardConnection != null && _cardConnection.IsConnected)
            {
                AxisMappingPage_CardConnectionChanged(sender, e);
            }
        }

        /// <summary>应用退出时断开已连接的控制卡，并报告句柄释放失败。</summary>
        protected override void OnExit(ExitEventArgs e)
        {
            if (_softwareConnectionTimer != null)
            {
                _softwareConnectionTimer.Stop();
                _softwareConnectionTimer.Tick -= SoftwareConnectionTimer_Tick;
            }
            if (_taskMonitorLauncher != null)
            {
                try
                {
                    _taskMonitorLauncher.CloseTaskSessionAsync().GetAwaiter().GetResult();
                }
                catch (Exception taskCloseError)
                {
                    Trace.TraceWarning("应用退出时关闭任务执行会话失败：" + taskCloseError.Message);
                }
            }

            if (_machineRuntime != null)
            {
                try
                {
                    _machineRuntime.StopControlInputMonitoringAsync().GetAwaiter().GetResult();
                }
                catch (Exception monitorError)
                {
                    Trace.TraceWarning("应用退出时停止现场按钮监控失败：" + monitorError.Message);
                }
            }

            if (_cardConnection != null && _cardConnection.IsConnected)
            {
                try
                {
                    _cardConnection.DisconnectAsync(CancellationToken.None).GetAwaiter().GetResult();
                }
                catch (Exception error)
                {
                    MessageBox.Show(
                        "关闭应用时断开运动控制卡失败：" + error.Message,
                        "运动控制卡连接",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }

            _exitingApplication = true;
            CloseAlarmHistory();
            FinishAlarmHistoryAtExit();
            ReleaseSingleInstanceMutex();

            base.OnExit(e);
        }

        /// <summary>解除页面引用，后台收尾并保留未写入对象；不让磁盘等待阻断设备停机。</summary>
        private void CloseAlarmHistory()
        {
            _mainWindow?.SetAlarmHistory(null);
            var history = _alarmHistoryJournal;
            _alarmHistoryJournal = null;
            if (history != null) _unfinishedAlarmHistories.Add(history);
            if (_exitingApplication) return;
            foreach (var item in _unfinishedAlarmHistories.ToArray())
            {
                if (_closingAlarmHistories.Add(item)) FinishAlarmHistoryAsync(item, !ReferenceEquals(item, history));
            }
        }

        /// <summary>断卡和重连时在后台等待历史写入，不占用界面线程或延迟停机。</summary>
        private async void FinishAlarmHistoryAsync(AlarmHistoryJournal history, bool retry)
        {
            try
            {
                var result = await Task.Run(() => retry
                    ? history.RetryPending(TimeSpan.FromSeconds(3))
                    : history.Close(TimeSpan.FromSeconds(3)));
                if (_exitingApplication) return;
                if (result.IsComplete) _unfinishedAlarmHistories.Remove(history);
                else ShowAlarmHistoryWarning(result.Error, result.PendingCount, result.WriterStopped, false);
            }
            catch (Exception error)
            {
                Trace.TraceError("报警历史收尾异常：" + error);
                if (!_exitingApplication) ShowAlarmHistoryWarning(error.Message, history.GetPendingSnapshot().Count, false, false);
            }
            finally { _closingAlarmHistories.Remove(history); }
        }

        /// <summary>关闭程序时控制卡已断开，最后等待并报告所有未完成历史。</summary>
        private void FinishAlarmHistoryAtExit()
        {
            foreach (var history in _unfinishedAlarmHistories.ToArray())
            {
                try
                {
                    var result = history.Close(TimeSpan.FromSeconds(3));
                    if (!result.IsComplete && result.WriterStopped) result = history.RetryPending(TimeSpan.FromSeconds(3));
                    if (result.IsComplete) _unfinishedAlarmHistories.Remove(history);
                    else ShowAlarmHistoryWarning(result.Error, result.PendingCount, result.WriterStopped, true);
                }
                catch (Exception error)
                {
                    Trace.TraceError("退出时报警历史收尾异常：" + error);
                    ShowAlarmHistoryWarning(error.Message, history.GetPendingSnapshot().Count, false, true);
                }
            }
        }

        /// <summary>明确说明历史未确认保存及退出后内存记录可能丢失，不干预设备停机。</summary>
        private static void ShowAlarmHistoryWarning(string error, int pending, bool stopped, bool exiting)
        {
            string status = stopped ? "未写入 " + pending + " 条。" : "后台写入尚未确认结束，当前待写 " + pending + " 条。";
            string action = exiting ? "程序退出后内存中的未写记录可能丢失。请检查磁盘并联系维护人员。"
                : "记录暂保留在本程序内存中；检查磁盘后重新连接可重试，程序退出后仍可能丢失。";
            MessageBox.Show("报警历史可能不完整。" + status + "\n" + (error ?? "未提供具体错误。") + "\n" + action,
                "报警历史保存异常", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        /// <summary>应用退出时释放本进程持有的单实例互斥锁。</summary>
        private void ReleaseSingleInstanceMutex()
        {
            if (_singleInstanceMutex == null)
            {
                return;
            }

            if (_ownsSingleInstanceMutex)
            {
                _singleInstanceMutex.ReleaseMutex();
            }

            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            _ownsSingleInstanceMutex = false;
        }

    }
}
