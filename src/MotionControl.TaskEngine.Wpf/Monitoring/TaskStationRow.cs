using System.ComponentModel;
using System.Linq;
using System;
using MotionControl.TaskEngine.Execution;
using MotionControl.TaskEngine.Machine;

namespace MotionControl.TaskEngine.Wpf.Monitoring
{
    /// <summary>上方工位表的稳定行，同时表达多条活动分支。</summary>
    public sealed class TaskStationRow : INotifyPropertyChanged
    {
        /// <summary>通知文字变化，不改变集合选择。</summary>
        public event PropertyChangedEventHandler PropertyChanged;
        /// <summary>工位稳定编号。</summary>
        public string Id { get; }
        /// <summary>最新工位快照。</summary>
        public StationTaskSnapshot Snapshot { get; private set; }
        /// <summary>工位中文名。</summary>
        public string Name => Snapshot.Name;
        /// <summary>单次、循环或信号触发。</summary>
        public string Mode { get; private set; }
        /// <summary>工位状态按工位阶段显示；复位后保留故障步骤但提示等待启动。</summary>
        public string State { get; private set; }
        /// <summary>全部正在执行的末端步骤名称。</summary>
        public string Action { get; private set; }
        /// <summary>当前等待和故障原因。</summary>
        public string Detail { get; private set; }
        /// <summary>完整的原始等待或故障诊断，供维护人员悬停查看。</summary>
        public string EngineeringDetail { get; private set; }
        /// <summary>成功周期数。</summary>
        public long Cycles => Snapshot.CompletedCycles;
        /// <summary>有对应文字的状态色。</summary>
        public string Color { get; private set; }

        /// <summary>创建工位行，运行数据通过 Update 设置。</summary>
        public TaskStationRow(string id) { Id = id; }

        /// <summary>只刷新行内容；并行组中多个末端动作同时显示。</summary>
        public void Update(StationTaskSnapshot snapshot, TaskMachineState machineState, Func<string, string> text)
        {
            Snapshot = snapshot;
            Mode = text(snapshot.Mode == StationTaskMode.Once ? "单次" : snapshot.Mode == StationTaskMode.Cyclic ? "循环" : "信号触发");
            var active = snapshot.Run?.Nodes.Where(node => node.State == TaskNodeState.Running || node.State == TaskNodeState.Waiting || node.State == TaskNodeState.Moving).ToList();
            var leaves = active?.Where(node => !active.Any(child => child.ParentExecutionId == node.ExecutionId)).ToList();
            var failed = snapshot.State == StationTaskState.WaitingReset
                ? snapshot.Run?.Nodes.Where(node => node.State == TaskNodeState.Failed).ToList() : null;
            Action = failed != null && failed.Count > 0 ? string.Join(" / ", failed.Select(node => node.Name))
                : leaves != null && leaves.Count > 0 ? string.Join(" / ", leaves.Select(node => node.Name)) : "—";
            EngineeringDetail = failed != null && failed.Count > 0
                ? string.Join("；", failed.Select(node => node.Detail)) : snapshot.Detail;
            Detail = failed != null && failed.Count > 0
                ? string.Join("；", failed.Select(node => TaskOperatorText.Detail(node, text))) : TaskOperatorText.StationDetail(snapshot.Detail, text);
            if ((failed == null || failed.Count == 0) && leaves != null && leaves.Count > 0)
            {
                EngineeringDetail = string.Join("；", leaves.Select(node => node.Detail));
                Detail = string.Join("；", leaves.Select(node => TaskOperatorText.Detail(node, text)));
            }
            if (machineState != TaskMachineState.Running && !string.IsNullOrEmpty(Detail))
            {
                Detail = Detail.Replace("（运动中）", "（设备未运行，目标未到位）")
                    .Replace("运动中：", "设备未运行，目标未到位：");
                EngineeringDetail = EngineeringDetail?.Replace("（运动中）", "（设备未运行，目标未到位）")
                    .Replace("运动中：", "设备未运行，目标未到位：");
            }
            string state = "等待"; Color = "#596B7D";
            if (snapshot.State == StationTaskState.Disabled) state = "未启用";
            else if (snapshot.State == StationTaskState.WaitingReset && machineState == TaskMachineState.Ready) state = "待启动";
            else if (snapshot.State == StationTaskState.WaitingReset) { state = "故障"; Color = "#B42318"; }
            else if (snapshot.State == StationTaskState.Completed) { state = "已完成"; Color = "#16824B"; }
            else if (snapshot.State == StationTaskState.Stopped) state = "已终止";
            else if (machineState == TaskMachineState.Faulted || machineState == TaskMachineState.NeedsReset)
            {
                state = machineState.ToString();
                Color = machineState == TaskMachineState.NeedsReset ? "#946200" : "#B42318";
            }
            else if (machineState != TaskMachineState.Running) state = machineState.ToString();
            else if (snapshot.State == StationTaskState.Executing)
            {
                state = leaves?.Any(node => node.State == TaskNodeState.Moving) == true ? "运动中"
                    : leaves?.Any(node => node.State == TaskNodeState.Running) == true ? "运行"
                    : WaitingState(leaves, text);
                Color = "#176BD0";
            }
            State = text(state);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        }

        /// <summary>多个并行步骤同时等待时，优先显示需要现场人员关注的等待类别。</summary>
        private static string WaitingState(System.Collections.Generic.IEnumerable<TaskNodeSnapshot> leaves,
            Func<string, string> text)
        {
            var keys = leaves?.Where(node => node.State == TaskNodeState.Waiting)
                .Select(TaskOperatorText.WaitingStateKey).Distinct().ToList();
            if (keys == null || keys.Count == 0) return text("等待");

            string[] priority = { "等待输入", "等待条件", "等待互锁", "等待资源", "工艺延时", "等待并行步骤" };
            string selected = priority.FirstOrDefault(keys.Contains);
            if (selected == null) selected = keys.Count > 1 ? "等待多项" : keys[0];
            return text(selected);
        }
    }
}
