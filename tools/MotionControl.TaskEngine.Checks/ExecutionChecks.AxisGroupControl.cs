using System;
using System.Threading.Tasks;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Execution;

namespace MotionControl.TaskEngine.Checks
{
    /// <summary>轴组前置检查、运行故障和暂停取消的回归。</summary>
    internal static partial class ExecutionChecks
    {
        /// <summary>后一根轴不满足条件或目标无效时，前一根轴也不接受动作。</summary>
        private static async Task GroupPreflight()
        {
            string[] reasons = { "未使能：Y", "尚未回零：Y", "测试 Y 报警", "尚无有效值", "超出配置行程" };
            for (int scenario = 0; scenario < reasons.Length; scenario++)
            {
                var device = Device();
                var group = Group(GroupTarget("X", "10"), GroupTarget("Y", "10"));
                device.ConfigureAxis("Y", 0, scenario != 0, scenario != 1);
                if (scenario == 2) device.SetAxisAlarm("Y", "测试 Y 报警");
                if (scenario >= 3)
                {
                    group.Targets[1].Target = Variable("Vision.OffsetX");
                    if (scenario == 4) device.SetVariable("Vision.OffsetX", "101");
                }
                Failure(await Complete(Start(TaskFixtures.Task(group, Output()), device)), reasons[scenario]);
                CheckAssert.True(device.ReadAxis("X").AcceptedCommands == 0 && device.ReadAxis("Y").AcceptedCommands == 0
                    && !device.ReadAxis("X").IsBusy && device.ReadResources().Count == 0
                    && device.ReadOutput("Vacuum") == null, "整组检查失败前已接受部分动作或没有清理。");
            }
            var relativeDevice = Device(); relativeDevice.ConfigureAxis("Y", 95, true, true);
            var relative = Group(GroupTarget("X", "10"), GroupTarget("Y", "10", 50, TaskMoveMode.Relative));
            Failure(await Complete(Start(TaskFixtures.Task(relative), relativeDevice)), "超出配置行程");
            CheckAssert.True(relativeDevice.ReadAxis("X").AcceptedCommands == 0, "相对终点越界时第一根轴已接受动作。");
            var durationDevice = Device();
            var durationGroup = Group(GroupTarget("X", "10"), GroupTarget("Y", "10", 1e-308));
            Failure(await Complete(Start(TaskFixtures.Task(durationGroup), durationDevice)), "运动时长超出有限范围：Y");
            CheckAssert.True(durationDevice.ReadAxis("X").AcceptedCommands == 0 && durationDevice.ReadAxis("Y").AcceptedCommands == 0
                && durationDevice.ReadResources().Count == 0, "时长溢出前接受了部分动作或遗留占用。");
        }

        /// <summary>暂停冻结全部剩余动作和活动时钟；恢复不会再读取变量、修改速度或叠加相对距离。</summary>
        private static async Task GroupPauseRelative()
        {
            var device = Device(); device.SetVariable("Vision.OffsetX", "10");
            var group = Group(GroupTarget("X", "10", 50, TaskMoveMode.Relative), GroupTarget("Y", "20", 50, TaskMoveMode.Relative));
            group.TimeoutMs = 900;
            group.Targets[0].Target = Variable("Vision.OffsetX");
            TaskRun run = Start(TaskFixtures.Task(group), device);
            await Until(() => device.ReadAxis("X").Position > 0 && device.ReadAxis("Y").Position > 0, "暂停前没有接受组动作。");
            CheckAssert.True(run.Pause(), "组动作不能暂停。");
            double x = device.ReadAxis("X").Position, y = device.ReadAxis("Y").Position;
            double active = run.GetSnapshot().ActiveMilliseconds;
            device.SetVariable("Vision.OffsetX", "70");
            group.Targets[1].Target.Constant = "80";
            group.Targets[1].Profile.Speed = 1;
            await Task.Delay(1000);
            CheckAssert.True(device.ReadAxis("X").Position == x && device.ReadAxis("Y").Position == y
                && run.GetSnapshot().ActiveMilliseconds == active && !run.Completion.IsCompleted, "暂停仍推进组动作或消耗时限。");
            run.Resume(); Success(await Complete(run));
            CheckAssert.True(device.ReadAxis("X").Position == 10 && device.ReadAxis("Y").Position == 20
                && device.ReadAxis("X").AcceptedCommands == 1 && device.ReadAxis("Y").AcceptedCommands == 1, "恢复重发或改变了原目标。");
        }

        /// <summary>暂停取消和节点/父级超时均停下整组，后续节点不执行，自动资源完整释放。</summary>
        private static async Task GroupCancellationAndTimeout()
        {
            for (int scenario = 0; scenario < 3; scenario++)
            {
                var device = Device();
                var group = Group(GroupTarget("X", "80", 1), GroupTarget("Y", "80", 2));
                var definition = TaskFixtures.Task(group, Output());
                if (scenario == 1) group.TimeoutMs = 150;
                if (scenario == 2) definition.Root.TimeoutMs = 150;
                TaskRun run = Start(definition, device);
                await Until(() => device.ReadAxis("X").IsBusy && device.ReadAxis("Y").IsBusy, "取消/超时场景未开始。");
                if (scenario == 0) { run.Pause(); run.Cancel(); }
                var result = await Complete(run);
                if (scenario == 0) CheckAssert.True(result.State == TaskRunState.Cancelled, "暂停取消终态错误。");
                else
                {
                    Failure(result, "执行超时");
                    CheckAssert.True(result.Error.NodeId == (scenario == 1 ? "group" : "root")
                        && result.Error.Message.Contains("轴组定位"), "超时缺少父子节点或组内进度。");
                }
                double x = device.ReadAxis("X").Position, y = device.ReadAxis("Y").Position;
                await Task.Delay(30);
                CheckAssert.True(!device.ReadAxis("X").IsBusy && !device.ReadAxis("Y").IsBusy
                    && device.ReadAxis("X").Position == x && device.ReadAxis("Y").Position == y
                    && x < 80 && y < 80 && device.ReadResources().Count == 0
                    && device.ReadOutput("Vacuum") == null, "异常后组动作仍继续或提前执行后续输出。");
            }
        }

        /// <summary>运动中的轴或已先到位的轴报警，都结束整组并保留准确轴名和原始原因。</summary>
        private static async Task GroupRuntimeAlarm()
        {
            for (int scenario = 0; scenario < 2; scenario++)
            {
                var device = Device();
                var group = Group(GroupTarget("X", scenario == 0 ? "20" : "1", scenario == 0 ? 10 : 100), GroupTarget("Y", "40", 10));
                TaskRun run = Start(TaskFixtures.Task(group, Output()), device);
                if (scenario == 0)
                    await Until(() => device.ReadAxis("X").Position > 0 && device.ReadAxis("Y").Position > 0, "报警场景未推进。");
                else
                    await Until(() => device.ReadAxis("X").Position == 1 && !device.ReadAxis("X").IsBusy
                        && device.ReadAxis("Y").IsBusy, "未观察到先到位轴。");
                string axis = scenario == 0 ? "Y" : "X";
                device.SetAxisAlarm(axis, "组内驱动器测试故障");
                var result = await Complete(run);
                Failure(result, "组内驱动器测试故障");
                CheckAssert.True(result.Error.NodeId == "group" && result.Error.Message.Contains(axis)
                    && result.Error.InnerException != null && !device.ReadAxis("X").IsBusy && !device.ReadAxis("Y").IsBusy
                    && device.ReadOutput("Vacuum") == null && device.ReadResources().Count == 0, "报警未停止整组或丢失故障位置。");
            }
        }
    }
}
