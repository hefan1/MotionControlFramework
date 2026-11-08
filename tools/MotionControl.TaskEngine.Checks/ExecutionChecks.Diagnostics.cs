using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Execution;

namespace MotionControl.TaskEngine.Checks
{
    /// <summary>监控诊断必须保留执行时证据，不能被后续值或暂停时间改写。</summary>
    internal static partial class ExecutionChecks
    {
        /// <summary>未知输入与 false 分开，旧快照保持未知，完成快照记录 true。</summary>
        private static async Task DiagnosticInputSamples()
        {
            var device = Device();
            var run = Start(TaskFixtures.Task(new WaitInputNode { Id = "di", Name = "等待输入", InputName = "PartReady", Expected = true,
                TimeoutAlarmCodes = new List<string> { "DEMO_SENSOR_TIMEOUT" } }), device);
            await Until(() => run.GetSnapshot().Nodes.Any(node => node.NodeId == "di" && node.Diagnostics != null), "未发布 DI 诊断。");
            var old = run.GetSnapshot().Nodes.Single(node => node.NodeId == "di");
            CheckAssert.True(old.Diagnostics.Values[0].Actual == null && old.Diagnostics.Values[0].Satisfied == null, "未知输入被当成 false。");
            device.SetInput("PartReady", false);
            await Until(() => run.GetSnapshot().Nodes.Single(node => node.NodeId == "di").Diagnostics.Values[0].Satisfied == false, "未记录 false。");
            device.SetInput("PartReady", true);
            var complete = await Complete(run); Success(complete);
            CheckAssert.True(complete.Nodes.Single(node => node.NodeId == "di").Diagnostics.Values[0].Satisfied == true, "结束后丢失到位证据。");
            CheckAssert.True(old.Diagnostics.Values[0].Actual == null, "旧快照被后续输入改写。");
        }

        /// <summary>分支判断后改变量，选择和执行采样保持当时的 true。</summary>
        private static async Task DiagnosticBranchDecision()
        {
            var device = Device(); device.SetVariable("Process.Ready", "true");
            var condition = new TaskCondition { Kind = TaskConditionKind.Compare,
                Left = new TaskValueBinding { Source = TaskValueSource.Variable, VariableName = "Process.Ready" },
                Comparison = TaskComparison.Equal,
                Right = new TaskValueBinding { Source = TaskValueSource.Constant, DataType = DeviceVariableType.Boolean, Constant = "true" } };
            var run = Start(TaskFixtures.Task(new ConditionBranchNode { Id = "judge", Name = "分支判断", Condition = condition,
                WhenTrue = Sequence("yes", new DelayNode { Id = "wait", Name = "等待", Milliseconds = 200 }),
                WhenFalse = Sequence("no", Output("wrong")) }), device);
            await Until(() => HasNode(run, "wait", TaskNodeState.Waiting), "未进入 true 分支。");
            device.SetVariable("Process.Ready", "false");
            var complete = await Complete(run); Success(complete);
            var diagnostic = complete.Nodes.Single(node => node.NodeId == "judge").Diagnostics;
            CheckAssert.True(diagnostic.Decision == true && diagnostic.SelectedBranchId == "yes"
                && diagnostic.Values[0].Actual == "True", "本次分支被后续变量改变。");
        }

        /// <summary>等到明确的轴采样后再修改变量，检查已接受的绝对终点和到位证据保持一致。</summary>
        private static async Task DiagnosticAcceptedTarget()
        {
            var device = Device();
            device.ConfigureAxis("X", 5, true, true);
            device.SetVariable("Vision.OffsetX", "3");
            var run = Start(TaskFixtures.Task(new MoveAxisNode { Id = "move", Name = "相对定位", AxisName = "X", Mode = TaskMoveMode.Relative,
                Target = new TaskValueBinding { Source = TaskValueSource.Variable, VariableName = "Vision.OffsetX" },
                Profile = new TaskMotionProfile { Speed = 10, Acceleration = 20, Deceleration = 20 } }), device);

            // 资源申请也会先发布诊断；只有轴采样才能证明相对目标已经被设备接受。
            await Until(() => run.GetSnapshot().Nodes.Any(node => node.NodeId == "move"
                && node.Diagnostics != null && node.Diagnostics.WaitKind == StationWaitKind.Axis
                && node.Diagnostics.Values.Any(value => value.ObjectKind == StationObjectKind.Axis && value.ObjectId == "X")),
                "没有取得相对运动接受后的轴采样。");
            var accepted = run.GetSnapshot().Nodes.Single(node => node.NodeId == "move").Diagnostics.Values
                .Single(value => value.ObjectKind == StationObjectKind.Axis && value.ObjectId == "X");
            CheckAssert.True(device.ReadAxis("X").AcceptedCommands == 1 && accepted.Expected == "8",
                "首个轴采样没有记录设备接受的绝对终点 8。");

            device.SetVariable("Vision.OffsetX", "30");
            var complete = await Complete(run);
            Success(complete);
            var completed = complete.Nodes.Single(node => node.NodeId == "move").Diagnostics.Values
                .Single(value => value.ObjectKind == StationObjectKind.Axis && value.ObjectId == "X");
            CheckAssert.True(accepted.Expected == "8" && completed.Expected == "8",
                "后续变量变化改写了已接受的相对运动终点。");
            CheckAssert.True(completed.Actual == "8" && completed.Satisfied == true && device.ReadAxis("X").Position == 8,
                "完成诊断没有保留真实位置与已接受终点一致的到位证据。");
            CheckAssert.True(completed.Unit == "mm", "轴采样没有保留目录明确配置的工程单位。");
        }

        /// <summary>延时剩余量在暂停期间冻结，恢复完成后归零。</summary>
        private static async Task DiagnosticDelayPause()
        {
            var run = Start(TaskFixtures.Task(new DelayNode { Id = "delay", Name = "延时", Milliseconds = 300 }), Device());
            await Until(() => run.GetSnapshot().Nodes.Any(node => node.NodeId == "delay" && node.Diagnostics != null), "未记录延时。");
            run.Pause();
            double? remaining = run.GetSnapshot().Nodes.Single(node => node.NodeId == "delay").Diagnostics.RemainingDelayMilliseconds;
            await Task.Delay(150);
            CheckAssert.True(run.GetSnapshot().Nodes.Single(node => node.NodeId == "delay").Diagnostics.RemainingDelayMilliseconds == remaining, "暂停仍消耗剩余延时。");
            run.Resume(); var complete = await Complete(run); Success(complete);
            CheckAssert.True(complete.Nodes.Single(node => node.NodeId == "delay").Diagnostics.RemainingDelayMilliseconds == 0, "延时完成后未归零。");
        }
    }
}
