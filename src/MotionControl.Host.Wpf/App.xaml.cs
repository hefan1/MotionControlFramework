using System;
using System.IO;
using System.Threading;
using System.Windows;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.Framework.Wpf.Configuration;

namespace MotionControl.Host.Wpf
{
    public partial class App : Application
    {
        /// <summary>由 Host 创建并在应用退出时释放的正运动连接服务。</summary>
        private ZMotionAxisMappingCardConnection _cardConnection;

        /// <summary>当前 Host 使用的轴映射页面，用于在应用启动时恢复控制卡连接。</summary>
        private AxisMappingConfigurationView _axisMappingPage;

        /// <summary>当前 Host 主窗口，用于显示控制卡的统一连接状态。</summary>
        private MainWindow _mainWindow;

        /// <summary>创建当前单机的 JSON 配置存储，并把轴映射页面接入主窗口。</summary>
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            string configurationRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MotionControlFramework",
                "Configurations");
            var configurationStore = new MachineConfigurationStore(configurationRoot);
            _cardConnection = new ZMotionAxisMappingCardConnection(configurationStore, "Machine01");
            _axisMappingPage = new AxisMappingConfigurationView(
                configurationStore,
                "Machine01",
                _cardConnection);
            _axisMappingPage.CardConnectionChanged += AxisMappingPage_CardConnectionChanged;
            _mainWindow = new MainWindow();
            _mainWindow.Loaded += MainWindow_Loaded;
            _mainWindow.SetAxisMappingConfigurationPage(_axisMappingPage);

            MainWindow = _mainWindow;
            _mainWindow.Show();
        }

        /// <summary>主窗口显示后按已保存的控制卡配置自动连接，不阻塞窗口创建。</summary>
        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await _axisMappingPage.InitializeSavedCardConnectionAsync();
        }

        /// <summary>轴映射页连接状态变化时更新主窗口底部的控制卡连接状态。</summary>
        private void AxisMappingPage_CardConnectionChanged(object sender, EventArgs e)
        {
            if (_mainWindow != null)
            {
                _mainWindow.SetMotionCardConnectionStatus(_cardConnection.IsConnected);
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

            base.OnExit(e);
        }
    }
}
