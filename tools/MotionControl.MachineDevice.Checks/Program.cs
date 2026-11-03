using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.IO;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Contracts.Safety;
using MotionControl.Framework.Contracts.Variables;
using MotionControl.Framework.Runtime.Alarms;
using MotionControl.Framework.Runtime.Machine;
using MotionControl.Framework.Runtime.Variables;
using MotionControl.TaskEngine.Catalog;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Device;
using MotionControl.TaskEngine.Device.InMemory;
using MotionControl.TaskEngine.Execution;
using MotionControl.TaskEngine.MachineDevice;
using MotionControl.TaskEngine.Machine;
using MotionControl.TaskEngine.Resources;

namespace MotionControl.MachineDevice.Checks
{
    /// <summary>检查真机任务设备的行为边界；不连接控制器，用假轴和假 IO 验证语义。</summary>
    internal static partial class Program
    {
        /// <summary>逐项运行检查并统计失败组数。</summary>
        private static int Main()
        {
            int failures = 0;
            Run("移动到位由轴反馈决定", MotionCompletesFromFeedback, ref failures);
            Run("取消时让轴受控停止并确认停稳", CancelStopsAxis, ref failures);
            Run("未使能、未回零和越行程拒绝动作", MotionPreconditionsAreRejected, ref failures);
            Run("轴报警无说明或状态未知不能视为正常", AxisAlarmWithoutDetailsIsNotCleared, ref failures);
            Run("确认停止必须有有效停稳速度反馈", ConfirmStoppedRequiresValidVelocity, ref failures);
            Run("轴动作前必须确认停稳且位置有效", MotionStartRequiresValidFeedback, ref failures);
            Run("回零完成必须确认原点状态", HomeCompletionRequiresHomedFeedback, ref failures);
            Run("轴组插补明确拒绝", GroupMoveIsRejected, ref failures);
            Run("IO 读取失败向上抛出", IoFailureIsReported, ref failures);
            Run("变量按目录类型写入", VariablesAreWrittenByType, ref failures);
            Run("真实设备握手保持完成直到请求复归", HandshakeWaitsForRequestClear, ref failures);
            Run("握手条件更新在并发下只成功一次", HandshakeTransitionIsAtomic, ref failures);
            Run("工位流程空闲时接管并在归还后恢复入口", ClaimTakesOverWhenStationFlowsAreIdle, ref failures);
            Run("任务复位拦截未回零的定位轴", TaskResetRequiresPositioningAxesHomed, ref failures);
            Run("任务回零诊断按设备反馈区分等待和完成", TaskHomeDiagnosticsFollowDeviceCompletion, ref failures);
            Run("资源恢复复核现场状态且只放行一次", ResourceRecoveryRequiresRecheck, ref failures);
            Run("资源恢复复核输出可读且两次一致", ResourceRecoveryRequiresOutputReadback, ref failures);
            Run("资源恢复要求运行时实际持有控制权", RecoveryRequiresActualClaim, ref failures);
            Run("清料租约独占资源并保留原隔离", ClearanceLeaseRetainsIsolation, ref failures);
            Run("真实设备清料凭证继承资源并保持失败隔离", MachineClearanceReservationChecks, ref failures);
            Run("整机清料成功与停止均保留正确资源边界", MachineClearanceLifecycleChecks, ref failures);
            Run("设备互锁有条件时拒绝执行", InterlockRejectsConfiguredConditions, ref failures);
            Run("进入与退出互锁在任务接管前拒绝", InterlocksRejectedBeforeTaskControl, ref failures);
            Run("隔离资源阻止关闭会话且恢复后可重试", BlockedResourcesPreventClose, ref failures);
            Run("资源记录持久化、异常重启与写失败拦截", ResourceJournalChecks, ref failures);
            Run("安全关闭后清理资源记录，失败保留控制权", ResourceJournalCloseChecks, ref failures);
            Run("输出写入或回读失败隔离资源", FailedOutputIsIsolated, ref failures);
            Run("工位输出故障先隔离父范围再进入复位等待", StationOutputFaultBlocksBeforeRecovery, ref failures);
            Run("停止先于状态读取且调用令牌取消仍发出", StopBeforeStatusRead, ref failures);
            Run("动作接收失败或空凭证立即隔离", BeginFailureIsIsolated, ref failures);
            Run("停止失败隔离临时轴与父范围", EndFailureIsIsolated, ref failures);
            Run("收尾回调重复及迟到不会停止新动作", LateStopDoesNotTouchNextMove, ref failures);
            Run("正常收尾未完成时暂停等待且故障隔离", PauseWaitsForNormalEnd, ref failures);
            Run("原动作迟到收尾期间禁止设备恢复和归还", PendingAxisBlocksRecovery, ref failures);
            Run("设备凭证跨设备及迟到调用保护", DeviceMoveTokenOwnership, ref failures);
            Run("命令前拒绝与命令后异常严格区分", PreflightDoesNotMaskStartedMotion, ref failures);

            Console.WriteLine("真机任务设备检查：共 35 组，失败 " + failures + "。未连接控制器。");
            return failures == 0 ? 0 : 1;
        }

        /// <summary>轴报警即使没有描述也必须保留；空状态和读取失败不能被伪装成正常。</summary>
        private static void AxisAlarmWithoutDetailsIsNotCleared()
        {
            var axis = new FakeAxis("X") { AlarmActive = true };
            var device = CreateDevice(axis, new FakeLogicalIo(), out _, out _);
            Ensure(!string.IsNullOrWhiteSpace(device.ReadAxis("X").Alarm), "无说明的轴报警被错误清除。 ");
            axis.AlarmActive = false;
            axis.NullStatus = true;
            ExpectMessage(() => device.ReadAxis("X"), "轴状态读取为空");
            axis.NullStatus = false;
            axis.ReadError = new InvalidOperationException("测试轴反馈读取失败");
            ExpectMessage(() => device.ReadAxis("X"), "测试轴反馈读取失败");
        }

