using System;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.TaskEngine.Device;

namespace MotionControl.TaskEngine.Device.InMemory
{
    /// <summary>单轴匀速进度模型；不模拟伺服动力学、限位电路或厂商回零模式。</summary>
    public sealed partial class InMemoryTaskDevice
    {
        /// <summary>设置测试起始条件；禁止覆盖正在运行、已占用或待确认的轴。</summary>
        public void ConfigureAxis(string name, double position, bool enabled, bool homed)
        {
            lock (_sync)
            {
                InMemoryAxis axis = FindAxis(name);
                if (axis.Operation != null || Resources.IsReserved("Axis:" + name))
                    throw new InvalidOperationException("轴仍被占用或待确认：" + name);
                CheckPosition(name, position);
                axis.Position = position;
                axis.Enabled = enabled;
                axis.Homed = homed;
            }
        }

        /// <summary>注入或解除仿真报警；引擎不自动清除报警。</summary>
        public void SetAxisAlarm(string name, string alarm)
        {
            lock (_sync) FindAxis(name).Alarm = alarm;
        }

        /// <summary>读取轴状态快照，不暴露内部占用凭证。</summary>
        public TaskAxisState ReadAxis(string name)
        {
            lock (_sync)
            {
                InMemoryAxis axis = FindAxis(name);
                return new TaskAxisState(name, axis.Position, axis.Enabled, axis.Homed,
                    axis.Operation != null, axis.Alarm, axis.AcceptedCommands);
            }
        }

        /// <summary>接受单轴操作并锁定终点；执行器先排队取得资源，这里再防止重复占用。</summary>
        public ITaskAxisMove BeginMove(string name, double target, bool relative, bool home)
        {
            lock (_sync)
            {
                InMemoryAxis axis = FindAxis(name);
                if (axis.Operation != null) throw new InvalidOperationException("轴被其他动作占用：" + name);
                CheckReady(name, axis, home);
                double end = home ? 0 : relative ? axis.Position + target : target;
                CheckPosition(name, end);
                var move = new InMemoryAxisMove(name, axis.Position, end, home);
                axis.Operation = move;
                axis.AcceptedCommands++;
                if (home) axis.Homed = false;
                return move;
            }
        }

        /// <summary>更新匀速模型的位置；进度由执行器按活动时间计算。</summary>
        public void AdvanceMove(ITaskAxisMove move, double progress)
        {
            lock (_sync)
            {
                InMemoryAxisMove memory = RequireInMemoryAxisMove(move);
                InMemoryAxis axis = FindAxis(memory.AxisName);
                if (axis.Operation != memory) throw new InvalidOperationException("轴占用凭证已失效：" + memory.AxisName);
                CheckReady(memory.AxisName, axis, memory.Home);
                axis.Position = memory.Start + (memory.Target - memory.Start) * Math.Min(1, Math.Max(0, progress));
            }
        }

        /// <summary>内存模型按进度判定到位；真实设备应按反馈位置和到位容差判定。</summary>
        public bool IsMoveComplete(ITaskAxisMove move, double progress)
        {
            RequireInMemoryAxisMove(move);
            return progress >= 1;
        }

        /// <summary>动作成功或异常退出后都释放占用；取消保留中途位置，不伪造到位。</summary>
        public Task EndMoveAsync(ITaskAxisMove move, bool completed, CancellationToken cancellationToken)
        {
            EndMoveCore(RequireInMemoryAxisMove(move), completed);
            return Task.CompletedTask;
        }

        /// <summary>停止节点仅确认闲置轴；不能跨分支夺走其他动作的控制权。</summary>
        public Task ConfirmStoppedAsync(string name, CancellationToken cancellationToken)
        {
            lock (_sync)
                if (FindAxis(name).Operation != null)
                    throw new InvalidOperationException("轴被其他动作占用；请取消所属运行并等待完成：" + name);
            return Task.CompletedTask;
        }

