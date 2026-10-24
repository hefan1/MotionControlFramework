using System;
using System.Windows;
using System.Windows.Controls;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Wpf.Simulation;
using MotionControl.TaskEngine.Wpf.Views;

namespace MotionControl.Host.Wpf.Tasks
{
    /// <summary>在现有 Host 中查看共享目录并打开独立的任务仿真工作台。</summary>
    internal sealed class TaskMonitorLauncher : UserControl
    {
        /// <summary>复用当前设备配置服务。</summary>
        private readonly MachineConfigurationStore _store;
        /// <summary>当前设备标识。</summary>
        private readonly string _machineId;
        /// <summary>负责导航到已有轴、IO 和变量页面的主窗口。</summary>
        private readonly MainWindow _host;
        /// <summary>目录读取结果和错误反馈。</summary>
        private readonly TextBlock _status;
        /// <summary>当前打开的仿真窗口，避免重复创建运行会话。</summary>
        private TaskSimulationWindow _window;

        /// <summary>创建明确的配置入口；不会从此处连接控制卡。</summary>
        internal TaskMonitorLauncher(MachineConfigurationStore store, string machineId, MainWindow host)
        {
            _store = store; _machineId = machineId; _host = host;
            var panel = new StackPanel { Margin = new Thickness(24) };
            var note = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,12), FontSize = 14 };
            note.SetResourceReference(TextBlock.TextProperty, "任务仿真入口说明"); panel.Children.Add(note);
            var buttons = new WrapPanel();
            AddButton(buttons, "读取已有任务配置", OpenMachine_Click);
            AddButton(buttons, "打开示例任务仿真", OpenExample_Click);
            AddButton(buttons, "刷新关联配置目录", RefreshCatalog_Click);
            panel.Children.Add(buttons);
            _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,14,0,0) };
            panel.Children.Add(_status); Content = panel;
            Loaded += Launcher_Loaded;
        }

        /// <summary>用中文资源键创建按钮，语言切换自动刷新。</summary>
        private static void AddButton(Panel panel, string key, RoutedEventHandler handler)
        {
            var button = new Button { Padding = new Thickness(12,8,12,8), Margin = new Thickness(0,0,10,0) };
            button.SetResourceReference(ContentControl.ContentProperty, key); button.Click += handler; panel.Children.Add(button);
        }
        /// <summary>显示最新目录数量，不缓存为第二份映射。</summary>
        private void Launcher_Loaded(object sender, RoutedEventArgs e) { RefreshCatalog(); }
        /// <summary>重新读取其他页面已保存的配置。</summary>
        private void RefreshCatalog_Click(object sender, RoutedEventArgs e) { RefreshCatalog(); }
        /// <summary>共享目录加载失败时给出明确文件错误。</summary>
        private void RefreshCatalog()
        {
            try
            {
                var namedResources = new MotionControl.TaskEngine.Configuration.TaskMachineConfigurationStore(_store)
                    .LoadNamedResources(_machineId);
                var catalog = new TaskResourceCatalogLoader(_store).Load(_machineId, namedResources);
                _status.Text = string.Format((string)FindResource("任务关联目录统计"), catalog.Axes.Count, catalog.Inputs.Count,
                    catalog.Outputs.Count, catalog.Variables.Count, catalog.TeachPoints.Count)
                    + "\n" + _store.GetMachineDirectory(_machineId) + "\n" + string.Join("\n", catalog.Notices);
            }
            catch (Exception error) { _status.Text = error.ToString(); }
        }
        /// <summary>用本机 Tasks 与 TaskMachine 配置创建内存会话，所有引用仍经过校验。</summary>
        private void OpenMachine_Click(object sender, RoutedEventArgs e) { Open(false); }
        /// <summary>示例使用独立目录，明确与现场同名对象区分。</summary>
        private void OpenExample_Click(object sender, RoutedEventArgs e) { Open(true); }
        /// <summary>同一 Host 只打开一个仿真窗口；更换配置需关闭旧会话后重新读取。</summary>
        private void Open(bool example)
        {
            if (_window != null) { _window.WindowState = WindowState.Normal; _window.Activate(); return; }
            TaskMonitorSession session = null;
            try
            {
                session = example ? TaskMonitorExample.Create() : TaskMonitorSession.LoadMachine(_store, _machineId);
                _window = new TaskSimulationWindow(session) { Owner = _host };
                _window.ReferenceRequested += ReferenceRequested;
                _window.Closed += Simulation_Closed;
                _window.Show();
            }
            catch (Exception error)
            {
                _window = null;
                if (session != null) session.Machine.CloseAsync().GetAwaiter().GetResult();
                _status.Text = error.ToString();
            }
        }
        /// <summary>窗口关闭完成后才允许创建下一个会话。</summary>
        private void Simulation_Closed(object sender, EventArgs e) { _window = null; }
        /// <summary>只请求导航，跳转不会发送手动运动或输出命令。</summary>
        private void ReferenceRequested(object sender, TaskReferenceEventArgs e)
        {
            _window.WindowState = WindowState.Minimized;
            _host.OpenTaskReference(e.Page, e.Name, e.Direction);
        }

        /// <summary>Host 关闭前等待仿真工位收尾；没有打开窗口时立即完成。</summary>
        internal System.Threading.Tasks.Task CloseSimulationAsync()
        { return _window == null ? System.Threading.Tasks.Task.CompletedTask : _window.CloseSessionAsync(); }

        /// <summary>是否还有需关闭的仿真工作台。</summary>
        internal bool HasSimulationWindow => _window != null;
    }
}
