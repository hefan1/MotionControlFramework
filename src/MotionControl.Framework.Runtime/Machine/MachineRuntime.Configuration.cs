using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace MotionControl.Framework.Runtime.Machine
{
    /// <summary>对已连接控制卡的配置变更使用与整机命令相同的互斥保护。</summary>
    public sealed partial class MachineRuntime
    {
        /// <summary>安全输入配置只有在命令空闲且所有动作收尾后才能保存并应用，不排队延后切换。</summary>
        public Task SaveSafetyInputConfigurationAsync(Action saveAndApply, CancellationToken cancellationToken)
        {
            if (saveAndApply == null) throw new ArgumentNullException(nameof(saveAndApply));
            return RunConfigurationChangeAsync(() => { saveAndApply(); return Task.CompletedTask; },
                cancellationToken, safetyInputSave: true);
        }

        /// <summary>自动流程及手动动作均停止时执行配置变更，期间禁止启动、复位和断卡。</summary>
        public Task RunConnectedConfigurationChangeAsync(Func<Task> change, CancellationToken cancellationToken)
        {
            return RunConfigurationChangeAsync(change, cancellationToken, safetyInputSave: false);
        }

        /// <summary>持命令锁停止现场按钮采集并更新配置，随后用新基线恢复采集。</summary>
        private async Task RunConfigurationChangeAsync(Func<Task> change, CancellationToken cancellationToken, bool safetyInputSave)
        {
            if (change == null) throw new ArgumentNullException(nameof(change));
            Guid? session = ConnectionSessionId;
            if (safetyInputSave)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_commandLock.Wait(0))
                    throw new InvalidOperationException("设备命令执行中，安全输入配置未保存。");
            }
            else await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(true);
            try
            {
                if (IsShutdownInProgress) throw new InvalidOperationException("设备正在安全退出，不能修改控制卡配置。");
                EnsureStopFeedbackReadSettled();
                if (!IsConnectionSessionCurrent(session))
                    throw new InvalidOperationException("控制卡连接会话未确认或已变化，不能在线应用配置。");
                if (State == MachineRunState.Running || State == MachineRunState.Pausing || _flowScheduler.IsRunning)
                    throw new InvalidOperationException("工位流程仍在运行或等待停止，请先暂停并确认流程停止后再应用配置。");
                EnsureNoManualOperationsInProgress("应用控制卡配置");
                if (safetyInputSave && (IsPreparing || IsClearing || IsTaskEngineControlClaimed
                    || !_flowScheduler.IsSettlementConfirmed || _flowObserverTask.Status != TaskStatus.RanToCompletion))
                    throw new InvalidOperationException("控制器连接、任务会话或设备动作收尾尚未确认，安全输入配置未保存。");

                // 点动的发起调用可以先于轴实际停止返回，保存安全输入前另取全轴停稳反馈。
                if (safetyInputSave)
                {
                    foreach (var axis in _motionCards.Axes)
                    {
                        var status = await ReadStopFeedbackAsync(axis, cancellationToken).ConfigureAwait(true);
                        if (!AxisStopFeedback.IsConfirmedStopped(status))
                            throw new InvalidOperationException("逻辑轴“" + axis.Name + "”运动中或停稳反馈未知，安全输入配置未保存。");
                    }
                }

                // 避免映射切换被现场按钮监控误识别为上升沿；恢复监控时重新建立输入基线。
                lock (_readSessionLock)
                {
                    _safetyObservationVersion++;
                    _onlineSafetyInputsChanging = true;
                }
                Exception failure = null;
                try
                {
                    await StopControlInputMonitorAsync().ConfigureAwait(true);
                    if (safetyInputSave) cancellationToken.ThrowIfCancellationRequested();
                    if (!IsConnectionSessionCurrent(session)) throw new InvalidOperationException("控制卡连接会话已失效，未应用配置。");
                    await change().ConfigureAwait(true);
                }
                catch (Exception error) { failure = error; }
                try { StartControlInputMonitor(); }
                catch (Exception monitorError)
                {
                    failure = failure == null ? monitorError
                        : new AggregateException("配置应用和现场按钮监控恢复均失败。", failure, monitorError);
                }
                if (failure is OperationCanceledException && safetyInputSave && cancellationToken.IsCancellationRequested)
                    ExceptionDispatchInfo.Capture(failure).Throw();
                if (failure != null)
                {
                    SetStatus(MachineRunState.Alarmed, new InvalidOperationException(
                        "控制卡配置应用未完成，请检查实际映射和设备状态后再复位。", failure));
                    ExceptionDispatchInfo.Capture(failure).Throw();
                }
            }
            finally
            {
                lock (_readSessionLock)
                {
                    if (_onlineSafetyInputsChanging)
                    {
                        _safetyObservationVersion++;
                        _onlineSafetyInputsChanging = false;
                    }
                }
                _commandLock.Release();
            }
        }
    }
}
