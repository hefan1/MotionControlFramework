using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Device;

namespace MotionControl.TaskEngine.MachineDevice
{
    /// <summary>真机单轴与轴组动作；轴组插补尚未接入，调用时明确拒绝。</summary>
    public sealed partial class MachineTaskDevice
    {
        /// <summary>发出停止请求后等待轴停稳的最长时间；超时说明驱动器没有按预期停下。</summary>
        private static readonly TimeSpan StopConfirmTimeout = TimeSpan.FromSeconds(10);

        /// <summary>复查轴是否停稳的间隔。</summary>
        private static readonly TimeSpan StopPollInterval = TimeSpan.FromMilliseconds(20);

        /// <summary>
        /// 接受一次单轴动作并立即启动真实运动。前置条件（使能、回零、报警、行程）在这里检查，
        /// 到位判定交给反馈位置；调用方随后通过 IsMoveComplete 轮询。
        /// </summary>
        public ITaskAxisMove BeginMove(string name, double target, bool relative, bool home, AxisMotionProfile profile)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new TaskMoveRejectedBeforeCommandException("轴名称不能为空。", null);
            lock (_sync)
            {
                RemoveSettledMoves();
                if (_beginningAxes.Contains(name) || _unsettledMoves.Any(item => string.Equals(item.AxisName, name, StringComparison.OrdinalIgnoreCase)))
                    throw new TaskMoveRejectedBeforeCommandException("该轴上一动作尚未完成收尾：" + name, null);
                _beginningAxes.Add(name);
            }
            try
            {
                IAxis axis;
                AxisStatus status;
                double end;
                try
                {
                    axis = _motionCards.GetAxis(name);
                    status = Wait(axis.ReadStatusAsync(CancellationToken.None));
                    if (!status.ServoEnabled) throw new InvalidOperationException("轴未使能，不能接受任务动作：" + name + "。");
                    if (status.AlarmActive) throw new InvalidOperationException("轴存在报警，不能接受任务动作：" + name + "，" + status.AlarmMessage);
                    if (!home && !status.Homed) throw new InvalidOperationException("轴尚未回零，不能接受定位动作：" + name + "。");
                    if (!home && profile == null) throw new ArgumentNullException(nameof(profile), "定位动作必须提供速度和加减速度。");
                    end = home ? 0 : relative ? status.Position + target : target;
                    if (!home) CheckTravel(name, end);
                }
                catch (Exception error)
                {
                    // 此边界仅查询状态和校验参数，尚未调用回零或运动命令。
                    throw new TaskMoveRejectedBeforeCommandException(error.Message, error);
                }

                var move = new MachineAxisMove(this, name, status.Position, end, home, axis);
                // 下发前登记；同步异常或空任务无法证明原动作结束，故继续保留凭证。
                lock (_sync) _unsettledMoves.Add(move);
                try { move.StartMotion(profile); }
                catch (Exception error)
                {
                    // 调用厂商运动入口后无法证明命令未下发，即使内层异常名字表示前置拒绝也不能放行。
                    throw new InvalidOperationException("轴命令启动失败，设备状态未知：" + name + "，" + error.Message, error);
                }
                if (move.Completion == null) throw new InvalidOperationException("轴动作未返回收尾任务：" + name);
                move.Completion.ContinueWith(_ =>
                {
                    lock (_sync) RemoveSettledMoves();
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                RecordAcceptedCommand(name);
                return move;
            }
            finally
            {
                lock (_sync) _beginningAxes.Remove(name);
            }
        }

        /// <summary>真机位置来自反馈，这里不需要按进度推算位置。</summary>
        public void AdvanceMove(ITaskAxisMove move, double progress)
        {
            RequireMachineMove(move);
        }

        /// <summary>动作任务结束且反馈速度停稳才算到位；未知反馈和驱动故障不能算成功。</summary>
        public bool IsMoveComplete(ITaskAxisMove move, double progress)
        {
            MachineAxisMove machine = RequireMachineMove(move);
            if (!machine.Completion.IsCompleted)
            {
                return false;
            }

            machine.ThrowIfFailed();
            AxisStatus status = Wait(machine.Axis.ReadStatusAsync(CancellationToken.None));
            if (status == null || double.IsNaN(status.Velocity) || double.IsInfinity(status.Velocity))
                throw new InvalidOperationException("轴速度状态未知，不能确认停止：" + machine.AxisName + "。");
            return !status.Moving && Math.Abs(status.Velocity) <= 0.001;
        }

        /// <summary>
        /// 收尾一次单轴动作。未完成时必须先让轴受控停止并确认停稳，
        /// 不能把中途位置当作到位，也不能让轴在任务已退出后继续运动。
        /// </summary>
        public Task EndMoveAsync(ITaskAxisMove move, bool completed, CancellationToken cancellationToken)
        {
            MachineAxisMove machine = RequireMachineMove(move);
            Task ending = machine.EndOnce(completed, () => EndMoveCoreAsync(machine, completed));
            ending.ContinueWith(_ => { lock (_sync) RemoveSettledMoves(); }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return ending;
        }

        /// <summary>执行首次收尾；调用方取消不能中断停止与停稳确认。</summary>
        private async Task EndMoveCoreAsync(MachineAxisMove machine, bool completed)
        {
            if (completed)
            {
                await machine.SettleAsync().ConfigureAwait(false);
                await WaitUntilStoppedAsync(machine, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            lock (_sync)
            {
                int count;
                _pendingStops.TryGetValue(machine, out count);
                _pendingStops[machine] = count + 1;
            }
            try
            {
                // 先请求停止，不把读状态作为停止前提；调用方取消也不能取消这次停止。
                Task stop;
                try { stop = machine.Axis.StopAsync(AxisStopMode.Controlled, CancellationToken.None)
                    ?? Task.FromException(new InvalidOperationException("停止操作未返回任务。")); }
                catch (Exception error) { stop = Task.FromException(error); }
                Exception cancelError = null;
                try { machine.RequestCancel(); }
                catch (Exception error) { cancelError = error; }
                try { await stop.ConfigureAwait(false); }
                catch (Exception error)
                {
                    Exception failure = cancelError == null ? error : new AggregateException("停止和取消均失败。", error, cancelError);
                    throw new TaskMoveCleanupPendingException("轴停止失败：" + machine.AxisName + "，" + failure.Message,
                        failure, machine.SettleAsync());
                }
                if (cancelError != null)
                    throw new TaskMoveCleanupPendingException("停止已发送，但取消轴动作失败：" + machine.AxisName + "，" + cancelError.Message,
                        cancelError, machine.SettleAsync());

                await machine.SettleAsync().ConfigureAwait(false);
                await WaitUntilStoppedAsync(machine, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                lock (_sync)
                {
                    int count = _pendingStops[machine];
                    if (count == 1) _pendingStops.Remove(machine);
                    else _pendingStops[machine] = count - 1;
                }
            }
        }

        /// <summary>确认轴当前没有运动；仍在运动时明确报错，不伪造停止结果。</summary>
        public async Task ConfirmStoppedAsync(string name, CancellationToken cancellationToken)
        {
            IAxis axis = _motionCards.GetAxis(name);
            AxisStatus status = await axis.ReadStatusAsync(cancellationToken).ConfigureAwait(false);
            if (status.Moving)
            {
                throw new InvalidOperationException("轴仍在运动，不能确认已停止：" + name + "。");
            }
        }

        /// <summary>轴组插补尚未接入；明确拒绝，不退化成逐轴顺序移动。</summary>
        public IReadOnlyList<ITaskAxisGroupMove> BeginGroupMove(IReadOnlyList<TaskAxisGroupTarget> targets)
        {
            throw new NotSupportedException(
                "真机任务暂不支持轴组插补：当前适配器未提供轴组运动能力，拒绝执行以免改变工艺时序。");
        }

        /// <summary>轴组插补尚未接入，任何推进请求都按错误处理。</summary>
        public bool AdvanceGroupMove(IReadOnlyList<ITaskAxisGroupMove> moves, double elapsedMilliseconds, out string detail)
        {
            throw new NotSupportedException("真机任务暂不支持轴组插补。");
        }

        /// <summary>轴组插补尚未接入，任何收尾请求都按错误处理。</summary>
        public Task EndGroupMoveAsync(IReadOnlyList<ITaskAxisGroupMove> moves, CancellationToken cancellationToken)
        {
            throw new NotSupportedException("真机任务暂不支持轴组插补。");
        }

        /// <summary>检查目标位置是否落在该轴配置行程内，越界时拒绝动作。</summary>
        private void CheckTravel(string axisName, double position)
        {
            TaskAxisInfo info = Catalog.Axes[axisName];
            if (double.IsNaN(position) || double.IsInfinity(position)
                || !info.MinimumPosition.HasValue || !info.MaximumPosition.HasValue
                || position < info.MinimumPosition.Value || position > info.MaximumPosition.Value)
            {
                throw new InvalidOperationException(
                    "轴目标超出配置行程或不是有限数：" + axisName + "，目标=" + position + "。");
            }
        }

        /// <summary>轮询反馈直到轴停稳；超时说明驱动器没有按停止指令停下。</summary>
        private static async Task WaitUntilStoppedAsync(MachineAxisMove move, CancellationToken cancellationToken)
        {
            DateTime deadline = DateTime.UtcNow + StopConfirmTimeout;
            while (true)
            {
                AxisStatus status = await move.Axis.ReadStatusAsync(cancellationToken).ConfigureAwait(false);
                if (status == null || double.IsNaN(status.Velocity) || double.IsInfinity(status.Velocity))
                    throw new InvalidOperationException("轴速度状态未知，不能确认停止：" + move.AxisName + "。");
                if (!status.Moving && Math.Abs(status.Velocity) <= 0.001)
                {
                    return;
                }

                if (DateTime.UtcNow >= deadline)
                {
                    throw new InvalidOperationException(
                        "轴在停止请求后仍未停稳，请检查驱动器状态：" + move.AxisName + "。");
                }

                await Task.Delay(StopPollInterval, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>只接受本设备产生的动作凭证；其他实现的凭证属于接线错误。</summary>
        private MachineAxisMove RequireMachineMove(ITaskAxisMove move)
        {
            var machine = move as MachineAxisMove;
            if (machine == null || !machine.BelongsTo(this))
            {
                throw new ArgumentException("动作凭证不属于本任务设备。", nameof(move));
            }

            return machine;
        }
    }
}
