using System;
using System.Collections.Generic;
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
            IAxis axis = _motionCards.GetAxis(name);
            AxisStatus status = Wait(axis.ReadStatusAsync(CancellationToken.None));
            if (!status.ServoEnabled)
            {
                throw new InvalidOperationException("轴未使能，不能接受任务动作：" + name + "。");
            }

            if (status.AlarmActive)
            {
                throw new InvalidOperationException("轴存在报警，不能接受任务动作：" + name + "，" + status.AlarmMessage);
            }

            if (!home && !status.Homed)
            {
                throw new InvalidOperationException("轴尚未回零，不能接受定位动作：" + name + "。");
            }

            if (!home && profile == null)
            {
                throw new ArgumentNullException(nameof(profile), "定位动作必须提供速度和加减速度。");
            }

            double end = home ? 0 : relative ? status.Position + target : target;
            if (!home)
            {
                CheckTravel(name, end);
            }

            var move = new MachineAxisMove(name, status.Position, end, home, axis);
            move.StartMotion(profile);
            RecordAcceptedCommand(name);
            return move;
        }

        /// <summary>真机位置来自反馈，这里不需要按进度推算位置。</summary>
        public void AdvanceMove(ITaskAxisMove move, double progress)
        {
            RequireMachineMove(move);
        }

        /// <summary>动作任务结束时才算到位；驱动故障在这里抛出原始异常。</summary>
        public bool IsMoveComplete(ITaskAxisMove move, double progress)
        {
            MachineAxisMove machine = RequireMachineMove(move);
            if (!machine.Completion.IsCompleted)
            {
                return false;
            }

            machine.ThrowIfFailed();
            return true;
        }

        /// <summary>
        /// 收尾一次单轴动作。未完成时必须先让轴受控停止并确认停稳，
        /// 不能把中途位置当作到位，也不能让轴在任务已退出后继续运动。
        /// </summary>
        public async Task EndMoveAsync(ITaskAxisMove move, bool completed, CancellationToken cancellationToken)
        {
            MachineAxisMove machine = RequireMachineMove(move);
            if (completed)
            {
                await machine.SettleAsync().ConfigureAwait(false);
                return;
            }

            machine.RequestCancel();
            AxisStatus status = await machine.Axis.ReadStatusAsync(cancellationToken).ConfigureAwait(false);
            if (status.Moving)
            {
                await machine.Axis.StopAsync(AxisStopMode.Controlled, cancellationToken).ConfigureAwait(false);
            }

            await machine.SettleAsync().ConfigureAwait(false);
            await WaitUntilStoppedAsync(machine, CancellationToken.None).ConfigureAwait(false);
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
                if (!status.Moving)
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
        private static MachineAxisMove RequireMachineMove(ITaskAxisMove move)
        {
            var machine = move as MachineAxisMove;
            if (machine == null)
            {
                throw new ArgumentException("动作凭证不属于本任务设备。", nameof(move));
            }

            return machine;
        }
    }
}
