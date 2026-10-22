using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Execution;

namespace MotionControl.TaskEngine.Checks
{
    /// <summary>验证超时、暂停和取消的边界与竞态。</summary>
    internal static partial class ExecutionChecks
    {
        /// <summary>叶节点和包含子动作的父节点都必须落实时限，并保留等待原因。</summary>
        private static async Task Timeouts()
        {
            var device = Device();
            var leaf = await Complete(Start(TaskFixtures.Task(Input(timeout: 120), Output()), device));
            Failure(leaf, "超时");
            CheckAssert.True(leaf.Error.NodeId == "input" && leaf.Error.InnerException is TimeoutException, "叶节点超时位置错误。");
            CheckAssert.True(leaf.Nodes.Any(node => node.NodeId == "input" && node.Detail.Contains("PartReady")), "超时后丢失等待对象。");

            var parentDefinition = TaskFixtures.Task(Parallel(Input(), TaskFixtures.Move()), Output());
            parentDefinition.Root.TimeoutMs = 150;
            var parent = await Complete(Start(parentDefinition, device));
            Failure(parent, "超时");
            CheckAssert.True(parent.Error.NodeId == "root" && !device.ReadAxis("X").IsBusy, "父级超时没有终止子动作。");
            CheckAssert.True(device.ReadOutput("Vacuum") == null, "超时后仍写输出。");
        }

        /// <summary>暂停冻结位置和活动时间；修改目标变量后恢复也不能重算相对终点。</summary>
        private static async Task PauseRelativeMove()
        {
            var device = Device();
            device.ConfigureAxis("X", 10, true, true);
            device.SetVariable("Vision.OffsetX", "4");
            var move = TaskFixtures.Move();
            move.Mode = TaskMoveMode.Relative;
            move.Target = Variable("Vision.OffsetX");
            move.Profile.Speed = 8;
            TaskRun run = Start(TaskFixtures.Task(move, Output()), device);
            await Until(() => device.ReadAxis("X").Position > 10.1 && device.ReadAxis("X").IsBusy, "相对运动没有开始。");
            CheckAssert.True(run.Pause() && !run.Pause(), "重复暂停状态不正确。");
            double position = device.ReadAxis("X").Position;
            double elapsed = run.GetSnapshot().ActiveMilliseconds;
            device.SetVariable("Vision.OffsetX", "60");
            await Task.Delay(160);
            CheckAssert.True(device.ReadAxis("X").Position == position && run.GetSnapshot().ActiveMilliseconds == elapsed,
                "暂停后仍推进位置或活动时间。");
            CheckAssert.True(device.ReadOutput("Vacuum") == null, "暂停期间进入后续输出。");
            CheckAssert.True(run.Resume() && !run.Resume(), "恢复状态错误。");
            Success(await Complete(run));
            CheckAssert.True(device.ReadAxis("X").Position == 14 && device.ReadAxis("X").AcceptedCommands == 1,
                "恢复重复发送相对位移或重新读取目标。");
        }

        /// <summary>暂停超过节点时限也不超时，恢复后完成剩余活动延时。</summary>
        private static async Task PauseTimeBudget()
        {
            var device = Device();
            var delay = new DelayNode { Id = "delay", Name = "工艺延时", Milliseconds = 350, TimeoutMs = 600 };
            TaskRun run = Start(TaskFixtures.Task(delay, Output()), device);
            await Until(() => HasNode(run, "delay", TaskNodeState.Waiting), "延时没有开始。");
            CheckAssert.True(run.Pause(), "不能暂停延时。");
            await Task.Delay(750);
            CheckAssert.True(!run.Completion.IsCompleted && device.ReadOutput("Vacuum") == null, "暂停消耗了节点时限。");
            run.Resume();
            var result = await Complete(run);
            Success(result);
            CheckAssert.True(result.ActiveMilliseconds >= 350 && result.ActiveMilliseconds < 700, "活动计时包含暂停时间或延时被跳过。");
        }

        /// <summary>暂停期间的取消无需恢复就能停止轴；终态后位置和输出不再变化。</summary>
        private static async Task CancelWhilePaused()
        {
            var device = Device();
            var move = TaskFixtures.Move();
            move.Target = TaskFixtures.Number("80");
            TaskRun run = Start(TaskFixtures.Task(Parallel(move, Input()), Output()), device);
            await Until(() => device.ReadAxis("X").IsBusy && HasNode(run, "input", TaskNodeState.Waiting), "并行场景未启动。");
            run.Pause();
            run.Cancel();
            var result = await Complete(run);
            CheckAssert.True(result.State == TaskRunState.Cancelled && result.Error == null, "取消被误报为执行故障。");
            double stopped = device.ReadAxis("X").Position;
            CheckAssert.True(!device.ReadAxis("X").IsBusy, "取消完成仍占用轴。");
            device.SetInput("PartReady", true);
            await Task.Delay(100);
            CheckAssert.True(device.ReadAxis("X").Position == stopped && device.ReadOutput("Vacuum") == null, "终态之后仍执行动作。");
        }

        /// <summary>预取消、立即取消和运行中外部取消都进入统一收尾，不遗留未完成任务。</summary>
        private static async Task ImmediateCancellation()
        {
            for (int index = 0; index < 15; index++)
            {
                var device = Device();
                using (var cancelled = new CancellationTokenSource())
                {
                    cancelled.Cancel();
                    var result = await Complete(Start(TaskFixtures.Task(Output()), device, token: cancelled.Token));
                    CheckAssert.True(result.State == TaskRunState.Cancelled && device.ReadOutput("Vacuum") == null, "预取消仍执行输出。");
                }
                TaskRun immediate = Start(TaskFixtures.Task(Input(), Output()), device);
                immediate.Pause();
                device.SetInput("PartReady", true);
                CheckAssert.True(device.ReadOutput("Vacuum") == null, "立即暂停后仍执行输出。");
                immediate.Cancel();
                CheckAssert.True((await Complete(immediate)).State == TaskRunState.Cancelled, "立即取消未收尾。");
            }
            using (var external = new CancellationTokenSource())
            {
                TaskRun run = Start(TaskFixtures.Task(Input()), Device(), token: external.Token);
                await Until(() => HasNode(run, "input", TaskNodeState.Waiting), "外部取消场景未开始。");
                external.Cancel();
                CheckAssert.True((await Complete(run)).State == TaskRunState.Cancelled, "运行中外部取消没有传播。");
            }
            for (int index = 0; index < 10; index++)
            {
                TaskRun raced = Start(TaskFixtures.Task(Input(), Output()), Device());
                await Task.WhenAll(Task.Run(() => raced.Pause()), Task.Run(() => raced.Resume()), Task.Run(() => raced.Cancel()));
                CheckAssert.True((await Complete(raced)).State == TaskRunState.Cancelled, "并发暂停、恢复、取消未能收尾。");
            }
        }
    }
}
