using System;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using MotionControl.Adapter.MotionCard.ZMotion;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.IO;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Runtime.Alarms;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.Framework.Runtime.Machine;
using MotionControl.Framework.Runtime.Safety;
using MotionControl.Framework.Wpf.Configuration;
using MotionControl.Framework.Wpf.ManualDebug;

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

        /// <summary>当前 Host 使用的轴映射页面，用于在应用启动时恢复控制卡连接。</summary>
        private AxisMappingConfigurationView _axisMappingPage;

        /// <summary>当前 Host 使用的 IO 映射页面，按设备配置目录读写 DI/DO 映射。</summary>
        private IoMappingConfigurationView _ioMappingPage;

        /// <summary>当前 Host 使用的安全联锁页面，独立读写急停 DI 设置。</summary>
        private SafetyInterlockConfigurationView _safetyInterlockPage;

        /// <summary>当前设备配置读写服务，也为示教位置提供持久化。</summary>
        private MachineConfigurationStore _configurationStore;

        /// <summary>由当前连接卡创建的设备运行时，仅用于手动调试页的状态和操作保护。</summary>
        private MachineRuntime _machineRuntime;

        /// <summary>运行时正在使用的软件急停 DI 状态读取器。</summary>
        private LogicalIoEmergencyStopStatus _machineSafetyStatus;

        /// <summary>当前已映射逻辑轴对应的手动调试页面。</summary>
        private ManualMotionDebugView _manualMotionDebugPage;

        /// <summary>当前设备已映射 DI/DO 对应的 IO 手动页面。</summary>
        private ManualIoDebugView _manualIoDebugPage;

        /// <summary>当前 Host 主窗口，用于显示控制卡的统一连接状态。</summary>
        private MainWindow _mainWindow;

        /// <summary>创建当前单机的 JSON 配置存储，并接入轴映射和 IO 映射页面。</summary>
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

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

            string configurationRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MotionControlFramework",
                "Configurations");
            _configurationStore = new MachineConfigurationStore(configurationRoot);
            _cardConnection = CreateMotionCardConnection();
            _axisMappingPage = new AxisMappingConfigurationView(
                _configurationStore,
                MachineId,
                _cardConnection);
            _axisMappingPage.CardConnectionChanged += AxisMappingPage_CardConnectionChanged;
            _axisMappingPage.AxisMappingApplied += AxisMappingPage_AxisMappingApplied;
            _ioMappingPage = new IoMappingConfigurationView(_configurationStore, MachineId, _cardConnection);
            _ioMappingPage.IoMappingApplied += IoMappingPage_IoMappingApplied;
            _safetyInterlockPage = new SafetyInterlockConfigurationView(_configurationStore, MachineId);
            _safetyInterlockPage.ConfigurationSaved += SafetyInterlockPage_ConfigurationSaved;
            _mainWindow = new MainWindow();
            _mainWindow.Loaded += MainWindow_Loaded;
            _mainWindow.LanguageChanged += MainWindow_LanguageChanged;
            _mainWindow.SetAxisMappingConfigurationPage(_axisMappingPage);
            _mainWindow.SetIoMappingConfigurationPage(_ioMappingPage);
            _mainWindow.SetSafetyInterlockConfigurationPage(_safetyInterlockPage);

            MainWindow = _mainWindow;
            _mainWindow.Show();
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
            return new ZMotionAxisMappingCardConnection(_configurationStore, MachineId);
        }

        /// <summary>主窗口显示后按已保存的控制卡配置自动连接，不阻塞窗口创建。</summary>
        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await _axisMappingPage.InitializeSavedCardConnectionAsync();
        }

        /// <summary>Host 语言切换后刷新轴映射页和 IO 映射页中由代码生成的文字。</summary>
        private void MainWindow_LanguageChanged(object sender, EventArgs e)
        {
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

            _mainWindow.SetMotionCardConnectionStatus(_cardConnection.IsConnected);
            if (!_cardConnection.IsConnected)
            {
                _machineRuntime = null;
                _machineSafetyStatus = null;
                _manualMotionDebugPage = null;
                _manualIoDebugPage = null;
                _mainWindow.SetMachineRuntime(null);
                _mainWindow.SetManualMotionDebugPage(null);
                _mainWindow.SetManualIoDebugPage(null);
                return;
            }

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
                _machineSafetyStatus = CreateSafetyStatus(connectedLogicalIo);
                var machineRuntime = new MachineRuntime(
                    motionCards,
                    new IStationFlow[0],
                    new MachineAlarmService(),
                    _machineSafetyStatus);
                await machineRuntime.ConnectAsync(CancellationToken.None);

                _machineRuntime = machineRuntime;
                _mainWindow.SetMachineRuntime(machineRuntime);
                RefreshManualDebugPages(machineRuntime, motionCards, connectedLogicalIo);
            }
            catch (Exception error)
            {
                _machineRuntime = null;
                _machineSafetyStatus = null;
                _manualMotionDebugPage = null;
                _manualIoDebugPage = null;
                _mainWindow.SetMachineRuntime(null);
                _mainWindow.SetManualMotionDebugPage(null);
                _mainWindow.SetManualIoDebugPage(null);
                MessageBox.Show(
                    "控制卡已连接，但创建轴手动页面失败：" + error.Message,
                    "轴手动页面",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        /// <summary>使用当前运行时和逻辑映射重建手动轴、IO 页面，不创建或连接新的 Runtime。</summary>
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
            ManualIoDebugView ioManualPage = new ManualIoDebugView(
                machineRuntime,
                connectedLogicalIo,
                _cardConnection.DigitalInputs.Select(point => point.Name),
                _cardConnection.DigitalOutputs.Select(point => point.Name));

            _manualMotionDebugPage = manualPage;
            _manualIoDebugPage = ioManualPage;
            _mainWindow.SetManualMotionDebugPage(manualPage);
            _mainWindow.SetManualIoDebugPage(ioManualPage);
        }

        /// <summary>IO 映射热更新后刷新安全 DI 选项和手动页，保留现有控制卡与 Runtime。</summary>
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

        /// <summary>轴映射在线应用后更新手动页显示，并保留当前控制卡和运行时对象。</summary>
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

        /// <summary>从独立安全配置读取急停 DI；留空表示仅关闭软件监控，生产配置仍由全量校验把关。</summary>
        private LogicalIoEmergencyStopStatus CreateSafetyStatus(ILogicalIo logicalIo)
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

        /// <summary>安全配置保存后立即更新运行时正在使用的急停 DI。</summary>
        private void SafetyInterlockPage_ConfigurationSaved(object sender, EventArgs e)
        {
            if (_machineSafetyStatus != null)
            {
                _machineSafetyStatus.UpdateEmergencyStopInputName(
                    _safetyInterlockPage.EmergencyStopInputName);
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

            ReleaseSingleInstanceMutex();

            base.OnExit(e);
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
