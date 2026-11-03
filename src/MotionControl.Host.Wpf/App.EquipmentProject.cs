using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Runtime.Machine;
using MotionControl.Host.Wpf.Configuration;
using MotionControl.TaskEngine.Configuration;

namespace MotionControl.Host.Wpf
{
    /// <summary>设备工程服务、启动恢复和正式 Host 的组合入口。</summary>
    public partial class App
    {
        /// <summary>单机工程包服务；与设备配置根目录和设备编号绑定。</summary>
        private MachineProjectPackageService _equipmentProjectService;
        /// <summary>本进程的配置已替换，必须正常退出后重新创建运行时。</summary>
        private bool _equipmentProjectRestartRequired;

        /// <summary>复用 Host 支持的设备定义和控制卡后端，不接受未安装的代码或适配器。</summary>
        private MachineProjectPackageService GetEquipmentProjectService()
        {
            if (_equipmentProjectService == null)
                _equipmentProjectService = new MachineProjectPackageService(_configurationStore, MachineId,
                    HostEquipmentSelection.SupportedDefinitions, HostMotionCardBackendSelection.SupportedBackends,
                    null, selection => HostEquipmentProjectCardValidation.Validate(selection, _configurationStore, MachineId));
            return _equipmentProjectService;
        }

        /// <summary>任何配置或控制器加载前检查中断应用；恢复失败或人员取消时禁止继续启动。</summary>
        private bool RecoverEquipmentProjectAtStartup()
        {
            try
            {
                MachineProjectPackageService service = GetEquipmentProjectService();
                if (!service.HasPendingApply) return true;
                service.RecoverPendingApply(() => _cardConnection == null && _machineRuntime == null);
                if (service.HasPendingApply) throw new InvalidOperationException("中断记录仍然存在，不能使用设备配置。");
                MessageBox.Show("上次设备工程应用未完成，已从导入前的备份恢复配置。请核查设备工程后再连接控制器。",
                    "设备工程已恢复", MessageBoxButton.OK, MessageBoxImage.Information);
                return true;
            }
            catch (Exception error)
            {
                MessageBox.Show("设备工程恢复失败，软件不会连接控制器。请保留备份和恢复记录并检查存储。\n" + error.Message,
                    "设备工程恢复失败", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        /// <summary>按需创建工程页面并与主窗口当前语言同步。</summary>
        internal EquipmentProjectView CreateEquipmentProjectPage()
        {
            var page = new EquipmentProjectView(GetEquipmentProjectService(), GetEquipmentProjectApplyBlockedReason,
                AcquireEquipmentProjectApplyGateAsync, RequireEquipmentProjectRestart);
            page.SetLanguage(_mainWindow.CurrentLanguage);
            _mainWindow.LanguageChanged += (sender, args) => page.SetLanguage(_mainWindow.CurrentLanguage);
            return page;
        }

        /// <summary>在 UI 线程复查设备、任务与草稿状态；未知或尚未收尾不能应用。</summary>
        private string GetEquipmentProjectApplyBlockedReason(bool applying)
        {
            try { return ReadEquipmentProjectApplyBlockedReason(applying); }
            catch (Exception error)
            {
                return ProjectText("无法确认设备工程应用条件：", "Cannot verify project application conditions: ") + error.Message;
            }
        }

        /// <summary>读取当前门禁；调用方将任何未预期的读取异常转换为禁止应用。</summary>
        private string ReadEquipmentProjectApplyBlockedReason(bool applying)
        {
            if (_equipmentProjectRestartRequired) return ProjectText("设备工程已经变更，请先关闭并重新打开软件。", "Equipment project changed. Close and reopen the application.");
            if (_cardConnection == null || _mainWindow == null || _machineRecoveryCoordinator == null)
                return ProjectText("设备连接或恢复状态尚未就绪，不能应用工程。", "Connection or recovery status is not available; the project cannot be applied.");
            if (!applying && GetEquipmentProjectService().HasPendingApply)
                return ProjectText("上次设备工程应用未完成，请重新启动软件恢复配置。", "The previous project application is incomplete. Restart to recover the configuration.");
            string taskPendingPath = Path.Combine(_configurationStore.GetMachineDirectory(MachineId), "Drafts", "TaskApplyPending.json");
            bool taskPending;
            try
            {
                File.GetAttributes(taskPendingPath);
                taskPending = true;
            }
            catch (FileNotFoundException) { taskPending = false; }
            catch (DirectoryNotFoundException) { taskPending = false; }
            catch (Exception error) { return ProjectText("无法确认任务配置恢复状态：", "Cannot verify task recovery status: ") + error.Message; }
            if (taskPending) return ProjectText("上次任务应用未完成，请先在任务配置页恢复。", "A task configuration application is incomplete. Recover it from the task configuration page first.");
            if (_cardConnection.IsConnected || _cardConnection.ConnectedMotionCard != null)
                return ProjectText("控制器仍连接，请先停止设备并安全断开控制器。", "Controller is connected. Stop the equipment and safely disconnect it first.");
            if (_mainWindow.HasMachineCommandInProgress)
                return ProjectText("整机命令仍在执行，请等待收尾。", "A machine command is still settling.");
            if (_taskMonitorLauncher == null || _taskMonitorLauncher.HasTaskSession || _taskMonitorLauncher.IsCommandInProgress)
                return ProjectText("任务会话仍在执行或尚未关闭，请先停止并关闭任务会话。", "A task session is active or still closing. Stop and close it first.");
            if (_machineRuntime != null && (_machineRuntime.State != MachineRunState.Disconnected
                || _machineRuntime.IsPreparing || _machineRuntime.IsClearing
                || _machineRuntime.GetTaskResourceRecoveryState() != TaskResourceRecoveryState.Clear))
                return ProjectText("设备动作、清料或资源状态尚未确认，不能应用工程。", "Equipment motion, clearing or resources are not confirmed.");
            string recovery = _machineRecoveryCoordinator.GetAutomaticStartBlockReason(_machineRuntime);
            if (!string.IsNullOrWhiteSpace(recovery)) return ProjectText("设备恢复状态待确认，不能应用工程：", "Recovery status requires inspection: ") + recovery;
            if (_taskConfigurationPage != null && _taskConfigurationPage.HasUnsavedChanges)
                return ProjectText("任务配置有未保存的编辑，请先保存草稿。", "Task changes are not saved. Save the draft first.");
            if (_alarmCatalogPage != null && _alarmCatalogPage.HasUnsavedChanges)
                return ProjectText("报警库有未保存的编辑，请先保存。", "Alarm changes are not saved. Save them first.");
            return null;
        }

        /// <summary>按主窗口语言显示工程应用的阻断原因。</summary>
        private string ProjectText(string chinese, string english)
        { return _mainWindow != null && string.Equals(_mainWindow.CurrentLanguage, "en-US", StringComparison.OrdinalIgnoreCase) ? english : chinese; }

        /// <summary>与控制卡连接使用同一排他门，取得许可后页面再次检查设备状态。</summary>
        private Task<IDisposable> AcquireEquipmentProjectApplyGateAsync()
        { return HostProjectOperationGate.EnterImportAsync(CancellationToken.None); }

        /// <summary>应用后在释放排他门前锁定连接，要求通过正常关闭流程重启。</summary>
        private void RequireEquipmentProjectRestart()
        {
            _equipmentProjectRestartRequired = true;
            HostProjectOperationGate.RequireRestart();
            _mainWindow.RequireRestartForEquipmentProject();
        }
    }
}
