using System;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.VendorApi.ZMotion.Native;

namespace MotionControl.Adapter.MotionCard.ZMotion
{
    /// <summary>正运动适配器中负责轴使能、回零、运动与受控停止的实现部分。</summary>
    public sealed partial class ZMotionCard
    {
        /// <summary>异步读取指定逻辑轴的状态。</summary>
        internal Task<AxisStatus> ReadAxisStatusAsync(
            ZMotionAxis axis,
            CancellationToken cancellationToken)
        {
            return Task.Run(() => ReadAxisStatus(axis), cancellationToken);
        }

        /// <summary>使能或关闭指定逻辑轴的伺服驱动。</summary>
        internal Task EnableServoAsync(ZMotionAxis axis, bool enabled, CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                lock (_nativeLock)
                {
                    var handle = GetConnectedHandle();
                    EnsureAxisParametersApplied(axis);
                    CheckResult(
                        zmcaux.ZAux_Direct_SetAxisEnable(handle, axis.AxisNumber, enabled ? 1 : 0),
                        enabled ? "使能伺服" : "关闭伺服",
                        axis.Name);
                }
            }, cancellationToken);
        }

        /// <summary>按轴配置下发正运动 DATUM 指令，并等待控制器报告回零完成。</summary>
        internal async Task HomeAsync(ZMotionAxis axis, CancellationToken cancellationToken)
        {
            await Task.Run(() =>
            {
                lock (_nativeLock)
                {
                    var handle = GetConnectedHandle();
                    axis.Configuration.ValidateHomeParameters();
                    EnsureAxisParametersApplied(axis);
                    EnsureAxisIdle(handle, axis);
                    EnsureServoEnabled(handle, axis);
                    axis.ClearHomedState();
                    SetHomeProfile(handle, axis);
                    CheckResult(
                        zmcaux.ZAux_Direct_Single_Datum(
                            handle,
                            axis.AxisNumber,
                            axis.Configuration.HomeMode),
                        "启动 DATUM 回零（模式 " + axis.Configuration.HomeMode + "）",
                        axis.Name);
                }
            }, cancellationToken).ConfigureAwait(false);

            try
            {
                await WaitForHomingAsync(axis, cancellationToken).ConfigureAwait(false);
                axis.MarkHomed();
            }
            catch (OperationCanceledException cancellationError)
            {
                await StopHomingAfterFailureAsync(axis, cancellationError).ConfigureAwait(false);
                throw;
            }
            catch (TimeoutException timeoutError)
            {
                await StopHomingAfterFailureAsync(axis, timeoutError).ConfigureAwait(false);
                throw;
            }
            catch (Exception homingError)
            {
                await StopHomingAfterFailureAsync(axis, homingError).ConfigureAwait(false);
                throw;
            }
        }

        /// <summary>确认故障原因已解除后清除轴报警，并在硬限位恢复后要求重新回零。</summary>
        internal Task ResetAxisAlarmAsync(ZMotionAxis axis, CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                lock (_nativeLock)
                {
                    var handle = GetConnectedHandle();
                    EnsureAxisIdle(handle, axis);

                    var statusBeforeReset = ReadAxisStatusBits(handle, axis, "读取报警复位前轴状态");
                    var stopReasonBeforeReset = ReadAxisStopReason(handle, axis, "读取报警复位前停止原因");
                    var activeLimitMessage = BuildActiveLimitMessage(statusBeforeReset);
                    if (!string.IsNullOrEmpty(activeLimitMessage))
                    {
                        throw new InvalidOperationException(
                            "逻辑轴 '" + axis.Name + "' 的" + activeLimitMessage
                            + "仍有效。请先低速向离开限位的方向点动，确认限位解除后再复位。");
                    }

                    if ((statusBeforeReset & AlarmSignalInputMask) != 0
                        && (statusBeforeReset & BusDriveAlarmMask) == 0)
                    {
                        throw new InvalidOperationException(
                            "逻辑轴 '" + axis.Name
                            + "' 的报警信号输入仍有效。请先处理驱动器报警或外部报警信号，再执行复位。");
                    }

                    var hasControllerError = (statusBeforeReset & ActiveAlarmStatusMask) != 0;
                    var hasStopReason = stopReasonBeforeReset != 0;
                    if (!hasControllerError && !hasStopReason)
                    {
                        return;
                    }

                    if (((statusBeforeReset | stopReasonBeforeReset) & BusDriveAlarmMask) != 0)
                    {
                        CheckResult(
                            zmcaux.ZAux_BusCmd_DriveClear(
                                handle,
                                (uint)axis.AxisNumber,
                                CurrentDriveAlarmClearMode),
                            "清除总线伺服当前报警",
                            axis.Name);
                        Thread.Sleep(DriveAlarmResetDelayMilliseconds);
                    }

                    CheckResult(
                        zmcaux.ZAux_Direct_Single_Datum(handle, axis.AxisNumber, 0),
                        "清除控制器轴错误状态（DATUM 模式 0）",
                        axis.Name);

                    if (hasStopReason)
                    {
                        CheckResult(
                            zmcaux.ZAux_Direct_SetParam(handle, "AXIS_STOPREASON", axis.AxisNumber, 0),
                            "清除轴历史停止原因",
                            axis.Name);
                    }

                    var statusAfterReset = ReadAxisStatusBits(handle, axis, "复核报警复位后的轴状态");
                    var stopReasonAfterReset = ReadAxisStopReason(handle, axis, "复核报警复位后的停止原因");
                    if ((statusAfterReset & ActiveAlarmStatusMask) != 0)
                    {
                        throw new InvalidOperationException(
                            "逻辑轴 '" + axis.Name + "' 复位后仍报告报警："
                            + BuildAxisAlarmMessage(statusAfterReset) + "。");
                    }

                    if (stopReasonAfterReset != 0)
                    {
                        throw new InvalidOperationException(
                            "逻辑轴 '" + axis.Name + "' 的历史停止原因仍未清除：0x"
                            + stopReasonAfterReset.ToString("X8") + "。");
                    }

                    if (((statusBeforeReset | stopReasonBeforeReset) & HardLimitStopReasonMask) != 0)
                    {
                        axis.RequireHomingAfterLimitAlarm();
                    }
                }
            }, cancellationToken);
        }

        /// <summary>检查运动参数和软限位，下发定位命令并返回控制器目标位置。</summary>
        internal Task<double> StartMoveAsync(
            ZMotionAxis axis,
            double positionOrDistance,
            AxisMotionProfile profile,
            bool absolute,
            CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                lock (_nativeLock)
                {
                    var handle = GetConnectedHandle();
                    EnsureAxisParametersApplied(axis);
                    if (axis.RequiresHomingAfterLimitAlarm)
                    {
                        throw new InvalidOperationException(
                            "逻辑轴 '" + axis.Name
                            + "' 曾触发正负硬限位，必须先完成回零，才能执行定位运动。");
                    }

                    EnsureServoEnabled(handle, axis);

                    double targetPosition = positionOrDistance;
                    if (!absolute)
                    {
                        float currentPosition = 0;
                        CheckResult(
                            zmcaux.ZAux_Direct_GetDpos(handle, axis.AxisNumber, ref currentPosition),
                            "读取相对运动起始位置",
                            axis.Name);
                        targetPosition = currentPosition + positionOrDistance;
                    }

                    EnsureTargetWithinLimits(axis, targetPosition);
                    SetMotionProfile(handle, axis, profile.Speed, profile.Acceleration, profile.Deceleration);

                    int result = absolute
                        ? zmcaux.ZAux_Direct_Single_MoveAbs(handle, axis.AxisNumber, (float)positionOrDistance)
                        : zmcaux.ZAux_Direct_Single_Move(handle, axis.AxisNumber, (float)positionOrDistance);
                    CheckResult(result, absolute ? "绝对运动" : "相对运动", axis.Name);
                    return targetPosition;
                }
            }, cancellationToken);
        }

        /// <summary>等待定位轴停稳且指令位置到达目标，防止控制卡尚未启动时被误判完成。</summary>
        internal Task WaitForAxisMoveCompletedAsync(
            ZMotionAxis axis,
            double targetPosition,
            int timeoutMilliseconds,
            CancellationToken cancellationToken)
        {
            return Task.Run(async () =>
            {
                var startedAt = DateTime.UtcNow;
                bool observedMotion = false;
                double positionTolerance = 0.5 / axis.Configuration.UnitsPerEngineeringUnit + 0.000001;

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AxisStatus status = await ReadAxisStatusAsync(axis, cancellationToken).ConfigureAwait(false);
                    if (status.Moving)
                    {
                        observedMotion = true;
                    }

                    double commandPosition = status.CommandPosition ?? status.Position;
                    bool targetReached = Math.Abs(commandPosition - (double)(float)targetPosition)
                        <= positionTolerance;

                    if (!status.Moving && targetReached)
                    {
                        return;
                    }

                    double elapsedMilliseconds = (DateTime.UtcNow - startedAt).TotalMilliseconds;
                    if (observedMotion && !status.Moving)
                    {
                        throw new InvalidOperationException(
                            "正运动逻辑轴 '" + axis.Name + "' 已停止，但指令位置 "
                            + commandPosition + " 未到达目标位置 " + targetPosition + "。");
                    }

                    if (!observedMotion && elapsedMilliseconds >= MoveStartTimeoutMilliseconds)
                    {
                        throw new TimeoutException(
                            "正运动逻辑轴 '" + axis.Name + "' 定位命令下发后未观察到轴启动或到达目标位置。目标位置："
                            + targetPosition + "，当前指令位置：" + commandPosition + "。");
                    }

                    if (elapsedMilliseconds >= timeoutMilliseconds)
                    {
                        throw new TimeoutException(
                            "正运动控制卡 '" + ControllerId + "' 逻辑轴 '" + axis.Name
                            + "' 等待定位完成超时，目标位置：" + targetPosition
                            + "，超时毫秒数：" + timeoutMilliseconds + "。");
                    }

                    await Task.Delay(PollIntervalMilliseconds, cancellationToken).ConfigureAwait(false);
                }
            }, cancellationToken);
        }

        /// <summary>检查点动参数、轴状态后下发连续点动命令。</summary>
        internal Task StartJogAsync(
            ZMotionAxis axis,
            MotionDirection direction,
            AxisMotionProfile profile,
            CancellationToken cancellationToken)
        {
            if (profile == null)
            {
                throw new ArgumentNullException(nameof(profile));
            }

            return Task.Run(() =>
            {
                lock (_nativeLock)
                {
                    var handle = GetConnectedHandle();
                    EnsureAxisParametersApplied(axis);
                    var statusBits = ReadAxisStatusBits(handle, axis, "读取点动前限位状态");
                    var positiveLimitMask = PositiveHardLimitMask | PositiveSoftLimitMask;
                    var negativeLimitMask = NegativeHardLimitMask | NegativeSoftLimitMask;
                    if (direction == MotionDirection.Positive && (statusBits & positiveLimitMask) != 0)
                    {
                        throw new InvalidOperationException(
                            "逻辑轴 '" + axis.Name
                            + "' 的正向限位仍有效，不能继续正向点动；请向反方向退出限位。");
                    }

                    if (direction == MotionDirection.Negative && (statusBits & negativeLimitMask) != 0)
                    {
                        throw new InvalidOperationException(
                            "逻辑轴 '" + axis.Name
                            + "' 的负向限位仍有效，不能继续反向点动；请向正方向退出限位。");
                    }

                    var otherActiveAlarmBits = statusBits & ActiveAlarmStatusMask & ~ActiveLimitMask;
                    if (otherActiveAlarmBits != 0)
                    {
                        throw new InvalidOperationException(
                            "逻辑轴 '" + axis.Name + "' 仍存在非限位报警，不能执行点动："
                            + BuildAxisAlarmMessage(statusBits) + "。");
                    }

                    var jogSpeed = profile.Speed;
                    if (axis.RequiresHomingAfterLimitAlarm || (statusBits & ActiveLimitMask) != 0)
                    {
                        var recoverySpeed = axis.Configuration.HomeCreepSpeed;
                        if (double.IsNaN(recoverySpeed)
                            || double.IsInfinity(recoverySpeed)
                            || recoverySpeed <= 0)
                        {
                            throw new InvalidOperationException(
                                "逻辑轴 '" + axis.Name
                                + "' 需要低速退出限位，但轴参数 HomeCreepSpeed 未配置为有效正数。");
                        }

                        jogSpeed = Math.Min(profile.Speed, recoverySpeed);
                    }

                    EnsureServoEnabled(handle, axis);
                    SetMotionProfile(
                        handle,
                        axis,
                        jogSpeed,
                        profile.Acceleration,
                        profile.Deceleration);
                    CheckResult(
                        zmcaux.ZAux_Direct_Single_Vmove(
                            handle,
                            axis.AxisNumber,
                            direction == MotionDirection.Positive ? 1 : -1),
                        "连续点动",
                        axis.Name);
                }
            }, cancellationToken);
        }

        /// <summary>向指定逻辑轴发送受控停止或中止命令。</summary>
        internal Task StopAxisAsync(
            ZMotionAxis axis,
            AxisStopMode stopMode,
            CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                lock (_nativeLock)
                {
                    var handle = GetConnectedHandle();
                    var nativeMode = stopMode == AxisStopMode.Controlled ? 0 : 2;
                    CheckResult(
                        zmcaux.ZAux_Direct_Single_Cancel(handle, axis.AxisNumber, nativeMode),
                        stopMode == AxisStopMode.Controlled ? "减速停止" : "中止运动指令",
                        axis.Name);
                }
            }, cancellationToken);
        }

        /// <summary>轮询指定逻辑轴，直到确认停止或超过等待时限。</summary>
        internal Task WaitForAxisIdleAsync(ZMotionAxis axis, int timeoutMilliseconds, CancellationToken cancellationToken)
        {
            return Task.Run(async () =>
            {
                var startedAt = DateTime.UtcNow;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var status = await ReadAxisStatusAsync(axis, cancellationToken).ConfigureAwait(false);
                    if (!status.Moving)
                    {
                        return;
                    }

                    if ((DateTime.UtcNow - startedAt).TotalMilliseconds >= timeoutMilliseconds)
                    {
                        throw new TimeoutException(
                            "正运动控制卡 '" + ControllerId + "' 逻辑轴 '" + axis.Name
                            + "' 等待停止超时，超时毫秒数：" + timeoutMilliseconds + "。");
                    }

                    await Task.Delay(PollIntervalMilliseconds, cancellationToken).ConfigureAwait(false);
                }
            }, cancellationToken);
        }

        /// <summary>等待正运动 HOMESTATUS 报告回零完成，或在轴停止后报告回零失败。</summary>
        private async Task WaitForHomingAsync(ZMotionAxis axis, CancellationToken cancellationToken)
        {
            var startedAt = DateTime.UtcNow;
            var observedMotion = false;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                uint homeStatus = 0;
                int idle = 1;
                int axisStatus = 0;
                lock (_nativeLock)
                {
                    var handle = GetConnectedHandle();
                    CheckResult(
                        zmcaux.ZAux_Direct_GetHomeStatus(handle, axis.AxisNumber, ref homeStatus),
                        "读取回零结果",
                        axis.Name);
                    CheckResult(
                        zmcaux.ZAux_Direct_GetIfIdle(handle, axis.AxisNumber, ref idle),
                        "读取回零运动状态",
                        axis.Name);
                    CheckResult(
                        zmcaux.ZAux_Direct_GetAxisStatus(handle, axis.AxisNumber, ref axisStatus),
                        "读取回零轴状态",
                        axis.Name);
                }

                if (idle != 0
                    && (homeStatus == HomeStatusSucceeded || homeStatus == HomeStatusCompleted))
                {
                    return;
                }

                if ((axisStatus & HomingFailureStatusMask) != 0)
                {
                    throw new InvalidOperationException(
                        "正运动控制卡 '" + ControllerId + "' 逻辑轴 '" + axis.Name
                        + "' 回零期间出现驱动器、通讯或报警输入异常，轴状态字：0x"
                        + axisStatus.ToString("X8") + "。");
                }

                if (idle == 0)
                {
                    observedMotion = true;
                }
                else if (observedMotion)
                {
                    throw new InvalidOperationException(
                        "正运动控制卡 '" + ControllerId + "' 逻辑轴 '" + axis.Name
                        + "' 已停止，但控制器回零状态不是成功（HOMESTATUS=" + homeStatus + "）。");
                }
                else if ((DateTime.UtcNow - startedAt).TotalMilliseconds >= HomingStartTimeoutMilliseconds)
                {
                    throw new InvalidOperationException(
                        "正运动控制卡 '" + ControllerId + "' 逻辑轴 '" + axis.Name
                        + "' 没有开始回零运动（HOMESTATUS=" + homeStatus + "）。");
                }

                if ((DateTime.UtcNow - startedAt).TotalMilliseconds >= _configuration.MotionTimeoutMilliseconds)
                {
                    throw new TimeoutException(
                        "正运动控制卡 '" + ControllerId + "' 逻辑轴 '" + axis.Name
                        + "' 回零超时，超时毫秒数：" + _configuration.MotionTimeoutMilliseconds + "。");
                }

                await Task.Delay(PollIntervalMilliseconds, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>把该轴配置的回零搜索速度、爬行速度和加减速度写入控制器。</summary>
        private void SetHomeProfile(IntPtr handle, ZMotionAxis axis)
        {
            CheckResult(
                zmcaux.ZAux_Direct_SetSpeed(handle, axis.AxisNumber, (float)axis.Configuration.HomeSearchSpeed),
                "设置回零搜索速度",
                axis.Name);
            CheckResult(
                zmcaux.ZAux_Direct_SetCreep(handle, axis.AxisNumber, (float)axis.Configuration.HomeCreepSpeed),
                "设置回零爬行速度",
                axis.Name);
            CheckResult(
                zmcaux.ZAux_Direct_SetAccel(handle, axis.AxisNumber, (float)axis.Configuration.HomeAcceleration),
                "设置回零加速度",
                axis.Name);
            CheckResult(
                zmcaux.ZAux_Direct_SetDecel(handle, axis.AxisNumber, (float)axis.Configuration.HomeDeceleration),
                "设置回零减速度",
                axis.Name);
        }

        /// <summary>回零取消或超时后请求中止，并确认轴停下；停止失败时保留原始故障。</summary>
        private async Task StopHomingAfterFailureAsync(ZMotionAxis axis, Exception originalError)
        {
            try
            {
                await StopAxisAsync(axis, AxisStopMode.Abort, CancellationToken.None).ConfigureAwait(false);
                using (var stopTimeout = new CancellationTokenSource(StopConfirmationTimeoutMilliseconds))
                {
                    await WaitForAxisIdleAsync(
                        axis,
                        StopConfirmationTimeoutMilliseconds,
                        stopTimeout.Token).ConfigureAwait(false);
                }
            }
            catch (Exception stopError)
            {
                throw new AggregateException(
                    "正运动逻辑轴 '" + axis.Name + "' 回零失败后未能确认停止。",
                    originalError,
                    stopError);
            }
        }

        /// <summary>阻止在工程单位和软件限位应用前操作逻辑轴。</summary>
        private void EnsureAxisParametersApplied(ZMotionAxis axis)
        {
            if (!_axisParametersApplied)
            {
                throw new InvalidOperationException(
                    "正运动控制卡 '" + ControllerId + "' 尚未应用逻辑轴参数，不能操作逻辑轴 '"
                    + axis.Name + "'。请先在轴映射页保存并应用轴参数。");
            }
        }

        /// <summary>读取控制卡状态并确认指定逻辑轴当前空闲。</summary>
        private void EnsureAxisIdle(IntPtr handle, ZMotionAxis axis)
        {
            EnsureAxisIdle(handle, axis.AxisNumber, axis.Name);
        }

        /// <summary>读取控制器状态并确认指定物理轴当前空闲。</summary>
        private void EnsureAxisIdle(IntPtr handle, int axisNumber, string axisName)
        {
            int idle = 1;
            CheckResult(
                zmcaux.ZAux_Direct_GetIfIdle(handle, axisNumber, ref idle),
                "确认轴空闲",
                axisName);
            if (idle == 0)
            {
                throw new InvalidOperationException(
                    "正运动物理轴 '" + axisName + "' 正在运动，不能应用参数或清零坐标。");
            }
        }

        /// <summary>确认指定逻辑轴已使能伺服后才允许下发运动命令。</summary>
        private void EnsureServoEnabled(IntPtr handle, ZMotionAxis axis)
        {
            int enabled = 0;
            CheckResult(
                zmcaux.ZAux_Direct_GetAxisEnable(handle, axis.AxisNumber, ref enabled),
                "确认伺服使能",
                axis.Name);
            if (enabled == 0)
            {
                throw new InvalidOperationException(
                    "正运动逻辑轴 '" + axis.Name + "' 尚未使能，拒绝下发运动命令。");
            }
        }

        /// <summary>检查目标位置是否落在轴配置的仿真软件限位范围内。</summary>
        private void EnsureTargetWithinLimits(ZMotionAxis axis, double targetPosition)
        {
            if (axis.Configuration.SoftLimitEnabled
                && (targetPosition < axis.Configuration.MinimumPosition
                    || targetPosition > axis.Configuration.MaximumPosition))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(targetPosition),
                    "逻辑轴 '" + axis.Name + "' 的目标位置 " + targetPosition
                    + " 超出仿真软限位 [" + axis.Configuration.MinimumPosition + ", "
                    + axis.Configuration.MaximumPosition + "]。");
            }
        }

        /// <summary>将速度、加速度和减速度写入指定轴的运动参数。</summary>
        private void SetMotionProfile(
            IntPtr handle,
            ZMotionAxis axis,
            double speed,
            double acceleration,
            double deceleration)
        {
            CheckResult(
                zmcaux.ZAux_Direct_SetSpeed(handle, axis.AxisNumber, (float)speed),
                "设置运动速度",
                axis.Name);
            CheckResult(
                zmcaux.ZAux_Direct_SetAccel(handle, axis.AxisNumber, (float)acceleration),
                "设置运动加速度",
                axis.Name);
            CheckResult(
                zmcaux.ZAux_Direct_SetDecel(handle, axis.AxisNumber, (float)deceleration),
                "设置运动减速度",
                axis.Name);
        }
    }
}
