using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MotionControl.Framework.Runtime.Machine
{
    /// <summary>单机运行时中的手动轴与 IO 操作入口和联锁检查。</summary>
    public sealed partial class MachineRuntime
    {
        /// <summary>检查手动操作条件并刷新已配置的急停状态；未配置软件 DI 不阻止手动调试。</summary>
        public async Task<ManualOperationBlockReason> GetManualOperationBlockReasonAsync(
            CancellationToken cancellationToken)
        {
            ManualOperationBlockReason stateReason = GetManualOperationStateBlockReason();
            if (stateReason != ManualOperationBlockReason.None)
            {
                return stateReason;
            }

            bool emergencyStopActive;
            try
            {
                emergencyStopActive = await UpdateEmergencyStopAlarmAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return ManualOperationBlockReason.EmergencyStopStatusUnavailable;
            }

            if (emergencyStopActive)
            {
                return ManualOperationBlockReason.EmergencyStopInputActive;
            }

            bool emergencyStopAlarmExists = _alarmService.Alarms.Any(
                alarm => string.Equals(alarm.Definition.Code, "EMERGENCY_STOP", StringComparison.OrdinalIgnoreCase));
            if (emergencyStopAlarmExists)
            {
                return ManualOperationBlockReason.EmergencyStopAlarmNotReset;
            }

            return GetManualOperationStateBlockReason();
        }

        /// <summary>检查联锁并执行一项手动操作；操作结束前阻止整机启动、复位和断卡。</summary>
        public async Task<ManualOperationBlockReason> RunManualOperationAsync(
            Func<Task> operation,
            CancellationToken cancellationToken)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            ManualOperationBlockReason blockReason = await BeginManualOperationAsync(cancellationToken)
                .ConfigureAwait(true);
            if (blockReason != ManualOperationBlockReason.None)
            {
                return blockReason;
            }

            try
            {
                await operation().ConfigureAwait(false);
                return ManualOperationBlockReason.None;
            }
            finally
            {
                EndManualOperation();
            }
        }

        /// <summary>
        /// 执行不启动运动、不写入 DO 的手动恢复或示教操作。
        /// 急停状态可以有效、未复位或暂时不可读，但控制卡必须连接且自动流程必须停止。
        /// </summary>
        public async Task<ManualOperationBlockReason> RunManualRecoveryOperationAsync(
            Func<Task> operation,
            CancellationToken cancellationToken)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            ManualOperationBlockReason blockReason = await BeginManualRecoveryOperationAsync(cancellationToken)
                .ConfigureAwait(true);
            if (blockReason != ManualOperationBlockReason.None)
            {
                return blockReason;
            }

            try
            {
                await operation().ConfigureAwait(false);
                return ManualOperationBlockReason.None;
            }
            finally
            {
                EndManualOperation();
            }
        }

        /// <summary>急停输入已恢复但整机急停报警仍锁存时，允许单独复位轴驱动报警。</summary>
        public async Task<ManualOperationBlockReason> RunManualAxisAlarmResetAsync(
            Func<Task> resetOperation,
            CancellationToken cancellationToken)
        {
            if (resetOperation == null)
            {
                throw new ArgumentNullException(nameof(resetOperation));
            }

            ManualOperationBlockReason blockReason = await BeginManualAxisAlarmResetAsync(cancellationToken)
                .ConfigureAwait(true);
            if (blockReason != ManualOperationBlockReason.None)
            {
                return blockReason;
            }

            try
            {
                await resetOperation().ConfigureAwait(false);
                return ManualOperationBlockReason.None;
            }
            finally
            {
                EndManualOperation();
            }
        }

        /// <summary>执行手动受控停止；停止不受急停联锁阻挡，但自动流程期间仍需整机暂停。</summary>
        public async Task<ManualOperationBlockReason> RunManualStopAsync(
            Func<Task> stopOperation,
            CancellationToken cancellationToken)
        {
            if (stopOperation == null)
            {
                throw new ArgumentNullException(nameof(stopOperation));
            }

            ManualOperationBlockReason blockReason = await BeginManualRecoveryOperationAsync(cancellationToken, true)
                .ConfigureAwait(true);
            if (blockReason != ManualOperationBlockReason.None)
            {
                return blockReason;
            }

            try
            {
                await stopOperation().ConfigureAwait(false);
                return ManualOperationBlockReason.None;
            }
            finally
            {
                EndManualOperation();
            }
        }

        /// <summary>根据整机状态返回当前是否允许手动轴或 IO 操作。</summary>
        private ManualOperationBlockReason GetManualOperationStateBlockReason(bool allowUnsettledStop = false)
        {
            if (IsPreparing) return ManualOperationBlockReason.AutomaticFlowActive;
            if (IsTaskEngineControlClaimed)
            {
                return ManualOperationBlockReason.TaskExecutionEntryClaimed;
            }

            MachineRunState state = State;
            if (state == MachineRunState.Disconnected)
            {
                return ManualOperationBlockReason.MotionCardDisconnected;
            }

            if (state == MachineRunState.Running || state == MachineRunState.Pausing)
            {
                return ManualOperationBlockReason.AutomaticFlowActive;
            }

            // 报警态不表示旧 C# 工位已退出；只让受控停止入口穿过未确认的收尾。
            if (!allowUnsettledStop && (_flowScheduler.IsRunning || !_flowScheduler.IsSettlementConfirmed))
            {
                return ManualOperationBlockReason.AutomaticFlowUnsettled;
            }

            return ManualOperationBlockReason.None;
        }

        /// <summary>在整机命令锁内检查手动联锁并登记正在执行的手动操作。</summary>
        private async Task<ManualOperationBlockReason> BeginManualOperationAsync(
            CancellationToken cancellationToken)
        {
            await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ManualOperationBlockReason blockReason = await GetManualOperationBlockReasonAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (blockReason == ManualOperationBlockReason.None)
                {
                    Interlocked.Increment(ref _activeManualOperationCount);
                    InvalidatePreparations("已执行手动轴或 IO 操作，请重新核对工位准备。");
                }

                return blockReason;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        /// <summary>在整机命令锁内登记恢复或专用停止；仅停止允许穿过未确认的自动流程收尾。</summary>
        private async Task<ManualOperationBlockReason> BeginManualRecoveryOperationAsync(CancellationToken cancellationToken, bool allowUnsettledStop = false)
        {
            await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ManualOperationBlockReason blockReason = GetManualOperationStateBlockReason(allowUnsettledStop);
                if (blockReason == ManualOperationBlockReason.None)
                {
                    Interlocked.Increment(ref _activeManualOperationCount);
                }

                return blockReason;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        /// <summary>急停输入恢复后，允许复位轴报警；急停仍有效或无法读取时继续阻止复位。</summary>
        private async Task<ManualOperationBlockReason> BeginManualAxisAlarmResetAsync(
            CancellationToken cancellationToken)
        {
            await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ManualOperationBlockReason blockReason = await GetManualOperationBlockReasonAsync(cancellationToken)
                    .ConfigureAwait(false);

                // 急停输入已经恢复；仅整机急停报警尚未复位时，允许先处理轴驱动报警。
                if (blockReason == ManualOperationBlockReason.EmergencyStopAlarmNotReset)
                {
                    blockReason = ManualOperationBlockReason.None;
                }

                if (blockReason == ManualOperationBlockReason.None)
                {
                    Interlocked.Increment(ref _activeManualOperationCount);
                }

                return blockReason;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        /// <summary>手动操作结束时减少运行计数，允许后续整机命令继续执行。</summary>
        private void EndManualOperation()
        {
            Interlocked.Decrement(ref _activeManualOperationCount);
        }

        /// <summary>启动、复位或断卡前拒绝仍有手动操作未完成的情况。</summary>
        private void EnsureNoManualOperationsInProgress(string operationName)
        {
            if (Interlocked.CompareExchange(ref _activeManualOperationCount, 0, 0) > 0)
            {
                throw new InvalidOperationException(
                    "手动轴或 IO 操作仍在执行，不能" + operationName + "；请先等待动作完成或受控停止。");
            }
        }
    }
}
