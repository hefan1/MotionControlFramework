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
        /// <summary>从总览或报警进入关联页面后的返回目标。</summary>
        private string _operationReturnPage;
        /// <summary>创建两个只读工作区，不连接任何控制卡。</summary>
        private void CreateOperationPages()
        {
            _equipmentOverviewPage = new EquipmentOverviewView();
            _currentAlarmsPage = new CurrentAlarmsView();
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
                || e.Page == "轴手动" && _manualMotionDebugPage == null
                || e.Page == "IO监控" && _ioMonitorPage == null;
            if (unavailable)
            {
                MessageBox.Show(GetLocalizedText("关联页面尚未就绪"), GetLocalizedText(e.Page), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            _operationReturnPage = ReferenceEquals(sender, _currentAlarmsPage) ? "当前报警"
                : ReferenceEquals(sender, _flowStatusPage) ? "流程状态" : "设备总览";
            _currentPageResourceKey = e.Page;
            string section = e.Page == "当前报警" ? "Alarms"
                : e.Page == "轴映射配置" ? "Settings"
                : e.Page == "轴手动" ? "Manual"
                : e.Page == "IO监控" || e.Page == "流程状态" ? "Monitor"
                : "Monitor";
            NavigateToSection(section);
            if (e.Page == "IO监控" && ReferenceEquals(sender, _flowStatusPage) && _ioMonitorPage is IoMonitorView ioMonitorPage)
                ioMonitorPage.SelectDigitalInputs();
            if (e.Page == "流程状态" && _flowStatusPage is FlowStatusView flow) flow.FocusStationByName(e.StationName);
            OperationReturnButton.Visibility = Visibility.Visible;
        }
        /// <summary>返回原操作页，原报警选择和卡片滚动对象仍保留。</summary>
        private void OperationReturn_Click(object sender, RoutedEventArgs e)
        {
            _currentPageResourceKey = _operationReturnPage ?? "设备总览";
            string section = _currentPageResourceKey == "当前报警" ? "Alarms"
                : _currentPageResourceKey == "流程状态" ? "Monitor" : "Home";
            NavigateToSection(section);
            OperationReturnButton.Visibility = Visibility.Collapsed;
            _operationReturnPage = null;
        }
    }
}
