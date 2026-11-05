using System;
using System.Collections.Generic;
using System.Linq;
using MotionControl.TaskEngine.Definitions;
namespace MotionControl.TaskEngine.Wpf.Editing
{
    /// <summary>从当前草稿对象派生只读选择说明，不读取运行时或更改节点。</summary>
    internal static class TaskEditorSelectionSummary
    {
        /// <summary>完整对象路径、直接结构子项数和已提供参数；未解析表单输入不冒充已接受草稿。</summary>
        internal static string Build(TaskDefinition task, TaskNode selected, bool english)
        {
            var path = new List<TaskNode>();
            bool found = Find(task.Root, selected, path, new HashSet<TaskNode>(), 0);
            string location = found ? string.Join(" > ", path.Select(node => node.Name + " [" + node.Id + "]")) : (english ? "Not found in current draft" : "未在当前草稿中找到");
            string parameters = (english ? "Type: " : "类型：") + selected.Kind + "; "
                + (english ? "Timeout (ms): " : "超时(ms)：") + selected.TimeoutMs;
            if (selected is WaitInputNode input) parameters += "; DI=" + input.InputName + "; " + (english ? "Expected=" : "期望逻辑值=") + input.Expected + "; " + string.Join(", ", input.TimeoutAlarmCodes ?? new List<string>());
            else if (selected is SetOutputNode output) parameters += "; DO=" + output.OutputName + "; " + (english ? "Value=" : "逻辑值=") + output.Value;
            else if (selected is DelayNode delay) parameters += "; " + (english ? "Delay(ms)=" : "延时(ms)=") + delay.Milliseconds;
            else if (selected is MoveAxisNode axis) parameters += "; " + (english ? "Axis=" : "轴=") + axis.AxisName + "; " + axis.Mode + "; " + Binding(axis.Target, english);
            else parameters += english ? "; Other parameters: see the type-specific form." : "；其他参数见下方类型专用表单。";
            return (english ? "Current draft objects only; not runtime state. Unaccepted form edits are not included.\nPath: " : "仅当前草稿对象，不是运行状态；未接受的表单输入不计入摘要。\n路径：")
                + task.Name + " [" + task.TaskId + "] > " + location + "\n"
                + (english ? "Direct structural children: " : "直接结构子项数：") + TaskEditorNodes.Children(selected).Count() + "\n" + parameters;
        }
        /// <summary>显示真实目标来源，不解析变量当前值或替代示教位置。</summary>
        private static string Binding(TaskValueBinding value, bool english)
        {
            if (value == null) return english ? "Target not provided" : "目标未提供";
            return value.Source + ": " + (value.Source == TaskValueSource.Constant ? value.Constant : value.Source == TaskValueSource.Variable ? value.VariableName : value.AxisName + "/" + value.PointId);
        }
        /// <summary>按对象身份查祖先，限制深度且防循环，不按重名或节点ID猜路径。</summary>
        private static bool Find(TaskNode current, TaskNode target, List<TaskNode> path, HashSet<TaskNode> ancestors, int depth)
        {
            if (current == null || depth > 128 || !ancestors.Add(current)) return false;
            path.Add(current);
            if (ReferenceEquals(current, target)) return true;
            foreach (var child in TaskEditorNodes.Children(current)) if (Find(child, target, path, ancestors, depth + 1)) return true;
            path.RemoveAt(path.Count - 1); ancestors.Remove(current); return false;
        }
    }
}
