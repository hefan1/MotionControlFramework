using System;
using System.Collections.Generic;
using System.Linq;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Device.InMemory;
using MotionControl.TaskEngine.Machine;
using MotionControl.TaskEngine.Projection;
using MotionControl.TaskEngine.Validation;

namespace MotionControl.Metadata.Checks
{
    internal static partial class Program
    {
        /// <summary>建立两根单位不同的轴以及两轴同编号点位。</summary>
        private static TaskResourceCatalog NumericCatalog(params DeviceVariableDefinition[] definitions)
        {
            var types = definitions.ToDictionary(item => item.Name, item => (DeviceVariableType)Enum.Parse(typeof(DeviceVariableType), item.DataType));
            return new TaskResourceCatalog(new[] { new TaskAxisInfo("X", "Card", "mm", -100, 100), new TaskAxisInfo("R", "Card", "deg", -180, 180) },
                new string[0], new string[0], types,
                new[] { new TaskTeachPoint("X", "P", "同名点位", 10), new TaskTeachPoint("R", "P", "同名点位", 20) },
                variableInitialValues: definitions.ToDictionary(item => item.Name, item => item.InitialValue),
                alarmCodes: new[] { "WAIT" }, variableDefinitions: definitions);
        }

        /// <summary>把单个节点放入合法顺序任务，只测试当前节点的定义规则。</summary>
        private static TaskDefinition Definition(TaskNode node)
        {
            node.Id = "step"; node.Name = "被检查步骤"; node.TimeoutMs = 1000;
            return new TaskDefinition { TaskId = "T", Name = "隔离任务", Root = new SequenceNode
                { Id = "root", Name = "顺序", TimeoutMs = 10000, Children = { node } } };
        }

        /// <summary>使用正式校验器查找具有明确字段和物理含义的错误。</summary>
        private static IReadOnlyList<TaskValidationIssue> Validate(TaskResourceCatalog catalog, TaskNode node)
        {
            return new TaskDefinitionValidator().Validate(Definition(node), catalog);
        }