        /// <summary>静止标志不能代替有限的零速度反馈；读取失败和空状态均拒绝确认。</summary>
        private static void ConfirmStoppedRequiresValidVelocity()
        {
            var axis = new FakeAxis("X");
            var device = CreateDevice(axis, new FakeLogicalIo(), out _, out _);
            device.ConfirmStoppedAsync("X", CancellationToken.None).GetAwaiter().GetResult();
            foreach (double velocity in new[] { double.NaN, double.PositiveInfinity, -0.1, 0.1 })
            {
                axis.ReportedVelocity = velocity;
                ExpectMessage(() => device.ConfirmStoppedAsync("X", CancellationToken.None).GetAwaiter().GetResult(), "不能确认已停止");
            }
            axis.ReportedVelocity = 0;
            axis.NullStatus = true;
            ExpectMessage(() => device.ConfirmStoppedAsync("X", CancellationToken.None).GetAwaiter().GetResult(), "不能确认已停止");
            axis.NullStatus = false;
            axis.ReadError = new InvalidOperationException("读取异常");
            ExpectMessage(() => device.ConfirmStoppedAsync("X", CancellationToken.None).GetAwaiter().GetResult(), "读取异常");
        }

        /// <summary>命令前的空状态或运动中状态不能伪装为可开始动作。</summary>
        private static void MotionStartRequiresValidFeedback()
        {
            var axis = new FakeAxis("X");
            var device = CreateDevice(axis, new FakeLogicalIo(), out _, out _);
            var profile = new AxisMotionProfile(10, 20, 20);
            foreach (double velocity in new[] { double.NaN, double.PositiveInfinity, 0.1 })
            {
                axis.ReportedVelocity = velocity;
                ExpectMessage(() => device.BeginMove("X", 1, false, false, profile), "不能接受任务动作");
            }
            axis.ReportedVelocity = 0;
            axis.NullStatus = true;
            ExpectMessage(() => device.BeginMove("X", 1, false, false, profile), "不能接受任务动作");
            axis.NullStatus = false;
            axis.Position = double.NaN;
            ExpectMessage(() => device.BeginMove("X", 1, false, false, profile), "位置状态未知");
        }

        /// <summary>回零任务返回后如果设备仍未确认回零，不得推进后续步骤。</summary>
        private static void HomeCompletionRequiresHomedFeedback()
        {
            var axis = new FakeAxis("X");
            var device = CreateDevice(axis, new FakeLogicalIo(), out _, out _);
            ITaskAxisMove move = device.BeginMove("X", 0, false, true, null);
            axis.Homed = false;
            Ensure(!device.IsMoveComplete(move, 1), "回零反馈未确认却允许进入后续动作。");
            axis.Homed = true;
            Ensure(device.IsMoveComplete(move, 1), "回零已确认仍被阻止。");
            axis.AlarmActive = true;
            ExpectMessage(() => device.IsMoveComplete(move, 1), "存在报警");
            axis.AlarmActive = false;
            device.EndMoveAsync(move, true, CancellationToken.None).GetAwaiter().GetResult();
        }

        /// <summary>无条件范围正常通过；任何互锁或旧退出条件均明确拒绝。</summary>
        private static void InterlockRejectsConfiguredConditions()
        {
            var device = CreateDevice(new FakeAxis("X"), new FakeLogicalIo(), out _, out _);
            string reason;
            Ensure(device.CheckInterlock(null, null, out reason), "未配置条件时阻止正常范围执行。");
            ExpectMessage(() => device.CheckInterlock(new TaskInterlock(), null, out _), "暂不支持");
            ExpectMessage(() => device.CheckInterlock(null, new TaskCondition(), out _), "暂不支持");
        }

        /// <summary>进入或退出互锁均在设备接管前拒绝，不能等正文执行后才发现不支持。</summary>
        private static void InterlocksRejectedBeforeTaskControl()
        {
            var axis = new FakeAxis("X");
            var device = CreateDevice(axis, new FakeLogicalIo(), out _, out MachineRuntime runtime);
            Ensure(!TaskExecutionEngine.SupportsInterlockConditions(new object()), "旧设备未声明互锁能力却被当作支持。");
            Ensure(!TaskExecutionEngine.SupportsInterlockConditions(device), "实际设备错误声明支持互锁。");
            Ensure(TaskExecutionEngine.SupportsInterlockConditions(new InMemoryTaskDevice(device.Catalog)), "内存设备互锁能力未被识别。");
            var scope = new ResourceScopeNode { Id = "scope", Name = "区域", Resources = new List<string> { "Axis:X" },
                Body = new SequenceNode { Id = "body", Name = "正文", Children = new List<TaskNode>
                    { new DelayNode { Id = "delay", Name = "等待", Milliseconds = 1 } } } };
            var task = new TaskDefinition { TaskId = "InterlockCheck", Name = "互锁检查",
                Root = new SequenceNode { Id = "root", Name = "步骤", Children = new List<TaskNode> { scope } } };
            var configuration = new TaskMachineConfiguration { MachineId = "InterlockCheck", Name = "互锁检查",
                Stations = new List<StationTaskConfiguration> { new StationTaskConfiguration {
                    StationId = "S1", Name = "工位", TaskId = task.TaskId, Enabled = true, Mode = StationTaskMode.Once } } };
            var condition = new TaskInterlock();
            condition.Inputs.Add(new TaskInputInterlock { InputName = "Sensor", Expected = true });
            scope.EntryInterlock = condition;
            ExpectMessage(() => new TaskMachineController(configuration, new[] { task }, device, runtime.AlarmService), "暂不支持");
            scope.EntryInterlock = null;
            scope.ExitInterlock = condition;
            ExpectMessage(() => new TaskMachineController(configuration, new[] { task }, device, runtime.AlarmService), "暂不支持");
            Ensure(runtime.ControlOwner == MachineControlOwner.None && axis.MoveCalls == 0,
                "不支持的任务已接管或发出轴动作。");
        }

