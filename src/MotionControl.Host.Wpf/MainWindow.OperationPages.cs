using System;
using System.Windows;
using System.Windows.Controls;
using MotionControl.Framework.Wpf.Configuration;
using MotionControl.Framework.Wpf.ManualDebug;
using MotionControl.Host.Wpf.Operation;

namespace MotionControl.Host.Wpf
{
    /// <summary>组合操作员工作区与关联导航，页面只观察当前执行入口。</summary>
    public partial class MainWindow
    {
        /// <summary>默认首页，未连接时也保留明确空状态。</summary>
        private EquipmentOverviewView _equipmentOverviewPage;
        /// <summary>当前报警页，和总览使用相同 Runtime。</summary>
        private CurrentAlarmsView _currentAlarmsPage;
        /// <summary>设备定义与任务列表共用的工位回零页面。</summary>
        private EquipmentPreparationView _equipmentPreparationPage;
        /// <summary>尚未建立运行环境时明确显示缺失条件。</summary>
        private UserControl _stationRunEmptyPage;
        /// <summary>向设备准备页提供实际设备目录，只用于查看恢复证据位置。</summary>
        internal void SetRecoveryEvidenceDirectory(string directory)
        { _equipmentPreparationPage.SetRecoveryEvidenceDirectory(directory); }
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
        /// <summary>创建总览、报警、设备准备和空状态页面；页面构造本身不连接控制卡。</summary>
        private void CreateOperationPages()
        {
            _equipmentOverviewPage = new EquipmentOverviewView();
            _equipmentOverviewPage.SetTaskPreparationScopeProvider(
                (runtime, session) => (_taskMonitorPage as Tasks.TaskMonitorLauncher)?.HasPublishedPreparationSession(runtime, session) == true);
            _currentAlarmsPage = new CurrentAlarmsView();
            _equipmentPreparationPage = new EquipmentPreparationView(EnsurePreparationConfigurationAsync, NavigatePreparationPage,
                (runtime, session) => (_taskMonitorPage as Tasks.TaskMonitorLauncher)?.HasPublishedPreparationSession(runtime, session) == true);
            var runMessage = new TextBlock { Margin = new Thickness(18), TextWrapping = TextWrapping.Wrap };
            runMessage.SetResourceReference(TextBlock.TextProperty, "工位运行未连接提示");
            _stationRunEmptyPage = new UserControl { Content = runMessage };
            _equipmentOverviewPage.NavigationRequested += OperationNavigationRequested;
            _currentAlarmsPage.NavigationRequested += OperationNavigationRequested;
        }
        /// <summary>将具体工作区放入原有内容区，不重排外壳。</summary>
        private bool TryShowOperationPage()
        {
            UserControl page = null;
            string description = null;
            if (_currentPageResourceKey == "工位运行")
            {
                page = CurrentStationRunPage() ?? _stationRunEmptyPage;
                description = "工位运行页面说明";
            }
            if (IsCurrentPageInSection("Home") && _currentPageResourceKey == "设备总览")
            {
                if (_axisMappingConfigurationPage is AxisMappingConfigurationView axisMappingPage)
                    _equipmentOverviewPage.SetAxisMappingStatus(axisMappingPage.ConfiguredAxisCount, axisMappingPage.ConfiguredAxisTotal);
                page = _equipmentOverviewPage; description = "设备总览页面说明";
            }
            if (IsCurrentPageInSection("Alarms") && _currentPageResourceKey == "当前报警")
            { page = _currentAlarmsPage; description = "当前报警页面说明"; }
            if (IsCurrentPageInSection("Home") && _currentPageResourceKey == "设备准备")
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
            bool unavailable = e.Page == "轴监控" && _axisMonitorPage == null
                || e.Page == "轴手动" && _manualMotionDebugPage == null && _disconnectedManualMotionPageFactory == null
                || e.Page == "IO监控" && _ioMonitorPage == null;
            if (unavailable)
            {
                MessageBox.Show(GetLocalizedText("关联页面尚未就绪"), GetLocalizedText(e.Page), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            SetOperationReturnTarget(_currentSectionKey, _currentPageResourceKey);
            string section = e.Page == "当前报警" ? "Alarms"
                : e.Page == "设备准备" ? "Home"
                : e.Page == "轴映射配置" ? "Settings"
                : e.Page == "轴手动" ? "Manual"
                : e.Page == "IO监控" || e.Page == "流程状态" ? "Monitor"
                : "Monitor";
            NavigateToRelatedPage(e.Page, section);
            if (e.Page == "IO监控" && _ioMonitorPage is IoMonitorView ioMonitorPage)
            {
                if (string.IsNullOrWhiteSpace(e.IoPointName)) ioMonitorPage.SelectDigitalInputs();
                else ioMonitorPage.FocusDigitalInputByName(e.IoPointName);
            }
            if (_currentPageResourceKey == "工位运行" && !string.IsNullOrWhiteSpace(e.StationName))
            {
                bool located = _machineRuntime?.ControlOwner == MotionControl.Framework.Contracts.Flow.MachineControlOwner.TaskEngine
                    ? (_taskMonitorPage as Tasks.TaskMonitorLauncher)?.FocusStationByName(e.StationName) == true
                    : (_flowStatusPage as FlowStatusView)?.FocusStationByName(e.StationName) == true;
                if (!located) PageDescription.Text += " · " + GetLocalizedText("工位运行未定位提示") + e.StationName;
            }
        }

        /// <summary>按设备当前控制入口选择内部视图，不读取任务、不更改控制权。</summary>
        private UserControl CurrentStationRunPage()
        {
            if (_machineRuntime?.ControlOwner == MotionControl.Framework.Contracts.Flow.MachineControlOwner.StationFlows)
                return _flowStatusPage;
            if (_machineRuntime?.ControlOwner == MotionControl.Framework.Contracts.Flow.MachineControlOwner.None)
                return _flowStatusPage;
            return _taskMonitorPage;
        }

        /// <summary>执行入口发生变化时替换内部视图，同一来源不重复挂载页面。</summary>
        private void RefreshStationRunSource()
        {
            if (_currentPageResourceKey != "工位运行") return;
            var page = CurrentStationRunPage() ?? _stationRunEmptyPage;
            if (!ReferenceEquals(ManualDebugContent.Content, page)) UpdatePageContent();
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
