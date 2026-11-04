using System;
using System.Collections.Generic;
using System.Linq;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Host.Wpf.Operation;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Execution;
using MotionControl.TaskEngine.Machine;
using MotionControl.TaskEngine.Wpf.Monitoring;

namespace MotionControl.Host.Wpf.Tasks
{
    /// <summary>将当前任务会话的只读工位快照交给操作员总览，并定位实际监控工位。</summary>
    internal sealed partial class TaskMonitorLauncher
    {
        /// <summary>仅采样当前连接会话的内存数据；未读取流程时返回空来源，不查询控制卡。</summary>
        internal IReadOnlyList<StationOverviewData> ReadStationOverview()
        {
            if (!IsControllerConnected() || _runtime.ControlOwner != MachineControlOwner.TaskEngine
                || _session == null || _monitor == null) return null;

            TaskMachineSnapshot snapshot = _session.Machine.GetSnapshot();
            var stations = new List<StationTaskSnapshot>();
            if (snapshot.Initialization != null) stations.Add(snapshot.Initialization);
            stations.AddRange(snapshot.Stations);
            var result = new List<StationOverviewData>();
            foreach (StationTaskSnapshot station in stations)
            {
                var row = new TaskStationRow(station.StationId);
                row.Update(station, snapshot.State, _monitor.T);
                bool retainedStep = station.State == StationTaskState.Executing
                    || station.State == StationTaskState.WaitingReset;
                result.Add(new StationOverviewData
                {
                    Id = station.StationId,
                    Name = station.Name,
                    StateText = row.State,
                    StateColor = row.Color,
                    // 请求等待、初始化和已完成阶段不把 LastRun 的活动节点当作当前动作。
                    CurrentStep = retainedStep ? row.Action
                        : station.State == StationTaskState.Completed ? PageText("本次动作已完成", "Current operation completed")
                        : station.State == StationTaskState.WaitingRequest ? PageText("等待启动条件", "Waiting for start conditions")
                        : station.State == StationTaskState.Disabled ? PageText("未参与自动运行", "Not participating in automatic operation")
                        : station.State == StationTaskState.Stopped ? PageText("工位已停止", "Station stopped")
                        : PageText("尚未开始执行", "Not started"),
                    Detail = retainedStep ? row.Detail : station.Detail,
                    WaitingInputName = retainedStep && snapshot.State != TaskMachineState.Ready
                        ? FindWaitingInput(station) : null
                });
            }
            return result.AsReadOnly();
        }

        /// <summary>按工位唯一标识或显示名定位当前步骤；不读取配置、不发运动命令。</summary>
        internal bool FocusStationByName(string name)
        {
            return IsControllerConnected() && _runtime.ControlOwner == MachineControlOwner.TaskEngine
                && _session != null && _monitor != null && _monitor.FocusStationByName(name);
        }

        /// <summary>仅从当前等待或故障的 WaitInput 定义取得唯一输入，不解析动作说明猜测信号。</summary>
        private string FindWaitingInput(StationTaskSnapshot station)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (TaskNodeSnapshot node in station.Run?.Nodes ?? new TaskNodeSnapshot[0])
            {
                if (node.State != TaskNodeState.Waiting && node.State != TaskNodeState.Failed) continue;
                if (!_session.Library.TryGetValue(node.TaskId, out TaskDefinition definition)) continue;
                WaitInputNode input = FindWaitInputDefinition(definition.Root, node.NodeId);
                if (!string.IsNullOrWhiteSpace(input?.InputName)) names.Add(input.InputName);
            }
            return names.Count == 1 ? names.Single() : null;
        }

        /// <summary>在一个任务定义内查找对应等待输入节点；子任务按快照的 TaskId 单独查找。</summary>
        private static WaitInputNode FindWaitInputDefinition(TaskNode node, string nodeId)
        {
            if (node == null) return null;
            if (string.Equals(node.Id, nodeId, StringComparison.OrdinalIgnoreCase)) return node as WaitInputNode;
            IEnumerable<TaskNode> children = null;
            if (node is SequenceNode sequence) children = sequence.Children;
            else if (node is ParallelGroupNode parallel) children = parallel.Branches;
            else if (node is ResourceScopeNode resource) children = new[] { resource.Body };
            else if (node is ConditionBranchNode branch) children = new[] { branch.WhenTrue, branch.WhenFalse };
            if (children == null) return null;
            foreach (TaskNode child in children)
            {
                WaitInputNode found = FindWaitInputDefinition(child, nodeId);
                if (found != null) return found;
            }
            return null;
        }
    }
}
