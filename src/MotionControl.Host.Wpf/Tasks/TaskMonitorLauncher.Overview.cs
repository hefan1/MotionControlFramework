using System;
using System.Collections.Generic;
using System.Linq;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Runtime.Machine;
using MotionControl.Host.Wpf.Operation;
using MotionControl.TaskEngine.Machine;
using MotionControl.TaskEngine.MachineDevice;
using MotionControl.TaskEngine.Resources;
using MotionControl.TaskEngine.Wpf.Monitoring;

namespace MotionControl.Host.Wpf.Tasks
{
    /// <summary>将当前任务会话的只读工位快照交给操作员总览，并定位实际监控工位。</summary>
    internal sealed partial class TaskMonitorLauncher
    {
        /// <summary>资源证据只属于仍绑定本次 Runtime 和连接的保留会话，不要求准备配置仍是原发布代次。</summary>
        internal object ReadOverviewSafetySessionIdentity(MachineRuntime runtime, Guid? connectionSession)
        {
            var session = _session;
            var device = session?.TaskDevice as MachineTaskDevice;
            if (session == null || runtime == null || !connectionSession.HasValue
                || !ReferenceEquals(runtime, _runtime) || !IsControllerConnected()
                || runtime.ControlOwner != MachineControlOwner.TaskEngine || !runtime.IsTaskEngineControlClaimed
                || !runtime.IsConnectionSessionCurrent(connectionSession)
                || device?.BoundConnectionSessionId != connectionSession) return null;
            return ReferenceEquals(session, _session) && ReferenceEquals(runtime, _runtime) ? session : null;
        }

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

        /// <summary>只读取有效发布会话的工位内存快照；读取期间来源变化时撤下本次结果。</summary>
        internal IReadOnlyList<StationOverviewData> ReadStationOverview()
        {
            var session = CurrentPublishedTaskSession;
            var monitor = _monitor;
            if (session == null || monitor == null) return null;

            TaskMachineSnapshot snapshot = session.Machine.GetSnapshot();
            var stations = new List<StationTaskSnapshot>();
            if (snapshot.Initialization != null) stations.Add(snapshot.Initialization);
            stations.AddRange(snapshot.Stations);
            var result = new List<StationOverviewData>();
            foreach (StationTaskSnapshot station in stations)
            {
                var row = new TaskStationRow(station.StationId);
                row.Update(station, snapshot.State, monitor.T);
                StationOperationStatus status = TaskStationOperationProjection.Read(station);
                result.Add(new StationOverviewData
                {
                    Id = status.StationId,
                    Name = status.StationName,
                    IndependentState = status.State,
                    StateText = monitor.T(StationOperatorPresentation.StateKey(status.State)),
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
            return ReferenceEquals(session, CurrentPublishedTaskSession) && ReferenceEquals(monitor, _monitor)
                ? result.AsReadOnly() : null;
        }

        /// <summary>只定位仍有效的发布会话工位，并在定位后复核来源；不读取配置、不发运动命令。</summary>
        internal bool FocusStationByName(string name)
        {
            var session = CurrentPublishedTaskSession;
            var monitor = _monitor;
            if (session == null || monitor == null || !monitor.FocusStationByName(name)) return false;
            return ReferenceEquals(session, CurrentPublishedTaskSession) && ReferenceEquals(monitor, _monitor);
        }

    }
}
