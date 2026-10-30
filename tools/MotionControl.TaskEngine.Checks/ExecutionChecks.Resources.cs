using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Execution;
using MotionControl.TaskEngine.Resources;
using MotionControl.TaskEngine.Simulation;

namespace MotionControl.TaskEngine.Checks
{
    /// <summary>共享资源的排队、占用和隔离检查；没有任何硬件对象。</summary>
    internal static partial class ExecutionChecks
    {
        /// <summary>构造完整资源范围，正文保持普通顺序步骤。</summary>
        private static ResourceScopeNode ResourceScope(string id, string[] names, params TaskNode[] children)
        {
            return new ResourceScopeNode { Id = id, Name = id, Resources = new List<string>(names),
                Body = Sequence(id + "Body", children) };
        }

        /// <summary>定位一个运行的当前资源记录。</summary>
        private static TaskResourceSnapshot ResourceFor(SimulatedTaskDevice device, TaskRun run)
        {
            return device.ReadResources().FirstOrDefault(item => item.RunId == run.RunId);
        }

        /// <summary>等待指定资源状态，并核对拥有者字段可定位。</summary>
        private static async Task WaitResource(SimulatedTaskDevice device, TaskRun run, TaskResourceState state)
        {
            await Until(() => ResourceFor(device, run)?.State == state, "运行没有达到资源状态：" + state);
            var item = ResourceFor(device, run);
            CheckAssert.True(!string.IsNullOrEmpty(item.TaskId) && !string.IsNullOrEmpty(item.NodeId)
                && !string.IsNullOrEmpty(item.ExecutionId), "资源诊断缺少定位信息。");
        }

        /// <summary>整组申请等待时不部分占轴；与队列无关的轴仍可动作。</summary>
        private static async Task ResourceAtomicGroups()
        {
            var device = Device();
            TaskRun owner = Start(TaskFixtures.Task(ResourceScope("owner", new[] { "Axis:X" }, Input())), device);
            await Until(() => HasNode(owner, "input", TaskNodeState.Waiting), "持有者未进入正文。");
            TaskRun group = Start(TaskFixtures.Task(ResourceScope("group", new[] { "Axis:X", "Axis:Y" }, Input("groupInput"))), device);
            await WaitResource(device, group, TaskResourceState.Waiting);
            device.ConfigureAxis("Y", 0, true, true); // 等待组不能先占住 Y。
            var independent = TaskFixtures.Move("z", "Z");
            independent.Profile.Speed = 100;
            Success(await Complete(Start(TaskFixtures.Task(independent), device)));
            CheckAssert.True(ResourceFor(device, group).State == TaskResourceState.Waiting, "部分资源可用时错误放行完整组。");
            device.SetInput("PartReady", true);
            Success(await Complete(owner));
            Success(await Complete(group));
            CheckAssert.True(device.ReadResources().Count == 0, "完整组未释放。");
        }

        /// <summary>相冲突的后来者不能绕过暂停等待者；取消等待者后才能继续。</summary>
        private static async Task ResourceQueueOrder()
        {
            var device = Device();
            TaskRun owner = Start(TaskFixtures.Task(ResourceScope("owner", new[] { "Zone:Camera" }, Input())), device);
            await Until(() => HasNode(owner, "input", TaskNodeState.Waiting), "区域未持有。");
            TaskRun first = Start(TaskFixtures.Task(ResourceScope("first", new[] { "Zone:Camera" })), device);
            await WaitResource(device, first, TaskResourceState.Waiting);
            CheckAssert.True(first.Pause(), "等待者不能暂停。");
            TaskRun second = Start(TaskFixtures.Task(ResourceScope("second", new[] { "Zone:Camera" })), device);
            await WaitResource(device, second, TaskResourceState.Waiting);
            device.SetInput("PartReady", true);
            Success(await Complete(owner));
            await Task.Delay(80);
            TaskResourceSnapshot firstRequest = ResourceFor(device, first);
            TaskResourceSnapshot secondRequest = ResourceFor(device, second);
            CheckAssert.True(!second.Completion.IsCompleted
                && firstRequest != null && firstRequest.State == TaskResourceState.Waiting
                && secondRequest != null && secondRequest.Detail.Contains("搬运任务")
                && secondRequest.Detail.Contains("“first”步骤"), "暂停等待者被插队或等待原因错误。");
            first.Cancel();
            CheckAssert.True((await Complete(first)).State == TaskRunState.Cancelled, "暂停排队不能取消。");
            Success(await Complete(second));
            CheckAssert.True(device.ReadResources().Count == 0, "队列未清理。");
        }

