using System;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.Motion;

namespace MotionControl.Framework.Runtime.Machine
{
    /// <summary>区分暂停和结束本次自动生产；保留旧工步及现场资源供核查。</summary>
    public sealed partial class MachineRuntime
    {
        /// <summary>本连接会话中已请求停止生产，不能未经明确核查复位并沿用旧工步。</summary>
        private bool _productionStopReviewRequired;

        /// <summary>最近一次停止反馈读取；超时后仍须等原始调用退出，不能抢先复位。</summary>
        private Task _stopFeedbackInFlight = Task.CompletedTask;

        /// <summary>停止请求之后，旧工步是否仍需操作员明确核查。</summary>
        public bool ProductionStopReviewRequired
        {
            get { lock (_statusLock) return _productionStopReviewRequired; }
        }

        /// <summary>冻结启动资格，受控暂停当前入口并复核轴和资源；不释放输出或清空工步。</summary>
        public async Task StopAutomaticProductionAsync(CancellationToken cancellationToken)
        {
            if (IsPreparing)
            {
                await StopPreparationAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (IsShutdownInProgress) throw new InvalidOperationException("设备正在退出收尾，不能另行结束生产。");
                EnsureOneOfStates(new[] { MachineRunState.Running, MachineRunState.Pausing, MachineRunState.Paused },
                    "只有正在运行或已暂停的生产可以请求停止。");
                lock (_statusLock) _productionStopReviewRequired = true;
            }
            finally { _commandLock.Release(); }

            try
            {
                if (State == MachineRunState.Running || State == MachineRunState.Pausing)
                    await PauseAsync(cancellationToken).ConfigureAwait(false);
                await ConfirmProductionStoppedAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                // 发生取消或异常也保留停止核查门禁，不能把未确认的收尾当成成功。
                if (State != MachineRunState.Disconnected)
                    SetStatus(MachineRunState.Alarmed, new InvalidOperationException("生产停止未确认，保留工步和现场资源。", error));
                throw;
            }
        }

        /// <summary>只有流程已暂停、资源可确认且所有映射轴均反馈停稳后才显示已停止。</summary>
        private async Task ConfirmProductionStoppedAsync(CancellationToken cancellationToken)
        {
            await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                EnsureState(MachineRunState.Paused, "生产尚未完成暂停或存在报警，不能确认停止。");
                EnsureNoManualOperationsInProgress("确认生产停止");
                if (_flowScheduler.IsRunning || !_flowScheduler.IsSettlementConfirmed)
                    throw new InvalidOperationException("工位动作仍在收尾，不能确认停止。");
                if (ControlOwner == MachineControlOwner.TaskEngine)
                {
                    IAutomaticProductionPause taskPause;
                    lock (_controlOwnerLock) taskPause = _taskEngineOwner as IAutomaticProductionPause;
                    if (taskPause == null || !taskPause.IsAutomaticPauseConfirmed)
                        throw new InvalidOperationException("任务执行器未确认暂停及动作收尾，不能确认生产停止。");
                    TaskResourceRecoveryState resources = GetTaskResourceRecoveryState();
                    if (resources == TaskResourceRecoveryState.Blocked || resources == TaskResourceRecoveryState.Unknown)
                        throw new InvalidOperationException("任务资源待确认或无法读取，不能确认生产停止。");
                }
                foreach (IAxis axis in _motionCards.Axes)
                {
                    AxisStatus status = await ReadStopFeedbackAsync(axis, cancellationToken).ConfigureAwait(false);
                    if (!AxisStopFeedback.IsConfirmedStopped(status))
                        throw new InvalidOperationException("逻辑轴 '" + axis.Name + "' 仍在运动或停止状态未知。");
                }
                lock (_statusLock)
                {
                    if (!_productionStopReviewRequired || _state != MachineRunState.Paused || _alarmService.HasLatchedStopAlarm)
                        throw new InvalidOperationException("停止核查期间设备状态或报警发生变化。");
                    _state = MachineRunState.Stopped;
                    _lastError = null;
                }
                RaiseStatusChanged();
            }
            finally { _commandLock.Release(); }
        }

        /// <summary>限时读取停稳反馈；超时保留原始读取任务，不能据此认定设备安全。</summary>
        private async Task<AxisStatus> ReadStopFeedbackAsync(IAxis axis, CancellationToken cancellationToken)
        {
            Task<AxisStatus> reading = axis.ReadStatusAsync(cancellationToken);
            if (reading == null) throw new InvalidOperationException("轴 '" + axis.Name + "' 未返回状态读取任务。");
            Volatile.Write(ref _stopFeedbackInFlight, reading);
            Task timeout = Task.Delay(_pauseTimeout, cancellationToken);
            if (await Task.WhenAny(reading, timeout).ConfigureAwait(false) != reading)
            {
                _ = reading.ContinueWith(task => { var ignored = task.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException("读取逻辑轴 '" + axis.Name + "' 的停止反馈超时，原始读取仍在进行。");
            }
            return await reading.ConfigureAwait(false);
        }

        /// <summary>停止反馈读取超时后，原始驱动调用未返回时不得复位、断卡或更换配置。</summary>
        private void EnsureStopFeedbackReadSettled()
        {
            if (!Volatile.Read(ref _stopFeedbackInFlight).IsCompleted)
                throw new InvalidOperationException("停止时的轴状态读取仍在进行，不能确认设备状态。");
        }
    }
}
