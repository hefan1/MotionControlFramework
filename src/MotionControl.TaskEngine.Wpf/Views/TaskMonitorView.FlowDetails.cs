using System.Collections.Generic;
using System.Linq;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Wpf.Monitoring;

namespace MotionControl.TaskEngine.Wpf.Views
{
    /// <summary>并行、子任务与资源详情直接沿用步骤树和整机资源快照。</summary>
    public partial class TaskMonitorView
    {
        /// <summary>显示分支进度与资源持有者，不把逻辑互斥解释成几何防碰撞。</summary>
        private void AddFlowDetails(TaskStepRow row, List<TaskDetailField> fields, List<TaskDetailField> parameters,
            List<TaskReferenceEventArgs> references)
        {
            if (row.Node is ParallelGroupNode || row.Node is SequenceNode || row.Node is CallTaskNode)
            {
                foreach (var child in row.Children)
                    fields.Add(new TaskDetailField(child.Name, child.StateText + " · " + child.Detail));
                if (row.Node is ParallelGroupNode) fields.Insert(0, Field("继续条件", T("所有并行分支完成后继续")));
                if (row.Node is CallTaskNode call) parameters.Add(Field("子任务", _session.Library[call.TaskId].Name + " [" + call.TaskId + "]"));
            }
            if (row.Node is ResourceScopeNode resource)
            {
                parameters.Add(Field("申请资源", string.Join(" / ", resource.Resources)));
                AddInterlock(T("进入条件"), resource.EntryInterlock, parameters, references);
                AddInterlock(T("退出条件"), resource.ExitInterlock, parameters, references);
                if (resource.ExitCondition != null) parameters.Add(Field("退出条件", ConditionText(resource.ExitCondition, references)));
                foreach (var request in _snapshot.Resources.Where(item => item.Resources.Intersect(resource.Resources, System.StringComparer.OrdinalIgnoreCase).Any()))
                {
                    var owner = _stations.FirstOrDefault(station => station.Snapshot.Run?.RunId == request.RunId);
                    fields.Add(new TaskDetailField(string.Join(" / ", request.Resources),
                        (owner?.Name ?? request.TaskId) + " · " + T(request.State.ToString()) + "\n" + request.Detail));
                }
                if (fields.Count == 0) fields.Add(Field("资源状态", T("当前没有相关占用记录")));
                fields.Add(Field("检查范围", T("资源互斥不替代机械避让条件")));
                foreach (string name in resource.Resources)
                {
                    if (name.StartsWith("Axis:", System.StringComparison.OrdinalIgnoreCase)) AxisReferences(name.Substring(5), references);
                    if (name.StartsWith("Output:", System.StringComparison.OrdinalIgnoreCase)) IoReferences(name.Substring(7), true, references);
                }
            }
        }

        /// <summary>列出明确配置的进入与退出要求，未配置不显示成已验证。</summary>
        private void AddInterlock(string label, TaskInterlock interlock, List<TaskDetailField> fields, List<TaskReferenceEventArgs> references)
        {
            if (interlock == null) { fields.Add(new TaskDetailField(label, T("未配置"))); return; }
            if (interlock.Condition != null) fields.Add(new TaskDetailField(label, ConditionText(interlock.Condition, references)));
            foreach (var input in interlock.Inputs)
            {
                fields.Add(new TaskDetailField(label, input.InputName + " = " + T(input.Expected == true ? "有效" : "无效")));
                IoReferences(input.InputName, false, references);
            }
            foreach (var axis in interlock.Axes)
            {
                fields.Add(new TaskDetailField(label, axis.AxisName + " [" + axis.MinimumPosition + ", " + axis.MaximumPosition + "] "
                    + _session.Device.Catalog.Axes[axis.AxisName].EngineeringUnit + " · " + T("停止且无报警") + (axis.RequireHomed ? " · " + T("已回零") : string.Empty)));
                AxisReferences(axis.AxisName, references);
            }
        }
    }
}