        /// <summary>释放动作占用；completed 为真时把位置落到终点并标记回零结果。</summary>
        private void EndMoveCore(InMemoryAxisMove move, bool completed)
        {
            lock (_sync)
            {
                InMemoryAxis axis = FindAxis(move.AxisName);
                if (axis.Operation != move) throw new InvalidOperationException("轴清理时占用不一致：" + move.AxisName);
                if (completed)
                {
                    axis.Position = move.Target;
                    if (move.Home) axis.Homed = true;
                }
                axis.Operation = null;
            }
        }

        /// <summary>只接受本设备产生的动作凭证；其他实现的凭证属于接线错误。</summary>
        private static InMemoryAxisMove RequireInMemoryAxisMove(ITaskAxisMove move)
        {
            var memory = move as InMemoryAxisMove;
            if (memory == null) throw new ArgumentException("动作凭证不属于本内存设备。", nameof(move));
            return memory;
        }

        /// <summary>按逻辑名称找轴。</summary>
        private InMemoryAxis FindAxis(string name)
        {
            InMemoryAxis axis;
            if (name == null || !_axes.TryGetValue(name, out axis)) throw new ArgumentException("轴未配置：" + name);
            return axis;
        }

        /// <summary>移动前和移动期间检查使能、位置参考及报警。</summary>
        private static void CheckReady(string name, InMemoryAxis axis, bool home)
        {
            if (!axis.Enabled) throw new InvalidOperationException("轴未使能：" + name);
            if (!string.IsNullOrWhiteSpace(axis.Alarm)) throw new InvalidOperationException("轴报警：" + name + "，" + axis.Alarm);
            if (!home && !axis.Homed) throw new InvalidOperationException("轴尚未回零：" + name);
        }

        /// <summary>依据实际起点和目录检查最终位置，拒绝无效和越界的动态目标。</summary>
        private void CheckPosition(string name, double position)
        {
            var info = Catalog.Axes[name];
            if (double.IsNaN(position) || double.IsInfinity(position) || !info.MinimumPosition.HasValue
                || !info.MaximumPosition.HasValue || position < info.MinimumPosition || position > info.MaximumPosition)
                throw new InvalidOperationException("轴目标超出配置行程或不是有限数：" + name + "，" + position);
        }

        /// <summary>仅在设备锁内读写的单轴状态。</summary>
        private sealed class InMemoryAxis
        {
            /// <summary>内存位置。</summary>
            internal double Position;
            /// <summary>测试使能状态。</summary>
            internal bool Enabled;
            /// <summary>位置参考状态。</summary>
            internal bool Homed;
            /// <summary>注入的报警文本。</summary>
            internal string Alarm;
            /// <summary>当前独占动作；为空表示未占用。</summary>
            internal InMemoryAxisMove Operation;
            /// <summary>累计接受动作次数。</summary>
            internal int AcceptedCommands;
        }
    }

    /// <summary>一次内存轴动作锁定的起点、终点和回零意图。</summary>
    internal sealed class InMemoryAxisMove : ITaskAxisMove
    {
        /// <summary>被占用的逻辑轴。</summary>
        internal string AxisName { get; }
        /// <summary>接受命令时的位置。</summary>
        internal double Start { get; }
        /// <summary>已经计算好的绝对终点，暂停后不重新叠加。</summary>
        internal double Target { get; }
        /// <summary>是否模拟回零。</summary>
        internal bool Home { get; }
        /// <summary>保存一次操作的固定参数。</summary>
        internal InMemoryAxisMove(string axisName, double start, double target, bool home)
        {
            AxisName = axisName; Start = start; Target = target; Home = home;
        }

        /// <summary>对执行器暴露的逻辑轴名称。</summary>
        string ITaskAxisMove.AxisName
        {
            get { return AxisName; }
        }

        /// <summary>对执行器暴露的接受命令位置。</summary>
        double ITaskAxisMove.Start
        {
            get { return Start; }
        }

        /// <summary>对执行器暴露的绝对终点。</summary>
        double ITaskAxisMove.Target
        {
            get { return Target; }
        }

        /// <summary>对执行器暴露的回零标记。</summary>
        bool ITaskAxisMove.IsHome
        {
            get { return Home; }
        }
    }
}
