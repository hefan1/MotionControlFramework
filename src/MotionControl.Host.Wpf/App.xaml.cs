using System;
using System.IO;
using System.Windows;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.Framework.Wpf.Configuration;

namespace MotionControl.Host.Wpf
{
    public partial class App : Application
    {
        /// <summary>创建当前单机的 JSON 配置存储，并把轴映射页面接入主窗口。</summary>
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            string configurationRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MotionControlFramework",
                "Configurations");
            var configurationStore = new MachineConfigurationStore(configurationRoot);
            var axisMappingPage = new AxisMappingConfigurationView(configurationStore, "Machine01");
            var mainWindow = new MainWindow();
            mainWindow.SetAxisMappingConfigurationPage(axisMappingPage);

            MainWindow = mainWindow;
            mainWindow.Show();
        }
    }
}
