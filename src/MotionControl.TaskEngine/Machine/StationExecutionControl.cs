using System.Threading;
using System.Threading.Tasks;
using MotionControl.TaskEngine.Execution;

namespace MotionControl.TaskEngine.Machine
{
    /// <summary>具体工位的运行记录，只由设备控制器与受控执行器共享，不对外暴露可变对象。</summary>
    internal sealed class StationExecutionControl
    {
        /// <summary>所属设备会话。</summary>
        internal TaskMachineController Machine { get; }
        /// <summary>启动时冻结的工位定义。</summary>
        internal StationTaskConfiguration Configuration { get; }
        /// <summary>当前周期运行，整机锁内替换。</summary>
        internal TaskRun CurrentRun { get; set; }
        /// <summary>最近完成周期的快照。</summary>
        internal TaskRunSnapshot LastRun { get; set; }
        /// <summary>本会话成功完成的周期数。</summary>
        internal long CompletedCycles { get; set; }
        /// <summary>当前工位阶段。</summary>
        internal StationTaskState State { get; set; }
        /// <summary>当前等待或阶段说明。</summary>
        internal string Detail { get; set; }
        /// <summary>故障步骤等待下一次复位后启动的许可，整机锁内替换。</summary>
        internal TaskCompletionSource<bool> RecoveryPermit { get; set; }
        /// <summary>执行器已异常结束，无法保证当前节点进度时禁止直接复位续跑。</summary>
        internal bool Unrecoverable { get; set; }

        /// <summary>建立一个清楚的工位记录，没有额外线程或硬件对象。</summary>
        internal StationExecutionControl(TaskMachineController machine, StationTaskConfiguration configuration)
        {
            Machine = machine; Configuration = configuration;
            State = configuration.Enabled ? StationTaskState.WaitingInitialization : StationTaskState.Disabled;
            Detail = configuration.Enabled ? "等待设备启动和初始化。" : "未启用。";
        }

        /// <summary>当前动作完成收尾后报告故障，暂停整机并等待复位启动；保留当前调用栈。</summary>
        internal async Task RecoverAsync(TaskExecutionScope scope, TaskExecutionException error)
        {
            scope.Token.ThrowIfCancellationRequested();
            Task permit = await Machine.ReportNodeFaultAsync(this, error).ConfigureAwait(false);
            await WaitSignalAsync(permit, scope.Token).ConfigureAwait(false);
            await scope.ResetTimeBudgetAsync().ConfigureAwait(false);
        }

        /// <summary>异步等待许可或取消，恢复后及时释放令牌注册，不遗留无限延时任务。</summary>
        internal static async Task WaitSignalAsync(Task signal, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (token.Register(() => cancelled.TrySetCanceled()))
            {
                await Task.WhenAny(signal, cancelled.Task).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                await signal.ConfigureAwait(false);
            }
        }
    }
}
