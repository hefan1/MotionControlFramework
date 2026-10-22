using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Execution;

namespace MotionControl.TaskEngine.Checks
{
    /// <summary>验证单轴内存模型的前置条件、冲突和清理行为。</summary>
    internal static partial class ExecutionChecks
    {
        /// <summary>同轴同时动作失败；取消失败运行不能释放另一独立运行持有的轴。</summary>
        private static async Task AxisConflicts()
        {
            var device = Device();
            var result = await Complete(Start(TaskFixtures.Task(Parallel(TaskFixtures.Move("a"), TaskFixtures.Move("b")), Output()), device));
            Failure(result, "占用");
            CheckAssert.True(!device.ReadAxis("X").IsBusy && device.ReadOutput("Vacuum") == null, "冲突后未清理或仍继续执行。");

            TaskRun owner = Start(TaskFixtures.Task(TaskFixtures.Move("owner")), device);
            await Until(() => device.ReadAxis("X").IsBusy, "首个运行没有取得轴。");
            var rejected = await Complete(Start(TaskFixtures.Task(TaskFixtures.Move("other")), device));
            Failure(rejected, "占用");
            CheckAssert.True(device.ReadAxis("X").IsBusy && !owner.Completion.IsCompleted, "失败运行释放了别人的轴。");
            owner.Cancel();
            await Complete(owner);
            CheckAssert.True(!device.ReadAxis("X").IsBusy, "所属运行取消后没有释放轴。");
        }

        /// <summary>未使能、未回零或有效报警都拒绝接受定位指令。</summary>
        private static async Task AxisPreconditions()
        {
            var device = Device();
            device.ConfigureAxis("X", 0, false, true);
            Failure(await Complete(Start(TaskFixtures.Task(TaskFixtures.Move()), device)), "未使能");
            device.ConfigureAxis("X", 0, true, false);
            Failure(await Complete(Start(TaskFixtures.Task(TaskFixtures.Move()), device)), "尚未回零");
            device.ConfigureAxis("X", 0, true, true);
            device.SetAxisAlarm("X", "测试驱动器报警");
            Failure(await Complete(Start(TaskFixtures.Task(TaskFixtures.Move()), device)), "测试驱动器报警");
            CheckAssert.True(device.ReadAxis("X").AcceptedCommands == 0, "前置条件失败仍接受了动作。");
        }

        /// <summary>变量目标和相对终点实时检查范围；运动中报警保留中途位置并释放占用。</summary>
        private static async Task DynamicMotionErrors()
        {
            var device = Device();
            var dynamicMove = TaskFixtures.Move();
            dynamicMove.Target = Variable("Vision.OffsetX");
            Failure(await Complete(Start(TaskFixtures.Task(dynamicMove), device)), "尚无有效值");
            device.SetVariable("Vision.OffsetX", "101");
            Failure(await Complete(Start(TaskFixtures.Task(dynamicMove), device)), "超出配置行程");
            var relative = TaskFixtures.Move();
            relative.Mode = TaskMoveMode.Relative;
            relative.Target = TaskFixtures.Number("20");
            device.ConfigureAxis("X", 90, true, true);
            Failure(await Complete(Start(TaskFixtures.Task(relative), device)), "超出配置行程");

            device.ConfigureAxis("X", 0, true, true);
            TaskRun run = Start(TaskFixtures.Task(TaskFixtures.Move(), Output()), device);
            await Until(() => device.ReadAxis("X").IsBusy, "报警场景未启动。");
            device.SetAxisAlarm("X", "运动中故障");
            Failure(await Complete(run), "运动中故障");
            CheckAssert.True(!device.ReadAxis("X").IsBusy && device.ReadOutput("Vacuum") == null
                && device.ReadAxis("X").Alarm == "运动中故障", "报警被自动清除或动作没有退出。");
        }

        /// <summary>内存回零建立参考；中断回零不置已回零，停止节点不能夺取其他动作。</summary>
        private static async Task HomeAndStop()
        {
            var device = Device();
            device.ConfigureAxis("X", 30, true, false);
            var home = new AxisCommandNode { Id = "home", Name = "仿真回零", AxisName = "X", Command = TaskAxisCommand.Home };
            var stop = new AxisCommandNode { Id = "stop", Name = "确认停止", AxisName = "X", Command = TaskAxisCommand.Stop, StopMode = AxisStopMode.Controlled };
            Success(await Complete(Start(TaskFixtures.Task(home, stop), device)));
            CheckAssert.True(device.ReadAxis("X").Homed && device.ReadAxis("X").Position == 0, "内存回零没有完成。");

            device.ConfigureAxis("X", 30, true, false);
            TaskRun homing = Start(TaskFixtures.Task(home), device);
            await Until(() => device.ReadAxis("X").IsBusy, "中断回零场景未开始。");
            homing.Cancel();
            await Complete(homing);
            CheckAssert.True(!device.ReadAxis("X").Homed && !device.ReadAxis("X").IsBusy, "中断回零被标成成功。");

            device.ConfigureAxis("X", 0, true, true);
            TaskRun moving = Start(TaskFixtures.Task(TaskFixtures.Move()), device);
            await Until(() => device.ReadAxis("X").IsBusy, "停止冲突场景未启动。");
            Failure(await Complete(Start(TaskFixtures.Task(stop), device)), "所属运行");
            CheckAssert.True(device.ReadAxis("X").IsBusy, "停止节点夺走了别人的动作。");
            moving.Cancel();
            await Complete(moving);
        }
    }
}
