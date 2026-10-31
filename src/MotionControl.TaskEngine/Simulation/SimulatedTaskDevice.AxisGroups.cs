using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.TaskEngine.Device;

namespace MotionControl.TaskEngine.Simulation
{
    /// <summary>轴组定位的内存状态操作；先整组检查，再接受，各轴按自身速度推进。</summary>
    public sealed partial class SimulatedTaskDevice
    {
        /// <summary>所有起始条件和终点均合法才接受任一动作，避免前面的轴先动、后面的轴才报错。</summary>
        public IReadOnlyList<ITaskAxisGroupMove> BeginGroupMove(IReadOnlyList<TaskAxisGroupTarget> targets)
        {
            lock (_sync)
            {
                if (targets == null || targets.Count < 2 || targets.Count > 64)
                    throw new InvalidOperationException("内存定位组需要 2 到 64 根轴。");
                var moves = new List<SimulatedGroupAxisMove>();
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (TaskAxisGroupTarget target in targets)
                {
                    if (target == null || !names.Add(target.AxisName ?? string.Empty))
                        throw new InvalidOperationException("内存定位组目标为空或逻辑轴重复。");
                    SimulatedAxis axis = FindAxis(target.AxisName);
                    if (axis.Operation != null) throw new InvalidOperationException("轴被其他动作占用：" + target.AxisName);
                    CheckReady(target.AxisName, axis, false);
                    double end = target.Relative ? axis.Position + target.Target : target.Target;
                    CheckPosition(target.AxisName, end);
                    double duration = Math.Abs(end - axis.Position) / target.Speed * 1000;
                    if (target.Speed <= 0 || double.IsInfinity(target.Speed) || double.IsNaN(target.Speed)
                        || double.IsInfinity(duration) || double.IsNaN(duration))
                        throw new InvalidOperationException("轴速度或运动时长超出有限范围：" + target.AxisName);
                    moves.Add(new SimulatedGroupAxisMove(new SimulatedMove(target.AxisName, axis.Position, end, false), duration));
                }
                // 前面的检查不改变设备状态，整组通过后才一次登记接受的命令。
                foreach (SimulatedGroupAxisMove move in moves)
                {
                    SimulatedAxis axis = FindAxis(move.Operation.AxisName);
                    axis.Operation = move.Operation;
                    axis.AcceptedCommands++;
                }
                return moves;
            }
        }

        /// <summary>一次检查全部轴状态，再分别推进；快轴到位后继续保留组资源，等待慢轴。</summary>
        public bool AdvanceGroupMove(IReadOnlyList<ITaskAxisGroupMove> moves, double elapsedMilliseconds, out string detail)
        {
            lock (_sync)
            {
                List<SimulatedGroupAxisMove> group = RequireGroupMoves(moves);
                foreach (SimulatedGroupAxisMove move in group)
                {
                    string name = move.Operation.AxisName;
                    SimulatedAxis axis = FindAxis(name);
                    if (!move.Completed && axis.Operation != move.Operation)
                        throw new InvalidOperationException("轴组动作凭证失效：" + name);
                    // 已到位的轴也仍属于本组；它出现报警时不能让整组继续。
                    CheckReady(name, axis, false);
                }
                bool completed = true;
                var descriptions = new List<string>();
                foreach (SimulatedGroupAxisMove move in group)
                {
                    if (!move.Completed)
                    {
                        double progress = move.DurationMilliseconds == 0 ? 1 : elapsedMilliseconds / move.DurationMilliseconds;
                        AdvanceMove(move.Operation, progress);
                        if (IsMoveComplete(move.Operation, progress))
                        {
                            EndMoveCore(move.Operation, true);
                            move.Completed = true;
                        }
                    }
                    completed = completed && move.Completed;
                    string name = move.Operation.AxisName;
                    string currentPosition = FindAxis(name).Position.ToString("0.######", CultureInfo.CurrentCulture);
                    string targetPosition = move.Operation.Target.ToString("0.######", CultureInfo.CurrentCulture);
                    descriptions.Add(name + " 当前=" + currentPosition + "，目标=" + targetPosition
                        + " " + Catalog.Axes[name].EngineeringUnit + (move.Completed ? "（已到位）" : "（运动中）"));
                }
                detail = "轴组定位：" + string.Join("；", descriptions);
                return completed;
            }
        }

        /// <summary>退出组动作时停下所有未完成轴；保留中途位置，不将取消伪装为到位。</summary>
        public Task EndGroupMoveAsync(IReadOnlyList<ITaskAxisGroupMove> moves, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                foreach (SimulatedGroupAxisMove move in RequireGroupMoves(moves))
                    if (!move.Completed) EndMoveCore(move.Operation, false);
            }
            return Task.CompletedTask;
        }

        /// <summary>只接受本设备产生的组动作凭证；其他实现的凭证属于接线错误。</summary>
        private static List<SimulatedGroupAxisMove> RequireGroupMoves(IReadOnlyList<ITaskAxisGroupMove> moves)
        {
            if (moves == null) throw new ArgumentNullException(nameof(moves));
            var group = new List<SimulatedGroupAxisMove>(moves.Count);
            foreach (ITaskAxisGroupMove move in moves)
            {
                var simulated = move as SimulatedGroupAxisMove;
                if (simulated == null) throw new ArgumentException("轴组动作凭证不属于本内存设备。", nameof(moves));
                group.Add(simulated);
            }
            return group;
        }
    }
}