        /// <summary>核对被隔离资源的现场条件、未知值、读取失败和两次采样变化均不会误放行。</summary>
        private static void ResourceRecoveryRequiresRecheck()
        {
            var axis = new FakeAxis("X");
            var io = new FakeLogicalIo();
            var device = CreateDevice(axis, io, out DeviceVariableStore variables, out _);
            var interlock = new TaskInterlock();
            interlock.Inputs.Add(new TaskInputInterlock { InputName = "Sensor", Expected = true });
            interlock.Axes.Add(new TaskAxisInterlock { AxisName = "X", MinimumPosition = 0, MaximumPosition = 0 });
            var request = device.Resources.Enqueue("run", "task", "工位", "node", "步骤", "call", new[] { "Axis:X" }, interlock);
            string reason;
            Ensure(device.GetResourceRecoveryState() == TaskResourceRecoveryState.Clear, "等待资源不能当成已占用。");
            Ensure(device.Resources.TryAcquire(request, out reason), reason);
            Ensure(device.GetResourceRecoveryState() == TaskResourceRecoveryState.Held, "占用资源没有报告持有状态。");
            device.Resources.Block(request, "未退出");
            Ensure(device.GetResourceRecoveryState() == TaskResourceRecoveryState.Blocked, "隔离资源没有报告待确认状态。");
            string id = device.ReadResources().Single().RequestId;
            Ensure(!device.TryRecoverResources(id, "", out reason), "缺少现场确认也放行。");
            Ensure(!device.TryRecoverResources(id, "现场已核实", out reason) && reason.Contains("控制权"), "未持有控制权时放行。");
            device.ClaimMachine(null);
            io.ReadError = new InvalidOperationException("采样失败");
            Ensure(!device.TryRecoverResources(id, "现场已核实", out reason) && reason.Contains("采样失败"), "读失败被放行。");
            io.ReadError = null;
            Ensure(!device.TryRecoverResources(id, "现场已核实", out reason) && reason.Contains("Sensor"), "输入不满足被放行。");
            io.InputValue = true;
            io.FlipAfterRead = true;
            Ensure(!device.TryRecoverResources(id, "现场已核实", out reason), "两次采样期间条件变化仍放行。");
            Ensure(device.ReadResources().Count == 1, "检查失败移除了隔离记录。");
            io.FlipAfterRead = false;
            io.InputValue = true;
            device.RegisterRun("仍在运行", null);
            Ensure(!device.TryRecoverResources(id, "现场已核实", out reason) && reason.Contains("运行"), "任务运行未退出被放行。");
            device.UnregisterRun("仍在运行");
            ExpectMessage(() => device.ReleaseMachine(null), "待确认");
            axis.ReadError = new InvalidOperationException("轴反馈不可读");
            Ensure(!device.TryRecoverResources(id, "现场已核实", out reason) && reason.Contains("不可读"), "轴反馈读取失败被放行。");
            axis.ReadError = null;
            axis.ServoEnabled = false;
            Ensure(!device.TryRecoverResources(id, "现场已核实", out reason), "退出轴未使能被放行。");
            axis.ServoEnabled = true;
            Ensure(device.TryRecoverResources(id, "现场已核实", out reason), reason);
            Ensure(device.GetResourceRecoveryState() == TaskResourceRecoveryState.Clear, "已恢复的资源仍被报告隔离。");
            Ensure(!device.TryRecoverResources(id, "重复确认", out reason), "旧凭证重复放行。");

            var variableCondition = new TaskCondition { Kind = TaskConditionKind.Compare,
                Left = new TaskValueBinding { Source = TaskValueSource.Variable, VariableName = "Unknown" },
                Comparison = TaskComparison.Equal,
                Right = new TaskValueBinding { Source = TaskValueSource.Constant, DataType = DeviceVariableType.Boolean, Constant = "true" } };
            var second = device.Resources.Enqueue("run2", "task", "工位", "node", "步骤", "call", new[] { "Axis:X" }, null, variableCondition);
            Ensure(device.Resources.TryAcquire(second, out reason), reason);
            device.Resources.Block(second, "未退出");
            string secondId = device.ReadResources().Single().RequestId;
            Ensure(!device.TryRecoverResources(secondId, "现场已核实", out reason), "未知变量被放行。");
            variables.SetBoolean("Unknown", true);
            Ensure(device.TryRecoverResources(secondId, "现场已核实", out reason), reason);
            device.ReleaseMachine(null);
        }

        /// <summary>资源输出读失败、有效或采样变化时保留隔离，不自动切换输出。</summary>
        private static void ResourceRecoveryRequiresOutputReadback()
        {
            var io = new FakeLogicalIo();
            var device = CreateDevice(new FakeAxis("X"), io, out _, out _);
            device.ClaimMachine(null);
            var request = device.Resources.Enqueue("output-run", "task", "工位", "node", "步骤", "call",
                new[] { "Axis:X", "Output:Vacuum" }, null);
            string reason;
            Ensure(device.Resources.TryAcquire(request, out reason), reason);
            device.Resources.Block(request, "输出待核对");
            string id = device.ReadResources().Single().RequestId;
            io.OutputReadError = new InvalidOperationException("输出反馈不可读");
            Ensure(!device.TryRecoverResources(id, "现场已核实", out reason) && reason.Contains("输出反馈不可读"), "输出读失败仍放行。");
            Ensure(device.ReadResources().Count == 1, "输出读失败丢失了资源隔离。");
            io.OutputReadError = null;
            io.FlipOutputAfterRead = true;
            Ensure(!device.TryRecoverResources(id, "现场已核实", out reason) && reason.Contains("仍有效"), "输出变化或有效仍放行。");
            Ensure(device.ReadResources().Count == 1, "输出变化丢失了资源隔离。");
            io.FlipOutputAfterRead = false;
            Ensure(device.TryRecoverResources(id, "现场已核实", out reason), reason);
            Ensure(io.OutputWrites == 0, "资源恢复意外改变了输出。");
            device.ReleaseMachine(null);
        }

