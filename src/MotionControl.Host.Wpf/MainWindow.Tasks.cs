using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using MotionControl.Framework.Runtime.Machine;

namespace MotionControl.Host.Wpf
{
    /// <summary>任务配置、任务运行页和 Host 导航的组合入口。</summary>
    public partial class MainWindow
    {
        /// <summary>正式任务运行页，并提供当前设备状态导航。</summary>
        private UserControl _taskMonitorPage;
        /// <summary>任务配置与草稿编辑页面，独立于运行监控。</summary>
        private UserControl _taskConfigurationPage;

        /// <summary>返回任务配置页应显示的当前设备状态，并把启动拦截与整机就绪区分开。</summary>
        internal string GetTaskConfigurationRuntimeStateText()
        {
            if (_machineRuntime == null)
            {
                return null;
            }

            MachineRunState state = _machineRuntime.State;
            if (state == MachineRunState.Ready)
            {
                if (!_automaticStartReadinessKnown)
                {
                    return "正在检查启动条件";
                }

                if (!string.IsNullOrWhiteSpace(_automaticStartBlockReason))
                {
                    return "启动条件未满足";
                }
            }

            return Operation.OperationUi.StateKey(state);
        }

        /// <summary>返回正式 Host 已采集的启动拦截原因，供任务配置页与总览共用。</summary>
        internal string GetTaskConfigurationReadinessReason()
        {
            if (_machineRuntime == null || _machineRuntime.State != MachineRunState.Ready
                || !_automaticStartReadinessKnown)
            {
                return null;
            }

            return _automaticStartBlockReason;
        }

        /// <summary>组合根显式接入任务配置页，不改变已有页面布局。</summary>
        public void SetTaskConfigurationPage(UserControl page) { _taskConfigurationPage = page; UpdatePageContent(); }

        /// <summary>由 Host 显式接入任务模块。</summary>
        public void SetTaskMonitorPage(UserControl page)
        {
            _taskMonitorPage = page;
            var launcher = page as Tasks.TaskMonitorLauncher;
            _equipmentOverviewPage.SetTaskSessionStateProvider(launcher == null ? (Func<bool>)null : () => launcher.HasTaskSession);
            UpdatePageContent();
        }

        /// <summary>任务会话接管或归还设备控制权后刷新顶部整机命令。</summary>
        internal void RefreshMachineStateDisplay()
        {
            UpdateMachineStateDisplay();
        }

        /// <summary>从任务接入状态页返回设备总览。</summary>
        internal void ShowEquipmentOverview()
        {
            SetOperationReturnTarget(_currentSectionKey, _currentPageResourceKey);
            _currentPageResourceKey = "设备总览";
            NavigateToSection("Home");
            Activate();
        }

        /// <summary>进入统一设备准备页，页面读取配置但不自动回零。</summary>
        internal void ShowEquipmentPreparation()
        {
            SetOperationReturnTarget(_currentSectionKey, _currentPageResourceKey);
            _currentPageResourceKey = "设备准备";
            NavigateToSection("Home");
            Activate();
        }

        /// <summary>从任务接入状态页查看当前工位步骤。</summary>
        public void ShowFlowStatus()
        {
            SetOperationReturnTarget(_currentSectionKey, _currentPageResourceKey);
            _currentPageResourceKey = "流程状态";
            NavigateToSection("Monitor");
            Activate();
        }

        /// <summary>查看任务会话，不读取任务或启动设备。</summary>
        public void ShowTaskRun()
        {
            SetOperationReturnTarget(_currentSectionKey, _currentPageResourceKey);
            _currentPageResourceKey = "任务接入状态";
            NavigateToSection("Monitor");
            Activate();
        }

        /// <summary>从任务目录诊断直接打开需要维护的配置页；仅导航，不连接控制卡或保存配置。</summary>
        internal void ShowConfigurationPage(string page)
        {
            if (page != "轴映射配置" && page != "IO映射" && page != "任务配置" && page != "设备变量")
            {
                return;
            }

            SetOperationReturnTarget(_currentSectionKey, _currentPageResourceKey);
            _currentPageResourceKey = page;
            NavigateToSection(page == "设备变量" ? "Monitor" : "Settings");
            Activate();
        }

        /// <summary>选择已有关联页并显示待查看的逻辑对象名，不能自动触发该页动作。</summary>
        public void OpenTaskReference(string page, string name, string direction = null)
        {
            if (page == "轴手动" && _manualMotionDebugPage == null)
            {
                MessageBox.Show(GetLocalizedText("任务关联手动页不可用"), GetLocalizedText("任务接入状态")); return;
            }
            string section = page == "轴映射配置" || page == "IO映射" ? "Settings" : page == "轴手动" ? "Manual" : "Monitor";
            SetOperationReturnTarget(_currentSectionKey, _currentPageResourceKey);
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
