using System;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Contracts.Flow;

namespace MotionControl.Framework.Runtime.Machine
{
    /// <summary>把图形化任务执行纳入整机运行状态、急停监控和手动操作互斥。</summary>
    public sealed partial class MachineRuntime
    {
        /// <summary>确认任务执行入口持有设备并启动运行期间的急停监控。</summary>
        public async Task BeginTaskEngineRunAsync(CancellationToken cancellationToken)
        {
            await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (ControlOwner != MachineControlOwner.TaskEngine)
                    throw new InvalidOperationException("当前设备控制权不属于图形化任务执行入口。");
                EnsureOneOfStates(new[] { MachineRunState.Ready }, "设备必须先复位到就绪状态，再启动任务。");
                if (_flowScheduler.IsRunning)
                    throw new InvalidOperationException("C# 工位流程仍在运行，任务执行入口不能启动。");
                EnsureNoManualOperationsInProgress("启动任务");
                _alarmService.EnsureStopAlarmConditionsRecovered();
                if (!IsEmergencyStopMonitoringConfigured())
                    throw new InvalidOperationException("任务自动运行必须配置有效的急停输入。");
                if (await UpdateEmergencyStopAlarmAsync(cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("急停输入仍处于触发状态，任务不能启动。");

                foreach (IAxis axis in _motionCards.Axes)
                {
                    AxisStatus status = await axis.ReadStatusAsync(cancellationToken).ConfigureAwait(false);
                    if (status.Moving)
                        throw new InvalidOperationException("逻辑轴仍在运动，不能启动任务：" + axis.Name + "。");
                    if (status.AlarmActive)
                        throw new InvalidOperationException("逻辑轴存在报警，不能启动任务：" + axis.Name + "，" + status.AlarmMessage);
                }

                SetRunningIfNoStopAlarm();
                StartEmergencyStopMonitor();
            }
            catch (Exception startError)
            {
                await StopEmergencyStopMonitorAsync().ConfigureAwait(false);
                if (_alarmService.HasLatchedStopAlarm)
                    SetStatus(MachineRunState.Alarmed, startError);
                else if (State == MachineRunState.Running)
                    SetStatus(MachineRunState.Ready, startError);
                throw;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        /// <summary>任务暂停或终止并停稳后，更新整机状态并停止急停轮询。</summary>
        public async Task FinishTaskEngineRunAsync(bool faulted, Exception error)
        {
            await _commandLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (ControlOwner != MachineControlOwner.TaskEngine)
                    throw new InvalidOperationException("当前设备控制权不属于图形化任务执行入口，不能结束任务运行状态。");
                await StopEmergencyStopMonitorAsync().ConfigureAwait(false);

                if (State == MachineRunState.Alarmed || _alarmService.HasLatchedStopAlarm || faulted)
                    SetStatus(MachineRunState.Alarmed, error);
                else if (State == MachineRunState.Running || State == MachineRunState.Pausing)
                    SetStatus(MachineRunState.Paused, error);
            }
            finally
            {
                _commandLock.Release();
            }
        }
    }
}
