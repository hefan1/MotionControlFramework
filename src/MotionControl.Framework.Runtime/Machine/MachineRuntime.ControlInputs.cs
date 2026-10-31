using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Alarms;

namespace MotionControl.Framework.Runtime.Machine
{
    /// <summary>读取现场整机按钮输入，并转发到已有的整机控制命令。</summary>
    public sealed partial class MachineRuntime
    {
        /// <summary>按钮 DI 读取失败时使用停机报警，恢复读取后仍需人工复位。</summary>
        private static readonly MachineAlarmDefinition ControlInputReadAlarm = new MachineAlarmDefinition(
            "MACHINE_CONTROL_INPUT_READ_FAILED",
            "整机操作按钮输入无法读取",
            MachineAlarmKind.StopMachine);

        /// <summary>更新现场启动、停止和复位按钮的逻辑 DI 名称；空名称表示该按钮不由现场 DI 控制。</summary>
        public void UpdateControlInputNames(string startInputName, string pauseInputName, string resetInputName)
        {
            lock (_controlInputLock)
            {
                _startInputName = NormalizeInputName(startInputName);
                _pauseInputName = NormalizeInputName(pauseInputName);
                _resetInputName = NormalizeInputName(resetInputName);
            }
        }

        /// <summary>停止现场按钮监控；控制卡已由外部断开时也应调用此方法回收后台任务。</summary>
        public async Task StopControlInputMonitoringAsync()
        {
            await StopControlInputMonitorAsync().ConfigureAwait(false);
        }

        /// <summary>控制卡连接成功后启动现场按钮监控，不会因 DI 初始为高电平而自动执行按钮命令。</summary>
        private void StartControlInputMonitor()
        {
            if (_controlInputIo == null)
            {
                return;
            }

            if (_controlInputMonitorTask != null && !_controlInputMonitorTask.IsCompleted)
            {
                throw new InvalidOperationException("上一次现场按钮监控尚未结束，不能重复启动。");
            }

            _controlInputMonitorCancellation = new CancellationTokenSource();
            _controlInputMonitorTask = MonitorControlInputsAsync(_controlInputMonitorCancellation.Token);
        }

        /// <summary>停止并等待现场按钮监控退出，防止控制卡断开后继续读取 DI。</summary>
        private async Task StopControlInputMonitorAsync()
        {
            CancellationTokenSource cancellation = _controlInputMonitorCancellation;
            if (cancellation == null)
            {
                return;
            }

            cancellation.Cancel();
            try
            {
                await _controlInputMonitorTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 正常断开时取消轮询，不作为设备故障处理。
            }
            finally
            {
                if (ReferenceEquals(_controlInputMonitorCancellation, cancellation))
                {
                    _controlInputMonitorCancellation = null;
                    cancellation.Dispose();
                }
            }
        }

