using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Framework.Contracts.Flow;

namespace MotionControl.Framework.Runtime.Machine
{
    /// <summary>单机运行时中的流程结束、报警响应和状态转换处理。</summary>
    public sealed partial class MachineRuntime
    {
        /// <summary>观察整机流程结束或故障，并更新设备运行状态。</summary>
        private async Task ObserveFlowRunAsync(Task runTask)
        {
            try
            {
                await runTask.ConfigureAwait(false);
                if (State == MachineRunState.Running)
                {
                    SetStatus(
                        MachineRunState.Alarmed,
                        new InvalidOperationException("整机运行期间工位流程调度意外结束。"));
                }
                else if (State == MachineRunState.Pausing)
                {
                    SetPausedOrAlarmed();
                }
            }
            catch (Exception flowError)
            {
                if (State == MachineRunState.Running || State == MachineRunState.Pausing)
                {
                    if (_alarmService.HasLatchedStopAlarm)
                    {
                        SetStatus(
                            MachineRunState.Alarmed,
                            CreateStopAlarmError(GetFirstStopAlarm(), flowError));
                        return;
                    }

                    SetStatus(
                        MachineRunState.Alarmed,
                        new InvalidOperationException("工位流程发生错误，整机已暂停。", flowError));
                }
            }
            finally
            {
                await StopEmergencyStopMonitorAsync().ConfigureAwait(false);
            }
        }

        /// <summary>收到停机报警后立即请求所有工位流程暂停。</summary>
        private void OnStopAlarmRaised(object sender, MachineAlarmRaisedEventArgs eventArgs)
        {
            if (!_alarmService.HasLatchedStopAlarm)
            {
                return;
            }

            // 停机报警可能改变机构或现场条件，之前的准备标记沿用资格立即失效。
            lock (_preparationSync) _preparationRecordReusable = false;
            var alarmError = CreateStopAlarmError(eventArgs.Alarm, null);
            if (IsPreparing)
            {
                SetStatus(MachineRunState.Alarmed, alarmError);
                // 先同步取消已发布的准备许可；停止命令随后等待命令锁时不允许准备继续发动作。
                CancellationTokenSource preparation = RequestPreparationStop();
                if (preparation != null) _ = StopPreparationAfterAlarmAsync(preparation);
                return;
            }
            var shouldPauseFlows = false;
            bool taskEngineOwnsDevice = ControlOwner == MachineControlOwner.TaskEngine;

            lock (_statusLock)
            {
                if (_state == MachineRunState.Disconnected)
                {
                    return;
                }

                if (taskEngineOwnsDevice)
                {
                    _state = MachineRunState.Alarmed;
                    _lastError = alarmError;
                }
                else if (_state == MachineRunState.Running || _state == MachineRunState.Pausing)
                {
                    _state = MachineRunState.Pausing;
                    shouldPauseFlows = true;
                }
                else
                {
                    _state = MachineRunState.Alarmed;
                }

                _lastError = alarmError;
            }

            Exception pauseRequestError = null;
            long pausedGeneration = -1;
            if (shouldPauseFlows)
            {
                try
                {
                    pausedGeneration = _flowScheduler.RequestPause();
                }
                catch (Exception pauseError)
                {
                    pauseRequestError = new InvalidOperationException(
                        "停机报警触发，但请求工位流程暂停时发生错误。",
                        pauseError);
                    lock (_statusLock)
                    {
                        _lastError = pauseRequestError;
                    }
                }
            }

            RaiseStatusChanged();

            if (taskEngineOwnsDevice)
            {
                return;
            }

            if (shouldPauseFlows)
            {
                _ = MarkPauseTimeoutIfStillRunningAsync(pausedGeneration);
            }

            if (pauseRequestError != null)
            {
                throw pauseRequestError;
            }
        }

