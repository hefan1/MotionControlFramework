using System;
using System.Collections.Generic;
using System.Linq;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.TaskEngine.Configuration;
using MotionControl.TaskEngine.Definitions;

namespace MotionControl.TaskEngine.Wpf.Editing
{
    /// <summary>列表编辑需要的明确节点操作，不执行流程，也不反射查找节点类型。</summary>
    internal static class TaskEditorNodes
    {
        /// <summary>按 UI 明确选择建立普通节点，未填引用保持空白供检查。</summary>
        internal static TaskNode New(string kind)
        {
            TaskNode node;
            switch (kind)
            {
                case "MoveAxis": node = new MoveAxisNode { AxisName = "", Target = Value(), Profile = Profile() }; break;
                case "MoveAxisGroup": node = new MoveAxisGroupNode { Targets = new List<AxisPositionTarget> { Target(), Target() } }; break;
                case "Home": node = new AxisCommandNode { AxisName = "", Command = TaskAxisCommand.Home }; break;
                case "Stop": node = new AxisCommandNode { AxisName = "", Command = TaskAxisCommand.Stop, StopMode = AxisStopMode.Controlled }; break;
                case "WaitInput": node = new WaitInputNode { InputName = "", Expected = true }; break;
                case "WaitOutput": node = new WaitOutputNode { OutputName = "", Expected = false }; break;
                case "ReadInput": node = new ReadInputNode { InputName = "", VariableName = "" }; break;
                case "WriteRecord": node = new WriteRecordNode { Category = "", Fields = new Dictionary<string, string>() }; break;
                case "ReadInputWord": node = new ReadInputWordNode { InputNames = Enumerable.Repeat("", 16).ToList(), ValidInputName = "", SuccessInputName = "", TargetVariable = "", Prefix = "" }; break;
                case "WaitAxisPosition": node = new WaitAxisPositionNode { AxisName = "", Tolerance = 0.01 }; break;
                case "WaitExclusiveInputs": node = new WaitExclusiveInputsNode { FirstInputName = "", SecondInputName = "", FirstVariableName = "", SecondVariableName = "" }; break;
                case "SetOutput": node = new SetOutputNode { OutputName = "", Value = false }; break;
                case "SetVariable": node = new SetVariableNode { VariableName = "", Value = Value() }; break;
                case "Delay": node = new DelayNode { Milliseconds = 1000 }; break;
                case "Sequence": node = new SequenceNode(); break;
                case "ParallelGroup": node = new ParallelGroupNode { Branches = new List<TaskNode> { New("Sequence"), New("Sequence") } }; break;
                case "ConditionBranch": node = new ConditionBranchNode { Condition = Condition(), WhenTrue = New("Sequence"), WhenFalse = New("Sequence") }; break;
                case "WaitCondition": node = new WaitConditionNode { Condition = Condition() }; break;
                case "CallTask": node = new CallTaskNode { TaskId = "" }; break;
                case "ResourceScope": node = new ResourceScopeNode { Body = New("Sequence") }; break;
                default: throw new InvalidOperationException("当前编辑器不支持新增步骤：" + kind);
            }
            node.Id = Guid.NewGuid().ToString("N"); node.Name = KindName(kind); return node;
        }
        /// <summary>供运动目标使用的明确 double 常量。</summary>
        private static TaskValueBinding Value()
        { return new TaskValueBinding { Source = TaskValueSource.Constant, DataType = DeviceVariableType.Number, Constant = "0" }; }
        /// <summary>新建工艺参数，人员仍须按设备核对。</summary>
        private static TaskMotionProfile Profile()
        { return new TaskMotionProfile { Speed = 10, Acceleration = 100, Deceleration = 100 }; }
        /// <summary>轴组的新行不默认选择某根轴。</summary>
        internal static AxisPositionTarget Target()
        { return new AxisPositionTarget { AxisName = "", Target = Value(), Profile = Profile() }; }
        /// <summary>条件左值保留未选变量，避免默认恒成立造成误解。</summary>
        internal static TaskCondition Condition()
        { return new TaskCondition { Kind = TaskConditionKind.Compare, Comparison = TaskComparison.Equal,
            Left = new TaskValueBinding { Source = TaskValueSource.Variable, VariableName = "" },
            Right = new TaskValueBinding { Source = TaskValueSource.Constant, DataType = DeviceVariableType.Boolean, Constant = "true" } }; }
        /// <summary>为操作者提供固定且可搜索的步骤类型文字。</summary>
        internal static string KindName(string kind)
        {
            switch (kind) { case "MoveAxis": return "单轴定位"; case "MoveAxisGroup": return "多轴一起定位"; case "Home": return "回零";
                case "Stop": return "停轴"; case "AxisCommand": return "轴命令"; case "WaitInput": return "等待DI"; case "WaitOutput": return "等待DO读回"; case "ReadInput": return "读取DI到变量"; case "ReadInputWord": return "读取锁存编码"; case "WaitAxisPosition": return "等待轴到位"; case "WaitExclusiveInputs": return "等待互斥DI"; case "SetOutput": return "设置DO";
                case "WriteRecord": return "保存记录"; case "SetVariable": return "变量赋值"; case "Delay": return "延时"; case "Sequence": return "顺序段"; case "ParallelGroup": return "并行段";
                case "ConditionBranch": return "条件分支"; case "WaitCondition": return "等待条件"; case "CallTask": return "调用子任务";
                case "ResourceScope": return "资源范围"; default: return "直线插补未接入"; }
        }
        /// <summary>遍历固定节点关系，显示树与引用检查复用同一关系。</summary>
        internal static IEnumerable<TaskNode> Children(TaskNode node)
        {
            if (node is SequenceNode sequence) return sequence.Children;
            if (node is ParallelGroupNode parallel) return parallel.Branches;
            if (node is ConditionBranchNode branch) return new[] { branch.WhenTrue, branch.WhenFalse }.Where(item => item != null);
            if (node is ResourceScopeNode scope) return scope.Body == null ? new TaskNode[0] : new[] { scope.Body };
            return new TaskNode[0];
        }
        /// <summary>查找可排序的实际父列表；根、分支根和范围正文不能作为普通行移除。</summary>
        internal static List<TaskNode> ParentList(TaskNode root, TaskNode target)
        {
            List<TaskNode> list = root is SequenceNode sequence ? sequence.Children : root is ParallelGroupNode parallel ? parallel.Branches : null;
            if (list != null && list.Contains(target)) return list;
            foreach (TaskNode child in Children(root)) { var found = ParentList(child, target); if (found != null) return found; }
            return null;
        }
        /// <summary>严格复制任务，防止编辑器改动运行或调用方原对象。</summary>
        internal static TaskDefinition CopyTask(TaskDefinition task)
        { return TaskDefinitionJson.Deserialize(TaskDefinitionJson.Serialize(task)); }
        /// <summary>复制步骤时所有内部节点分配新 ID，子任务引用保持原目标。</summary>
        internal static TaskNode CopyNode(TaskNode node)
        {
            var wrapper = new TaskDefinition { TaskId = "Copy", Name = "Copy", Root = new SequenceNode { Id = "copyRoot", Name = "Copy", Children = new List<TaskNode> { node } } };
            TaskNode copied = ((SequenceNode)CopyTask(wrapper).Root).Children[0]; RenewIds(copied); return copied;
        }
        /// <summary>递归更新内部节点 ID，不改步序以外的业务参数。</summary>
        internal static void RenewIds(TaskNode node)
        { node.Id = Guid.NewGuid().ToString("N"); foreach (TaskNode child in Children(node)) RenewIds(child); }
    }
}
