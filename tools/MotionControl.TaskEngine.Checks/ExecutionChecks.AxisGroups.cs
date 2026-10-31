using System;
using System.Linq;
using System.Threading.Tasks;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Execution;
using MotionControl.TaskEngine.Resources;

namespace MotionControl.TaskEngine.Checks
{
    /// <summary>轴组定位的汇合、资源排队和恢复检查，只操作独立内存设备。</summary>
    internal static partial class ExecutionChecks
    {
        /// <summary>创建各轴独立的定位参数，速度使用本轴工程单位每秒。</summary>
        private static AxisPositionTarget GroupTarget(string axis, string value, double speed = 50,
            TaskMoveMode mode = TaskMoveMode.Absolute)
        {
            return new AxisPositionTarget { AxisName = axis, Mode = mode, Target = TaskFixtures.Number(value),
                Profile = new TaskMotionProfile { Speed = speed, Acceleration = 100, Deceleration = 100 } };
        }

        /// <summary>创建一个有独立节点身份和时限的定位组。</summary>
        private static MoveAxisGroupNode Group(params AxisPositionTarget[] targets)
        {
            return new MoveAxisGroupNode { Id = "group", Name = "XY 轴组定位", Targets = targets.ToList() };
        }

        /// <summary>快轴先到仍等待慢轴且保留轴占用，独立 R 轴不受全局锁阻塞。</summary>
        private static async Task GroupIndependentArrival()
        {
            var device = Device();
            TaskRun run = Start(TaskFixtures.Task(Group(GroupTarget("X", "5", 100), GroupTarget("Y", "20", 20)), Output()), device);
            await Until(() => !device.ReadAxis("X").IsBusy && device.ReadAxis("X").Position == 5
                && device.ReadAxis("Y").IsBusy && HasNode(run, "group", TaskNodeState.Moving), "没有观察到快轴先到、慢轴仍运动。");
            CheckAssert.True(!run.Completion.IsCompleted && device.ReadOutput("Vacuum") == null, "快轴完成就提前执行后续节点。");
            var detail = run.GetSnapshot().Nodes.Single(node => node.NodeId == "group").Detail;
            CheckAssert.True(detail.Contains("X") && detail.Contains("已到位") && detail.Contains("Y"), "等待诊断没有区分各轴进度。");
            TaskRun follower = Start(TaskFixtures.Task(TaskFixtures.Move("nextX")), device);
            await WaitResource(device, follower, TaskResourceState.Waiting);
            var independent = TaskFixtures.Move("r", "R"); independent.Profile.Speed = 100;
            Success(await Complete(Start(TaskFixtures.Task(independent), device)));
            CheckAssert.True(device.ReadAxis("X").AcceptedCommands == 1, "快轴资源被提前让出。");
            follower.Cancel(); await Complete(follower);
            Success(await Complete(run));
            CheckAssert.True(device.ReadAxis("X").Position == 5 && device.ReadAxis("Y").Position == 20
                && device.ReadOutput("Vacuum") == true && device.ReadResources().Count == 0, "整组没有完成或占用未释放。");
        }

        /// <summary>整组等待时不占住其余轴，取消不影响拥有者，取得占用后才解析目标。</summary>
        private static async Task GroupWaitingTargets()
        {
            var device = Device();
            TaskRun owner = Start(TaskFixtures.Task(ResourceScope("hold", new[] { "Axis:X" }, Input())), device);
            await Until(() => HasNode(owner, "input", TaskNodeState.Waiting), "持有者没有进入正文。");
            var group = Group(GroupTarget("X", "10"), GroupTarget("Y", "2", 50, TaskMoveMode.Relative));
            group.Targets[0].Target = Variable("Vision.OffsetX");
            TaskRun cancelled = Start(TaskFixtures.Task(group), device);
            await WaitResource(device, cancelled, TaskResourceState.Waiting);
            cancelled.Cancel(); await Complete(cancelled);
            CheckAssert.True(device.ReadResources().Count == 1 && device.ReadAxis("X").AcceptedCommands == 0
                && device.ReadAxis("Y").AcceptedCommands == 0, "等待者取消破坏占用或提前接受动作。");
            device.SetVariable("Vision.OffsetX", "10");
            TaskRun waiting = Start(TaskFixtures.Task(group), device);
            await WaitResource(device, waiting, TaskResourceState.Waiting);
            device.ConfigureAxis("Y", 3, true, true);
            device.SetVariable("Vision.OffsetX", "25");
            device.SetInput("PartReady", true);
            Success(await Complete(owner)); Success(await Complete(waiting));
            CheckAssert.True(device.ReadAxis("X").Position == 25 && device.ReadAxis("Y").Position == 5
                && device.ReadResources().Count == 0, "没有按取得资源后的目标和起点执行。");
        }

