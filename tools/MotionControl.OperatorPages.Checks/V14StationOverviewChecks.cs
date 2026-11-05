using System;
using System.Windows;
using System.Windows.Controls;
using MotionControl.Host.Wpf.Operation;
using MotionControl.Framework.Runtime.Machine;

namespace MotionControl.OperatorPages.Checks
{
    /// <summary>独立核对总览选择、缺字段及只读导航；由检查入口显式调用。</summary>
    internal static class V14StationOverviewChecks
    {
        /// <summary>验证双工位、空步骤、输入定位和断连不显示旧步骤。</summary>
        internal static void Run()
        {
            var snapshot = new OperationSnapshot { IsPreview = true, HasRuntime = true, State = MachineRunState.Running };
            snapshot.Stations.Add(new StationOverviewData { Id = "a", Name = "工位", WaitingInputName = "Input_01" });
            snapshot.Stations.Add(new StationOverviewData { Id = "b", Name = "工位", CurrentStep = "真实动作" });
            var view = new EquipmentOverviewView();
            view.SetPreview(snapshot);
            var table = (ListView)view.FindName("StationTable");
            var columns = ((GridView)table.View).Columns;
            if (columns.Count != 5 || !Equals(columns[0].Header, "工位 / 模组") || !Equals(columns[4].Header, "读取质量"))
                throw new InvalidOperationException("总览未显示真实五列表头");
            if (ScrollViewer.GetVerticalScrollBarVisibility(table) != ScrollBarVisibility.Auto || ScrollViewer.GetHorizontalScrollBarVisibility(table) != ScrollBarVisibility.Auto)
                throw new InvalidOperationException("窄窗或多工位不能滚动");
            if (table.Items.Count != 2) throw new InvalidOperationException("总览未采用实际工位集合");
            if (!((TextBlock)view.FindName("SelectedStationText")).Text.Contains("未提供")) throw new InvalidOperationException("缺步骤未标未提供");
            OperatorNavigationEventArgs route = null;
            view.NavigationRequested += (sender, args) => route = args;
            ((Button)view.FindName("SelectedInputButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (route == null || route.IoPointName != "Input_01") throw new InvalidOperationException("丢失逻辑输入导航参数");
            table.SelectedIndex = 1;
            var previousRow = table.Items[1];
            var previousSelection = table.SelectedItem;
            snapshot.Stations[1].CurrentStep = "新动作";
            view.SetPreview(snapshot);
            if (!ReferenceEquals(previousRow, table.Items[1]) || !ReferenceEquals(previousSelection, table.SelectedItem))
                throw new InvalidOperationException("稳定工位刷新重建了行或丢失选择");
            if (!((TextBlock)view.FindName("SelectedStationText")).Text.Contains("新动作"))
                throw new InvalidOperationException("稳定行详情未更新");
            if (((Button)view.FindName("SelectedInputButton")).Visibility != Visibility.Collapsed) throw new InvalidOperationException("无输入仍显示定向入口");
            view.SetLanguage("en-US");
            if (!Equals(columns[0].Header, "Station / module") || !Equals(columns[4].Header, "Read quality"))
                throw new InvalidOperationException("英文总览表头未切换");
            if (!ReferenceEquals(previousSelection, table.SelectedItem)) throw new InvalidOperationException("语言切换丢失真实选择");
            view.SetLanguage("zh-CN");
            view.SetConnectionStatus(false, "断连");
            if (((Button)view.FindName("SelectedFlowButton")).IsEnabled) throw new InvalidOperationException("断连仍允许工位定位");
        }
    }
}