        /// <summary>按上升沿采样现场按钮；停止优先，其次复位，最后启动，避免同一周期执行多个命令。</summary>
        private async Task MonitorControlInputsAsync(CancellationToken cancellationToken)
        {
            string previousStartName = null;
            string previousPauseName = null;
            string previousResetName = null;
            bool hasStartBaseline = false;
            bool hasPauseBaseline = false;
            bool hasResetBaseline = false;
            bool previousStartActive = false;
            bool previousPauseActive = false;
            bool previousResetActive = false;

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(_safetyPollInterval, cancellationToken).ConfigureAwait(false);

                    string startName;
                    string pauseName;
                    string resetName;
                    lock (_controlInputLock)
                    {
                        startName = _startInputName;
                        pauseName = _pauseInputName;
                        resetName = _resetInputName;
                    }

                    if (!string.Equals(previousStartName, startName, StringComparison.OrdinalIgnoreCase))
                    {
                        ClearControlInputReadAlarm(previousStartName);
                        previousStartName = startName;
                        hasStartBaseline = false;
                    }

                    if (!string.Equals(previousPauseName, pauseName, StringComparison.OrdinalIgnoreCase))
                    {
                        ClearControlInputReadAlarm(previousPauseName);
                        previousPauseName = pauseName;
                        hasPauseBaseline = false;
                    }

                    if (!string.Equals(previousResetName, resetName, StringComparison.OrdinalIgnoreCase))
                    {
                        ClearControlInputReadAlarm(previousResetName);
                        previousResetName = resetName;
                        hasResetBaseline = false;
                    }

                    bool startEdge = false;
                    bool pauseEdge = false;
                    bool resetEdge = false;

                    if (!string.IsNullOrWhiteSpace(startName))
                    {
                        bool? startActive = await TryReadControlInputAsync(startName, cancellationToken)
                            .ConfigureAwait(false);
                        startEdge = startActive.HasValue && hasStartBaseline
                            && startActive.Value && !previousStartActive;
                        hasStartBaseline = startActive.HasValue;
                        previousStartActive = startActive ?? false;
                    }
                    else
                    {
                        hasStartBaseline = false;
                    }

                    if (!string.IsNullOrWhiteSpace(pauseName))
                    {
                        bool? pauseActive = await TryReadControlInputAsync(pauseName, cancellationToken)
                            .ConfigureAwait(false);
                        pauseEdge = pauseActive.HasValue && hasPauseBaseline
                            && pauseActive.Value && !previousPauseActive;
                        hasPauseBaseline = pauseActive.HasValue;
                        previousPauseActive = pauseActive ?? false;
                    }
                    else
                    {
                        hasPauseBaseline = false;
                    }

                    if (!string.IsNullOrWhiteSpace(resetName))
                    {
                        bool? resetActive = await TryReadControlInputAsync(resetName, cancellationToken)
                            .ConfigureAwait(false);
                        resetEdge = resetActive.HasValue && hasResetBaseline
                            && resetActive.Value && !previousResetActive;
                        hasResetBaseline = resetActive.HasValue;
                        previousResetActive = resetActive ?? false;
                    }
                    else
                    {
                        hasResetBaseline = false;
                    }

                    if (pauseEdge && State == MachineRunState.Running)
                    {
                        try
                        {
                            await PauseAsync(cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            return;
                        }
                        catch (Exception commandError)
                        {
                            Trace.TraceWarning("现场停止按钮请求暂停失败：" + commandError.Message);
                        }

                        continue;
                    }

                    if (resetEdge && (State == MachineRunState.ResetRequired
                        || State == MachineRunState.Paused
                        || State == MachineRunState.Alarmed))
                    {
                        try
                        {
                            await ResetAsync(cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            return;
                        }
                        catch (Exception commandError)
                        {
                            Trace.TraceWarning("现场复位按钮请求复位失败：" + commandError.Message);
                        }

                        continue;
                    }

                    if (startEdge && State == MachineRunState.Ready)
                    {
                        try
                        {
                            await StartAsync(cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            return;
                        }
                        catch (Exception commandError)
                        {
                            Trace.TraceWarning("现场启动按钮请求启动失败：" + commandError.Message);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 正常断开时取消轮询，不把取消操作报告为设备故障。
            }
            catch (Exception monitorError)
            {
                Trace.TraceError("现场按钮监控意外结束：" + monitorError);
            }
        }

        /// <summary>读取按钮 DI；通信错误产生停机报警，输入恢复后保留报警供人工复位。</summary>
        private async Task<bool?> TryReadControlInputAsync(string inputName, CancellationToken cancellationToken)
        {
            bool active;
            try
            {
                active = await _controlInputIo.ReadInputAsync(inputName, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception readError)
            {
                _alarmService.ReportCondition(
                    ControlInputReadAlarm,
                    "整机按钮输入：" + inputName,
                    true,
                    "无法读取已配置的 DI，整机禁止继续运行。原因：" + readError.Message);
                return null;
            }

            _alarmService.ReportCondition(
                ControlInputReadAlarm,
                "整机按钮输入：" + inputName,
                false,
                "按钮 DI 已恢复读取。");
            return active;
        }

        /// <summary>映射名称被清空或更换时解除旧输入的活动条件，已锁存停机报警仍需复位。</summary>
        private void ClearControlInputReadAlarm(string inputName)
        {
            if (string.IsNullOrWhiteSpace(inputName))
            {
                return;
            }

            _alarmService.ReportCondition(
                ControlInputReadAlarm,
                "整机按钮输入：" + inputName,
                false,
                "按钮 DI 映射已移除或更换。");
        }

        /// <summary>清理可选 DI 名称两端空白；空值保持未配置状态。</summary>
        private static string NormalizeInputName(string inputName)
        {
            return string.IsNullOrWhiteSpace(inputName) ? string.Empty : inputName.Trim();
        }
    }
}
