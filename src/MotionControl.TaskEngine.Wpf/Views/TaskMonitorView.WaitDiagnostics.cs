using System.Collections.Generic;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.TaskEngine.Execution;
using MotionControl.TaskEngine.Wpf.Monitoring;

namespace MotionControl.TaskEngine.Wpf.Views
{
    /// <summary>步骤详情直接读取等待证据，不从原始说明、标点或语言推断等待类别。</summary>
    public partial class TaskMonitorView
    {
        /// <summary>显示稳定类别、原因代码、真实活动时限和完整等待集合，并保留原始说明。</summary>
        private void AddWaitDiagnostics(TaskNodeSnapshot snapshot, List<TaskDetailField> fields,
            List<TaskReferenceEventArgs> references)
        {
            if (snapshot == null) return;
            TaskStepDiagnostics diagnostics = snapshot.Diagnostics;
            if (diagnostics == null)
            {
                if (snapshot.State == TaskNodeState.Waiting || snapshot.State == TaskNodeState.Failed)
                {
                    fields.Add(Field("等待诊断", T("等待诊断未提供")));
                    fields.Add(Field("原始等待说明", snapshot.Detail ?? T("未提供")));
                }
                return;
            }
            if (diagnostics.WaitKind == StationWaitKind.NotProvided && !diagnostics.IsTimeout
                && snapshot.State != TaskNodeState.Waiting && snapshot.State != TaskNodeState.Failed) return;
            fields.Add(Field("等待类别", T(StationOperatorPresentation.WaitKindKey(diagnostics.WaitKind))));
            fields.Add(Field("原因代码", diagnostics.ReasonCode ?? T("未提供")));
            fields.Add(Field("实际活动时限", diagnostics.TimeoutMilliseconds.HasValue
                ? diagnostics.TimeoutMilliseconds.Value + " ms" : T("未提供")));
            fields.Add(Field("时限所属步骤", diagnostics.TimeoutNodeId ?? T("未提供")));
            fields.Add(Field("等待集合", StationOperatorPresentation.DescribeWaitItems(
                diagnostics.WaitItems, diagnostics.HasWaitInformation, T)));
            fields.Add(Field("原始等待说明", diagnostics.Summary ?? snapshot.Detail ?? T("未提供")));
            if (!string.IsNullOrWhiteSpace(diagnostics.FailureMessage))
                fields.Add(Field("原始失败原因", diagnostics.FailureMessage));
            foreach (StationWaitItem item in diagnostics.WaitItems)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.ObjectId)) continue;
                switch (item.ObjectKind)
                {
                    case StationObjectKind.Input: IoReferences(item.ObjectId, false, references); break;
                    case StationObjectKind.Output: IoReferences(item.ObjectId, true, references); break;
                    case StationObjectKind.Axis: AxisReferences(item.ObjectId, references); break;
                    case StationObjectKind.Variable:
                        references.Add(new TaskReferenceEventArgs("设备变量", item.ObjectId)); break;
                }
            }
        }
    }
}
