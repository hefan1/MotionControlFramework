using System;
using System.Collections.Generic;
using MotionControl.TaskEngine.Definitions;

namespace MotionControl.TaskEngine.Projection
{
    /// <summary>显式读取节点定义支持的字段；新增节点未经支持时报告具体类型。</summary>
    internal sealed partial class TaskProjectionBuilder
    {
        /// <summary>解析一个节点自身的字段，子节点和子任务由树遍历处理。</summary>
        private void ReadFields(string station, TaskDefinition task, TaskNode node, string path)
        {
            // 给每个引用保留源字段，避免同一节点的多个相同引用互相覆盖。
            void Ref(string field, string kind, string target, string axis = null)
            {
                Reference(station, task, node, path + "/" + field, kind, target, axis);
            }

            // 单轴移动、轴组、回零和置输出会申请对应的预留资源；状态读取与确认停止不会申请。
            void ActionResource(string field, string prefix, string name)
            {
                if (!string.IsNullOrWhiteSpace(name)) Ref(field + "/ActionResource", "Resource", prefix + name);
            }

            // 列表的索引仅标识字段中的这一次出现。
            void ListRefs(string field, string kind, List<string> names)
            {
                if (names == null) { issues.Add(path + "/" + field + "：引用集合缺失。"); return; }
                for (int i = 0; i < names.Count && visits <= MaxVisits; i++) Ref(field + "/" + i, kind, names[i]);
            }

            // 点位使用源绑定的轴与稳定编号，不用动作轴或显示名称补齐。
            void Binding(string field, TaskValueBinding value)
            {
                if (value == null) { issues.Add(path + "/" + field + "：绑定未提供。"); return; }
                switch (value.Source)
                {
                    case TaskValueSource.Constant: break;
                    case TaskValueSource.Variable: Ref(field + "/VariableName", "Variable", value.VariableName); break;
                    case TaskValueSource.TeachPoint:
                        Ref(field + "/AxisName", "Axis", value.AxisName);
                        Ref(field + "/PointId", "TeachPoint", value.PointId, value.AxisName);
                        break;
                    default: issues.Add(path + "/" + field + "：不支持的绑定类型 " + value.Source); break;
                }
            }

            // 深度及工作量限制也约束内存草稿可能形成的条件循环。
            var conditionStack = new HashSet<TaskCondition>();
            void Condition(string field, TaskCondition condition, int depth = 0)
            {
                if (!Visit(path + "/" + field)) return;
                if (condition == null || depth > 32) { issues.Add(path + "/" + field + "：条件缺失或深度超限。"); return; }
                if (!conditionStack.Add(condition)) { issues.Add(path + "/" + field + "：条件对象形成循环。"); return; }
                if (condition.Kind == TaskConditionKind.Compare)
                {
                    Binding(field + "/Left", condition.Left);
                    Binding(field + "/Right", condition.Right);
                }
                else if (condition.Kind == TaskConditionKind.All || condition.Kind == TaskConditionKind.Any || condition.Kind == TaskConditionKind.Not)
                {
                    if (condition.Conditions == null) issues.Add(path + "/" + field + "：条件集合缺失。");
                    else for (int i = 0; i < condition.Conditions.Count && visits <= MaxVisits; i++)
                        Condition(field + "/Conditions/" + i, condition.Conditions[i], depth + 1);
                }
                else issues.Add(path + "/" + field + "：不支持的条件类型 " + condition.Kind);
                conditionStack.Remove(condition);
            }

            // 互锁条件、轴位置条件与输入确认各自保留独立来源。
            void Interlock(string field, TaskInterlock interlock)
            {
                if (interlock == null) return;
                if (interlock.Condition != null) Condition(field + "/Condition", interlock.Condition);
                if (interlock.Axes != null)
                    for (int i = 0; i < interlock.Axes.Count && visits <= MaxVisits; i++)
                        Ref(field + "/Axes/" + i, "Axis", interlock.Axes[i]?.AxisName);
                if (interlock.Inputs != null)
                    for (int i = 0; i < interlock.Inputs.Count && visits <= MaxVisits; i++)
                        Ref(field + "/Inputs/" + i, "Input", interlock.Inputs[i]?.InputName);
            }

            switch (node)
            {
                case SequenceNode _:
                case ParallelGroupNode _:
                case DelayNode _: break;
                case ConditionBranchNode branch:
                    Condition("Condition", branch.Condition);
                    if (branch.WhenFalseAlarmCodes != null) ListRefs("WhenFalseAlarmCodes", "Alarm", branch.WhenFalseAlarmCodes);
                    break;
                case ResourceScopeNode scope:
                    ListRefs("Resources", "Resource", scope.Resources);
                    // Axis: 和 Output: 是 TaskResourceCatalog 预留的资源键，不解析其他资源名。
                    if (scope.Resources != null)
                        for (int i = 0; i < scope.Resources.Count && visits <= MaxVisits; i++)
                        {
                            string key = scope.Resources[i];
                            if (key?.StartsWith("Axis:", StringComparison.OrdinalIgnoreCase) == true)
                                Ref("Resources/" + i + "/Axis", "Axis", key.Substring("Axis:".Length));
                            else if (key?.StartsWith("Output:", StringComparison.OrdinalIgnoreCase) == true)
                                Ref("Resources/" + i + "/Output", "Output", key.Substring("Output:".Length));
                        }
                    Interlock("EntryInterlock", scope.EntryInterlock);
                    Interlock("ExitInterlock", scope.ExitInterlock);
                    if (scope.ExitCondition != null) Condition("ExitCondition", scope.ExitCondition);
                    break;
                case MoveAxisNode move:
                    Ref("AxisName", "Axis", move.AxisName);
                    ActionResource("AxisName", "Axis:", move.AxisName);
                    Binding("Target", move.Target);
                    break;
                case MoveAxisGroupNode group:
                    if (group.Targets == null) { issues.Add(path + "/Targets：目标集合缺失。"); break; }
                    for (int i = 0; i < group.Targets.Count && visits <= MaxVisits; i++)
                    {
                        Ref("Targets/" + i + "/AxisName", "Axis", group.Targets[i]?.AxisName);
                        ActionResource("Targets/" + i + "/AxisName", "Axis:", group.Targets[i]?.AxisName);
                        Binding("Targets/" + i + "/Target", group.Targets[i]?.Target);
                    }
                    break;
                case CoordinatedMoveNode coordinated:
                    if (coordinated.Targets == null) { issues.Add(path + "/Targets：目标集合缺失。"); break; }
                    for (int i = 0; i < coordinated.Targets.Count && visits <= MaxVisits; i++)
                    {
                        Ref("Targets/" + i + "/AxisName", "Axis", coordinated.Targets[i]?.AxisName);
                        Binding("Targets/" + i + "/Target", coordinated.Targets[i]?.Target);
                    }
                    break;
                case AxisCommandNode command:
                    Ref("AxisName", "Axis", command.AxisName);
                    if (command.Command == TaskAxisCommand.Home) ActionResource("AxisName", "Axis:", command.AxisName);
                    break;
                case WaitAxisPositionNode axis:
                    Ref("AxisName", "Axis", axis.AxisName);
                    ListRefs("TimeoutAlarmCodes", "Alarm", axis.TimeoutAlarmCodes);
                    break;
                case SetOutputNode output:
                    Ref("OutputName", "Output", output.OutputName);
                    ActionResource("OutputName", "Output:", output.OutputName);
                    break;
                case WaitInputNode input:
                    Ref("InputName", "Input", input.InputName);
                    ListRefs("TimeoutAlarmCodes", "Alarm", input.TimeoutAlarmCodes);
                    break;
                case WaitOutputNode waitOutput:
                    Ref("OutputName", "Output", waitOutput.OutputName);
                    ListRefs("TimeoutAlarmCodes", "Alarm", waitOutput.TimeoutAlarmCodes);
                    break;
                case ReadInputNode read:
                    Ref("InputName", "Input", read.InputName);
                    Ref("VariableName", "Variable", read.VariableName);
                    break;
                case WaitExclusiveInputsNode pair:
                    Ref("FirstInputName", "Input", pair.FirstInputName);
                    Ref("SecondInputName", "Input", pair.SecondInputName);
                    Ref("FirstVariableName", "Variable", pair.FirstVariableName);
                    Ref("SecondVariableName", "Variable", pair.SecondVariableName);
                    ListRefs("TimeoutAlarmCodes", "Alarm", pair.TimeoutAlarmCodes);
                    break;
                case ReadInputWordNode word:
                    ListRefs("InputNames", "Input", word.InputNames);
                    Ref("ValidInputName", "Input", word.ValidInputName);
                    Ref("SuccessInputName", "Input", word.SuccessInputName);
                    Ref("TargetVariable", "Variable", word.TargetVariable);
                    ListRefs("TimeoutAlarmCodes", "Alarm", word.TimeoutAlarmCodes);
                    break;
                case SetVariableNode assignment:
                    Ref("VariableName", "Variable", assignment.VariableName);
                    Binding("Value", assignment.Value);
                    break;
                case WaitConditionNode wait:
                    Condition("Condition", wait.Condition);
                    ListRefs("TimeoutAlarmCodes", "Alarm", wait.TimeoutAlarmCodes);
                    break;
                case WriteRecordNode record:
                    if (record.Fields == null) { issues.Add(path + "/Fields：记录字段集合缺失。"); break; }
                    foreach (var field in record.Fields)
                    {
                        if (visits > MaxVisits) break;
                        Ref("Fields/" + Segment(field.Key), "Variable", field.Value);
                    }
                    break;
                default: issues.Add(path + "：未支持的节点类型 " + node.GetType().FullName); break;
            }
        }
    }
}
