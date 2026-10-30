using System.Collections.Generic;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Definitions;

namespace MotionControl.TaskEngine.Checks
{
    /// <summary>仅供离线检查的配置和定义，不包含控制器连接。</summary>
    internal static class TaskFixtures
    {
        /// <summary>建立两个同卡轴、一个异卡轴、IO、变量和未示教点。</summary>
        internal static TaskResourceCatalog Catalog()
        {
            return new TaskResourceCatalog(
                new[] {
                    new TaskAxisInfo("X", "Card01", "mm", -100, 100),
                    new TaskAxisInfo("Y", "Card01", "mm", -100, 100),
                    new TaskAxisInfo("Z", "Card02", "mm", -100, 100),
                    new TaskAxisInfo("R", "Card01", "deg", -180, 180)
                },
                new[] { "PartReady" }, new[] { "Vacuum" },
                new Dictionary<string, DeviceVariableType> {
                    { "Vision.OffsetX", DeviceVariableType.Number },
                    { "Vision.Result", DeviceVariableType.String },
                    { "Product.Count", DeviceVariableType.UInt32 },
                    { "Process.Ready", DeviceVariableType.Boolean }
                },
                new[] {
                    new TaskTeachPoint("X", "P001", "取料位", 10),
                    new TaskTeachPoint("Y", "P001", "放料位", 20),
                    new TaskTeachPoint("X", "P002", "尚未示教", null)
                },
                new[] { "Zone:Camera" },
                alarmCodes: new[] { "DEMO_SENSOR_TIMEOUT", "OPERATOR_NOTICE" });
        }

        /// <summary>用一个顺序根节点包装待检查动作。</summary>
        internal static TaskDefinition Task(params TaskNode[] nodes)
        {
            return new TaskDefinition {
                TaskId = "Transfer", Name = "搬运任务",
                Root = new SequenceNode { Id = "root", Name = "生产周期", Children = new List<TaskNode>(nodes) }
            };
        }

        /// <summary>建立有明确工艺参数的绝对定位定义。</summary>
        internal static MoveAxisNode Move(string id = "move", string axis = "X")
        {
            return new MoveAxisNode {
                Id = id, Name = "移动到目标", AxisName = axis,
                Mode = TaskMoveMode.Absolute, Target = Number("10"), Profile = Profile()
            };
        }

        /// <summary>提供有限且为正数的工艺运动参数。</summary>
        internal static TaskMotionProfile Profile()
        {
            return new TaskMotionProfile { Speed = 5, Acceleration = 10, Deceleration = 10 };
        }

        /// <summary>建立 double 常量来源。</summary>
        internal static TaskValueBinding Number(string value)
        {
            return new TaskValueBinding { Source = TaskValueSource.Constant, DataType = DeviceVariableType.Number, Constant = value };
        }

        /// <summary>建立引用已有布尔变量的比较条件。</summary>
        internal static TaskCondition Condition()
        {
            return new TaskCondition {
                Kind = TaskConditionKind.Compare, Comparison = TaskComparison.Equal,
                Left = new TaskValueBinding { Source = TaskValueSource.Variable, VariableName = "Process.Ready" },
                Right = new TaskValueBinding { Source = TaskValueSource.Constant, DataType = DeviceVariableType.Boolean, Constant = "true" }
            };
        }

        /// <summary>建立可用于分支的简单延时。</summary>
        internal static DelayNode Delay(string id)
        {
            return new DelayNode { Id = id, Name = "等待工艺时间", Milliseconds = 10 };
        }
    }
}
