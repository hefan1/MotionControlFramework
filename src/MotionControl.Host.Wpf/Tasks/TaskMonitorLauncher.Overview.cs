using System;
using System.Collections.Generic;
using System.Linq;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Host.Wpf.Operation;
using MotionControl.TaskEngine.Machine;
using MotionControl.TaskEngine.Resources;
using MotionControl.TaskEngine.Wpf.Monitoring;

namespace MotionControl.Host.Wpf.Tasks
{
    /// <summary>将当前任务会话的只读工位快照交给操作员总览，并定位实际监控工位。</summary>
    internal sealed partial class TaskMonitorLauncher
    {
        /// <summary>只读汇总本会话待确认资源；不采样硬件，也不把保持输出的命令当成夹具反馈。</summary>
        internal string ReadOverviewSafetySummary()
        {
            var session = _session;
            if (session == null) return null;
            var blocked = session.TaskDevice.ReadResources()
                .Where(item => item.State == TaskResourceState.Blocked).ToList();
            if (blocked.Count == 0) return null;
            string owners = string.Join("、", blocked.Select(item => item.TaskName)
                .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct().Take(3));
            string scope = string.IsNullOrWhiteSpace(owners) ? string.Empty : " · " + owners;
            return string.Format(PageText("待确认资源：{0} 组", "Resources awaiting inspection: {0} group(s)"), blocked.Count)
                + scope + "\n" + PageText("保持输出可能仍有效，夹具反馈和物料状态须现场核查；请进入工位运行查看资源及处理条件。",
                    "Holding outputs may remain active. Check fixture feedback and material state on site; open Station operation for resources and handling conditions.");
        }

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
                StationOperationStatus status = TaskStationOperationProjection.Read(station);
                result.Add(new StationOverviewData
                {
                    Id = status.StationId,
                    Name = status.StationName,
                    IndependentState = status.State,
                    StateText = _monitor.T(StationOperatorPresentation.StateKey(status.State)),
                    StateColor = row.Color,
                    CurrentStepId = status.StepId,
                    CurrentStep = status.StepName,
                    Detail = status.Description,
                    WaitItems = status.WaitItems,
                    HasWaitInformation = status.HasWaitInformation,
                    NextAction = status.NextAction,
                    CompletionCondition = status.CompletionCondition,
                    StatusCapturedAtUtc = status.CapturedAtUtc,
                    WaitingInputName = OperationSnapshot.FindSingleWaitingInput(status.WaitItems, status.HasWaitInformation),
                    CompletedCycles = station.CompletedCycles
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

    }
}
