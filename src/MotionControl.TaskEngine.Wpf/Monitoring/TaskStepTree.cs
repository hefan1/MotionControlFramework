using System;
using System.Collections.Generic;
using System.Linq;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Execution;
using MotionControl.TaskEngine.Machine;

namespace MotionControl.TaskEngine.Wpf.Monitoring
{
    /// <summary>把定义树与本轮调用树关联；重复调用同一个子任务时按父调用区分。</summary>
    public static class TaskStepTree
    {
        /// <summary>建立选中工位的步骤，限制展开规模，避免合法的大任务库卡住界面。</summary>
        public static IReadOnlyList<TaskStepRow> Build(string taskId, IReadOnlyDictionary<string, TaskDefinition> library)
        {
            var result = new List<TaskStepRow>();
            int count = 0;
            var root = (SequenceNode)library[taskId].Root;
            for (int index = 0; index < root.Children.Count; index++)
                result.Add(Create(taskId, root.Children[index], ((index + 1) * 10).ToString(), null, library, ref count));
            return result;
        }

        /// <summary>按具体结构添加子行，避免通过反射推断节点含义。</summary>
        private static TaskStepRow Create(string taskId, TaskNode node, string number, TaskStepRow parent,
            IReadOnlyDictionary<string, TaskDefinition> library, ref int count)
        {
            if (++count > 10000) throw new InvalidOperationException("步骤显示超过 10000 行，请拆分工位任务。");
            var row = new TaskStepRow(taskId, node, number, parent);
            var children = new List<TaskNode>();
            string childTaskId = taskId;
            if (node is SequenceNode sequence) children.AddRange(sequence.Children);
            else if (node is ParallelGroupNode parallel) children.AddRange(parallel.Branches);
            else if (node is ConditionBranchNode branch)
            {
                children.Add(branch.WhenTrue);
                if (branch.WhenFalse != null) children.Add(branch.WhenFalse);
            }
            else if (node is ResourceScopeNode resource) children.Add(resource.Body);
            else if (node is CallTaskNode call) { childTaskId = call.TaskId; children.Add(library[call.TaskId].Root); }
            for (int index = 0; index < children.Count; index++)
                row.Children.Add(Create(childTaskId, children[index], ((index + 1) * 10).ToString(), row, library, ref count));
            return row;
        }

        /// <summary>平铺仅用于查找当前动作，不改变树的显示顺序。</summary>
        public static IEnumerable<TaskStepRow> Flatten(IEnumerable<TaskStepRow> rows)
        {
            foreach (var row in rows)
            {
                yield return row;
                foreach (var child in Flatten(row.Children)) yield return child;
            }
        }

        /// <summary>把快照匹配到定义调用路径，并按整机状态显示活动步骤。</summary>
        public static void Update(IEnumerable<TaskStepRow> rows, TaskRunSnapshot run, TaskMachineState machineState, Func<string, string> text)
        {
            var nodes = run?.Nodes ?? new TaskNodeSnapshot[0];
            var byParent = nodes.ToLookup(node => node.ParentExecutionId ?? string.Empty);
            var root = nodes.FirstOrDefault(node => string.IsNullOrEmpty(node.ParentExecutionId));
            foreach (var row in rows) Apply(row, root?.ExecutionId, byParent, false, machineState, text);
        }

        /// <summary>逐层匹配父调用，确保同一子任务的两次调用互不覆盖。</summary>
        private static void Apply(TaskStepRow row, string parentId, ILookup<string, TaskNodeSnapshot> nodes,
            bool notSelected, TaskMachineState machineState, Func<string, string> text)
        {
            var snapshot = parentId == null ? null : nodes[parentId].FirstOrDefault(node =>
                string.Equals(node.TaskId, row.TaskId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(node.NodeId, row.Node.Id, StringComparison.OrdinalIgnoreCase));
            string state = notSelected ? "未选择" : "未执行";
            string color = "#596B7D";
            if (snapshot != null)
            {
                switch (snapshot.State)
                {
                    case TaskNodeState.Completed: state = "已完成"; color = "#16824B"; break;
                    case TaskNodeState.Failed: state = "故障"; color = "#B42318"; break;
                    case TaskNodeState.Cancelled: state = "已终止"; break;
                    default:
                        if (machineState == TaskMachineState.Faulted || machineState == TaskMachineState.NeedsReset)
                        {
                            state = machineState.ToString();
                            color = "#B42318";
                        }
                        else if (machineState != TaskMachineState.Running)
                        {
                            state = machineState.ToString();
                        }
                        else
                        {
                            state = snapshot.State == TaskNodeState.Waiting ? "等待" : "运行";
                            color = "#176BD0";
                        }
                        break;
                }
            }
            string detail = snapshot?.Detail ?? text(notSelected ? "本次未选择此分支" : "尚未进入本步骤");
            if (snapshot != null && machineState != TaskMachineState.Running)
                detail = detail.Replace("（运动中）", "（设备未运行，目标未到位）");
            row.Update(snapshot, notSelected, text(state), detail, color);
            foreach (var child in row.Children)
            {
                bool skipped = notSelected;
                if (row.Node is ConditionBranchNode && snapshot?.Diagnostics?.Decision != null)
                    skipped = !string.Equals(child.Node.Id, snapshot.Diagnostics.SelectedBranchId, StringComparison.OrdinalIgnoreCase);
                Apply(child, snapshot?.ExecutionId, nodes, skipped, machineState, text);
            }
        }
    }
}