        /// <summary>关闭任务会话时保留待确认区域，现场核实后允许重试关闭并归还控制权。</summary>
        private static void BlockedResourcesPreventClose()
        {
            var device = CreateDevice(new FakeAxis("X"), new FakeLogicalIo(), out _, out MachineRuntime runtime);
            var task = new TaskDefinition { TaskId = "CloseCheck", Name = "关闭检查",
                Root = new SequenceNode { Id = "sequence", Name = "步骤", Children = new List<TaskNode>
                    { new DelayNode { Id = "delay", Name = "等待", Milliseconds = 1 } } } };
            var configuration = new TaskMachineConfiguration { MachineId = "CloseCheck", Name = "关闭检查",
                Stations = new List<StationTaskConfiguration> { new StationTaskConfiguration {
                    StationId = "S1", Name = "工位", TaskId = task.TaskId, Enabled = true, Mode = StationTaskMode.Once } } };
            var controller = new TaskMachineController(configuration, new[] { task }, device, runtime.AlarmService);
            string reason;
            var request = device.Resources.Enqueue("run", "task", "工位", "node", "步骤", "call", new[] { "Axis:X" });
            Ensure(device.Resources.TryAcquire(request, out reason), reason);
            ExpectMessage(() => controller.CloseAsync().GetAwaiter().GetResult(), "待确认");
            Ensure(runtime.IsTaskEngineControlClaimed, "资源仍被持有时关闭会话丢失了控制权。");
            device.Resources.Block(request, "未退出");
            string id = device.ReadResources().Single().RequestId;
            ExpectMessage(() => controller.CloseAsync().GetAwaiter().GetResult(), "待确认");
            Ensure(runtime.ControlOwner == MachineControlOwner.TaskEngine && device.ReadResources().Count == 1,
                "关闭失败后丢失控制权或隔离记录。");
            ExpectMessage(() => device.ClaimMachine(new object()), "占用");
            Ensure(device.TryRecoverResources(id, "现场已核实", out reason), reason);
            controller.CloseAsync().GetAwaiter().GetResult();
            Ensure(runtime.ControlOwner == MachineControlOwner.None, "恢复后重试关闭仍未归还控制权。");
        }

        /// <summary>即使设备保留旧会话标记，运行时已失去实际控制权时也不能解除隔离。</summary>
        private static void RecoveryRequiresActualClaim()
        {
            var device = CreateDevice(new FakeAxis("X"), new FakeLogicalIo(), out _, out MachineRuntime runtime);
            device.ClaimMachine(null);
            var request = device.Resources.Enqueue("run", "task", "工位", "node", "步骤", "call", new[] { "Axis:X" });
            string reason;
            Ensure(device.Resources.TryAcquire(request, out reason), reason);
            device.Resources.Block(request, "未退出");
            string id = device.ReadResources().Single().RequestId;
            runtime.ReleaseTaskEngineControl(device);
            Ensure(!device.TryRecoverResources(id, "现场已核实", out reason) && reason.Contains("控制权"),
                "运行时已失去控制权时错误解除隔离。");
            Ensure(device.ReadResources().Count == 1, "控制权失效后丢失隔离记录。");
        }

        /// <summary>运行一项检查，捕获异常并计入失败。</summary>
        private static void Run(string name, Action check, ref int failures)
        {
            try
            {
                check();
                Console.WriteLine("[通过] " + name);
            }
            catch (Exception error)
            {
                failures++;
                Console.WriteLine("[失败] " + name + "：" + error.Message);
            }
        }

        /// <summary>确认 BeginMove 只启动动作，到位由轴反馈完成情况决定。</summary>
        private static void MotionCompletesFromFeedback()
        {
            var axis = new FakeAxis("X");
            MachineTaskDevice device = CreateDevice(axis, new FakeLogicalIo(), out _, out _);
            ITaskAxisMove move = device.BeginMove("X", 10, false, false, Profile());
            Ensure(!device.IsMoveComplete(move, 0.5), "轴还没到位就报告已完成。");
            Ensure(axis.MoveCalls == 1, "BeginMove 没有启动真实轴动作。");

            axis.Complete();
            Ensure(device.IsMoveComplete(move, 0.5), "轴已到位但设备仍报告未完成。");

            device.EndMoveAsync(move, true, CancellationToken.None).GetAwaiter().GetResult();
            Ensure(device.ReadAxis("X").Position == 10, "到位后轴位置不是目标值。");
        }

        /// <summary>确认取消时即使驱动器不响应取消，设备也会补发受控停止并确认停稳。</summary>
        private static void CancelStopsAxis()
        {
            var axis = new FakeAxis("X") { IgnoresCancellation = true };
            MachineTaskDevice device = CreateDevice(axis, new FakeLogicalIo(), out _, out _);
            ITaskAxisMove move = device.BeginMove("X", 10, false, false, Profile());
            Ensure(axis.Moving, "动作接受后轴没有进入运动状态。");

            device.EndMoveAsync(move, false, CancellationToken.None).GetAwaiter().GetResult();
            Ensure(axis.StopCalls == 1, "未完成的动作没有触发受控停止。实际停止次数：" + axis.StopCalls + "。");
            Ensure(!axis.Moving, "收尾后轴仍在运动。");
            Ensure(axis.Position != 10, "取消时把中途位置当成了到位位置。");
        }

        /// <summary>确认设备在启动动作前检查使能、回零和行程，不把错误留到运动阶段。</summary>
        private static void MotionPreconditionsAreRejected()
        {
            var disabled = new FakeAxis("X") { ServoEnabled = false };
            MachineTaskDevice disabledDevice = CreateDevice(disabled, new FakeLogicalIo(), out _, out _);
            ExpectMessage(() => disabledDevice.BeginMove("X", 1, false, false, Profile()), "未使能");

            var notHomed = new FakeAxis("X") { Homed = false };
            MachineTaskDevice notHomedDevice = CreateDevice(notHomed, new FakeLogicalIo(), out _, out _);
            ExpectMessage(() => notHomedDevice.BeginMove("X", 1, false, false, Profile()), "尚未回零");

            var axis = new FakeAxis("X");
            MachineTaskDevice device = CreateDevice(axis, new FakeLogicalIo(), out _, out _);
            ExpectMessage(() => device.BeginMove("X", 500, false, false, Profile()), "超出配置行程");
            Ensure(axis.MoveCalls == 0, "前置检查失败后仍向轴发出了运动命令。");
        }

        /// <summary>确认轴组动作在真机适配器上明确拒绝，不退化成逐轴移动。</summary>
        private static void GroupMoveIsRejected()
        {
            var axis = new FakeAxis("X");
            MachineTaskDevice device = CreateDevice(axis, new FakeLogicalIo(), out _, out _);
            var targets = new List<TaskAxisGroupTarget>
            {
                new TaskAxisGroupTarget("X", 5, false, 10)
            };
            ExpectMessage(() => device.BeginGroupMove(targets), "轴组插补");
        }