        /// <summary>停机报警触发后，若工位超过暂停时限仍未退出，则把整机状态置为报警。</summary>
        private async Task MarkPauseTimeoutIfStillRunningAsync(long generation)
        {
            await Task.Delay(_pauseTimeout).ConfigureAwait(false);
            lock (_statusLock)
            {
                if (_state != MachineRunState.Pausing || !_flowScheduler.IsRunActive(generation))
                {
                    return;
                }

                _state = MachineRunState.Alarmed;
                _lastError = new TimeoutException(
                    "停机报警后等待工位流程退出超时；工位仍在后台运行，不能复位或断开运动控制卡。");
            }

            RaiseStatusChanged();
        }

        /// <summary>将报警服务的变化事件转发给界面。</summary>
        private void OnAlarmsChanged(object sender, EventArgs eventArgs)
        {
            var alarmsChanged = AlarmsChanged;
            if (alarmsChanged != null)
            {
                alarmsChanged(this, EventArgs.Empty);
            }
        }

        /// <summary>检查启动条件并把设备状态切换到运行或报警。</summary>
        private void SetRunningIfNoStopAlarm()
        {
            var hasStopAlarm = false;

            lock (_statusLock)
            {
                if (_state != MachineRunState.Ready)
                {
                    throw new InvalidOperationException(
                        "启动前必须先成功复位设备。当前状态：" + _state + "。");
                }

                if (_alarmService.HasLatchedStopAlarm)
                {
                    hasStopAlarm = true;
                    _state = MachineRunState.Alarmed;
                    _lastError = CreateStopAlarmError(GetFirstStopAlarm(), null);
                }
                else
                {
                    _state = MachineRunState.Running;
                    _lastError = null;
                }
            }

            RaiseStatusChanged();

            if (hasStopAlarm)
            {
                throw new InvalidOperationException("存在尚未复位的停机报警，设备不能启动。");
            }
        }

        /// <summary>无未复位停机报警时把设备状态切换到就绪。</summary>
        private void SetReadyIfNoStopAlarm()
        {
            var hasStopAlarm = false;

            lock (_statusLock)
            {
                if (_alarmService.HasLatchedStopAlarm)
                {
                    hasStopAlarm = true;
                    _state = MachineRunState.Alarmed;
                    _lastError = CreateStopAlarmError(GetFirstStopAlarm(), null);
                }
                else
                {
                    _state = MachineRunState.Ready;
                    _lastError = null;
                }
            }

            RaiseStatusChanged();

            if (hasStopAlarm)
            {
                throw new InvalidOperationException("复位期间出现新的停机报警，设备仍处于报警状态。");
            }
        }

        /// <summary>流程暂停完成后，根据停机报警状态更新设备状态。</summary>
        private void SetPausedOrAlarmed()
        {
            lock (_statusLock)
            {
                if (_state != MachineRunState.Pausing)
                {
                    return;
                }

                if (_alarmService.HasLatchedStopAlarm)
                {
                    _state = MachineRunState.Alarmed;
                    _lastError = CreateStopAlarmError(GetFirstStopAlarm(), null);
                }
                else
                {
                    _state = MachineRunState.Paused;
                    _lastError = null;
                }
            }

            RaiseStatusChanged();
        }

        /// <summary>返回当前最先找到的停机报警记录。</summary>
        private MachineAlarmRecord GetFirstStopAlarm()
        {
            return _alarmService.Alarms.FirstOrDefault(
                alarm => alarm.Definition.Kind == MachineAlarmKind.StopMachine);
        }

        /// <summary>根据停机报警记录创建带设备定位信息的异常。</summary>
        private static Exception CreateStopAlarmError(MachineAlarmRecord alarm, Exception innerError)
        {
            if (alarm == null)
            {
                return new InvalidOperationException("存在尚未复位的停机报警，整机不能继续运行。", innerError);
            }

            var message = "停机报警 '" + alarm.SourceName + "/" + alarm.Definition.Code
                + "'：" + alarm.Definition.Message + "。整机已请求暂停。";
            if (!string.IsNullOrWhiteSpace(alarm.Detail))
            {
                message += " " + alarm.Detail;
            }

            return new InvalidOperationException(message, innerError);
        }
    }
}
