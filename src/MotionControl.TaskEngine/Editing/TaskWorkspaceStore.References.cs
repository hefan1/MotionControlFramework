using System;
using System.Collections.Generic;
using System.IO;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Machine;
using MotionControl.TaskEngine.Preparation;

namespace MotionControl.TaskEngine.Editing
{
    public sealed partial class TaskWorkspaceStore
    {
        /// <summary>统一列出生产、初始化、准备和子任务的直接引用；查看引用与删除保护复用本入口。</summary>
        public IReadOnlyList<TaskWorkspaceTaskReference> FindTaskReferences(TaskWorkspaceDocument draft, string taskId)
        {
            RequireDraftShape(draft?.MachineId, draft);
            var references = new List<TaskWorkspaceTaskReference>();
            if (draft.Machine.Stations == null) throw new InvalidDataException("生产工位列表为空，不能确认任务引用。");
            for (int index = 0; index < draft.Machine.Stations.Count; index++)
            {
                StationTaskConfiguration station = draft.Machine.Stations[index];
                if (station == null) throw new InvalidDataException("生产工位列表包含空项，不能确认任务引用。");
                if (string.Equals(station.TaskId, taskId, StringComparison.OrdinalIgnoreCase))
                    references.Add(new TaskWorkspaceTaskReference(TaskWorkspaceReferenceKind.ProductionStation, station.StationId,
                        station.Name, taskId, "Parameters/TaskMachine.json/stations[" + index + "].taskId"));
            }
            if (string.Equals(draft.Machine.InitializationTaskId, taskId, StringComparison.OrdinalIgnoreCase))
                references.Add(new TaskWorkspaceTaskReference(TaskWorkspaceReferenceKind.Initialization, draft.Machine.MachineId,
                    draft.Machine.Name, taskId, "Parameters/TaskMachine.json/initializationTaskId"));

            TaskStationPreparationConfiguration preparation = new TaskStationPreparationStore(_store).Load(draft.MachineId);
            if (preparation != null)
            {
                if (preparation.Stations == null) throw new InvalidDataException("准备配置缺少 stations 列表，不能确认任务引用。");
                for (int index = 0; index < preparation.Stations.Count; index++)
                {
                    TaskStationPreparationDefinition item = preparation.Stations[index];
                    if (item == null) throw new InvalidDataException("准备配置包含空项，不能确认任务引用。");
                    if (string.Equals(item.TaskId, taskId, StringComparison.OrdinalIgnoreCase))
                        references.Add(new TaskWorkspaceTaskReference(TaskWorkspaceReferenceKind.Preparation, item.Id,
                            item.Name, taskId, TaskStationPreparationStore.RelativePath + "/stations[" + index + "].taskId"));
                }
            }

            foreach (TaskWorkspaceTask task in draft.Tasks)
                CollectTaskCalls(task.Definition, task.Definition.Root, "Tasks/" + task.FileName + "/root", taskId, references);
            return references.AsReadOnly();
        }

        /// <summary>按已有节点类型沿明确字段遍历，保留调用节点和 JSON 位置，不反射猜测引用。</summary>
        private static void CollectTaskCalls(TaskDefinition owner, TaskNode node, string path, string taskId,
            List<TaskWorkspaceTaskReference> references)
        {
            if (node == null) return;
            if (node is CallTaskNode call && string.Equals(call.TaskId, taskId, StringComparison.OrdinalIgnoreCase))
                references.Add(new TaskWorkspaceTaskReference(TaskWorkspaceReferenceKind.SubTask, owner.TaskId,
                    owner.Name + " / " + call.Name, taskId, path + ".taskId", call.Id));

            if (node is SequenceNode sequence)
            {
                if (sequence.Children == null) throw new InvalidDataException("顺序节点缺少 children，不能确认任务引用：" + path);
                for (int index = 0; index < sequence.Children.Count; index++)
                    CollectTaskCalls(owner, sequence.Children[index], path + ".children[" + index + "]", taskId, references);
            }
            else if (node is ParallelGroupNode parallel)
            {
                if (parallel.Branches == null) throw new InvalidDataException("并行节点缺少 branches，不能确认任务引用：" + path);
                for (int index = 0; index < parallel.Branches.Count; index++)
                    CollectTaskCalls(owner, parallel.Branches[index], path + ".branches[" + index + "]", taskId, references);
            }
            else if (node is ResourceScopeNode scope)
                CollectTaskCalls(owner, scope.Body, path + ".body", taskId, references);
            else if (node is ConditionBranchNode branch)
            {
                CollectTaskCalls(owner, branch.WhenTrue, path + ".whenTrue", taskId, references);
                CollectTaskCalls(owner, branch.WhenFalse, path + ".whenFalse", taskId, references);
            }
        }
    }

    /// <summary>任务引用的明确来源，准备项目与生产工位分开表达。</summary>
    public enum TaskWorkspaceReferenceKind
    {
        /// <summary>生产清单中的工位任务。</summary>
        ProductionStation,
        /// <summary>整机初始化任务。</summary>
        Initialization,
        /// <summary>独立准备配置中的任务。</summary>
        Preparation,
        /// <summary>任务节点中的直接子任务调用。</summary>
        SubTask
    }

    /// <summary>供引用列表和删除保护共同使用的只读定位条目。</summary>
    public sealed class TaskWorkspaceTaskReference
    {
        /// <summary>引用由哪一种配置对象声明。</summary>
        public TaskWorkspaceReferenceKind Kind { get; }
        /// <summary>引用所有者的稳定标识，准备标识不当作生产工位标识。</summary>
        public string OwnerId { get; }
        /// <summary>用于阅读的所有者名称，不参与引用匹配。</summary>
        public string OwnerName { get; }
        /// <summary>被引用的任务稳定标识。</summary>
        public string TaskId { get; }
        /// <summary>引用所在文件及字段，便于直接定位维护入口。</summary>
        public string FieldPath { get; }
        /// <summary>子任务引用所在节点；非节点来源时为空。</summary>
        public string NodeId { get; }

        /// <summary>保存一次直接引用的位置，不保留可编辑任务对象。</summary>
        internal TaskWorkspaceTaskReference(TaskWorkspaceReferenceKind kind, string ownerId, string ownerName,
            string taskId, string fieldPath, string nodeId = null)
        {
            Kind = kind;
            OwnerId = ownerId;
            OwnerName = ownerName;
            TaskId = taskId;
            FieldPath = fieldPath;
            NodeId = nodeId;
        }
    }
}