        /// <summary>确认 IO 读取失败不会被吞掉成 false。</summary>
        private static void IoFailureIsReported()
        {
            var io = new FakeLogicalIo { ReadError = new InvalidOperationException("DI 读取失败：Sensor") };
            var axis = new FakeAxis("X");
            MachineTaskDevice device = CreateDevice(axis, io, out _, out _);
            try
            {
                device.ReadInput("Sensor");
            }
            catch (InvalidOperationException error)
            {
                Ensure(error.Message.Contains("DI 读取失败"), "IO 失败信息没有保留原始原因。" + error.Message);
                return;
            }

            throw new InvalidOperationException("IO 读取失败被静默吞掉了。");
        }

        /// <summary>确认变量写入按目录中的类型落到变量存储。</summary>
        private static void VariablesAreWrittenByType()
        {
            var axis = new FakeAxis("X");
            MachineTaskDevice device = CreateDevice(axis, new FakeLogicalIo(), out DeviceVariableStore store, out _);
            device.WriteVariable("Ready", true);
            Ensure(store.GetBoolean("Ready"), "布尔变量没有写入存储。");
        }

        /// <summary>请求有效期间完成信号保持，未知变量不放行，配置错误明确报告。</summary>
        private static void HandshakeWaitsForRequestClear()
        {
            MachineTaskDevice device = CreateDevice(new FakeAxis("X"), new FakeLogicalIo(), out DeviceVariableStore store, out _);
            Ensure(!device.TryPublishCompletion("Ready", "Complete"), "请求无效时发布了完成信号。");
            device.WriteVariable("Ready", true);
            Ensure(device.TryPublishCompletion("Ready", "Complete"), "请求有效时没有发布完成信号。");
            Ensure(!device.TryPublishCompletion("Ready", "Complete"), "完成信号已发布时重复发布。");
            Ensure(!device.TryClearCompletion("Ready", "Complete") && store.GetBoolean("Complete"), "请求仍有效时完成信号被提前清除。");
            device.WriteVariable("Ready", false);
            Ensure(device.TryClearCompletion("Ready", "Complete") && !store.GetBoolean("Complete"), "请求清除后完成信号没有复归。");
            bool? request, complete;
            device.ReadHandshake("Unknown", "Complete", out request, out complete);
            Ensure(request == null && complete == false, "未知请求被当成有效电平。");
            Ensure(!device.TryPublishCompletion("Unknown", "Complete"), "未知请求放行了完成信号。");
            ExpectMessage(() => device.TryClearCompletion("Missing", "Complete"), "握手变量");
        }

        /// <summary>多个工位并发发布同一完成信号时，仅一次条件更新可以成功。</summary>
        private static void HandshakeTransitionIsAtomic()
        {
            MachineTaskDevice device = CreateDevice(new FakeAxis("X"), new FakeLogicalIo(), out DeviceVariableStore store, out _);
            device.WriteVariable("Ready", true);
            for (int iteration = 0; iteration < 100; iteration++)
            {
                store.SetBoolean("Complete", false);
                int successes = 0;
                Parallel.For(0, 20, index =>
                {
                    if (device.TryPublishCompletion("Ready", "Complete")) Interlocked.Increment(ref successes);
                });
                Ensure(successes == 1 && store.GetBoolean("Complete"), "并发发布完成信号不是一次成功，次数：" + successes);
            }
        }

        /// <summary>确认工位流程空闲时任务入口可以接管，归还后执行入口回到工位流程。</summary>
        private static void ClaimTakesOverWhenStationFlowsAreIdle()
        {
            var axis = new FakeAxis("X");
            MachineTaskDevice device = CreateDevice(
                axis, new FakeLogicalIo(), out _, out MachineRuntime runtime, withStationFlow: true);

            ExpectMessage(() => device.RegisterRun("independent", null), "先取得整机控制权");
            device.ClaimMachine(null);
            Ensure(runtime.ControlOwner == MachineControlOwner.TaskEngine, "接管后运行时执行入口不是任务入口。");
            ExpectMessage(() => runtime.AcquireTaskEngineControlAsync(new object(), CancellationToken.None)
                .GetAwaiter().GetResult(), "其他会话占用");
            ExpectMessage(() => runtime.ReleaseTaskEngineControl(new object()), "所有者不匹配");
            device.RegisterRun("active", null);
            ExpectMessage(() => device.ReleaseMachine(null), "未收尾");
            device.UnregisterRun("active");
            device.ReleaseMachine(null);
            Ensure(runtime.ControlOwner == MachineControlOwner.StationFlows, "归还后执行入口没有回到工位流程。");
        }

        /// <summary>确认正式设备任务不会在定位轴未使能或未回零时进入就绪状态。</summary>
        private static void TaskResetRequiresPositioningAxesHomed()
        {
            var axis = new FakeAxis("X") { Homed = false };
            MachineRuntime runtime;
            MachineTaskDevice device = CreateDevice(axis, new FakeLogicalIo(), out _, out runtime);
            var task = new TaskDefinition
            {
                TaskId = "PositionTask",
                Name = "定位检查",
                Root = new SequenceNode
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = "定位步骤",
                    Children = new List<TaskNode>
                    {
                        new MoveAxisNode
                        {
                            Id = Guid.NewGuid().ToString("N"),
                            Name = "定位到位",
                            AxisName = "X",
                            Mode = TaskMoveMode.Absolute,
                            Target = new TaskValueBinding
                            {
                                Source = TaskValueSource.Constant,
                                DataType = DeviceVariableType.Number,
                                Constant = "1"
                            },
                            Profile = new TaskMotionProfile { Speed = 10, Acceleration = 100, Deceleration = 100 }
                        }
                    }
                }
            };
            var configuration = new TaskMachineConfiguration
            {
                MachineId = "ReadinessCheck",
                Name = "ReadinessCheck",
                Stations = new List<StationTaskConfiguration>
                {
                    new StationTaskConfiguration
                    {
                        StationId = "PositionStation",
                        Name = "定位工位",
                        TaskId = task.TaskId,
                        Enabled = true,
                        Mode = StationTaskMode.Once
                    }
                }
            };
            var controller = new TaskMachineController(configuration, new[] { task }, device, runtime.AlarmService);
            try
            {
                ExpectMessage(() => controller.ValidateResetPrerequisites(), "尚未回零");
                Ensure(controller.GetSnapshot().State == TaskMachineState.NeedsReset,
                    "复位预检查不应改变任务整机状态。");
                ExpectMessage(() => controller.Reset(), "尚未回零");
                Ensure(controller.GetSnapshot().State == TaskMachineState.NeedsReset,
                    "回零前的复位拒绝后，任务整机错误进入就绪状态。");
                Ensure(axis.MoveCalls == 0, "回零检查不应发出定位动作。");
            }
            finally
            {
                controller.CloseAsync().GetAwaiter().GetResult();
            }
        }

