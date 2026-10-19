using System;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using MotionControl.Adapter.MotionCard.ZMotion;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Contracts.Safety;
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
        /// <summary>由 Host 创建并在应用退出时释放的正运动连接服务。</summary>
        private ZMotionAxisMappingCardConnection _cardConnection;

        /// <summary>当前 Host 使用的轴映射页面，用于在应用启动时恢复控制卡连接。</summary>
        private AxisMappingConfigurationView _axisMappingPage;

        /// <summary>当前 Host 使用的 IO 映射页面，按设备配置目录读写 DI/DO 映射。</summary>
        private IoMappingConfigurationView _ioMappingPage;

        /// <summary>当前设备配置读写服务，也为示教位置提供持久化。</summary>
        private MachineConfigurationStore _configurationStore;

        /// <summary>由当前连接卡创建的设备运行时，仅用于手动调试页的状态和操作保护。</summary>
        private MachineRuntime _machineRuntime;

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

            string configurationRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MotionControlFramework",
                "Configurations");
            _configurationStore = new MachineConfigurationStore(configurationRoot);
            _cardConnection = new ZMotionAxisMappingCardConnection(_configurationStore, "Machine01");
            _axisMappingPage = new AxisMappingConfigurationView(
                _configurationStore,
                "Machine01",
                _cardConnection);
            _axisMappingPage.CardConnectionChanged += AxisMappingPage_CardConnectionChanged;
            _ioMappingPage = new IoMappingConfigurationView(_configurationStore, "Machine01", _cardConnection);
            _mainWindow = new MainWindow();
            _mainWindow.Loaded += MainWindow_Loaded;
            _mainWindow.LanguageChanged += MainWindow_LanguageChanged;
            _mainWindow.SetAxisMappingConfigurationPage(_axisMappingPage);
            _mainWindow.SetIoMappingConfigurationPage(_ioMappingPage);

            MainWindow = _mainWindow;
            _mainWindow.Show();
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
        }

        /// <summary>连接状态变化后同步底栏，并用当前卡轴映射创建或移除手动调试页。</summary>
        private async void AxisMappingPage_CardConnectionChanged(object sender, EventArgs e)
        {
            if (_mainWindow == null)
            {
                return;
            }

            _mainWindow.SetMotionCardConnectionStatus(_cardConnection.IsConnected);
            if (!_cardConnection.IsConnected)
            {
                _machineRuntime = null;
                _manualMotionDebugPage = null;
                _manualIoDebugPage = null;
                _mainWindow.SetManualMotionDebugPage(null);
                _mainWindow.SetManualIoDebugPage(null);
                return;
            }

            try
            {
                ZMotionCard connectedCard = _cardConnection.ConnectedCard;
                if (connectedCard == null)
                {
                    throw new InvalidOperationException("控制卡连接状态有效，但没有可供手动调试使用的卡对象。");
                }

                var motionCards = new MotionCardGroup(new IMotionCard[] { connectedCard });
                IMachineSafetyStatus safetyStatus = CreateSafetyStatus(connectedCard);
                var machineRuntime = new MachineRuntime(
                    motionCards,
                    new IStationFlow[0],
                    new MachineAlarmService(),
                    safetyStatus);
                await machineRuntime.ConnectAsync(CancellationToken.None);

                ManualMotionDebugView manualPage = new ManualMotionDebugView(
                    machineRuntime,
                    motionCards,
                    _cardConnection.AxisHardwareDefinitions,
                    _configurationStore,
                    "Machine01");
                ManualIoDebugView ioManualPage = new ManualIoDebugView(
                    machineRuntime,
                    connectedCard,
                    _cardConnection.DigitalInputs.Select(point => point.Name),
                    _cardConnection.DigitalOutputs.Select(point => point.Name));

                _machineRuntime = machineRuntime;
                _manualMotionDebugPage = manualPage;
                _manualIoDebugPage = ioManualPage;
                _mainWindow.SetManualMotionDebugPage(manualPage);
                _mainWindow.SetManualIoDebugPage(ioManualPage);
            }
            catch (Exception error)
            {
                _machineRuntime = null;
                _manualMotionDebugPage = null;
                _manualIoDebugPage = null;
                _mainWindow.SetManualMotionDebugPage(null);
                _mainWindow.SetManualIoDebugPage(null);
                MessageBox.Show(
                    "控制卡已连接，但创建轴手动页面失败：" + error.Message,
                    "轴手动页面",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        /// <summary>使用已映射急停 DI 创建安全状态读取器；缺少配置时按急停触发处理。</summary>
        private IMachineSafetyStatus CreateSafetyStatus(ZMotionCard connectedCard)
        {
            string emergencyStopInputName = _cardConnection.EmergencyStopInputName;
            if (string.IsNullOrWhiteSpace(emergencyStopInputName))
            {
                return new MissingEmergencyStopStatus();
            }

            if (!_cardConnection.DigitalInputs.Any(point => string.Equals(
                point.Name,
                emergencyStopInputName,
                StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    "急停输入 '" + emergencyStopInputName + "' 没有对应的 DI 映射，不能创建运行时。");
            }

            return new LogicalIoEmergencyStopStatus(connectedCard, emergencyStopInputName);
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

            base.OnExit(e);
        }

        /// <summary>尚未映射急停 DI 时返回触发状态，避免整机复位或启动绕过安全输入。</summary>
        private sealed class MissingEmergencyStopStatus : IMachineSafetyStatus
        {
            /// <summary>没有可读取的急停输入时按触发处理。</summary>
            public Task<bool> ReadEmergencyStopActiveAsync(CancellationToken cancellationToken)
            {
                return Task.FromResult(true);
            }
        }
    }
}
