using System;
using System.Collections.Generic;
using MotionControl.TaskEngine.Execution;
using MotionControl.TaskEngine.Resources;

namespace MotionControl.TaskEngine.Machine
{
    /// <summary>内存整机的运行状态；正常暂停后必须复位再启动。</summary>
    public enum TaskMachineState
    {
        /// <summary>新会话等待首次复位。</summary>
        NeedsReset,
        /// <summary>复位检查通过，尚未启动。</summary>
        Ready,
        /// <summary>自动运行许可有效，各工位独立推进或等待请求。</summary>
        Running,
        /// <summary>操作员暂停，保存各工位当前进度。</summary>
        Paused,
        /// <summary>停机故障已锁存，全部工位等待条件恢复和复位。</summary>
        Faulted,
        /// <summary>已明确终止，旧会话不能重新启动。</summary>
        Stopped,
        /// <summary>全部任务退出并归还设备所有权。</summary>
        Closed
    }

    /// <summary>一个工位在当前生产会话中的位置。</summary>
    public enum StationTaskState
    {
        /// <summary>未参加自动运行。</summary>
        Disabled,
        /// <summary>等待设备启动或初始化成功。</summary>
        WaitingInitialization,
        /// <summary>等待请求/完成信号按协议变更。</summary>
        WaitingRequest,
        /// <summary>正在执行一个周期。</summary>
        Executing,
        /// <summary>当前节点故障，保存进度等待复位。</summary>
        WaitingReset,
        /// <summary>单次工位已完成。</summary>
        Completed,
        /// <summary>会话已终止。</summary>
        Stopped
    }

    /// <summary>供监控页读取的工位快照，定义和运行数据分别保存。</summary>
    public sealed class StationTaskSnapshot
    {
        /// <summary>工位唯一标识。</summary>
        public string StationId { get; }
        /// <summary>工位显示名。</summary>
        public string Name { get; }
        /// <summary>本工位执行的任务标识。</summary>
        public string TaskId { get; }
        /// <summary>循环或触发方式。</summary>
        public StationTaskMode Mode { get; }
        /// <summary>当前工位阶段，全机暂停状态另看整机 State。</summary>
        public StationTaskState State { get; }
        /// <summary>已成功完成的周期数。</summary>
        public long CompletedCycles { get; }
        /// <summary>等待的信号或阶段说明。</summary>
        public string Detail { get; }
        /// <summary>当前或最近一个周期的节点进度，尚未运行时为空。</summary>
        public TaskRunSnapshot Run { get; }

        /// <summary>在整机锁内复制工位状态，不向界面暴露控制句柄。</summary>
        internal StationTaskSnapshot(StationExecutionControl station)
        {
            StationId = station.Configuration.StationId; Name = station.Configuration.Name;
            TaskId = station.Configuration.TaskId; Mode = station.Configuration.Mode;
            State = station.State; CompletedCycles = station.CompletedCycles; Detail = station.Detail;
            Run = station.CurrentRun?.GetSnapshot() ?? station.LastRun;
        }
    }

    /// <summary>一次锁存故障，保留工位、任务和节点上下文。</summary>
    public sealed class TaskMachineFault
    {
        /// <summary>故障来源工位或外部停机报警代码。</summary>
        public string Source { get; }
        /// <summary>节点异常，外部停机报警时为空。</summary>
        public TaskExecutionException Error { get; }
        /// <summary>可直接显示的中文原因。</summary>
        public string Message { get; }
        /// <summary>首次记录的 UTC 时间。</summary>
        public DateTime OccurredAtUtc { get; } = DateTime.UtcNow;
        /// <summary>保存本次故障证据。</summary>
        internal TaskMachineFault(string source, string message, TaskExecutionException error)
        {
            Source = source; Message = message; Error = error;
        }
    }

    /// <summary>整机、各工位和共享资源的一次只读监控副本。</summary>
    public sealed class TaskMachineSnapshot
    {
        /// <summary>设备标识。</summary>
        public string MachineId { get; }
        /// <summary>整机状态。</summary>
        public TaskMachineState State { get; }
        /// <summary>本会话初始化已成功或没有配置初始化。</summary>
        public bool InitializationCompleted { get; }
        /// <summary>可选初始化工位的进度。</summary>
        public StationTaskSnapshot Initialization { get; }
        /// <summary>配置顺序中的全部工位，包含未启用项。</summary>
        public IReadOnlyList<StationTaskSnapshot> Stations { get; }
        /// <summary>等待复位确认的故障副本。</summary>
        public IReadOnlyList<TaskMachineFault> Faults { get; }
        /// <summary>所有等待、持有和待确认资源的副本。</summary>
        public IReadOnlyList<TaskResourceSnapshot> Resources { get; }

        /// <summary>保存控制器在锁内取得的列表副本。</summary>
        internal TaskMachineSnapshot(string machineId, TaskMachineState state, bool initialized,
            StationTaskSnapshot initialization, List<StationTaskSnapshot> stations,
            List<TaskMachineFault> faults, IReadOnlyList<TaskResourceSnapshot> resources)
        {
            MachineId = machineId; State = state; InitializationCompleted = initialized; Initialization = initialization;
            Stations = stations.AsReadOnly(); Faults = faults.AsReadOnly(); Resources = resources;
        }
    }
}
