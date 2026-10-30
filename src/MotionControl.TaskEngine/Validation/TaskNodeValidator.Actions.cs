using System;
using System.Collections.Generic;
using System.Linq;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Definitions;

namespace MotionControl.TaskEngine.Validation
{
    /// <summary>动作参数和现有设备配置之间的检查；不执行任何动作。</summary>
    internal sealed partial class TaskNodeValidator
    {
        /// <summary>通过明确的类型分支检查动作，未知类型必须报错。</summary>
        private void CheckAction(TaskNode node, string path)
        {
            if (node is MoveAxisNode move)
            {
                bool validMode = Enum.IsDefined(typeof(TaskMoveMode), move.Mode);
                if (!validMode) Error(node, path + ".mode", "坐标方式无效。");
                CheckAxisTarget(move.AxisName, move.Target, move.Mode == TaskMoveMode.Relative, node, path);
                CheckProfile(move.Profile, node, path + ".profile");
            }
            else if (node is MoveAxisGroupNode group)
            {
                CheckAxisGroup(group, path);
            }
            else if (node is CoordinatedMoveNode coordinated)
            {
                CheckCoordinatedMove(coordinated, path);
            }
            else if (node is AxisCommandNode command)
            {
                FindAxis(command.AxisName, node, path + ".axisName");
                if (!Enum.IsDefined(typeof(TaskAxisCommand), command.Command))
                    Error(node, path + ".command", "轴命令无效。");
                if (command.Command == TaskAxisCommand.Stop
                    && (!command.StopMode.HasValue || !Enum.IsDefined(typeof(AxisStopMode), command.StopMode.Value)))
                    Error(node, path + ".stopMode", "停止命令必须明确指定支持的停止方式。");
                if (command.Command == TaskAxisCommand.Home && command.StopMode.HasValue)
                    Error(node, path + ".stopMode", "回零命令不能同时指定停止方式。");
            }
            else if (node is SetOutputNode output)
            {
                if (!ValidName(output.OutputName) || !_catalog.Outputs.Contains(output.OutputName, StringComparer.OrdinalIgnoreCase))
                    Error(node, path + ".outputName", "找不到逻辑 DO；不能把 DI 用作输出：" + output.OutputName);
            }
            else if (node is WaitInputNode input)
            {
                if (!ValidName(input.InputName) || !_catalog.Inputs.Contains(input.InputName, StringComparer.OrdinalIgnoreCase))
                    Error(node, path + ".inputName", "找不到逻辑 DI；不能把 DO 用作输入：" + input.InputName);
                CheckRequiredTimeoutAlarm(input.TimeoutAlarmCodes, node, path + ".timeoutAlarmCodes");
                CheckAlarmReferences(input.TimeoutAlarmCodes, node, path + ".timeoutAlarmCodes");
            }
            else if (node is DelayNode delay)
            {
                if (delay.Milliseconds <= 0 || delay.Milliseconds > delay.TimeoutMs)
                    Error(node, path + ".milliseconds", "延时须大于零且不超过节点总时限。");
            }
            else if (node is WaitConditionNode wait)
            {
                CheckCondition(wait.Condition, node, path + ".condition", new HashSet<TaskCondition>(), 0);
                CheckRequiredTimeoutAlarm(wait.TimeoutAlarmCodes, node, path + ".timeoutAlarmCodes");
                CheckAlarmReferences(wait.TimeoutAlarmCodes, node, path + ".timeoutAlarmCodes");
            }
            else if (node is SetVariableNode assignment)
            {
                DeviceVariableType targetType;
                if (!ValidName(assignment.VariableName) || !_catalog.Variables.TryGetValue(assignment.VariableName, out targetType))
                {
                    Error(node, path + ".variableName", "目标设备变量不存在：" + assignment.VariableName);
                    return;
                }
                double? number;
                var sourceType = CheckValue(assignment.Value, node, path + ".value", out number);
                if (sourceType.HasValue && sourceType.Value != targetType)
                {
                    // 数值到 double 不会缩小当前支持的整数范围；其他跨类型赋值不做隐式转换。
                    bool numberToDouble = targetType == DeviceVariableType.Number && TaskLiteral.IsNumeric(sourceType.Value);
                    if (!numberToDouble) Error(node, path + ".value", "赋值类型不一致；不隐式截断数字或转换字符串。");
                }
            }
            else Error(node, path + ".kind", "当前阶段不支持此节点：" + node.GetType().Name);
        }