        /// <summary>相互重叠的定位组整体排队，等待组不能先让自己的独立轴运动。</summary>
        private static async Task GroupOverlappingResources()
        {
            var device = Device();
            TaskRun first = Start(TaskFixtures.Task(Group(GroupTarget("X", "20", 20), GroupTarget("Y", "20", 20))), device);
            await Until(() => device.ReadAxis("X").IsBusy && device.ReadAxis("Y").IsBusy, "首组没有同时进入运动状态。");
            var next = Group(GroupTarget("Y", "-20", 100), GroupTarget("R", "30", 100)); next.Id = "nextGroup";
            TaskRun second = Start(TaskFixtures.Task(next), device);
            await WaitResource(device, second, TaskResourceState.Waiting);
            CheckAssert.True(!device.ReadAxis("R").IsBusy && device.ReadAxis("R").AcceptedCommands == 0, "等待组先执行了 R 轴。");
            Success(await Complete(first)); Success(await Complete(second));
            CheckAssert.True(device.ReadAxis("X").Position == 20 && device.ReadAxis("Y").Position == -20
                && device.ReadAxis("R").Position == 30 && device.ReadResources().Count == 0, "重叠组没有依次完成。");
        }

        /// <summary>线性轴与旋转轴各用独立速度；零距离轴立即到位但仍等待整组。</summary>
        private static async Task GroupMixedUnitsAndZeroMove()
        {
            var device = Device();
            TaskRun run = Start(TaskFixtures.Task(Group(GroupTarget("X", "0"), GroupTarget("R", "90", 180))), device);
            await Until(() => device.ReadAxis("R").IsBusy && !device.ReadAxis("X").IsBusy, "零距离与旋转轴进度没有分开。");
            Success(await Complete(run));
            CheckAssert.True(device.ReadAxis("X").Position == 0 && device.ReadAxis("R").Position == 90
                && device.ReadAxis("X").AcceptedCommands == 1, "混合工程单位或零距离处理错误。");
        }

        /// <summary>组动作在子任务中继承范围；取消后仍隔离区域，显式恢复才放行其他运行。</summary>
        private static async Task GroupSubtaskAndScope()
        {
            var device = Device();
            var group = Group(GroupTarget("X", "10"), GroupTarget("Y", "20"));
            var child = TaskFixtures.Task(group); child.TaskId = "Child";
            var call = new CallTaskNode { Id = "call", Name = "执行子任务定位", TaskId = "Child" };
            var scope = ResourceScope("zone", new[] { "Zone:Camera", "Axis:X", "Axis:Y" }, call);
            scope.ExitInterlock = new TaskInterlock();
            scope.ExitInterlock.Axes.Add(AxisInterlock(10, 10));
            scope.ExitInterlock.Axes.Add(new TaskAxisInterlock { AxisName = "Y", MinimumPosition = 20, MaximumPosition = 20 });
            Success(await Complete(Start(TaskFixtures.Task(scope), device, new[] { child })));
            CheckAssert.True(device.ReadResources().Count == 0, "正常子任务范围未释放。");

            group.Targets[0].Target = TaskFixtures.Number("80"); group.Targets[0].Profile.Speed = 1;
            group.Targets[1].Target = TaskFixtures.Number("80"); group.Targets[1].Profile.Speed = 1;
            scope.ExitInterlock = null;
            TaskRun cancelled = Start(TaskFixtures.Task(scope), device, new[] { child });
            await Until(() => device.ReadAxis("X").IsBusy && device.ReadAxis("Y").IsBusy, "取消场景没有开始。");
            cancelled.Cancel(); await Complete(cancelled);
            var blocked = ResourceFor(device, cancelled);
            CheckAssert.True(blocked.State == TaskResourceState.Blocked && blocked.Resources.Count == 3
                && !device.ReadAxis("X").IsBusy && !device.ReadAxis("Y").IsBusy, "组动作取消后区域被提前释放。");
            TaskRun waiting = Start(TaskFixtures.Task(Group(GroupTarget("X", "0", 100), GroupTarget("Y", "0", 100))), device);
            await WaitResource(device, waiting, TaskResourceState.Waiting);
            string reason;
            CheckAssert.True(device.TryRecoverResources(blocked.RequestId, "已核实内存场景区域清空", out reason), reason);
            Success(await Complete(waiting));
            CheckAssert.True(device.ReadResources().Count == 0, "恢复后仍有资源遗留。");
        }
    }
}