        /// <summary>设备回零超过内存进度时仍显示等待，设备确认完成后才显示回零完成。</summary>
        private static void TaskHomeDiagnosticsFollowDeviceCompletion()
        {
            TaskHomeDiagnosticsFollowDeviceCompletionAsync().GetAwaiter().GetResult();
        }

        /// <summary>用任务控制器和可控轴替身检查回零节点快照；不连接控制器。</summary>
        private static async Task TaskHomeDiagnosticsFollowDeviceCompletionAsync()
        {
            var axis = new FakeAxis("X") { Homed = false, DeferHomeCompletion = true };
            MachineTaskDevice device = CreateDevice(axis, new FakeLogicalIo(), out _, out MachineRuntime runtime);
            var task = new TaskDefinition
            {
                TaskId = "HomeTask",
                Name = "回零检查",
                Root = new SequenceNode
                {
                    Id = "sequence",
                    Name = "回零步骤",
                    Children = new List<TaskNode>
                    {
                        new AxisCommandNode { Id = "home", Name = "X回零", AxisName = "X", Command = TaskAxisCommand.Home }
                    }
                }
            };
            var configuration = new TaskMachineConfiguration
            {
                MachineId = "HomeCheck",
                Name = "HomeCheck",
                Stations = new List<StationTaskConfiguration>
                {
                    new StationTaskConfiguration
                    {
                        StationId = "HomeStation", Name = "回零工位", TaskId = task.TaskId,
                        Enabled = true, Mode = StationTaskMode.Once
                    }
                }
            };
            var controller = new TaskMachineController(configuration, new[] { task }, device, runtime.AlarmService);
            try
            {
                await controller.ResetAsync().ConfigureAwait(false);
                controller.Start();
                await WaitForHomeNodeAsync(controller, node => node.State == TaskNodeState.Moving).ConfigureAwait(false);
                // 真实设备不以 100ms 的内存时间估算完成；确保在此之后仍检查未完成状态。
                await Task.Delay(250).ConfigureAwait(false);
                TaskNodeSnapshot waiting = await WaitForHomeNodeAsync(controller,
                    node => node.State == TaskNodeState.Moving && node.Diagnostics != null).ConfigureAwait(false);
                Ensure(waiting.Diagnostics.Summary == "正在等待轴回零完成。", "设备尚未完成时错误显示回零完成：" + waiting.Diagnostics.Summary);
                Ensure(!waiting.Detail.Contains("内存") && !waiting.Diagnostics.Summary.Contains("模拟"), "正式设备回零信息不应描述为内存模型。");
                axis.Complete();
                TaskNodeSnapshot completed = await WaitForHomeNodeAsync(controller,
                    node => node.State == TaskNodeState.Completed).ConfigureAwait(false);
                Ensure(completed.Diagnostics != null && completed.Diagnostics.Summary == "轴回零已完成。", "设备确认回零后缺少完成诊断。");
            }
            finally
            {
                await controller.CloseAsync().ConfigureAwait(false);
            }
        }

