using System;
using System.Collections.Generic;
using System.Linq;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.TaskEngine.Execution;
using MotionControl.TaskEngine.Machine;

namespace MotionControl.TaskEngine.Wpf.Monitoring
{
    /// <summary>把列表任务工位的内存证据投影到与 C# 工位相同的只读契约，不读取硬件。</summary>
    public static class TaskStationOperationProjection
    {
        /// <summary>保留所有活动末端的等待项；任何一项缺少诊断时明确标记集合不完整。</summary>
        public static StationOperationStatus Read(StationTaskSnapshot station)
        {
            if (station == null) throw new ArgumentNullException(nameof(station));
            var activeNodes = new List<TaskNodeSnapshot>();
            var nodes = new List<TaskNodeSnapshot>();
            if ((station.State == StationTaskState.Executing || station.State == StationTaskState.WaitingReset)
                && station.Run != null)
            {
                activeNodes.AddRange(station.Run.Nodes.Where(node => node.State == TaskNodeState.Running
                    || node.State == TaskNodeState.Waiting || node.State == TaskNodeState.Moving
                    || (station.State == StationTaskState.WaitingReset && node.State == TaskNodeState.Failed)));
                nodes = activeNodes.Where(node => !activeNodes.Any(child => child.ParentExecutionId == node.ExecutionId)).ToList();
            }
            var waits = new List<StationWaitItem>();
            bool provided = true;
            DateTime? captured = null;
            var descriptions = new List<string>();
            var pendingBranchNames = new List<string>();
            // 并行分支逐个登记。父项已有全部配置时，尚未登记的分支不能因过滤父项而消失。
            foreach (TaskNodeSnapshot parent in activeNodes)
            {
                if (nodes.Contains(parent) || parent.Diagnostics == null
                    || parent.Diagnostics.WaitKind != StationWaitKind.Parallel) continue;
                foreach (StationWaitItem item in parent.Diagnostics.WaitItems)
                {
                    if (item.ObjectKind != StationObjectKind.Step || string.IsNullOrEmpty(item.ObjectId)) continue;
                    bool registered = station.Run.Nodes.Any(child => child.ParentExecutionId == parent.ExecutionId
                        && child.NodeId == item.ObjectId);
                    if (registered) continue;
                    waits.Add(item);
                    pendingBranchNames.Add(item.ObjectName ?? item.ObjectId);
                    provided = false;
                    if (!captured.HasValue || parent.Diagnostics.CapturedAtUtc < captured.Value)
                        captured = parent.Diagnostics.CapturedAtUtc;
                }
            }
            foreach (TaskNodeSnapshot node in nodes)
            {
                if (!string.IsNullOrWhiteSpace(node.Detail)) descriptions.Add(node.Detail);
                if (node.State != TaskNodeState.Waiting && node.State != TaskNodeState.Moving
                    && node.State != TaskNodeState.Failed) continue;
                TaskStepDiagnostics diagnostics = node.Diagnostics;
                if (diagnostics == null || !diagnostics.HasWaitInformation) provided = false;
                if (diagnostics == null) continue;
                waits.AddRange(diagnostics.WaitItems);
                // 多项采样以最早证据的时间提示，不能用刷新时间伪装为新硬件采样。
                if (!captured.HasValue || diagnostics.CapturedAtUtc < captured.Value)
                    captured = diagnostics.CapturedAtUtc;
            }
            if (nodes.Count == 0 && (station.State == StationTaskState.WaitingRequest
                || station.State == StationTaskState.WaitingReset))
            {
                TaskStepDiagnostics diagnostics = station.Diagnostics;
                provided = diagnostics != null && diagnostics.HasWaitInformation;
                if (diagnostics != null)
                {
                    waits.AddRange(diagnostics.WaitItems);
                    captured = diagnostics.CapturedAtUtc;
                }
            }
            string description = descriptions.Count == 0 ? station.Detail : string.Join("；", descriptions);
            return new StationOperationStatus(station.StationId, station.Name,
                nodes.Count == 1 && pendingBranchNames.Count == 0 ? nodes[0].NodeId : null,
                nodes.Count == 0 ? null : string.Join(" / ", nodes.Select(node => node.Name).Concat(pendingBranchNames)),
                ReadState(station, nodes), waits, description, capturedAtUtc: captured,
                hasWaitInformation: provided);
        }

        /// <summary>只使用工位阶段和该工位运行状态，不把整机状态复制给所有工位。</summary>
        private static StationOperationState ReadState(StationTaskSnapshot station, IReadOnlyList<TaskNodeSnapshot> nodes)
        {
            switch (station.State)
            {
                case StationTaskState.Disabled: return StationOperationState.Disabled;
                case StationTaskState.WaitingInitialization: return StationOperationState.Idle;
                case StationTaskState.WaitingRequest: return StationOperationState.Waiting;
                case StationTaskState.WaitingReset: return StationOperationState.WaitingReset;
                case StationTaskState.Completed: return StationOperationState.Completed;
                case StationTaskState.Stopped: return StationOperationState.Stopped;
                case StationTaskState.Executing:
                    if (station.Run == null) return StationOperationState.NotProvided;
                    if (station.Run.State == TaskRunState.Paused) return StationOperationState.Paused;
                    if (nodes.Any(node => node.State == TaskNodeState.Waiting)) return StationOperationState.Waiting;
                    return StationOperationState.Running;
                default: return StationOperationState.NotProvided;
            }
        }
    }
}
