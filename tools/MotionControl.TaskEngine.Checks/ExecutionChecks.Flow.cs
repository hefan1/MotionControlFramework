using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Execution;

namespace MotionControl.TaskEngine.Checks
{
    /// <summary>验证独立并行、汇合、调用关系与运行状态。</summary>
    internal static partial class ExecutionChecks
    {
        /// <summary>两个分支同时等待；只完成一个分支不能进入汇合后动作。</summary>
        private static async Task ParallelJoin()
        {
            var device = Device();
            var parallel = Parallel(Input(), new WaitConditionNode { Id = "waitReady", Name = "等待变量",
                Condition = TaskFixtures.Condition() });
            TaskRun run = Start(TaskFixtures.Task(parallel, Output()), device);
            await Until(() => HasNode(run, "input", TaskNodeState.Waiting) && HasNode(run, "waitReady", TaskNodeState.Waiting), "分支没有独立等待。");
            device.SetInput("PartReady", true);
            await Until(() => HasNode(run, "branchA", TaskNodeState.Completed), "第一分支没有完成。");
            CheckAssert.True(device.ReadOutput("Vacuum") == null && !run.Completion.IsCompleted, "没有等待全部分支汇合。");
            device.SetVariable("Process.Ready", "true");
            Success(await Complete(run));
        }

        /// <summary>独立轴能同时保持动作，而不是用全局等待锁串行执行。</summary>
        private static async Task IndependentAxes()
        {
            var device = Device();
            var first = TaskFixtures.Move("x", "X");
            var second = TaskFixtures.Move("y", "Y");
            first.Profile.Speed = second.Profile.Speed = 20;
            TaskRun run = Start(TaskFixtures.Task(Parallel(first, second), Output()), device);
            await Until(() => device.ReadAxis("X").IsBusy && device.ReadAxis("Y").IsBusy, "独立轴没有同时动作。");
            CheckAssert.True(device.ReadOutput("Vacuum") == null, "轴未完成就通过汇合。");
            Success(await Complete(run));
        }

        /// <summary>一条分支失败后取消另一条长运动，完成时已停下且后续输出未执行。</summary>
        private static async Task ParallelFailure()
        {
            var device = Device();
            var move = TaskFixtures.Move();
            move.Target = TaskFixtures.Number("80");
            move.Profile.Speed = 1;
            var parallel = Parallel(Input(), move);
            ((SequenceNode)parallel.Branches[0]).Children.Add(Assign("badValue", "Vision.OffsetX", Variable("Vision.OffsetX")));
            TaskRun run = Start(TaskFixtures.Task(parallel, Output()), device);
            await Until(() => device.ReadAxis("X").IsBusy && HasNode(run, "input", TaskNodeState.Waiting), "故障场景未就绪。");
            device.SetInput("PartReady", true);
            var result = await Complete(run);
            Failure(result, "Vision.OffsetX");
            CheckAssert.True(result.Error.NodeId == "badValue" && result.Error.InnerException != null, "原始异常位置丢失。");
            CheckAssert.True(!device.ReadAxis("X").IsBusy && device.ReadAxis("X").Position < 80, "兄弟分支没有取消清理。");
            CheckAssert.True(device.ReadOutput("Vacuum") == null, "失败后仍进入后续输出。");
            CheckAssert.True(result.Nodes.Any(node => node.NodeId == "move" && node.State == TaskNodeState.Cancelled), "未记录连带取消。");
        }

        /// <summary>同一子任务多次调用有独立编号；运行中的定义已与调用方修改隔离。</summary>
        private static async Task CallsAndFrozenDefinitions()
        {
            var device = Device();
            var assignment = Assign("setCount", "Product.Count", Constant(DeviceVariableType.UInt32, "7"));
            var sub = TaskFixtures.Task(assignment);
            sub.TaskId = "Sub";
            var main = TaskFixtures.Task(Input(), new CallTaskNode { Id = "call1", Name = "第一次调用", TaskId = "Sub" },
                new CallTaskNode { Id = "call2", Name = "第二次调用", TaskId = "Sub" });
            TaskRun run = Start(main, device, new[] { sub });
            await Until(() => HasNode(run, "input", TaskNodeState.Waiting), "主任务没有开始等待。");
            assignment.Value.Constant = "99";
            main.Revision = 9;
            ((SequenceNode)main.Root).Children.Clear();
            device.SetInput("PartReady", true);
            var result = await Complete(run);
            Success(result);
            var calls = result.Nodes.Where(node => node.TaskId == "Sub" && node.NodeId == "setCount").ToList();
            CheckAssert.True(Value(device, "Product.Count") == "7" && result.Revision == 1, "运行定义被外部修改。");
            CheckAssert.True(calls.Count == 2 && calls[0].ExecutionId != calls[1].ExecutionId
                && calls[0].ParentExecutionId != calls[1].ParentExecutionId, "子任务调用状态互相覆盖。");
        }