        /// <summary>取消和超时只移除自己的等待记录，不释放持有者。</summary>
        private static async Task ResourceWaitingCleanup()
        {
            var device = Device();
            TaskRun owner = Start(TaskFixtures.Task(ResourceScope("owner", new[] { "Axis:X", "Output:Vacuum" }, Input())), device);
            await Until(() => HasNode(owner, "input", TaskNodeState.Waiting), "占用者未进入正文。");
            TaskRun cancelled = Start(TaskFixtures.Task(TaskFixtures.Move()), device);
            await WaitResource(device, cancelled, TaskResourceState.Waiting);
            cancelled.Cancel();
            await Complete(cancelled);
            var output = Output(); output.TimeoutMs = 120;
            TaskRun timeout = Start(TaskFixtures.Task(output), device);
            Failure(await Complete(timeout), "等待资源");
            CheckAssert.True(device.ReadResources().Count == 1 && ResourceFor(device, owner).State == TaskResourceState.Held
                && device.ReadOutput("Vacuum") == null, "等待退出破坏拥有者或偷偷写输出。");
            device.SetInput("PartReady", true);
            Success(await Complete(owner));
        }

        /// <summary>等待者取得轴后才读取目标，回零也不能绕过已经持有的轴资源。</summary>
        private static async Task ResourceWaitingTarget()
        {
            var device = Device();
            var hold = TaskFixtures.Task(ResourceScope("owner", new[] { "Axis:X" }, Input()));
            TaskRun owner = Start(hold, device);
            await Until(() => HasNode(owner, "input", TaskNodeState.Waiting), "轴未持有。");
            device.SetVariable("Vision.OffsetX", "10");
            var move = TaskFixtures.Move(); move.Profile.Speed = 100;
            move.Target = Variable("Vision.OffsetX");
            TaskRun waiting = Start(TaskFixtures.Task(move), device);
            await WaitResource(device, waiting, TaskResourceState.Waiting);
            device.SetVariable("Vision.OffsetX", "20");
            device.SetInput("PartReady", true);
            Success(await Complete(owner));
            Success(await Complete(waiting));
            CheckAssert.True(device.ReadAxis("X").Position == 20, "排队前就锁定了尚未接受的目标。");

            device.SetInput("PartReady", false);
            owner = Start(hold, device);
            await Until(() => HasNode(owner, "input", TaskNodeState.Waiting), "回零场景轴未持有。");
            var home = new AxisCommandNode { Id = "home", Name = "回零", AxisName = "X", Command = TaskAxisCommand.Home };
            TaskRun homing = Start(TaskFixtures.Task(home), device);
            await WaitResource(device, homing, TaskResourceState.Waiting);
            CheckAssert.True(device.ReadAxis("X").AcceptedCommands == 1, "回零绕过了资源持有者。");
            device.SetInput("PartReady", true);
            Success(await Complete(owner));
            Success(await Complete(homing));
            CheckAssert.True(device.ReadAxis("X").Position == 0 && device.ReadAxis("X").Homed
                && device.ReadResources().Count == 0, "回零排队后未完成或留下占用。");
        }

        /// <summary>子任务继承同一凭证，多个连续范围各自完整申请和释放。</summary>
        private static async Task ResourceSubtasks()
        {
            var device = Device();
            var move = TaskFixtures.Move(); move.Profile.Speed = 100;
            var child = TaskFixtures.Task(move, Output()); child.TaskId = "Sub";
            var call = new CallTaskNode { Id = "call", Name = "子任务", TaskId = "Sub" };
            var scope = ResourceScope("zone", new[] { "Zone:Camera", "Axis:X", "Output:Vacuum" }, call);
            var again = ResourceScope("again", new[] { "Zone:Camera", "Output:Vacuum" }, Output("off", false));
            TaskRun run = Start(TaskFixtures.Task(scope, again), device, new[] { child });
            Success(await Complete(run));
            CheckAssert.True(device.ReadAxis("X").Position == 10 && device.ReadOutput("Vacuum") == false
                && device.ReadResources().Count == 0, "子任务重复申请、未执行或未释放资源。");
        }

        /// <summary>相同名字在两个设备实例中互不占用；旧快照不随释放变化。</summary>
        private static async Task ResourceDeviceIsolation()
        {
            var first = Device(); var second = Device();
            var definition = TaskFixtures.Task(ResourceScope("zone", new[] { "Zone:Camera" }, Input()));
            TaskRun one = Start(definition, first); TaskRun two = Start(definition, second);
            await Until(() => HasNode(one, "input", TaskNodeState.Waiting) && HasNode(two, "input", TaskNodeState.Waiting), "设备资源串用。");
            var old = ResourceFor(first, one);
            first.SetInput("PartReady", true);
            Success(await Complete(one));
            CheckAssert.True(first.ReadResources().Count == 0 && ResourceFor(second, two).State == TaskResourceState.Held
                && old.State == TaskResourceState.Held && old.Resources.Single() == "Zone:Camera", "资源或快照未隔离。");
            second.SetInput("PartReady", true);
            Success(await Complete(two));
        }
    }
}
