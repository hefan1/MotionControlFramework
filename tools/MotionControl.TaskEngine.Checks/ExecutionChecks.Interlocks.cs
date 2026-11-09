using System;
using System.Linq;
using System.Threading.Tasks;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Execution;
using MotionControl.TaskEngine.Resources;

namespace MotionControl.TaskEngine.Checks
{
    /// <summary>显式互锁、异常隔离及恢复检查。</summary>
    internal static partial class ExecutionChecks
    {
        /// <summary>构造明确的输入互锁，不用未知状态代替 false。</summary>
        private static TaskInterlock InputInterlock(bool expected)
        {
            var result = new TaskInterlock();
            result.Inputs.Add(new TaskInputInterlock { InputName = "PartReady", Expected = expected });
            return result;
        }

        /// <summary>构造一个工程单位位置范围。</summary>
        private static TaskAxisInterlock AxisInterlock(double minimum, double maximum)
        {
            return new TaskAxisInterlock { AxisName = "X", MinimumPosition = minimum, MaximumPosition = maximum };
        }

        /// <summary>进入要等输入，退出要等位置、输入和变量全部满足；定义修改不能改变运行。</summary>
        private static async Task ResourceInterlocks()
        {
            var device = Device();
            var move = TaskFixtures.Move(); move.Profile.Speed = 100;
            var scope = ResourceScope("zone", new[] { "Zone:Camera", "Axis:X", "Output:Vacuum" }, move, Output());
            scope.EntryInterlock = InputInterlock(true);
            scope.EntryInterlock.Axes.Add(AxisInterlock(0, 0));
            scope.ExitInterlock = InputInterlock(false);
            scope.ExitInterlock.Axes.Add(AxisInterlock(10, 10));
            scope.ExitInterlock.Condition = TaskFixtures.Condition();
            TaskRun run = Start(TaskFixtures.Task(scope), device);
            await Until(() => HasNode(run, "zone", TaskNodeState.Waiting), "没有等待未知输入。");
            CheckAssert.True(device.ReadAxis("X").AcceptedCommands == 0 && device.ReadOutput("Vacuum") == null, "进入互锁未通过却执行正文。");
            scope.ExitInterlock.Inputs[0].Expected = true; // 不能改到启动时冻结的定义。
            device.SetInput("PartReady", true);
            await Until(() => HasNode(run, "zoneBody", TaskNodeState.Completed), "正文没有完成。");
            // 等到退出互锁真正进入等待态，避免用正文刚完成的过渡时刻检查资源。
            await Until(() => HasNode(run, "zone", TaskNodeState.Waiting), "正文完成后没有进入退出互锁等待。");
            var resourceBeforePause = ResourceFor(device, run);
            CheckAssert.True(!run.Completion.IsCompleted && resourceBeforePause != null
                && resourceBeforePause.State == TaskResourceState.Held,
                "退出条件不满足却释放资源；当前运行=" + run.GetSnapshot().State
                + "，资源条数=" + device.ReadResources().Count + "。");
            device.SetVariable("Process.Ready", "true");
            await Task.Delay(40);
            CheckAssert.True(!run.Completion.IsCompleted, "退出条件被外部定义改动覆盖。");
            CheckAssert.True(run.Pause(), "退出等待不能暂停。");
            device.SetInput("PartReady", false);
            await Task.Delay(40);
            var resourceDuringPause = ResourceFor(device, run);
            CheckAssert.True(resourceDuringPause != null && resourceDuringPause.State == TaskResourceState.Held,
                "暂停期间资源丢失或提前释放；当前运行=" + run.GetSnapshot().State
                + "，资源条数=" + device.ReadResources().Count + "。");
            run.Resume();
            Success(await Complete(run));
            CheckAssert.True(device.ReadResources().Count == 0, "正常退出未释放。");
        }

