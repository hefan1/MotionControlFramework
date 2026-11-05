using System;
using System.Linq;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Machine;
using MotionControl.TaskEngine.Projection;

namespace MotionControl.TaskProjection.Checks
{
    /// <summary>核对各动作字段、可选引用与损坏对象环。</summary>
    internal static partial class Program
    {
        /// <summary>按节点配置检查缺失和多出的依赖，涉及无效引用时仍要求字段可定位。</summary>
        private static void ActionFields()
        {
            var variable = new TaskValueBinding { Source = TaskValueSource.Variable, VariableName = "flag" };
            var point = new TaskValueBinding { Source = TaskValueSource.TeachPoint, AxisName = "Y", PointId = "P" };

            // 每一项列出类型、对象键及字段；顺序对比前排序，防止遗漏或添加猜测关系。
            void Expect(TaskNode node, params string[] fields)
            {
                node.Id = "item";
                var projection = TaskProjectProjection.FromSaved(Machine("A"), new[] { Task("main", node) }, Catalog());
                string path = projection.References.Single(row => row.Kind == "Node" && row.NodeId == node.Id).Path;
                var actual = projection.References.Where(row => row.NodeId == node.Id && row.Kind != "Node")
                    .Select(row => row.Kind + ":" + row.Target + "@" + row.Path.Substring(path.Length + 1))
                    .OrderBy(value => value, StringComparer.Ordinal);
                Check(actual.SequenceEqual(fields.OrderBy(value => value, StringComparer.Ordinal)),
                    node.Kind + " 字段不相符：" + string.Join(" | ", actual));
            }

            Expect(new AxisCommandNode { AxisName = "X", Command = TaskAxisCommand.Home },
                "Axis:X@AxisName", "Resource:Axis:X@AxisName/ActionResource");
            Expect(new AxisCommandNode { AxisName = "X", Command = TaskAxisCommand.Stop },
                "Axis:X@AxisName");
            Expect(new MoveAxisGroupNode { Targets = {new AxisPositionTarget { AxisName = "X", Target = variable }} },
                "Axis:X@Targets/0/AxisName", "Resource:Axis:X@Targets/0/AxisName/ActionResource", "Variable:flag@Targets/0/Target/VariableName");
            Expect(new CoordinatedMoveNode { Targets = {new AxisTarget { AxisName = "Y", Target = point }} },
                "Axis:Y@Targets/0/AxisName", "Axis:Y@Targets/0/Target/AxisName", "TeachPoint:P@Targets/0/Target/PointId");
            Expect(new WaitAxisPositionNode { AxisName = "X", TimeoutAlarmCodes = { "bad" } },
                "Axis:X@AxisName", "Alarm:bad@TimeoutAlarmCodes/0");
            Expect(new ReadInputNode { InputName = "sensor", VariableName = "flag" },
                "Input:sensor@InputName", "Variable:flag@VariableName");
            Expect(new WaitInputNode { InputName = "sensor", TimeoutAlarmCodes = { "bad" } },
                "Input:sensor@InputName", "Alarm:bad@TimeoutAlarmCodes/0");
            Expect(new WaitOutputNode { OutputName = "valve", TimeoutAlarmCodes = { "bad" } },
                "Output:valve@OutputName", "Alarm:bad@TimeoutAlarmCodes/0");
            Expect(new WaitExclusiveInputsNode { FirstInputName = "sensor", SecondInputName = "other", FirstVariableName = "flag", SecondVariableName = "second", TimeoutAlarmCodes = { "bad" } },
                "Input:sensor@FirstInputName", "Input:other@SecondInputName", "Variable:flag@FirstVariableName", "Variable:second@SecondVariableName", "Alarm:bad@TimeoutAlarmCodes/0");
            Expect(new ReadInputWordNode { InputNames = { "sensor","other"  }, ValidInputName = "valid", SuccessInputName = "success", TargetVariable = "result", TimeoutAlarmCodes = { "bad" } },
                "Input:sensor@InputNames/0", "Input:other@InputNames/1", "Input:valid@ValidInputName", "Input:success@SuccessInputName", "Variable:result@TargetVariable", "Alarm:bad@TimeoutAlarmCodes/0");
            Expect(new SetVariableNode { VariableName = "result", Value = variable },
                "Variable:result@VariableName", "Variable:flag@Value/VariableName");
            Expect(new WaitConditionNode { Condition = new TaskCondition { Kind = TaskConditionKind.Compare, Left = variable, Right = point  }, TimeoutAlarmCodes = { "bad" } },
                "Variable:flag@Condition/Left/VariableName", "Axis:Y@Condition/Right/AxisName", "TeachPoint:P@Condition/Right/PointId", "Alarm:bad@TimeoutAlarmCodes/0");
            Expect(new WriteRecordNode { Fields = {{ "a/b","result" }} },
                "Variable:result@Fields/a%2Fb");
        }

        /// <summary>可选字段不误报；坏条件和节点对象环能定位到原路径。</summary>
        private static void OptionalFieldsAndCycles()
        {
            var condition = new TaskCondition { Kind = TaskConditionKind.Any };
            condition.Conditions.Add(condition);
            var branch = new ConditionBranchNode { Id = "branch", Condition = condition, WhenFalseAlarmCodes = null,
                WhenTrue = new SequenceNode { Id = "yes" } };
            var cyclic = new SequenceNode { Id = "cyclic" };
            cyclic.Children.Add(cyclic);
            var machine = Machine("A");
            machine.InitializationTaskId = "";
            machine.Stations[0].Mode = StationTaskMode.OnRequest;
            machine.Stations[0].RequestVariable = "flag";
            machine.Stations[0].CompletedVariable = "missing";
            var result = TaskProjectProjection.FromSaved(machine, new[] { Task("main", branch, cyclic) }, Catalog(), "  ");
            Check(result.SourceSignature == null, "空白签名应明确标记未提供。");
            Check(!result.Diagnostics.Any(x => x.Contains("WhenFalseAlarmCodes")), "可选条件分支报警为空不应报错。");
            Check(result.Diagnostics.Any(x => x.Contains("Initialization/Task/") && x.Contains("引用失效")), "空初始化 ID 应保留失败定位。");
            Check(result.Diagnostics.Any(x => x.Contains("/branch/Condition/Conditions/0") && x.Contains("条件对象形成循环")), "未定位条件环。");
            Check(result.Diagnostics.Any(x => x.Contains("/cyclic/Children/cyclic") && x.Contains("节点对象形成循环")), "未定位节点对象环。");
            Check(result.FindReferences("Variable", "missing").Single().Path.EndsWith("/CompletedVariable"), "工位握手字段定位错误。");
            Check(result.References.Count < 20, "循环不应重复展开几十层。");
        }
    }
}