        /// <summary>数值类型相同也要拒绝已明确不兼容的单位或量纲；旧配置只提示未定义。</summary>
        private static void NumericBindingCompatibility()
        {
            var length = LengthVariable();
            var angle = new DeviceVariableDefinition { Name = "Angle", DataType = "Number", InitialValue = "3", EngineeringUnit = "deg", Dimension = "Angle" };
            var old = new DeviceVariableDefinition { Name = "Legacy", DataType = "Number" };
            TaskResourceCatalog catalog = NumericCatalog(length, angle, old);
            length.EngineeringUnit = "m"; length.DisplayName = "外部修改";
            Check(catalog.VariableDefinitions["Offset"].EngineeringUnit == "mm" && catalog.VariableDefinitions["Offset"].DisplayName == "补偿距离", "目录持有可变元数据对象。");
            var assignment = new SetVariableNode { VariableName = "Offset", Value = new TaskValueBinding { Source = TaskValueSource.Variable, VariableName = "Angle" } };
            Check(Validate(catalog, assignment).Any(issue => issue.Severity == TaskIssueSeverity.Error && issue.Message.Contains("量纲不兼容")), "角度到长度赋值被接受。");
            assignment.Value = new TaskValueBinding { Source = TaskValueSource.Constant, DataType = DeviceVariableType.Number, Constant = "11" };
            Check(Validate(catalog, assignment).Any(issue => issue.Message.Contains("超出目标变量")), "静态越界常量被接受。");
            assignment.Value = new TaskValueBinding { Source = TaskValueSource.Variable, VariableName = "Legacy" };
            var oldIssues = Validate(catalog, assignment);
            Check(!oldIssues.Any(issue => issue.Severity == TaskIssueSeverity.Error) && oldIssues.Any(issue => issue.Message.Contains("未定义")), "缺元数据旧配置应提示而不是全拒绝。");

            var condition = new WaitConditionNode { TimeoutAlarmCodes = { "WAIT" }, Condition = new TaskCondition
            {
                Kind = TaskConditionKind.Compare, Comparison = TaskComparison.Equal,
                Left = new TaskValueBinding { Source = TaskValueSource.Variable, VariableName = "Offset" },
                Right = new TaskValueBinding { Source = TaskValueSource.Variable, VariableName = "Angle" }
            } };
            Check(Validate(catalog, condition).Any(issue => issue.Message.Contains("量纲不兼容")), "条件比较接受了不同量纲。");
            var move = new MoveAxisNode { AxisName = "X", Mode = TaskMoveMode.Absolute,
                Profile = new TaskMotionProfile { Speed = 1, Acceleration = 1, Deceleration = 1 },
                Target = new TaskValueBinding { Source = TaskValueSource.Variable, VariableName = "Angle" } };
            Check(Validate(catalog, move).Any(issue => issue.Message.Contains("位置来源量纲不兼容")), "直线轴接受了角度变量。");
            move.AxisName = "R";
            Check(!Validate(catalog, move).Any(issue => issue.Severity == TaskIssueSeverity.Error), "兼容角度目标被错误拒绝。");
            Check(NumericValueMetadata.GetCompatibilityError("Length", "mm", "Length", "m") != null, "同量纲不同单位发生了隐式换算。");
            Check(NumericValueMetadata.GetCompatibilityError("Angle", "deg", "Angle", "°") == null, "相同单位别名错误拒绝。");
            Check(NumericValueMetadata.GetCompatibilityError("Pressure", "mPa", "Pressure", "MPa") != null, "大小写不同的 SI 压力单位被当成相同。");
            Check(NumericValueMetadata.GetCompatibilityError("Force", "mN", "Force", "MN") != null, "大小写不同的 SI 力单位被当成相同。");
            Check(NumericValueMetadata.KnownDimension("mPa") == "Pressure" && NumericValueMetadata.KnownDimension("MPa") == "Pressure",
                "压力单位的量纲识别丢失。");
            var pressureCatalog = NumericCatalog(
                new DeviceVariableDefinition { Name = "SmallPressure", DataType = "Number", EngineeringUnit = "mPa", Dimension = "Pressure" },
                new DeviceVariableDefinition { Name = "LargePressure", DataType = "Number", EngineeringUnit = "MPa", Dimension = "Pressure" });
            var pressureAssignment = new SetVariableNode { VariableName = "LargePressure", Value = new TaskValueBinding
                { Source = TaskValueSource.Variable, VariableName = "SmallPressure" } };
            Check(Validate(pressureCatalog, pressureAssignment).Any(issue => issue.Severity == TaskIssueSeverity.Error && issue.Message.Contains("工程单位不一致")),
                "正式任务校验接受了 mPa 到 MPa 的隐式赋值。");
            var device = new InMemoryTaskDevice(catalog);
            Check(device.ReadVariables().Single(value => value.Name == "Offset").EngineeringUnit == "mm", "隔离设备丢失权威单位。");
            device.SetVariable("Offset", null);
            Check(!device.ReadVariables().Single(value => value.Name == "Offset").HasValue, "失效注入把未知改为零或未失效。");
        }

        /// <summary>等待轴引用按同批目录中的精确逻辑键带单位，不被跨轴同编号点位或显示名干扰。</summary>
        private static void ProjectionUsesExactAxis()
        {
            TaskResourceCatalog catalog = NumericCatalog(LengthVariable());
            var wait = new WaitAxisPositionNode { AxisName = "R", Position = 10, Tolerance = 0.5, TimeoutAlarmCodes = { "WAIT" } };
            TaskDefinition task = Definition(wait);
            var machine = new TaskMachineConfiguration { MachineId = "M", Name = "设备", Stations = {
                new StationTaskConfiguration { StationId = "S", Name = "工位", TaskId = "T" } } };
            TaskProjectProjection projection = TaskProjectProjection.FromSaved(machine, new[] { task }, catalog);
            TaskProjectReference axis = projection.FindReferences("Axis", "R").Single();
            Check(axis.EngineeringUnit == "deg" && axis.Dimension == "Angle" && axis.Target == "R" && axis.IsNumericValue, "等待轴投影单位或稳定引用错误。");
            Check(!projection.FindReferences("Axis", "X").Any(), "等待 R 轴却关联了第一根 X 轴。");
            Check(catalog.FindTeachPoint("X", "P").Position == 10 && catalog.FindTeachPoint("R", "P").Position == 20, "跨轴相同 PointId 被混用。");
        }
    }
}