        /// <summary>检查同卡直线轴组的定义；真实能力在后续适配器阶段确认。</summary>
        private void CheckCoordinatedMove(CoordinatedMoveNode node, string path)
        {
            CheckProfile(node.Profile, node, path + ".profile");
            if (node.Targets == null || node.Targets.Count < 2 || node.Targets.Count > 64)
            {
                Error(node, path + ".targets", "协调运动定义需要 2 到 64 根轴；此上限仅用于限制文件规模，不代表卡能力。");
                return;
            }
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string controller = null;
            for (int index = 0; index < node.Targets.Count; index++)
            {
                AxisTarget target = node.Targets[index];
                string targetPath = path + ".targets[" + index + "]";
                if (target == null) { Error(node, targetPath, "轴目标不能为空。"); continue; }
                if (!names.Add(target.AxisName ?? string.Empty)) Error(node, targetPath, "同一逻辑轴不能重复加入轴组。");
                TaskAxisInfo axis = CheckAxisTarget(target.AxisName, target.Target, false, node, targetPath);
                if (axis == null) continue;
                if (controller == null) controller = axis.ControllerId;
                else if (!string.Equals(controller, axis.ControllerId, StringComparison.OrdinalIgnoreCase))
                    Error(node, targetPath, "首期只定义同卡协调运动，不能混用不同控制卡。");
                if (!string.Equals(axis.EngineeringUnit, "mm", StringComparison.OrdinalIgnoreCase))
                    Error(node, targetPath, "首期协调运动仅定义统一 mm 单位的直线轴；未知或混合单位不能直接使用路径速度。");
            }
            Warning(node, path, "卡型/固件的插补、最大轴数、组完成和组停止能力尚待核实；定义有效不代表可执行。");
        }

        /// <summary>检查轴、目标类型、点位归属和能够离线判断的绝对行程。</summary>
        private TaskAxisInfo CheckAxisTarget(string axisName, TaskValueBinding target, bool relative, TaskNode node, string path)
        {
            TaskAxisInfo axis = FindAxis(axisName, node, path + ".axisName");
            double? position;
            DeviceVariableType? type = CheckValue(target, node, path + ".target", out position);
            if (type.HasValue && !TaskLiteral.IsNumeric(type.Value))
                Error(node, path + ".target", "轴目标必须是数值，不能使用字符串、布尔或日期。");
            if (target != null && target.Source == TaskValueSource.TeachPoint)
            {
                if (relative) Error(node, path + ".target", "示教点是绝对位置，不能当作相对距离。");
                if (!string.Equals(target.AxisName, axisName, StringComparison.OrdinalIgnoreCase))
                    Error(node, path + ".target.axisName", "示教点不属于本次动作选定的逻辑轴。");
            }
            if (axis != null)
            {
                if (!axis.MinimumPosition.HasValue || !axis.MaximumPosition.HasValue)
                    Error(node, path + ".target", "轴配置缺少行程上下限，无法确认目标范围。");
                else if (!relative && position.HasValue && (position < axis.MinimumPosition || position > axis.MaximumPosition))
                    Error(node, path + ".target", "目标位置超出配置行程。");
            }
            if (relative) Warning(node, path, "相对运动需在执行时根据起点计算绝对终点并检查行程；不能在此认定安全。");
            return axis;
        }

        /// <summary>按逻辑名查轴，不将错误名称猜测为物理轴号。</summary>
        private TaskAxisInfo FindAxis(string name, TaskNode node, string path)
        {
            TaskAxisInfo axis;
            if (!ValidName(name) || !_catalog.Axes.TryGetValue(name, out axis))
            {
                Error(node, path, "逻辑轴不存在：" + name);
                return null;
            }
            return axis;
        }

        /// <summary>要求明确的正数速度和加减速度，不继承手动调试速度。</summary>
        private void CheckProfile(TaskMotionProfile profile, TaskNode node, string path)
        {
            if (profile == null) { Error(node, path, "请配置工艺运动参数。"); return; }
            if (!PositiveFinite(profile.Speed)) Error(node, path + ".speed", "速度必须是大于零的有限数。");
            if (!PositiveFinite(profile.Acceleration)) Error(node, path + ".acceleration", "加速度必须是大于零的有限数。");
            if (!PositiveFinite(profile.Deceleration)) Error(node, path + ".deceleration", "减速度必须是大于零的有限数。");
        }

        /// <summary>同时排除非正数、NaN 和无穷大。</summary>
        private static bool PositiveFinite(double value)
        {
            return value > 0 && !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
