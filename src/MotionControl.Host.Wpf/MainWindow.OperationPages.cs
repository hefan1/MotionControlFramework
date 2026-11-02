using System;
using System.Windows;
using System.Windows.Controls;
using MotionControl.Framework.Wpf.Configuration;
using MotionControl.Framework.Wpf.ManualDebug;
using MotionControl.Host.Wpf.Operation;

namespace MotionControl.Host.Wpf
{
    /// <summary>PDF 操作工作区的明确组合，页面只观察当前传统 Runtime。</summary>
    public partial class MainWindow
    {
        /// <summary>默认首页，未连接时也保留明确空状态。</summary>
        private EquipmentOverviewView _equipmentOverviewPage;
        /// <summary>当前报警页，和总览使用相同 Runtime。</summary>
        private CurrentAlarmsView _currentAlarmsPage;
        /// <summary>历史报警与当前报警共用一个设备日志来源。</summary>
        private AlarmHistoryView _alarmHistoryPage;
        /// <summary>设备定义与任务列表共用的工位回零页面。</summary>
        private EquipmentPreparationView _equipmentPreparationPage;
        /// <summary>将当前设备的报警历史交给报警页面，写入器由应用管理。</summary>
        internal void SetAlarmHistory(MotionControl.Framework.Runtime.Alarms.AlarmHistoryJournal history)
        { _currentAlarmsPage.SetHistory(history); _alarmHistoryPage.SetHistory(history); }
        /// <summary>从总览或报警进入关联页面后的返回目标。</summary>
        private string _operationReturnPage;
        /// <summary>返回目标所在分区，避免任务配置等非操作页被错误映射到首页。</summary>
        private string _operationReturnSection;

        /// <summary>保存当前页为返回目标并显示返回入口。</summary>
        private void SetOperationReturnTarget(string section, string page)
        {
            _operationReturnSection = section;
            _operationReturnPage = page;
            OperationReturnButton.Visibility = string.IsNullOrWhiteSpace(section) || string.IsNullOrWhiteSpace(page)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        /// <summary>普通主导航开始新路径时清除上一条关联导航的返回状态。</summary>
        private void ClearOperationReturnTarget()
        {
            _operationReturnSection = null;
            _operationReturnPage = null;
            OperationReturnButton.Visibility = Visibility.Collapsed;
        }
        /// <summary>创建两个只读工作区，不连接任何控制卡。</summary>
        private void CreateOperationPages()
        {
            _equipmentOverviewPage = new EquipmentOverviewView();
            _currentAlarmsPage = new CurrentAlarmsView();
            _alarmHistoryPage = new AlarmHistoryView();
            _equipmentPreparationPage = new EquipmentPreparationView(EnsurePreparationConfigurationAsync, NavigatePreparationPage);
            _equipmentOverviewPage.NavigationRequested += OperationNavigationRequested;
            _currentAlarmsPage.NavigationRequested += OperationNavigationRequested;
        }
        /// <summary>将具体工作区放入原有内容区，不重排外壳。</summary>
        private bool TryShowOperationPage()
        {
            UserControl page = null;
            string description = null;
            if (_currentSectionKey == "Home" && _currentPageResourceKey == "设备总览")
            {
                if (_axisMappingConfigurationPage is AxisMappingConfigurationView axisMappingPage)
                    _equipmentOverviewPage.SetAxisMappingStatus(axisMappingPage.ConfiguredAxisCount, axisMappingPage.ConfiguredAxisTotal);
                page = _equipmentOverviewPage; description = "设备总览页面说明";
            }
            if (_currentSectionKey == "Alarms" && _currentPageResourceKey == "当前报警")
            { page = _currentAlarmsPage; description = "当前报警页面说明"; }
            if (_currentSectionKey == "Alarms" && _currentPageResourceKey == "历史报警")
            { page = _alarmHistoryPage; description = "历史报警页面说明"; }
            if (_currentSectionKey == "Home" && _currentPageResourceKey == "设备准备")
            { page = _equipmentPreparationPage; description = "设备准备页面说明"; }
            if (page == null) return false;
            ManualDebugContent.Content = page; ManualDebugContent.Visibility = Visibility.Visible;
            PagePlaceholder.Visibility = Visibility.Collapsed;
            PageDescription.SetResourceReference(TextBlock.TextProperty, description);
            return true;
        }
        /// <summary>关联页面只能查看；缺少 Runtime 对应页面时显示具体未就绪提示。</summary>
        private void OperationNavigationRequested(object sender, OperatorNavigationEventArgs e)
        {
            bool unavailable = e.Page == "流程状态" && _flowStatusPage == null
                || e.Page == "轴监控" && _axisMonitorPage == null
                || e.Page == "轴手动" && _manualMotionDebugPage == null
                || e.Page == "IO监控" && _ioMonitorPage == null;
            if (unavailable)
            {
                MessageBox.Show(GetLocalizedText("关联页面尚未就绪"), GetLocalizedText(e.Page), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            SetOperationReturnTarget(_currentSectionKey, _currentPageResourceKey);
            _currentPageResourceKey = e.Page;
            string section = e.Page == "当前报警" || e.Page == "历史报警" ? "Alarms"
                : e.Page == "设备准备" ? "Home"
                : e.Page == "轴映射配置" ? "Settings"
                : e.Page == "轴手动" ? "Manual"
                : e.Page == "IO监控" || e.Page == "流程状态" ? "Monitor"
                : "Monitor";
            NavigateToSection(section);
            if (e.Page == "IO监控" && _ioMonitorPage is IoMonitorView ioMonitorPage)
            {
                if (string.IsNullOrWhiteSpace(e.IoPointName)) ioMonitorPage.SelectDigitalInputs();
                else ioMonitorPage.FocusDigitalInputByName(e.IoPointName);
            }
            if (e.Page == "流程状态" && _flowStatusPage is FlowStatusView flow) flow.FocusStationByName(e.StationName);
        }

        /// <summary>任务入口首次进入准备页时读取当前会话；不发送回零或生产命令。</summary>
        private async System.Threading.Tasks.Task EnsurePreparationConfigurationAsync()
        {
            var launcher = _taskMonitorPage as Tasks.TaskMonitorLauncher;
            if (_machineRuntime != null && _machineRuntime.ControlOwner == MotionControl.Framework.Contracts.Flow.MachineControlOwner.TaskEngine && launcher != null)
                await launcher.EnsureTaskSessionAsync();
        }

        /// <summary>准备页的查看入口使用已有生产和诊断页面。</summary>
        private void NavigatePreparationPage(string page)
        {
            if (page == "生产流程")
            {
                if (_machineRuntime?.ControlOwner == MotionControl.Framework.Contracts.Flow.MachineControlOwner.TaskEngine) ShowTaskRun();
                else ShowFlowStatus();
                return;
            }
            OperationNavigationRequested(this, new OperatorNavigationEventArgs(page));
        }
        /// <summary>返回原操作页，原报警选择和卡片滚动对象仍保留。</summary>
        private void OperationReturn_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_operationReturnPage) || string.IsNullOrWhiteSpace(_operationReturnSection))
            {
                ClearOperationReturnTarget();
                return;
            }

            _currentPageResourceKey = _operationReturnPage;
            string section = _operationReturnSection;
            ClearOperationReturnTarget();
            NavigateToSection(section);
        }
    }
}