        /// <summary>进入条件不满足时取消或超时可退还资源；轴各前置状态均参与检查。</summary>
        private static async Task ResourceEntryFailure()
        {
            var device = Device();
            var scope = ResourceScope("zone", new[] { "Axis:X", "Output:Vacuum" }, Output());
            scope.EntryInterlock = new TaskInterlock();
            scope.EntryInterlock.Axes.Add(AxisInterlock(0, 0));
            scope.TimeoutMs = 150;
            for (int scenario = 0; scenario < 4; scenario++)
            {
                device.ConfigureAxis("X", scenario == 0 ? 1 : 0, scenario != 1, scenario != 2);
                device.SetAxisAlarm("X", scenario == 3 ? "测试报警" : null);
                Failure(await Complete(Start(TaskFixtures.Task(scope), device)), "轴 X");
                CheckAssert.True(device.ReadResources().Count == 0 && device.ReadOutput("Vacuum") == null, "进入失败留下待确认或执行正文。");
            }
            device.SetAxisAlarm("X", null);
            device.ConfigureAxis("X", 0, true, true);
            scope.TimeoutMs = 5000;
            scope.EntryInterlock = InputInterlock(true);
            TaskRun cancelled = Start(TaskFixtures.Task(scope), device);
            await Until(() => HasNode(cancelled, "zone", TaskNodeState.Waiting), "取消场景没有进入互锁等待。");
            cancelled.Cancel();
            await Complete(cancelled);
            CheckAssert.True(device.ReadResources().Count == 0, "正文开始前取消仍隔离资源。");
        }

        /// <summary>故障不回滚输出且不自动释放；复核退出条件和报警后才允许后续运行取得资源。</summary>
        private static async Task ResourceFailureRecovery()
        {
            var device = Device();
            var scope = ResourceScope("zone", new[] { "Zone:Camera", "Axis:X", "Output:Vacuum" },
                Output(), Assign("bad", "Vision.OffsetX", Variable("Vision.OffsetX")));
            scope.ExitInterlock = InputInterlock(true);
            TaskRun failed = Start(TaskFixtures.Task(scope), device);
            Failure(await Complete(failed), "Vision.OffsetX");
            var blocked = ResourceFor(device, failed);
            CheckAssert.True(blocked.State == TaskResourceState.Blocked && blocked.Resources.Count == 3
                && blocked.Detail.Contains("Vision.OffsetX") && device.ReadOutput("Vacuum") == true, "故障没有保留完整占用或输出。");
            TaskRun follower = Start(TaskFixtures.Task(Output("off", false)), device);
            await WaitResource(device, follower, TaskResourceState.Waiting);
            string reason;
            CheckAssert.True(!device.TryRecoverResources(blocked.RequestId, "", out reason) && reason.Contains("确认说明"), "空确认放行。");
            CheckAssert.True(!device.TryRecoverResources(blocked.RequestId, "已检查", out reason) && reason.Contains("未知"), "未知输入放行。");
            device.SetInput("PartReady", true);
            device.SetAxisAlarm("X", "报警尚未解除");
            CheckAssert.True(!device.TryRecoverResources(blocked.RequestId, "已检查", out reason) && reason.Contains("报警"), "报警轴放行。");
            device.SetAxisAlarm("X", null);
            CheckAssert.True(device.TryRecoverResources(blocked.RequestId, "已核实区域清空", out reason), reason);
            Success(await Complete(follower));
            CheckAssert.True(!device.TryRecoverResources(blocked.RequestId, "再次确认", out reason), "旧申请重复恢复成功。");
            CheckAssert.True(failed.GetSnapshot().State == TaskRunState.Failed && device.ReadOutput("Vacuum") == false
                && device.ReadResources().Count == 0, "恢复重启了旧运行或没有让出输出。");
        }