        /// <summary>限时等待指定回零节点快照，避免检查失败时无限等待。</summary>
        private static async Task<TaskNodeSnapshot> WaitForHomeNodeAsync(TaskMachineController controller,
            Func<TaskNodeSnapshot, bool> predicate)
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(5))
            {
                TaskRunSnapshot run = controller.GetSnapshot().Stations.Single().Run;
                TaskNodeSnapshot node = run?.Nodes.FirstOrDefault(item => item.NodeId == "home");
                if (node != null && predicate(node)) return node;
                await Task.Delay(10).ConfigureAwait(false);
            }
            throw new InvalidOperationException("等待回零节点状态超时。");
        }

        /// <summary>创建一套最小设备环境；可选加入一个工位流程用于验证互斥。</summary>
        private static MachineTaskDevice CreateDevice(
            FakeAxis axis,
            FakeLogicalIo io,
            out DeviceVariableStore variables,
            out MachineRuntime runtime,
            bool withStationFlow = false, TaskResourceJournal journal = null, FakeSafetyStatus safety = null,
            TimeSpan? pauseTimeout = null, IEnumerable<string> namedResources = null)
        {
            TaskResourceCatalog catalog = new TaskResourceCatalog(
                new[] { new TaskAxisInfo("X", "CardA", "mm", -100, 100) },
                new[] { "Sensor" },
                new[] { "Vacuum" },
                new Dictionary<string, DeviceVariableType> { { "Ready", DeviceVariableType.Boolean }, { "Complete", DeviceVariableType.Boolean }, { "Unknown", DeviceVariableType.Boolean } },
                new TaskTeachPoint[0],
                namedResources,
                null,
                new Dictionary<string, string> { { "Ready", "false" } });

            variables = new DeviceVariableStore(new[]
            {
                new DeviceVariableDefinition
                {
                    Name = "Ready",
                    DataType = DeviceVariableType.Boolean.ToString(),
                    InitialValue = "false",
                    AllowOperatorWrite = false
                },
                new DeviceVariableDefinition { Name = "Complete", DataType = "Boolean", InitialValue = "false", AllowOperatorWrite = false },
                new DeviceVariableDefinition { Name = "Unknown", DataType = "Boolean", InitialValue = null, AllowOperatorWrite = false }
            });

            var card = new FakeMotionCard("CardA", axis);
            var group = new MotionCardGroup(new IMotionCard[] { card });
            var flows = withStationFlow
                ? new IStationFlow[] { new WaitingStationFlow() }
                : new IStationFlow[0];
            runtime = new MachineRuntime(group, flows, new MachineAlarmService(), safety ?? new FakeSafetyStatus(),
                pauseTimeout: pauseTimeout, safetyPollInterval: TimeSpan.FromMilliseconds(10), controlInputIo: io);
            return new MachineTaskDevice(catalog, group, io, variables, runtime, journal);
        }

        /// <summary>检查使用的固定速度曲线。</summary>
        private static AxisMotionProfile Profile()
        {
            return new AxisMotionProfile(10, 100, 100);
        }

        /// <summary>要求调用抛出异常且信息包含指定片段。</summary>
        private static void ExpectMessage(Action action, string expectedFragment)
        {
            try
            {
                action();
            }
            catch (Exception error)
            {
                Ensure(error.Message.Contains(expectedFragment),
                    "错误信息缺少预期片段“" + expectedFragment + "”： " + error.Message);
                return;
            }

            throw new InvalidOperationException("预期拒绝的操作没有抛出异常。");
        }

        /// <summary>断言条件成立，否则抛出检查失败。</summary>
        private static void Ensure(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        /// <summary>可控的单轴替身：动作完成与停止都由检查显式触发。</summary>
        private sealed class FakeAxis : IAxis
        {
            /// <summary>未完成的动作任务。</summary>
            private TaskCompletionSource<bool> _motion;

            /// <summary>本次动作的目标位置。</summary>
            private double _target;

            /// <summary>当前未完成动作是否为测试用回零。</summary>
            private bool _homeInProgress;

            /// <summary>建立默认已使能、已回零的测试轴。</summary>
            internal FakeAxis(string name)
            {
                Name = name;
                ServoEnabled = true;
                Homed = true;
            }

            /// <summary>逻辑轴名称。</summary>
            public string Name { get; }

            /// <summary>当前反馈位置。</summary>
            internal double Position { get; set; }

            /// <summary>是否正在运动。</summary>
            internal bool Moving { get; private set; }

            /// <summary>伺服是否使能。</summary>
            internal bool ServoEnabled { get; set; }

            /// <summary>是否已回零。</summary>
            internal bool Homed { get; set; }

            /// <summary>模拟轴存在报警但驱动未提供文本。</summary>
            internal bool AlarmActive { get; set; }

            /// <summary>模拟适配器错误返回空轴状态。</summary>
            internal bool NullStatus { get; set; }

            /// <summary>模拟读取轴状态失败。</summary>
            internal Exception ReadError { get; set; }

            /// <summary>可注入的停止命令错误。</summary>
            internal Exception StopError { get; set; }

            /// <summary>在模拟命令下发后注入启动异常。</summary>
            internal Exception StartError { get; set; }

            /// <summary>轴状态反馈速度，用于模拟未知速度。</summary>
            internal double ReportedVelocity { get; set; }

            /// <summary>可选状态读取阻塞，用于测试命令前轴预留。</summary>
            internal TaskCompletionSource<bool> StatusGate { get; set; }

            /// <summary>状态读取已进入的通知。</summary>
            internal readonly ManualResetEventSlim StatusEntered = new ManualResetEventSlim(false);

            /// <summary>状态读取次数。</summary>
            internal int StatusReadCount;

            /// <summary>是否模拟驱动器不响应取消，用于验证补发受控停止。</summary>
            internal bool IgnoresCancellation { get; set; }

            /// <summary>非空时延迟完成取消，用于检查后台收尾期间的资源隔离。</summary>
            internal TaskCompletionSource<bool> CancellationGate { get; set; }

            /// <summary>非空时延迟显式停止调用返回。</summary>
            internal TaskCompletionSource<bool> StopGate { get; set; }

            /// <summary>为真机任务检查保持回零进行中，直到检查显式完成或取消。</summary>
            internal bool DeferHomeCompletion { get; set; }

            /// <summary>已收到的运动命令次数。</summary>
            internal int MoveCalls { get; private set; }

            /// <summary>已收到的停止命令次数。</summary>
            internal int StopCalls { get; private set; }

            /// <summary>让当前动作正常完成并把位置落到目标。</summary>
            internal void Complete()
            {
                Position = _target;
                Moving = false;
                if (_homeInProgress) Homed = true;
                _homeInProgress = false;
                _motion?.TrySetResult(true);
            }

            /// <summary>读取当前状态快照。</summary>
            public async Task<AxisStatus> ReadStatusAsync(CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref StatusReadCount);
                StatusEntered.Set();
                if (StatusGate != null) await StatusGate.Task.ConfigureAwait(false);
                if (ReadError != null) throw ReadError;
                if (NullStatus) return null;
                return new AxisStatus(Position, ReportedVelocity, ServoEnabled, Moving, Homed,
                    false, false, false, false, AlarmActive, null, false);
            }

            /// <summary>测试替身不模拟伺服使能，直接返回。</summary>
            public Task EnableServoAsync(CancellationToken cancellationToken)
            {
                ServoEnabled = true;
                return Task.CompletedTask;
            }

            /// <summary>测试替身不模拟伺服断使能，直接返回。</summary>
            public Task DisableServoAsync(CancellationToken cancellationToken)
            {
                ServoEnabled = false;
                return Task.CompletedTask;
            }

            /// <summary>测试替身把回零作为一次普通动作处理。</summary>
            public Task HomeAsync(CancellationToken cancellationToken)
            {
                if (DeferHomeCompletion)
                {
                    _homeInProgress = true;
                    Homed = false;
                    return MoveAbsoluteAsync(0, null, cancellationToken);
                }
                MoveCalls++;
                Homed = true;
                return Task.CompletedTask;
            }

            /// <summary>接受绝对定位并保持运动状态，直到检查显式完成或停止。</summary>
            public Task MoveAbsoluteAsync(double position, AxisMotionProfile profile, CancellationToken cancellationToken)
            {
                MoveCalls++;
                _target = position;
                Moving = true;
                _motion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (StartError != null) throw StartError;
                if (!IgnoresCancellation)
                {
                    TaskCompletionSource<bool> currentMotion = _motion;
                    cancellationToken.Register(() =>
                    {
                        if (CancellationGate != null) _ = CompleteDelayedCancellationAsync(currentMotion, CancellationGate.Task);
                        else { Moving = false; _homeInProgress = false; currentMotion.TrySetCanceled(); }
                    });
                }

                return _motion.Task;
            }

            /// <summary>模拟厂商动作任务迟到执行停止并结束取消。</summary>
            private async Task CompleteDelayedCancellationAsync(TaskCompletionSource<bool> motion, Task gate)
            {
                await gate.ConfigureAwait(false);
                StopCalls++;
                Moving = false;
                _homeInProgress = false;
                motion.TrySetCanceled();
            }

            /// <summary>相对定位按当前位置换算成绝对目标。</summary>
            public Task MoveRelativeAsync(double distance, AxisMotionProfile profile, CancellationToken cancellationToken)
            {
                return MoveAbsoluteAsync(Position + distance, profile, cancellationToken);
            }

            /// <summary>测试替身不模拟点动。</summary>
            public Task StartJogAsync(MotionDirection direction, AxisMotionProfile profile, CancellationToken cancellationToken)
            {
                throw new NotSupportedException("检查不支持点动。");
            }

            /// <summary>停止动作：结束当前运动任务并保留中途位置。</summary>
            public async Task StopAsync(AxisStopMode stopMode, CancellationToken cancellationToken)
            {
                StopCalls++;
                if (StopGate != null) await StopGate.Task.ConfigureAwait(false);
                if (StopError != null) throw StopError;
                Moving = false;
                _homeInProgress = false;
                _motion?.TrySetCanceled();
            }

            /// <summary>测试替身没有轴报警可清。</summary>
            public Task ResetAlarmAsync(CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }
        }

        /// <summary>可控的逻辑 IO 替身，可注入读取失败。</summary>
        private sealed class FakeLogicalIo : ILogicalIo
        {
            /// <summary>注入输出写入错误，模拟指令结果不确定。</summary>
            internal Exception WriteError { get; set; }
            /// <summary>注入输出反馈错误。</summary>
            internal Exception OutputReadError { get; set; }
            /// <summary>模拟驱动接收命令后的实际输出状态。</summary>
            internal bool OutputValue { get; private set; }
            /// <summary>记录驱动收到的输出命令次数。</summary>
            internal int OutputWrites;
            /// <summary>记录输出读取次数，用于等待动作后回读阶段。</summary>
            internal int OutputReads;
            /// <summary>读取 DI 时抛出的异常；为空表示正常返回假值。</summary>
            internal Exception ReadError { get; set; }

            /// <summary>当前测试输入状态。</summary>
            internal bool InputValue { get; set; }
            /// <summary>可独立指定清料完成输入，避免将它与许可输入混用。</summary>
            internal bool? ClearanceFeedback { get; set; }
            /// <summary>首次读取后切换信号，用于检查复核期间变化。</summary>
            internal bool FlipAfterRead { get; set; }
            /// <summary>首次读取输出后切换电平，模拟复核期间现场变化。</summary>
            internal bool FlipOutputAfterRead { get; set; }

            /// <summary>读取 DI；注入错误时抛出原始异常。</summary>
            public Task<bool> ReadInputAsync(string inputName, CancellationToken cancellationToken)
            {
                if (ReadError != null)
                {
                    throw ReadError;
                }

                if (inputName == "ClearanceFeedback" && ClearanceFeedback.HasValue)
                    return Task.FromResult(ClearanceFeedback.Value);

                bool value = InputValue;
                if (FlipAfterRead) InputValue = !InputValue;
                return Task.FromResult(value);
            }

            /// <summary>读取 DO，固定返回假值。</summary>
            public Task<bool> ReadOutputAsync(string outputName, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref OutputReads);
                if (OutputReadError != null) throw OutputReadError;
                bool value = OutputValue;
                if (FlipOutputAfterRead) OutputValue = !OutputValue;
                return Task.FromResult(value);
            }

            /// <summary>写入 DO，检查中不保存状态。</summary>
            public Task WriteOutputAsync(string outputName, bool value, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref OutputWrites);
                OutputValue = value;
                if (WriteError != null) throw WriteError;
                return Task.CompletedTask;
            }
        }

        /// <summary>只有一个逻辑轴的测试控制卡。</summary>
        private sealed class FakeMotionCard : IMotionCard
        {
            /// <summary>建立只包含一根轴的控制卡。</summary>
            internal FakeMotionCard(string controllerId, IAxis axis)
            {
                ControllerId = controllerId;
                Axes = new[] { axis };
            }

            /// <summary>控制卡标识。</summary>
            public string ControllerId { get; }

            /// <summary>当前连接状态；检查不建立真实连接。</summary>
            public bool IsConnected
            {
                get { return true; }
            }

            /// <summary>本卡的逻辑轴。</summary>
            public IReadOnlyCollection<IAxis> Axes { get; }

            /// <summary>检查不需要真实连接。</summary>
            public Task ConnectAsync(CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }

            /// <summary>检查不需要真实断开。</summary>
            public Task DisconnectAsync(CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }
        }

        /// <summary>急停回路恒为释放状态的测试替身。</summary>
        private sealed class FakeSafetyStatus : IConfigurableMachineSafetyStatus
        {
            /// <summary>模拟软件急停输入。</summary>
            internal bool Active { get; set; }
            /// <summary>视为已配置，允许运行时启动检查继续。</summary>
            public bool IsConfigured
            {
                get { return true; }
            }

            /// <summary>急停始终未触发。</summary>
            public Task<bool> ReadEmergencyStopActiveAsync(CancellationToken cancellationToken)
            {
                return Task.FromResult(Active);
            }
        }

        /// <summary>永远等待输入的工位流程，用于制造"设备已配置自动流程"的状态。</summary>
        private sealed class WaitingStationFlow : IStationFlow
        {
            /// <summary>工位名称。</summary>
            public string StationName
            {
                get { return "检查工位"; }
            }

            /// <summary>当前步骤名称。</summary>
            public string CurrentStepName
            {
                get { return "等待输入"; }
            }

            /// <summary>一直等待，直到运行被取消。</summary>
            public Task ExecuteCurrentStepAsync(CancellationToken cancellationToken)
            {
                return Task.Delay(Timeout.Infinite, cancellationToken);
            }

            /// <summary>清除可复位报警，不改变步骤。</summary>
            public Task ResetAsync(CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }
        }
    }
}
