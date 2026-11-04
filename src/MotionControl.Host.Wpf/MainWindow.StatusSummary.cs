using System;
using System.Linq;
using System.Windows.Media;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Framework.Contracts.Flow;

namespace MotionControl.Host.Wpf
{
    public partial class MainWindow
    {
        /// <summary>分别读取报警和任务内存快照；单个来源失败不覆盖其他已知异常，正文与详情共用一次采样。</summary>
        private void RefreshImportantExceptionSummary()
        {
            if (ImportantExceptionSummary == null) return;
            bool english = string.Equals(CurrentLanguage, "en-US", StringComparison.OrdinalIgnoreCase);
            MachineAlarmRecord[] alarms = null;
            string alarmReadError = null;
            try
            {
                alarms = _machineRuntime?.AlarmService.Alarms?.ToArray();
            }
            catch (Exception error)
            {
                alarmReadError = (english ? "Alarm read failed: " : "报警读取失败：") + error.Message;
            }
            string taskBlock = null;
            string resourceReadError = null;
            bool resourcesUnknown = false;
            try
            {
                if (_machineRuntime?.ControlOwner == MachineControlOwner.TaskEngine)
                {
                    var launcher = _taskMonitorPage as Tasks.TaskMonitorLauncher;
                    resourcesUnknown = launcher == null;
                    taskBlock = launcher?.ReadOverviewSafetySummary();
                }
            }
            catch (Exception error)
            {
                resourcesUnknown = true;
                resourceReadError = (english ? "Resource read failed: " : "资源读取失败：") + error.Message;
            }
            bool settlementPending = false;
            string settlementError = null;
            try
            {
                settlementPending = _motionCardSessionConnected && _machineRuntime != null
                    && _machineRuntime.ControlOwner == MachineControlOwner.StationFlows
                    && _machineRuntime.State == MotionControl.Framework.Runtime.Machine.MachineRunState.Ready
                    && FooterExceptionText.NeedsSettlementNotice(_machineRuntime.ControlOwner, true,
                        _machineRuntime.IsStationFlowSettlementConfirmed);
            }
            catch (Exception error)
            {
                settlementError = (english ? "Flow settlement unknown: " : "流程收尾状态未知：") + error.Message;
            }
            bool runtimeKnown = _machineRuntime != null;
            bool connected = _motionCardSessionConnected;
            bool ioFailed = _latestVisibleDiReadFailed == true || _latestVisibleDoReadFailed == true;
            bool checking = !_automaticStartReadinessKnown;
            string recovery = _machineRuntime?.RecoveryBlockReason;
            string block = string.IsNullOrWhiteSpace(recovery) ? _automaticStartBlockReason : recovery;

            ImportantExceptionSummary.Text = FooterExceptionText.Format(alarms, runtimeKnown, connected,
                ioFailed, checking, block, taskBlock, english, false, resourcesUnknown, settlementPending, settlementError != null);
            string detail = FooterExceptionText.Format(alarms, runtimeKnown, connected,
                ioFailed, checking, block, taskBlock, english, true, resourcesUnknown, settlementPending, settlementError != null);
            if (settlementError != null) detail += "\n" + settlementError;
            if (alarmReadError != null) detail += "\n" + alarmReadError;
            if (resourceReadError != null) detail += "\n" + resourceReadError;
            ImportantExceptionSummary.ToolTip = detail;
            bool hasStopAlarm = alarms != null && alarms.Any(alarm => alarm.RequiresReset);
            bool clear = ImportantExceptionSummary.Text == (english ? "No pending alarms" : "无待处理报警");
            ImportantExceptionSummary.Foreground = new SolidColorBrush(hasStopAlarm
                ? Color.FromRgb(178, 59, 59) : clear ? Color.FromRgb(89, 107, 125) : Color.FromRgb(139, 75, 33));
        }
    }
}
