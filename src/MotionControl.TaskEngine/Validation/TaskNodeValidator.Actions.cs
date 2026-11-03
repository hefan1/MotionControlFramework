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
            else if (node is WaitOutputNode outputWait)
            {
                if (!ValidName(outputWait.OutputName) || !_catalog.Outputs.Contains(outputWait.OutputName, StringComparer.OrdinalIgnoreCase))
                    Error(node, path + ".outputName", "找不到逻辑 DO；不能把 DI 用作输出：" + outputWait.OutputName);
                CheckRequiredTimeoutAlarm(outputWait.TimeoutAlarmCodes, node, path + ".timeoutAlarmCodes");
                CheckAlarmReferences(outputWait.TimeoutAlarmCodes, node, path + ".timeoutAlarmCodes");
            }
            else if (node is WaitAxisPositionNode axisWait)
            {
                TaskAxisInfo axis = FindAxis(axisWait.AxisName, node, path + ".axisName");
                if (double.IsNaN(axisWait.Position) || double.IsInfinity(axisWait.Position)
                    || (axis != null && axis.MinimumPosition.HasValue && axis.MaximumPosition.HasValue
                        && (axisWait.Position < axis.MinimumPosition || axisWait.Position > axis.MaximumPosition)))
                    Error(node, path + ".position", "期望位置必须是轴行程内的有限数。");
                if (double.IsNaN(axisWait.Tolerance) || double.IsInfinity(axisWait.Tolerance) || axisWait.Tolerance < 0)
                    Error(node, path + ".tolerance", "位置容差必须是非负有限数。");
                CheckRequiredTimeoutAlarm(axisWait.TimeoutAlarmCodes, node, path + ".timeoutAlarmCodes");
                CheckAlarmReferences(axisWait.TimeoutAlarmCodes, node, path + ".timeoutAlarmCodes");
            }
            else if (node is WriteRecordNode record)
            {
                if (!ValidName(record.Category) || record.Category.Length > 128)
                    Error(node, path + ".category", "请填写不超过 128 字的记录类别。");
                if (record.Fields == null || record.Fields.Count == 0 || record.Fields.Count > 64)
                    Error(node, path + ".fields", "记录必须包含 1 到 64 个字段。");
                else foreach (var field in record.Fields)
                {
                    DeviceVariableType type;
                    if (!ValidName(field.Key) || field.Key.Length > 128 || !ValidName(field.Value)
                        || !_catalog.Variables.TryGetValue(field.Value, out type) || type != DeviceVariableType.String)
                        Error(node, path + ".fields", "记录字段名称无效，或来源不是已配置的字符串变量：" + field.Key);
                }
            }
            else if (node is ReadInputWordNode word)
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (word.InputNames == null || word.InputNames.Count != 16)
                    Error(node, path + ".inputNames", "必须明确配置 16 路数据输入，按低位到高位排列。");
                foreach (string name in (IEnumerable<string>)word.InputNames ?? Array.Empty<string>())
                    if (!ValidName(name) || !names.Add(name) || !_catalog.Inputs.Contains(name, StringComparer.OrdinalIgnoreCase))
                        Error(node, path + ".inputNames", "数据输入不存在或重复：" + name);
                foreach (string name in new[] { word.ValidInputName, word.SuccessInputName })
                    if (!ValidName(name) || !names.Add(name) || !_catalog.Inputs.Contains(name, StringComparer.OrdinalIgnoreCase))
                        Error(node, path, "有效或成功输入不存在，或者与其他输入重复：" + name);
                DeviceVariableType wordType;
                if (!ValidName(word.TargetVariable) || !_catalog.Variables.TryGetValue(word.TargetVariable, out wordType)
                    || wordType != DeviceVariableType.String)
                    Error(node, path + ".targetVariable", "目标必须是已配置的字符串设备变量。 ");
                if (word.Prefix == null || word.Prefix.Length > 64) Error(node, path + ".prefix", "前缀不能为空引用或超过 64 个字符。");
                CheckRequiredTimeoutAlarm(word.TimeoutAlarmCodes, node, path + ".timeoutAlarmCodes");
                CheckAlarmReferences(word.TimeoutAlarmCodes, node, path + ".timeoutAlarmCodes");
                Warning(node, path, "设备须在读取期间锁存输入；两轮软件采样不能替代硬件原子采样。");
            }
            else if (node is ReadInputNode sample)
            {
                if (!ValidName(sample.InputName) || !_catalog.Inputs.Contains(sample.InputName, StringComparer.OrdinalIgnoreCase))
                    Error(node, path + ".inputName", "找不到逻辑 DI；不能把 DO 用作输入：" + sample.InputName);
                DeviceVariableType targetType;
                if (!ValidName(sample.VariableName) || !_catalog.Variables.TryGetValue(sample.VariableName, out targetType))
                    Error(node, path + ".variableName", "目标设备变量不存在：" + sample.VariableName);
                else if (targetType != DeviceVariableType.Boolean)
                    Error(node, path + ".variableName", "输入采样只能写入布尔设备变量：" + sample.VariableName);
            }
            else if (node is WaitExclusiveInputsNode pair)
            {
                foreach (var pairInput in new[] { new { Name = pair.FirstInputName, Field = ".firstInputName" }, new { Name = pair.SecondInputName, Field = ".secondInputName" } })
                    if (!ValidName(pairInput.Name) || !_catalog.Inputs.Contains(pairInput.Name, StringComparer.OrdinalIgnoreCase))
                        Error(node, path + pairInput.Field, "找不到逻辑 DI：" + pairInput.Name);
                if (ValidName(pair.FirstInputName) && string.Equals(pair.FirstInputName, pair.SecondInputName, StringComparison.OrdinalIgnoreCase))
                    Error(node, path + ".secondInputName", "两路输入不能相同。");
                foreach (var variable in new[] { new { Name = pair.FirstVariableName, Field = ".firstVariableName" }, new { Name = pair.SecondVariableName, Field = ".secondVariableName" } })
                {
                    DeviceVariableType type;
                    if (!ValidName(variable.Name) || !_catalog.Variables.TryGetValue(variable.Name, out type))
                        Error(node, path + variable.Field, "目标设备变量不存在：" + variable.Name);
                    else if (type != DeviceVariableType.Boolean)
                        Error(node, path + variable.Field, "输入采样只能写入布尔设备变量：" + variable.Name);
                }
                if (ValidName(pair.FirstVariableName) && string.Equals(pair.FirstVariableName, pair.SecondVariableName, StringComparison.OrdinalIgnoreCase))
                    Error(node, path + ".secondVariableName", "两个目标变量不能相同。");
                CheckRequiredTimeoutAlarm(pair.TimeoutAlarmCodes, node, path + ".timeoutAlarmCodes");
                CheckAlarmReferences(pair.TimeoutAlarmCodes, node, path + ".timeoutAlarmCodes");
                Warning(node, path, "两路输入按顺序读取，要求外部设备锁存结果至确认；不能代替硬件原子采样或安全联锁。");
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