        /// <summary>两次运行状态独立；旧快照不会在后台被改写。</summary>
        private static async Task IsolatedSnapshots()
        {
            var firstDevice = Device();
            var secondDevice = Device();
            var definition = TaskFixtures.Task(Input(), Output());
            TaskRun first = Start(definition, firstDevice);
            TaskRun second = Start(definition, secondDevice);
            await Until(() => HasNode(first, "input", TaskNodeState.Waiting) && HasNode(second, "input", TaskNodeState.Waiting), "两个运行没有启动。");
            var old = first.GetSnapshot();
            firstDevice.SetInput("PartReady", true);
            Success(await Complete(first));
            CheckAssert.True(!second.Completion.IsCompleted && secondDevice.ReadOutput("Vacuum") == null, "两个运行串值。");
            CheckAssert.True(old.State == TaskRunState.Running && old.Nodes.Any(node => node.State == TaskNodeState.Waiting), "旧快照发生变化。");
            CheckAssert.True(first.RunId != second.RunId && !first.Pause() && !first.Cancel(), "终态仍接受控制请求。");
            second.Cancel();
            CheckAssert.True((await Complete(second)).State == TaskRunState.Cancelled, "第二次运行不能单独取消。");
        }

        /// <summary>已保存但尚不支持的节点必须在任何输出前拒绝，包括未选中的分支。</summary>
        private static Task UnsupportedBeforeStart()
        {
            var device = Device();
            var coordinated = new CoordinatedMoveNode { Id = "xy", Name = "插补", Profile = TaskFixtures.Profile(),
                Targets = new List<AxisTarget> {
                    new AxisTarget { AxisName = "X", Target = TaskFixtures.Number("10") },
                    new AxisTarget { AxisName = "Y", Target = TaskFixtures.Number("20") } } };
            ExpectStartFailure(TaskFixtures.Task(Output(), coordinated), device, "CoordinatedMove");
            var branch = new ConditionBranchNode { Id = "choose", Name = "条件", Condition = TaskFixtures.Condition(),
                WhenTrue = Sequence("yes"), WhenFalse = Sequence("no", coordinated) };
            ExpectStartFailure(TaskFixtures.Task(Output(), branch), device, "CoordinatedMove");
            ExpectStartFailure(TaskFixtures.Task(Output(), ResourceScope("zone", new[] { "Axis:X", "Axis:Y" }, coordinated)),
                device, "CoordinatedMove");
            CheckAssert.True(device.ReadOutput("Vacuum") == null, "能力拒绝前执行了输出。");
            return Task.CompletedTask;
        }

        /// <summary>确认启动失败来自明确的能力错误，不接受任意异常作为通过。</summary>
        private static void ExpectStartFailure(TaskDefinition definition, Simulation.SimulatedTaskDevice device, string expected)
        {
            try { Start(definition, device); }
            catch (System.NotSupportedException error)
            {
                CheckAssert.True(error.Message.Contains(expected), "启动失败原因不符。");
                return;
            }
            throw new System.InvalidOperationException("没有拒绝未实现能力：" + expected);
        }

        /// <summary>合法无环定义仍可能指数展开；达到总量上限后应诊断退出。</summary>
        private static async Task ExpansionLimit()
        {
            var tasks = new List<TaskDefinition>();
            for (int index = 0; index < 14; index++)
            {
                var definition = TaskFixtures.Task();
                definition.TaskId = "Task" + index;
                if (index < 13)
                    ((SequenceNode)definition.Root).Children.AddRange(new TaskNode[] {
                        new CallTaskNode { Id = "a", Name = "调用 a", TaskId = "Task" + (index + 1) },
                        new CallTaskNode { Id = "b", Name = "调用 b", TaskId = "Task" + (index + 1) } });
                tasks.Add(definition);
            }
            var result = await Complete(Start(tasks[0], Device(), tasks.Skip(1)));
            Failure(result, "10000");
            CheckAssert.True(result.Nodes.Count == 10000, "展开数量保护无效。");
        }
    }
}
