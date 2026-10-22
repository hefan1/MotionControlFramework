using System;

namespace MotionControl.TaskEngine.Simulation
{
    /// <summary>单轴匀速进度模型；不模拟伺服动力学、限位电路或厂商回零模式。</summary>
    public sealed partial class SimulatedTaskDevice
    {
        /// <summary>设置停止轴的测试起始条件；禁止覆盖正在运行的轴。</summary>
        public void ConfigureAxis(string name, double position, bool enabled, bool homed)
        {
            lock (_sync)
            {
                SimulatedAxis axis = FindAxis(name);
                if (axis.Operation != null) throw new InvalidOperationException("仿真轴仍被占用：" + name);
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
        public SimulatedAxisSnapshot ReadAxis(string name)
        {
            lock (_sync)
            {
                SimulatedAxis axis = FindAxis(name);
                return new SimulatedAxisSnapshot(name, axis.Position, axis.Enabled, axis.Homed,
                    axis.Operation != null, axis.Alarm, axis.AcceptedCommands);
            }
        }

        /// <summary>接受一次单轴操作并锁定终点；同轴争用立即失败，不伪装资源仲裁。</summary>
        internal SimulatedMove BeginMove(string name, double target, bool relative, bool home)
        {
            lock (_sync)
            {
                SimulatedAxis axis = FindAxis(name);
                if (axis.Operation != null) throw new InvalidOperationException("仿真轴被其他动作占用：" + name);
                CheckReady(name, axis, home);
                double end = home ? 0 : relative ? axis.Position + target : target;
                CheckPosition(name, end);
                var move = new SimulatedMove(name, axis.Position, end, home);
                axis.Operation = move;
                axis.AcceptedCommands++;
                if (home) axis.Homed = false;
                return move;
            }
        }

        /// <summary>更新匀速模型的位置；真实运动完成检测由未来设备接入层承担。</summary>
        internal void AdvanceMove(SimulatedMove move, double progress)
        {
            lock (_sync)
            {
                SimulatedAxis axis = FindAxis(move.AxisName);
                if (axis.Operation != move) throw new InvalidOperationException("仿真轴占用凭证已失效：" + move.AxisName);
                CheckReady(move.AxisName, axis, move.Home);
                axis.Position = move.Start + (move.Target - move.Start) * Math.Min(1, Math.Max(0, progress));
            }
        }

        /// <summary>动作成功或异常退出后都释放占用；取消保留中途位置，不伪造到位。</summary>
        internal void EndMove(SimulatedMove move, bool completed)
        {
            lock (_sync)
            {
                SimulatedAxis axis = FindAxis(move.AxisName);
                if (axis.Operation != move) throw new InvalidOperationException("仿真轴清理时占用不一致：" + move.AxisName);
                if (completed)
                {
                    axis.Position = move.Target;
                    if (move.Home) axis.Homed = true;
                }
                axis.Operation = null;
            }
        }

        /// <summary>停止节点仅确认闲置轴；不能跨分支夺走其他动作的控制权。</summary>
        internal void ConfirmStopped(string name)
        {
            lock (_sync)
                if (FindAxis(name).Operation != null)
                    throw new InvalidOperationException("轴被其他仿真动作占用；请取消所属运行并等待完成：" + name);
        }

        /// <summary>按逻辑名称找轴。</summary>
        private SimulatedAxis FindAxis(string name)
        {
            SimulatedAxis axis;
            if (name == null || !_axes.TryGetValue(name, out axis)) throw new ArgumentException("仿真轴不存在：" + name);
            return axis;
        }

        /// <summary>移动前和移动期间检查使能、位置参考及报警。</summary>
        private static void CheckReady(string name, SimulatedAxis axis, bool home)
        {
            if (!axis.Enabled) throw new InvalidOperationException("仿真轴未使能：" + name);
            if (!string.IsNullOrWhiteSpace(axis.Alarm)) throw new InvalidOperationException("仿真轴报警：" + name + "，" + axis.Alarm);
            if (!home && !axis.Homed) throw new InvalidOperationException("仿真轴尚未回零：" + name);
        }

        /// <summary>依据实际起点和目录检查最终位置，拒绝无效和越界的动态目标。</summary>
        private void CheckPosition(string name, double position)
        {
            var info = Catalog.Axes[name];
            if (double.IsNaN(position) || double.IsInfinity(position) || !info.MinimumPosition.HasValue
                || !info.MaximumPosition.HasValue || position < info.MinimumPosition || position > info.MaximumPosition)
                throw new InvalidOperationException("仿真轴目标超出配置行程或不是有限数：" + name + "，" + position);
        }

        /// <summary>仅在设备锁内读写的单轴状态。</summary>
        private sealed class SimulatedAxis
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
            internal SimulatedMove Operation;
            /// <summary>累计接受动作次数。</summary>
            internal int AcceptedCommands;
        }
    }

    /// <summary>一次内存轴动作锁定的起点、终点和回零意图。</summary>
    internal sealed class SimulatedMove
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
        internal SimulatedMove(string axisName, double start, double target, bool home)
        {
            AxisName = axisName; Start = start; Target = target; Home = home;
        }
    }
}