        /// <summary>正文已完成但退出条件超时，仍需显式确认，兼容旧 ExitCondition。</summary>
        private static async Task ResourceExitTimeout()
        {
            var device = Device();
            var scope = ResourceScope("zone", new[] { "Zone:Camera" }, TaskFixtures.Delay("work"));
            scope.TimeoutMs = 150;
            scope.ExitCondition = TaskFixtures.Condition();
            TaskRun run = Start(TaskFixtures.Task(scope), device);
            Failure(await Complete(run), "退出确认");
            var blocked = ResourceFor(device, run);
            CheckAssert.True(blocked.State == TaskResourceState.Blocked, "退出超时却释放了区域。");
            string reason;
            CheckAssert.True(!device.TryRecoverResources(blocked.RequestId, "已检查", out reason), "无效变量被当作退出满足。");
            device.SetVariable("Process.Ready", "true");
            CheckAssert.True(device.TryRecoverResources(blocked.RequestId, "区域已清空", out reason), reason);

            var wrongPosition = ResourceScope("position", new[] { "Axis:X" });
            wrongPosition.TimeoutMs = 120;
            wrongPosition.ExitInterlock = new TaskInterlock();
            wrongPosition.ExitInterlock.Axes.Add(AxisInterlock(10, 10));
            TaskRun positionRun = Start(TaskFixtures.Task(wrongPosition), device);
            Failure(await Complete(positionRun), "轴 X");
            CheckAssert.True(!device.TryRecoverResources(ResourceFor(device, positionRun).RequestId, "尝试确认", out reason)
                && reason.Contains("当前位置=0"), "确认说明绕过了未满足的位置条件。");
        }

        /// <summary>取消并停下自己的内存运动，但区域不能自动释放，也不能修改起始位置绕过隔离。</summary>
        private static async Task ResourceMotionCancellation()
        {
            var device = Device();
            TaskRun run = Start(TaskFixtures.Task(ResourceScope("zone", new[] { "Axis:X", "Zone:Camera" }, TaskFixtures.Move())), device);
            await Until(() => device.ReadAxis("X").IsBusy, "范围内运动没有开始。");
            CheckAssert.True(run.Pause(), "动作不能暂停。");
            run.Cancel();
            CheckAssert.True((await Complete(run)).State == TaskRunState.Cancelled && !device.ReadAxis("X").IsBusy, "取消没有等待轴停止。");
            var blocked = ResourceFor(device, run);
            CheckAssert.True(blocked.State == TaskResourceState.Blocked, "取消正文后自动让出区域。");
            try { device.ConfigureAxis("X", 0, true, true); throw new Exception("待确认轴被配置覆盖。"); }
            catch (InvalidOperationException error) { CheckAssert.True(error.Message.Contains("待确认"), "配置失败原因错误。"); }
            string reason;
            CheckAssert.True(device.TryRecoverResources(blocked.RequestId, "仿真场景已人工核实退出", out reason), reason);
            CheckAssert.True(device.ReadResources().Count == 0, "显式恢复未解除占用。");
        }

        /// <summary>并行某范围失败时，另一范围停止动作并分别保留待确认记录。</summary>
        private static async Task ResourceParallelFailure()
        {
            var device = Device();
            var fault = ResourceScope("fault", new[] { "Zone:Camera" }, Input(),
                Assign("bad", "Vision.OffsetX", Variable("Vision.OffsetX")));
            var moving = ResourceScope("moving", new[] { "Axis:X" }, TaskFixtures.Move());
            TaskRun run = Start(TaskFixtures.Task(Parallel(fault, moving)), device);
            await Until(() => device.ReadAxis("X").IsBusy && HasNode(run, "input", TaskNodeState.Waiting), "并行范围没有同时运行。");
            device.SetInput("PartReady", true);
            Failure(await Complete(run), "Vision.OffsetX");
            CheckAssert.True(!device.ReadAxis("X").IsBusy && device.ReadResources().Count == 2
                && device.ReadResources().All(item => item.State == TaskResourceState.Blocked), "并行异常清理自动让出区域。");
            foreach (var item in device.ReadResources())
            {
                string reason;
                CheckAssert.True(device.TryRecoverResources(item.RequestId, "逐项确认仿真区域", out reason), reason);
            }
        }
    }
}
