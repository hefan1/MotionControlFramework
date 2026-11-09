using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Contracts.Clearance;

namespace MotionControl.Framework.Runtime.Machine
{
    /// <summary>清料中断后的现场确认；只解除清料独占，旧生产资源和断点保留。</summary>
    public sealed partial class MachineRuntime
    {
        /// <summary>内部验证入口；设备批准恢复条件及操作员入口完成前不对正式 Host 开放。</summary>
        internal async Task ConfirmControlledClearanceRecoveryAsync(string confirmation, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(confirmation)) throw new ArgumentException("必须填写现场确认说明。", nameof(confirmation));
            await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ControlledClearanceExecutor executor;
                ControlledClearancePlan plan;
                ControlledClearanceProfile profile;
                IControlledClearanceReservation reservation;
                Task operation;
                Task cancellationTask;
                CancellationTokenSource oldCancellation;
                lock (_preparationSync)
                {
                    if (!_clearing || !_preparationStopUnconfirmed) throw new InvalidOperationException("当前没有可核查的清料失败。");
                    if (_clearanceStopRequests != 0) throw new InvalidOperationException("清料停止请求仍在执行或收尾，不能核查恢复。");
                    executor = _clearanceExecutor;
                    plan = _clearancePlan;
                    profile = _clearanceProfile;
                    reservation = _clearanceReservation;
                    operation = _preparationTask;
                    cancellationTask = _clearanceCancellationTask;
                    oldCancellation = _preparationCancellation;
                }
                if (_clearanceInspection == null) throw new InvalidOperationException("清料核查记录未配置，不能解除隔离。");
                if (executor == null || plan == null || profile == null || !ReferenceEquals(profile.Plan, plan)
                    || operation == null || !operation.IsCompleted)
                    throw new InvalidOperationException("清料任务仍在执行或收尾，不能核查恢复。");
                ControlledClearanceRecovery recovery = profile.Recovery;
                lock (_controlOwnerLock)
                    if (_taskEngineOwner != null && reservation == null)
                        throw new InvalidOperationException("任务清料资源凭证未建立或状态未知，不能解除隔离。");
                if (cancellationTask == null || !cancellationTask.IsCompleted)
                    throw new InvalidOperationException("清料取消回调仍在执行，不能核查恢复。");
                if (cancellationTask.IsCanceled || cancellationTask.IsFaulted)
                    throw new InvalidOperationException("清料取消回调失败或被取消，不能直接核查恢复。", cancellationTask.Exception);
                Task pending = executor.InFlightSettlement;
                if (!pending.IsCompleted) throw new InvalidOperationException("原始设备命令或停止任务仍未结束，不能核查恢复。");
                if (!_motionCards.IsConnected || _controlInputIo == null) throw new InvalidOperationException("控制器或逻辑 IO 不可用，不能核查恢复。");
                EnsureAlarmContinuityTrusted();
                await StopEmergencyStopMonitorAsync().ConfigureAwait(false);
                if (_safetyMonitorTask == null || !_safetyMonitorTask.IsCompleted || _safetyMonitorTask.IsFaulted)
                    throw new InvalidOperationException("急停监控仍在收尾或发生故障，不能核查恢复。", _safetyMonitorTask?.Exception);
                if (await UpdateEmergencyStopAlarmAsync(cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("急停输入有效，不能核查恢复。");
                await StopPreparationAxesAsync().ConfigureAwait(false);
                if (!executor.InFlightSettlement.IsCompleted) throw new InvalidOperationException("设备命令仍在收尾，不能解除隔离。");
                await ReadClearanceRecoveryAsync(recovery, cancellationToken).ConfigureAwait(false);
                string observed = await ReadClearanceRecoveryAsync(recovery, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                // 核查只是当时的事实；原 marker、快照及资源隔离记录由 Host 持续保留。
                _clearanceInspection("设备清料配置：" + profile.Id + Environment.NewLine
                    + "现场确认：" + confirmation.Trim() + Environment.NewLine + observed);
                cancellationToken.ThrowIfCancellationRequested();
                await ReadClearanceRecoveryAsync(recovery, cancellationToken).ConfigureAwait(false);
                if (await UpdateEmergencyStopAlarmAsync(cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("复核期间急停输入有效，不能解除隔离。");
                _alarmService.EnsureStopAlarmConditionsRecovered();
                _alarmService.ResetStopAlarms();
                var gate = _alarmService as IMachineAlarmStartGate;
                if (gate == null) throw new InvalidOperationException("报警服务不支持原子检查恢复许可。");
                lock (_statusLock)
                {
                    gate.ExecuteWhenNoStopAlarm(() =>
                    {
                        lock (_preparationSync)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (!_clearing || !ReferenceEquals(executor, _clearanceExecutor)
                                || !ReferenceEquals(operation, _preparationTask) || !ReferenceEquals(profile, _clearanceProfile)
                                || !executor.InFlightSettlement.IsCompleted)
                                throw new InvalidOperationException("清料现场或收尾状态已改变，请重新核查。");
                            if (!ReferenceEquals(cancellationTask, _clearanceCancellationTask) || !cancellationTask.IsCompleted)
                                throw new InvalidOperationException("清料取消回调仍在执行，不能解除隔离。");
                            if (_clearanceStopRequests != 0) throw new InvalidOperationException("清料停止请求仍在执行或收尾，不能解除隔离。");
                            reservation?.ConfirmRecovery();
                            _clearing = false;
                            _preparing = false;
                            _preparationStopUnconfirmed = false;
                            _clearanceStopRequested = false;
                            _preparationCancellation = null;
                            _clearanceReservation = null;
                            _clearanceExecutor = null;
                            _clearancePlan = null;
                            _clearanceProfile = null;
                            _state = MachineRunState.ResetRequired;
                            _lastError = null;
                        }
                    });
                }
                oldCancellation?.Dispose();
                _clearanceExternalCancellation.Dispose();
                InvalidateClearancePreparations("清料现场已核查；旧生产工步不可直接续跑。");
                NotifyClearanceStatusChanged();
            }
            finally { _commandLock.Release(); }
        }

        /// <summary>读取一遍设备条件；每次读取必须达到声明状态，异常或超时一律拒绝。</summary>
        private async Task<string> ReadClearanceRecoveryAsync(ControlledClearanceRecovery recovery, CancellationToken token)
        {
            var details = new List<string>();
            foreach (ClearanceInputCondition input in recovery.Inputs)
            {
                bool value = await ReadClearanceRecoveryValueAsync(_controlInputIo.ReadInputAsync(input.Name, token), token).ConfigureAwait(false);
                if (value != input.Expected) throw new InvalidOperationException("恢复输入不满足：" + input.Name);
                details.Add("DI " + input.Name + "=" + value);
            }
            foreach (ClearanceAxisCondition axis in recovery.Axes)
            {
                AxisStatus status = await ReadClearanceRecoveryValueAsync(_motionCards.GetAxis(axis.Name).ReadStatusAsync(token), token).ConfigureAwait(false);
                if (!AxisStopFeedback.IsConfirmedStopped(status) || double.IsNaN(status.Position) || double.IsInfinity(status.Position)
                    || !status.ServoEnabled || !status.Homed || status.AlarmActive || status.RequiresHomingAfterLimitAlarm
                    || status.PositiveLimitActive != false || status.NegativeLimitActive != false
                    || status.PositiveSoftLimitActive != false || status.NegativeSoftLimitActive != false
                    || status.Position < axis.Minimum || status.Position > axis.Maximum)
                    throw new InvalidOperationException("恢复轴状态或位置不满足：" + axis.Name);
                details.Add("轴 " + axis.Name + "=" + status.Position + "，速度=" + status.Velocity);
            }
            foreach (ClearanceRecoveryOutput output in recovery.Outputs)
            {
                bool value = await ReadClearanceRecoveryValueAsync(_controlInputIo.ReadOutputAsync(output.Name, token), token).ConfigureAwait(false);
                if (value != output.Value) throw new InvalidOperationException("恢复输出不满足：" + output.Name);
                details.Add("DO " + output.Name + "=" + value);
            }
            return "现场采样：" + string.Join("；", details);
        }

        /// <summary>读取超时或取消后只放弃核查，不将仍在途的读取视为成功。</summary>
        private async Task<T> ReadClearanceRecoveryValueAsync<T>(Task<T> reading, CancellationToken token)
        {
            if (reading == null) throw new InvalidOperationException("设备读取未返回任务。");
            if (!ReferenceEquals(await Task.WhenAny(reading, Task.Delay(_pauseTimeout, token)).ConfigureAwait(false), reading))
            { token.ThrowIfCancellationRequested(); throw new TimeoutException("设备恢复反馈读取超时。"); }
            return await reading.ConfigureAwait(false);
        }
    }
}
