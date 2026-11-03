using System;
using System.Windows;
using System.Windows.Controls;

namespace MotionControl.Host.Wpf
{
    /// <summary>设备工程页面的导航及导入后重启保护。</summary>
    public partial class MainWindow
    {
        /// <summary>当前单机工程配置页面，按需创建以便复用组合根的配置服务。</summary>
        private UserControl _equipmentProjectPage;
        /// <summary>导入后当前进程的设备配置已过期，只允许正常关闭。</summary>
        private bool _equipmentProjectRestartRequired;

        /// <summary>启动、暂停、停止或复位命令尚在执行时不能应用工程。</summary>
        internal bool HasMachineCommandInProgress { get { return _isMachineCommandRunning || _isResetting; } }

        /// <summary>只在设置分区选择设备工程时创建并显示该页。</summary>
        private bool TryShowEquipmentProjectPage()
        {
            if (!string.Equals(_currentSectionKey, "Settings", StringComparison.Ordinal)
                || !string.Equals(_currentPageResourceKey, "设备工程", StringComparison.Ordinal)) return false;
            if (_equipmentProjectPage == null && Application.Current is App app)
            {
                try { _equipmentProjectPage = app.CreateEquipmentProjectPage(); }
                catch (Exception error)
                {
                    MessageBox.Show("设备工程页面初始化失败，未改变设备配置。\n" + error.Message,
                        "设备工程", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
            }
            if (_equipmentProjectPage == null) return false;
            ManualDebugContent.Content = _equipmentProjectPage;
            ManualDebugContent.Visibility = Visibility.Visible;
            PagePlaceholder.Visibility = Visibility.Collapsed;
            PageDescription.SetResourceReference(TextBlock.TextProperty, "设备工程页面说明");
            return true;
        }

        /// <summary>工程应用后禁止旧配置页面和整机命令，保留窗口正常关闭入口。</summary>
        internal void RequireRestartForEquipmentProject()
        {
            if (_equipmentProjectRestartRequired) return;
            _equipmentProjectRestartRequired = true;
            PrimaryNavigation.IsEnabled = false;
            SidebarNavigation.IsEnabled = false;
            OperationReturnButton.IsEnabled = false;
            if (MachineStartButton.Parent is UIElement commands) commands.IsEnabled = false;
        }

        /// <summary>导出期间阻止切换配置页面，顶部的停止按钮继续可用。</summary>
        internal void SetEquipmentProjectNavigationBusy(bool busy)
        {
            PrimaryNavigation.IsEnabled = !busy && !_equipmentProjectRestartRequired;
            SidebarNavigation.IsEnabled = !busy && !_equipmentProjectRestartRequired;
            OperationReturnButton.IsEnabled = !busy && !_equipmentProjectRestartRequired;
        }
    }
}
