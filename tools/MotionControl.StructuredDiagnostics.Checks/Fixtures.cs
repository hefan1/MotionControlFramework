using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Execution;
using MotionControl.TaskEngine.Machine;

namespace MotionControl.StructuredDiagnostics.Checks
{
    /// <summary>检查使用的具体快照和内存目录，不含真实设备连接。</summary>
    internal static class Fixtures
    {
        /// <summary>保留分号、等号和中英文字符，验证对象名称不被文本解析拆开。</summary>
        internal const string FirstInput = "夹具;Ready=A";
        /// <summary>第二输入用于证明多等待不会只显示第一项。</summary>
        internal const string SecondInput = "安全门=Closed;B";

        /// <summary>断言关键行为；失败信息直接描述需要维护的业务约束。</summary>
        internal static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        /// <summary>提供检查需要的格式资源；普通键按原键返回以便检查稳定类别。</summary>
        internal static string Text(string key)
        {
            switch (key)
            {
                case "等待对象采样格式": return "{0}：期望 {1}；实际 {2}；{3}";
                case "等待项时限格式": return "活动时限 {0} ms";
                case "结构化等待超时格式": return "等待超时：活动时限 {0}";
                case "毫秒格式": return "{0} ms";
                case "秒格式": return "{0} s";
                case "剩余延时格式": return "剩余 {0} ms";
                case "输入条件满足格式": return "输入 {0} 已满足：{1}";
                default: return key;
            }
        }

        /// <summary>建立明确输入等待，不依赖任何说明字符串识别类别。</summary>
        internal static StationWaitItem Input(string id, string actual = null,
            StationValueQuality quality = StationValueQuality.NotProvided)
        {
            return new StationWaitItem(StationWaitKind.Input, "input.expected_state", StationObjectKind.Input,
                id, id, "True", actual, quality, timeoutMilliseconds: 2000);
        }

        /// <summary>建立带等待集合的不可变诊断。</summary>
        internal static TaskStepDiagnostics Diagnostic(string description, params StationWaitItem[] items)
        {
            return new TaskStepDiagnostics(description, waitKind: StationWaitKind.Input,
                reasonCode: "input.expected_state", waitItems: items);
        }

        /// <summary>建立一个节点快照，用于直接验证显示层不会解析原文。</summary>
        internal static TaskNodeSnapshot Node(string id, string parent, TaskStepDiagnostics diagnostic,
            string detail = null, TaskNodeState state = TaskNodeState.Waiting)
        {
            return new TaskNodeSnapshot(id, parent, "task", id, id, state, detail, diagnostic);
        }

        /// <summary>建立工位快照；最近运行与交接诊断各自独立，便于检查旧周期污染。</summary>
        internal static StationTaskSnapshot Station(StationTaskState state, IReadOnlyList<TaskNodeSnapshot> nodes,
            TaskStepDiagnostics stationDiagnostic = null)
        {
            var control = new StationExecutionControl(null, new StationTaskConfiguration {
                StationId = "S1", Name = "工位一", TaskId = "task", Enabled = true
            });
            control.State = state;
            control.Diagnostics = stationDiagnostic;
            control.LastRun = new TaskRunSnapshot("run", "task", 1, TaskRunState.Running,
                DateTime.UtcNow, 20, nodes, null);
            return new StationTaskSnapshot(control);
        }

        /// <summary>只登记本组检查所需的输入、变量和报警，不引入示例工艺。</summary>
        internal static TaskResourceCatalog Catalog()
        {
            return new TaskResourceCatalog(new TaskAxisInfo[0], new[] { FirstInput, SecondInput },
                new string[0], new Dictionary<string, DeviceVariableType> {
                    { "A;未知=1", DeviceVariableType.Boolean }, { "B=未读取;2", DeviceVariableType.Boolean }
                }, new TaskTeachPoint[0], new[] { "区域;Camera=1", "区域=Move;2" },
                alarmDefinitions: new[] { new TaskAlarmInfo("WAIT_TIMEOUT", "诊断等待超时", MachineAlarmKind.StopMachine) });
        }

        /// <summary>创建有明确故障绑定的等待节点。</summary>
        internal static WaitInputNode Wait(string id, string input)
        {
            return new WaitInputNode { Id = id, Name = id, InputName = input, Expected = true,
                TimeoutAlarmCodes = new List<string> { "WAIT_TIMEOUT" } };
        }

        /// <summary>用顺序根节点包装待执行节点。</summary>
        internal static TaskDefinition Definition(params TaskNode[] nodes)
        {
            return new TaskDefinition { TaskId = "task", Name = "结构化诊断检查",
                Root = new SequenceNode { Id = "root", Name = "检查根", Children = new List<TaskNode>(nodes) } };
        }

        /// <summary>建立布尔比较，左侧可为常量或变量。</summary>
        internal static TaskCondition Compare(TaskValueBinding left, TaskValueBinding right)
        {
            return new TaskCondition { Kind = TaskConditionKind.Compare, Comparison = TaskComparison.Equal,
                Left = left, Right = right };
        }

        /// <summary>建立变量引用，名称完整保留。</summary>
        internal static TaskValueBinding Variable(string name)
        {
            return new TaskValueBinding { Source = TaskValueSource.Variable, VariableName = name };
        }

        /// <summary>建立布尔常量。</summary>
        internal static TaskValueBinding True()
        {
            return new TaskValueBinding { Source = TaskValueSource.Constant, DataType = DeviceVariableType.Boolean, Constant = "true" };
        }
    }

    /// <summary>旧 C# 流程没有新增接口时，系统只能提供工步，不能猜测独立工位状态。</summary>
    internal sealed class LegacyFlow : IStationFlow, IStationInputWaitState
    {
        /// <summary>示例工位的明确名称。</summary>
        public string StationName => "旧流程工位";
        /// <summary>文字中故意包含“等待”，验证不会据此推断状态。</summary>
        public string CurrentStepName => "等待输入;状态=运行中";
        /// <summary>旧契约只提供唯一输入名，其他采样信息仍未提供。</summary>
        public string WaitingInputName { get; set; }
        /// <summary>离线检查不执行设备动作。</summary>
        public Task ExecuteCurrentStepAsync(CancellationToken token) { return Task.CompletedTask; }
        /// <summary>离线检查不改变设备状态。</summary>
        public Task ResetAsync(CancellationToken token) { return Task.CompletedTask; }
    }

    /// <summary>显式实现可选契约的 C# 工位；提供的结构与列表任务使用同一组字段。</summary>
    internal sealed class DescribedFlow : IStationFlow, IStationOperationStatusProvider
    {
        /// <summary>示例工位名称。</summary>
        public string StationName => "结构工位";
        /// <summary>基础工步兼容信息。</summary>
        public string CurrentStepName => "步骤一";
        /// <summary>检查时提供的只读状态。</summary>
        internal StationOperationStatus Status { get; set; }
        /// <summary>用于验证状态读取异常不会伪装为空闲。</summary>
        internal bool ThrowOnRead { get; set; }
        /// <summary>只返回内存状态；可按检查要求模拟状态提供方异常。</summary>
        public StationOperationStatus ReadOperationStatus()
        {
            if (ThrowOnRead) throw new InvalidOperationException("状态源读取失败;id=工位");
            return Status;
        }
        /// <summary>离线检查不执行设备动作。</summary>
        public Task ExecuteCurrentStepAsync(CancellationToken token) { return Task.CompletedTask; }
        /// <summary>离线检查不执行设备复位。</summary>
        public Task ResetAsync(CancellationToken token) { return Task.CompletedTask; }
    }
}
