using System;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.TaskEngine.Device;

namespace MotionControl.TaskEngine.MachineDevice
{
    /// <summary>
    /// 一次已经启动的真实轴动作。起点和终点在接受时固定，暂停恢复后不重新叠加相对位移；
    /// 到位由轴自身的异步任务完成情况决定。
    /// </summary>
    internal sealed class MachineAxisMove : ITaskAxisMove
    {
        /// <summary>本次动作的取消源；收尾后不再使用。</summary>
        private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();

        /// <summary>动作产生的设备；其他设备不能使用此凭证。</summary>
        private readonly MachineTaskDevice _owner;
        /// <summary>保护动作的单次收尾结果。</summary>
        private readonly object _endSync = new object();
        /// <summary>首次调用创建的收尾任务，迟到调用只能等待它。</summary>
        private Task _endTask;

        /// <summary>本次动作占用的逻辑轴。</summary>
        internal IAxis Axis { get; }

        /// <summary>轴动作任务；回零或定位，完成即表示驱动器报告到位。</summary>
        internal Task Completion { get; private set; }

        /// <summary>本次动作占用的逻辑轴名称。</summary>
        public string AxisName { get; }

        /// <summary>接受命令时的反馈位置。</summary>
        public double Start { get; }

        /// <summary>已经计算好的绝对终点。</summary>
        public double Target { get; }

        /// <summary>本次动作是否是回零。</summary>
        public bool IsHome { get; }

        /// <summary>保存固定参数和轴入口，不启动动作。</summary>
        internal MachineAxisMove(MachineTaskDevice owner, string axisName, double start, double target, bool home, IAxis axis)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            AxisName = axisName;
            Start = start;
            Target = target;
            IsHome = home;
            Axis = axis;
        }

        /// <summary>确认凭证属于当前设备实例。</summary>
        internal bool BelongsTo(MachineTaskDevice owner) { return ReferenceEquals(_owner, owner); }

        /// <summary>原动作与首次收尾均已退出；失败时仍须现场确认后才可退休。</summary>
        internal bool IsQuiescent
        {
            get { lock (_endSync) return Completion != null && Completion.IsCompleted && _endTask != null && _endTask.IsCompleted; }
        }

        /// <summary>原动作已退出且首次收尾成功时，凭证才能自动退休。</summary>
        internal bool IsSettled
        {
            get { lock (_endSync) return Completion != null && Completion.IsCompleted && _endTask != null && _endTask.Status == TaskStatus.RanToCompletion; }
        }

        /// <summary>只创建一次收尾；正常完成必须已经由设备报告成功。</summary>
        internal Task EndOnce(bool completed, Func<Task> end)
        {
            TaskCompletionSource<bool> result;
            lock (_endSync)
            {
                if (_endTask != null) return _endTask;
                if (completed && (Completion == null || !Completion.IsCompleted || Completion.IsCanceled || Completion.IsFaulted))
                    throw new InvalidOperationException("轴动作未确认成功，不能按完成收尾：" + AxisName);
                result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _endTask = result.Task;
            }
            _ = CompleteEndAsync(end, result);
            return result.Task;
        }

        /// <summary>发布唯一收尾任务的完成或原始错误。</summary>
        private static async Task CompleteEndAsync(Func<Task> end, TaskCompletionSource<bool> result)
        {
            try { await end().ConfigureAwait(false); result.TrySetResult(true); }
            catch (Exception error) { result.TrySetException(error); }
        }

        /// <summary>启动真实轴动作；回零使用轴配置中的回零参数，定位使用给定速度曲线。</summary>
        internal void StartMotion(AxisMotionProfile profile)
        {
            Completion = IsHome
                ? Axis.HomeAsync(_cancellation.Token)
                : Axis.MoveAbsoluteAsync(Target, profile, _cancellation.Token);
        }

        /// <summary>请求取消；轴契约规定取消时执行受控停止。</summary>
        internal void RequestCancel()
        {
            if (!_cancellation.IsCancellationRequested)
            {
                _cancellation.Cancel();
            }
        }

        /// <summary>等待动作任务收尾；取消不算错误，驱动故障仍然抛出。</summary>
        internal async Task SettleAsync()
        {
            try
            {
                await Completion.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 取消是预期结果：轴已按受控停止退出，位置保留在中途。
            }
        }

        /// <summary>动作任务已结束但失败时，重新抛出原始驱动异常。</summary>
        internal void ThrowIfFailed()
        {
            if (Completion.IsCanceled)
            {
                throw new OperationCanceledException("轴动作已取消：" + AxisName + "。");
            }

            if (Completion.IsFaulted)
            {
                // GetResult 会把驱动异常按原始类型重新抛出，避免包成 AggregateException 丢失上下文。
                Completion.GetAwaiter().GetResult();
            }
        }
    }
}
