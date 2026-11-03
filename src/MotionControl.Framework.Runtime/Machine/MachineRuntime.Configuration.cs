using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace MotionControl.Framework.Runtime.Machine
{
    /// <summary>对已连接控制卡的配置变更使用与整机命令相同的互斥保护。</summary>
    public sealed partial class MachineRuntime
    {
        /// <summary>自动流程及手动动作均停止时执行配置变更，期间禁止启动、复位和断卡。</summary>
        public async Task RunConnectedConfigurationChangeAsync(Func<Task> change, CancellationToken cancellationToken)
        {
            if (change == null) throw new ArgumentNullException(nameof(change));
            await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(true);
            try
            {
                if (IsShutdownInProgress) throw new InvalidOperationException("设备正在安全退出，不能修改控制卡配置。");
                EnsureStopFeedbackReadSettled();
                if (State == MachineRunState.Disconnected)
                    throw new InvalidOperationException("控制卡尚未连接，不能在线应用配置。");
                if (State == MachineRunState.Running || State == MachineRunState.Pausing || _flowScheduler.IsRunning)
                    throw new InvalidOperationException("工位流程仍在运行或等待停止，请先暂停并确认流程停止后再应用配置。");
                EnsureNoManualOperationsInProgress("应用控制卡配置");

                // 避免映射切换被现场按钮监控误识别为上升沿；恢复监控时重新建立输入基线。
                Exception failure = null;
                try
                {
                    await StopControlInputMonitorAsync().ConfigureAwait(true);
                    await change().ConfigureAwait(true);
                }
                catch (Exception error) { failure = error; }
                try { StartControlInputMonitor(); }
                catch (Exception monitorError)
                {
                    failure = failure == null ? monitorError
                        : new AggregateException("配置应用和现场按钮监控恢复均失败。", failure, monitorError);
                }
                if (failure != null)
                {
                    SetStatus(MachineRunState.Alarmed, new InvalidOperationException(
                        "控制卡配置应用未完成，请检查实际映射和设备状态后再复位。", failure));
                    ExceptionDispatchInfo.Capture(failure).Throw();
                }
            }
            finally { _commandLock.Release(); }
        }
    }
}
