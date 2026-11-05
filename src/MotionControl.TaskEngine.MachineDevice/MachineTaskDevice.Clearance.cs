using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Runtime.Machine;
using MotionControl.TaskEngine.Resources;

namespace MotionControl.TaskEngine.MachineDevice
{
    /// <summary>在任务持权期间继承待确认资源；设备动作仍由 Runtime 统一管理。</summary>
    public sealed partial class MachineTaskDevice : IControlledClearanceResources
    {
        /// <summary>独占全部原隔离资源及清料新增资源，旧记录和任务控制权均保持不变。</summary>
        IControlledClearanceReservation IControlledClearanceResources.ReserveClearance(IReadOnlyCollection<string> requiredResources)
        {
            if (requiredResources == null) throw new ArgumentNullException(nameof(requiredResources));
            lock (_sync)
            {
                if (_journal == null || _journalCleared) throw new InvalidOperationException("清料必须有有效的设备资源持久记录。");
                if (_changingOwner || _machineOwner == null || !_runtime.IsTaskEngineControlClaimed || _runtime.ControlOwner != MachineControlOwner.TaskEngine)
                    throw new InvalidOperationException("任务会话未持有设备控制权，不能继承隔离资源。");
                if (!_motionCards.IsConnected) throw new InvalidOperationException("控制卡未连接，不能清料。");
                RemoveSettledMoves();
                if (_activeRuns.Count != 0 || _pendingStops.Count != 0 || _beginningAxes.Count != 0
                    || _issuingBoundOutputs != 0 || _pendingOutputWrites.Count != 0 || _unsettledMoves.Any(item => !item.IsQuiescent))
                    throw new InvalidOperationException("任务或轴动作仍在执行或收尾，不能清料。");
                TaskResourceSnapshot[] snapshot = _resources.Read().ToArray();
                if (snapshot.Length == 0 || snapshot.Any(item => item.State != TaskResourceState.Blocked))
                    throw new InvalidOperationException("清料仅能继承全部已停止且待确认的任务资源。");
                string[] names = snapshot.SelectMany(item => item.Resources).Concat(requiredResources)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                TaskClearanceLease lease = _resources.BeginClearance(snapshot.Select(item => item.RequestId), names);
                TaskResourceCoordinator.ClearanceSequence sequence = _resources.BeginClearanceSequence(lease);
                return new ClearanceReservation(_resources, lease, sequence);
            }
        }

        /// <summary>只允许成功结束或锁存失败；失败不丢弃序列和原资源证据。</summary>
        private sealed class ClearanceReservation : IControlledClearanceReservation
        {
            private readonly object _sync = new object();
            private readonly TaskResourceCoordinator _resources;
            private readonly TaskClearanceLease _lease;
            private readonly TaskResourceCoordinator.ClearanceSequence _sequence;
            private bool _ended;
            internal ClearanceReservation(TaskResourceCoordinator resources, TaskClearanceLease lease,
                TaskResourceCoordinator.ClearanceSequence sequence)
            { _resources = resources; _lease = lease; _sequence = sequence; }
            public Task Ready => _lease.JournalWrite;
            public void MarkUnconfirmed(string reason)
            {
                lock (_sync)
                {
                    if (_ended) throw new InvalidOperationException("清料资源凭证已结束。");
                    _resources.MarkClearanceUnconfirmed(_lease, reason);
                }
            }
            public void Complete()
            {
                lock (_sync)
                {
                    if (_ended) throw new InvalidOperationException("清料资源凭证已结束。");
                    if (_lease.UnconfirmedReason != null) throw new InvalidOperationException("清料资源仍待确认：" + _lease.UnconfirmedReason);
                    _sequence.Dispose();
                    _resources.EndClearance(_lease);
                    _ended = true;
                }
            }
            public void ConfirmRecovery()
            {
                lock (_sync)
                {
                    if (_ended) throw new InvalidOperationException("清料资源凭证已结束。");
                    _resources.ConfirmClearanceRecovery(_lease, _sequence);
                    _ended = true;
                }
            }
        }
    }
}
