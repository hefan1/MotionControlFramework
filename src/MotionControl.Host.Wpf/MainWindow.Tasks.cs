using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace MotionControl.Host.Wpf
{
    /// <summary>任务监控的导航组合，不改变现有整机按钮所控制的 Runtime。</summary>
    public partial class MainWindow
    {
        /// <summary>本机任务目录及独立仿真入口页。</summary>
        private UserControl _taskMonitorPage;

        /// <summary>由 Host 显式接入任务模块。</summary>
        public void SetTaskMonitorPage(UserControl page) { _taskMonitorPage = page; UpdatePageContent(); }

        /// <summary>选择已有关联页并显示待查看的逻辑对象名，不能自动触发该页动作。</summary>
        public void OpenTaskReference(string page, string name, string direction = null)
        {
            if (page == "轴手动" && _manualMotionDebugPage == null)
            {
                MessageBox.Show(GetLocalizedText("任务关联手动页不可用"), GetLocalizedText("任务运行")); return;
            }
            string section = page == "轴映射配置" || page == "IO映射" ? "Settings" : page == "轴手动" ? "Manual" : "Monitor";
            _currentPageResourceKey = page;
            NavigateToSection(section);
            bool found = FocusTaskReference(page, name, direction);
            string suffix = string.IsNullOrEmpty(direction) ? string.Empty : " (" + direction + ")";
            PageDescription.Text += " · " + GetLocalizedText("任务关联对象") + "：" + name + suffix + " · "
                + GetLocalizedText(found ? "任务关联定位成功" : "任务关联定位未找到");
            Activate();
        }

        /// <summary>调用已有页面的只读定位入口，不执行伺服、IO 或运动动作。</summary>
        private bool FocusTaskReference(string page, string name, string direction)
        {
            if (page == "轴映射配置")
            {
                var axisPage = _axisMappingConfigurationPage as MotionControl.Framework.Wpf.Configuration.AxisMappingConfigurationView;
                return axisPage != null && axisPage.FocusAxisByLogicalName(name);
            }
            if (page == "轴手动")
            {
                var manualPage = _manualMotionDebugPage as MotionControl.Framework.Wpf.ManualDebug.ManualMotionDebugView;
                return manualPage != null && manualPage.FocusAxisByLogicalName(name);
            }
            if (page == "IO映射")
            {
                var ioPage = _ioMappingConfigurationPage as MotionControl.Framework.Wpf.Configuration.IoMappingConfigurationView;
                return ioPage != null && ioPage.FocusPointByLogicalName(name, direction == "DO");
            }
            if (page == "设备变量")
            {
                var variablePage = _deviceVariablePage as MotionControl.Framework.Wpf.Variables.DeviceVariableView;
                return variablePage != null && variablePage.FocusVariableByName(name);
            }
            return false;
        }
    }
}
