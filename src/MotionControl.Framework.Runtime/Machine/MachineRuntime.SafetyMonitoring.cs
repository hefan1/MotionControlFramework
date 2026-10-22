using System;
using System.Threading;
using System.Threading.Tasks;

using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Framework.Contracts.Safety;

namespace MotionControl.Framework.Runtime.Machine
{
    /// <summary>单机运行时中的软件急停状态读取与监控。</summary>
    public sealed partial class MachineRuntime
    {
        /// <summary>读取已配置的急停输入并更新停机报警；未配置时关闭软件监控且不生成急停报警。</summary>
        private async Task<bool> UpdateEmergencyStopAlarmAsync(CancellationToken cancellationToken)
        {
            if (!IsEmergencyStopMonitoringConfigured())
            {
                _alarmService.ReportCondition(
                    EmergencyStopAlarm,
                    "整机急停输入",
                    false,
                    "软件急停 DI 未配置，软件状态监控未启用；设备安全停机仍由硬件安全回路负责。");
                return false;
            }

            bool emergencyStopActive;
            try
            {
                emergencyStopActive = await _safetyStatus
                    .ReadEmergencyStopActiveAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception readError)
            {
                _alarmService.ReportCondition(
                    EmergencyStopAlarm,
                    "整机急停输入",
                    true,
                    "已配置的急停 DI 映射无效或读取失败，按软件联锁配置禁止动作。原因：" + readError.Message);
                throw new InvalidOperationException("已配置的急停 DI 状态读取失败，软件联锁已禁止动作。", readError);
            }

            _alarmService.ReportCondition(
                EmergencyStopAlarm,
                "整机急停输入",
                emergencyStopActive,
                emergencyStopActive ? "急停输入有效，请释放急停并检查安全回路。" : "急停输入已恢复。");
            return emergencyStopActive;
        }

        /// <summary>启动流程期间周期读取急停输入，触发时借助停机报警暂停整机流程。</summary>
        private void StartEmergencyStopMonitor()
        {
            if (!IsEmergencyStopMonitoringConfigured())
            {
                throw new InvalidOperationException("自动流程运行期间必须保持软件急停 DI 配置有效。");
            }

            if (_safetyMonitorTask != null && !_safetyMonitorTask.IsCompleted)
            {
                throw new InvalidOperationException("上一次急停状态监控尚未结束，不能重复启动设备。");
            }

            _safetyMonitorCancellation = new CancellationTokenSource();
            _safetyMonitorTask = MonitorEmergencyStopAsync(_safetyMonitorCancellation.Token);
        }

        /// <summary>在整机运行期间监控急停输入；读取异常时也触发停机报警。</summary>
        private async Task MonitorEmergencyStopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (State == MachineRunState.Running)
                {
                    await Task.Delay(_safetyPollInterval, cancellationToken).ConfigureAwait(false);
                    if (State != MachineRunState.Running)
                    {
                        return;
                    }

                    try
                    {
                        var active = await _safetyStatus
                            .ReadEmergencyStopActiveAsync(cancellationToken)
                            .ConfigureAwait(false);
                        _alarmService.ReportCondition(
                            EmergencyStopAlarm,
                            "整机急停输入",
                            active,
                            active ? "运行期间检测到急停输入有效。" : "急停输入已恢复。");
                        if (active)
                        {
                            return;
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception readError)
                    {
                        _alarmService.ReportCondition(
                            EmergencyStopAlarm,
                            "整机急停输入",
                            true,
                            "运行期间已配置的急停 DI 被移除、映射无效或读取失败，按软件联锁配置停机。原因：" + readError.Message);
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 正常暂停时取消轮询，不把监控取消当作设备故障。
            }
        }

        /// <summary>整机流程退出后取消并回收急停状态监控任务。</summary>
        private async Task StopEmergencyStopMonitorAsync()
        {
            var cancellation = _safetyMonitorCancellation;
            if (cancellation == null)
            {
                return;
            }

            cancellation.Cancel();
            try
            {
                await _safetyMonitorTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 监控任务因整机暂停而取消，属于正常结束。
            }
            finally
            {
                if (ReferenceEquals(_safetyMonitorCancellation, cancellation))
                {
                    _safetyMonitorCancellation = null;
                    cancellation.Dispose();
                }
            }
        }

        /// <summary>判断安全状态来源是否声明了有效配置；普通安全状态来源默认始终可用。</summary>
        private bool IsEmergencyStopMonitoringConfigured()
        {
            var configurableStatus = _safetyStatus as IConfigurableMachineSafetyStatus;
            return configurableStatus == null || configurableStatus.IsConfigured;
        }
    }
}
