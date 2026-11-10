using System;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using MotionControl.TaskEngine.Resources;
using MotionControl.Framework.Runtime.Machine;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.Clearance;

namespace MotionControl.MachineDevice.Checks
{
    /// <summary>核验清料租约只提供隔离资源排他能力，不执行设备动作。</summary>
    internal static partial class Program
    {
        /// <summary>先核对失败记录保留，再以真实设备适配器和假轴验证任务持权下的清料、停止及急停入口。</summary>
        private static void MachineClearanceLifecycleChecks()
        {
            ClearanceFailureKeepsOpenGuardAndOriginalException();
            foreach (string scenario in new[] { "完成", "状态订阅异常", "恢复订阅异常", "停止", "急停", "记录时停止", "记录时报警", "核查写入失败", "核查后变化", "取消重入", "取消回调失败" })
                MachineClearanceLifecycleScenarioAsync(scenario).GetAwaiter().GetResult();
            ClearanceIsolationErrorIsNotHiddenAsync().GetAwaiter().GetResult();
            StationFlowClearanceKeepsRecoveryBlockedAsync().GetAwaiter().GetResult();
            PendingClearanceCannotRecoverAsync().GetAwaiter().GetResult();
            ClearanceRecordFailureBlocksStartAsync().GetAwaiter().GetResult();
            ClearanceMonitorStartupFailureCanBeInspectedAsync().GetAwaiter().GetResult();
            ClearanceRecoveryRequiresAllDeclaredInputs();
            ClearanceSharedResourceUsesApprovedFeedbackAsync().GetAwaiter().GetResult();
            ClearanceUnmappedAxisDoesNotBlockConnectionAsync().GetAwaiter().GetResult();
            ClearanceMonitorsAndRecoversGuardedOutputAsync().GetAwaiter().GetResult();
            ClearanceTargetOutputDropRetainsIsolationAsync().GetAwaiter().GetResult();
        }

        /// <summary>完成 DI 等待中目标 DO 失效时，整机和任务资源都必须保持隔离。</summary>
        private static async Task ClearanceTargetOutputDropRetainsIsolationAsync()
        {
            string root = Path.Combine(Path.GetTempPath(), "mcf-clearance-target-do-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var axis = new FakeAxis("X");
                var io = new FakeLogicalIo { InputValue = true, ClearanceFeedback = false };
                var device = CreateDevice(axis, io, out _, out MachineRuntime runtime, journal: new TaskResourceJournal(root, "M1"));
                var input = new[] { new ClearanceInputCondition("Sensor", true) };
                var action = new ClearanceSetOutput("夹具", "Vacuum", false, true, input,
                    new[] { new ClearanceAxisCondition("X", -1, 1) },
                    new[] { new ClearanceInputCondition("ClearanceFeedback", true) }, TimeSpan.FromSeconds(5), maintainInputs: input);
                var recovery = new ControlledClearanceRecovery(new[] { new ClearanceInputCondition("Sensor", true),
                    new ClearanceInputCondition("ClearanceFeedback", false) }, new[] { new ClearanceAxisCondition("X", -1, 1) },
                    new[] { new ClearanceRecoveryOutput("Vacuum", false) });
                var profile = new ControlledClearanceProfile("target", new ControlledClearancePlan(new[] { action }), recovery,
                    new[] { new ClearanceAxisLimit("X", -10, 10, 10, 100, 100) });
                runtime.ConfigureControlledClearanceProfiles(new[] { profile });
                runtime.SetClearanceRecordCallbacks(() => { }, () => { }, value => { });
                await runtime.ConnectAsync(CancellationToken.None);
                await runtime.ResetAsync(CancellationToken.None);
                device.ClaimMachine(new object());
                var blocked = device.Resources.Enqueue("old", "T", "工位", "n", "动作", "e", new[] { "Axis:X" });
                Ensure(SpinWait.SpinUntil(() => device.Resources.TryAcquire(blocked, out _), 5000), "原资源记录未完成。");
                device.Resources.Block(blocked, "待确认");
                await runtime.StartControlledClearanceAsync("target", CancellationToken.None);
                Ensure(SpinWait.SpinUntil(() => io.OutputValue && Volatile.Read(ref io.OutputReads) >= 5, 5000),
                    "清料目标输出未写入或未进入完成反馈等待。");
                await io.WriteOutputAsync("Vacuum", false, CancellationToken.None);
                try { await runtime.ClearanceCompletion; } catch (InvalidOperationException) { }
                Ensure(SpinWait.SpinUntil(() => runtime.IsClearanceStopSettled, 5000) && runtime.IsClearing
                    && device.Resources.IsReserved("Output:Vacuum") && !device.Resources.Recover(blocked),
                    "目标 DO 失效后未保持清料及原资源隔离。");
                await runtime.ConfirmControlledClearanceRecoveryAsync("现场已核实", CancellationToken.None);
                Ensure(!runtime.IsClearing && device.ReadResources().Count == 1, "核查后丢失原 Blocked 记录。");
            }
            finally { Directory.Delete(root, true); }
        }

        /// <summary>工艺 DO 必须随计划独占、动作中监测并在恢复时按设备声明复核。</summary>
        private static async Task ClearanceMonitorsAndRecoversGuardedOutputAsync()
        {
            string root = Path.Combine(Path.GetTempPath(), "mcf-clearance-guarded-do-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var axis = new FakeAxis("X");
                var io = new FakeLogicalIo { InputValue = true };
                var device = CreateDevice(axis, io, out _, out MachineRuntime runtime, journal: new TaskResourceJournal(root, "M1"));
                var inputs = new[] { new ClearanceInputCondition("Sensor", true) };
                var move = new ClearanceAbsoluteMove("搬运", "X", 5, 0.01, Profile(), inputs,
                    new[] { new ClearanceAxisCondition("X", -1, 1) }, TimeSpan.FromSeconds(5), maintainInputs: inputs,
                    maintainOutputs: new[] { new ClearanceOutputCondition("Vacuum", false) });
                var recovery = new ControlledClearanceRecovery(inputs, new[] { new ClearanceAxisCondition("X", -1, 6) },
                    new[] { new ClearanceRecoveryOutput("Vacuum", false) });
                var profile = new ControlledClearanceProfile("guarded", new ControlledClearancePlan(new[] { move }), recovery,
                    new[] { new ClearanceAxisLimit("X", -10, 10, 10, 100, 100) });
                runtime.ConfigureControlledClearanceProfiles(new[] { profile });
                runtime.SetClearanceRecordCallbacks(() => { }, () => { }, value => { });
                await runtime.ConnectAsync(CancellationToken.None);
                await runtime.ResetAsync(CancellationToken.None);
                device.ClaimMachine(new object());
                var blocked = device.Resources.Enqueue("old", "T", "工位", "n", "动作", "e", new[] { "Axis:X" });
                Ensure(SpinWait.SpinUntil(() => device.Resources.TryAcquire(blocked, out _), 5000), "原资源记录未完成。");
                device.Resources.Block(blocked, "待确认");
                await runtime.StartControlledClearanceAsync("guarded", CancellationToken.None);
                Ensure(SpinWait.SpinUntil(() => axis.Moving, 5000) && device.Resources.IsReserved("Output:Vacuum"),
                    "清料未启动或维持输出未独占。");
                await io.WriteOutputAsync("Vacuum", true, CancellationToken.None);
                try { await runtime.ClearanceCompletion; } catch (InvalidOperationException) { }
                Ensure(SpinWait.SpinUntil(() => runtime.IsClearanceStopSettled, 5000) && axis.StopCalls > 0,
                    "维持输出异常后未确认停轴。");
                ExpectMessage(() => runtime.ConfirmControlledClearanceRecoveryAsync("现场已核实", CancellationToken.None)
                    .GetAwaiter().GetResult(), "恢复输出不满足");
                Ensure(device.Resources.IsReserved("Output:Vacuum") && !device.Resources.Recover(blocked), "输出异常却解除隔离。");
                await io.WriteOutputAsync("Vacuum", false, CancellationToken.None);
                await runtime.ConfirmControlledClearanceRecoveryAsync("现场已核实", CancellationToken.None);
                Ensure(!runtime.IsClearing && device.ReadResources().Count == 1, "输出恢复后丢失原隔离。");
            }
            finally { Directory.Delete(root, true); }
        }

        /// <summary>可选设备清料未映射轴时不能启动动作，但不能妨碍普通设备连接。</summary>
        private static async Task ClearanceUnmappedAxisDoesNotBlockConnectionAsync()
        {
            var axis = new FakeAxis("X");
            CreateDevice(axis, new FakeLogicalIo { InputValue = true }, out _, out MachineRuntime runtime);
            var input = new[] { new ClearanceInputCondition("Sensor", true) };
            var move = new ClearanceAbsoluteMove("清料", "Y", 1, 0.01, Profile(), input,
                new[] { new ClearanceAxisCondition("Y", 0, 0) }, TimeSpan.FromSeconds(5), maintainInputs: input);
            var profile = new ControlledClearanceProfile("unmapped", new ControlledClearancePlan(new[] { move }),
                new ControlledClearanceRecovery(input, new[] { new ClearanceAxisCondition("Y", 0, 1.1) }, new ClearanceRecoveryOutput[0]),
                new[] { new ClearanceAxisLimit("Y", -10, 10, 10, 100, 100) });
            int before = 0;
            runtime.ConfigureControlledClearanceProfiles(new[] { profile });
            runtime.SetClearanceRecordCallbacks(() => before++, () => { }, value => { });
            await runtime.ConnectAsync(CancellationToken.None);
            await runtime.ResetAsync(CancellationToken.None);
            ExpectMessage(() => runtime.StartControlledClearanceAsync("unmapped", CancellationToken.None).GetAwaiter().GetResult(), "找不到逻辑轴");
            Ensure(before == 0 && axis.MoveCalls == 0 && string.IsNullOrWhiteSpace(runtime.RecoveryBlockReason),
                "未映射清料轴时错误写证据、发出动作或阻断普通设备连接。");
        }

        /// <summary>共享区域随任务隔离继承，只有预注册的对应输入满足后才可恢复清料租约。</summary>
        private static async Task ClearanceSharedResourceUsesApprovedFeedbackAsync()
        {
            string root = Path.Combine(Path.GetTempPath(), "mcf-clearance-zone-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var axis = new FakeAxis("X");
                var io = new FakeLogicalIo { InputValue = true };
                var device = CreateDevice(axis, io, out _, out MachineRuntime runtime,
                    journal: new TaskResourceJournal(root, "M1"), namedResources: new[] { "Zone:Rotary" });
                var original = TestClearanceProfile();
                var plan = new ControlledClearancePlan(original.Plan.Actions, new[] { "Zone:Rotary" });
                var profile = new ControlledClearanceProfile("zone", plan, original.Recovery, original.AxisLimits,
                    new[] { new ClearanceSharedFeedback("Zone:Rotary", new[] { new ClearanceInputCondition("Sensor", true) }) });
                runtime.ConfigureControlledClearanceProfiles(new[] { profile });
                runtime.SetClearanceRecordCallbacks(() => { }, () => { }, value => { });
                await runtime.ConnectAsync(CancellationToken.None);
                await runtime.ResetAsync(CancellationToken.None);
                device.ClaimMachine(new object());
                var blocked = device.Resources.Enqueue("old", "T", "工位", "n", "动作", "e", new[] { "Axis:X" });
                Ensure(SpinWait.SpinUntil(() => device.Resources.TryAcquire(blocked, out _), 5000), "原资源记录未完成。");
                device.Resources.Block(blocked, "待确认");
                await runtime.StartControlledClearanceAsync("zone", CancellationToken.None);
                Ensure(SpinWait.SpinUntil(() => axis.Moving, 5000), "清料轴未开始。");
                Ensure(device.Resources.IsReserved("Zone:Rotary"), "共享区域未独占。");
                try { await runtime.StopControlledClearanceAsync(); } catch (InvalidOperationException) { }
                try { await runtime.ClearanceCompletion; } catch (InvalidOperationException) { }
                Ensure(SpinWait.SpinUntil(() => runtime.IsClearanceStopSettled, 5000), "共享区域停止尚未结算。");
                io.InputValue = false;
                ExpectMessage(() => runtime.ConfirmControlledClearanceRecoveryAsync("现场已核实", CancellationToken.None)
                    .GetAwaiter().GetResult(), "恢复输入不满足");
                Ensure(device.Resources.IsReserved("Zone:Rotary") && !device.Resources.Recover(blocked), "共享区域条件不符却丢失隔离。");
                io.InputValue = true;
                await runtime.ConfirmControlledClearanceRecoveryAsync("现场已核实", CancellationToken.None);
                Ensure(!device.Resources.IsReserved("Zone:Rotary") && device.ReadResources().Count == 1,
                    "共享区域恢复后没有保留原隔离。");
            }
            finally { Directory.Delete(root, true); }
        }

        /// <summary>为检查设备预先注册固定动作和恢复条件。</summary>
        private static ControlledClearanceProfile TestClearanceProfile(bool includeOutput = false, bool recoveryOutput = false)
        {
            var input = new[] { new ClearanceInputCondition("Sensor", true) };
            var move = new ClearanceAbsoluteMove("固定清料点", "X", 5, 0.01, Profile(), input,
                new[] { new ClearanceAxisCondition("X", -1, 1) }, TimeSpan.FromSeconds(5), maintainInputs: input);
            var actions = new List<ControlledClearanceAction> { move };
            if (includeOutput)
                actions.Add(new ClearanceSetOutput("固定输出", "Vacuum", false, true, input,
                    new[] { new ClearanceAxisCondition("X", 4.99, 5.01) }, input, TimeSpan.FromSeconds(5), maintainInputs: input));
            var recovery = new ControlledClearanceRecovery(input, new[] { new ClearanceAxisCondition("X", -1, 6) },
                includeOutput ? new[] { new ClearanceRecoveryOutput("Vacuum", recoveryOutput) } : new ClearanceRecoveryOutput[0]);
            return new ControlledClearanceProfile("test", new ControlledClearancePlan(actions), recovery,
                new[] { new ClearanceAxisLimit("X", -100, 100, 10, 100, 100) });
        }

        /// <summary>所有前置、维持和完成 DI 必须明确覆盖；无区域反馈策略的共享资源不得恢复。</summary>
        private static void ClearanceRecoveryRequiresAllDeclaredInputs()
        {
            var output = new ClearanceSetOutput("夹具", "Vacuum", false, true,
                new[] { new ClearanceInputCondition("Sensor", true) },
                new[] { new ClearanceAxisCondition("X", -1, 1) },
                new[] { new ClearanceInputCondition("Feedback", true) }, TimeSpan.FromSeconds(5),
                maintainInputs: new[] { new ClearanceInputCondition("DoorSafe", true) });
            var plan = new ControlledClearancePlan(new[] { output });
            var partial = new ControlledClearanceRecovery(new[] { new ClearanceInputCondition("Sensor", true) },
                new[] { new ClearanceAxisCondition("X", -1, 1) }, new[] { new ClearanceRecoveryOutput("Vacuum", false) });
            var limits = new[] { new ClearanceAxisLimit("X", -10, 10, 10, 100, 100) };
            ExpectMessage(() => new ControlledClearanceProfile("test", plan, partial, limits), "遗漏清料输入");
            var complete = new ControlledClearanceRecovery(new[] { new ClearanceInputCondition("Sensor", true),
                new ClearanceInputCondition("DoorSafe", true), new ClearanceInputCondition("Feedback", false) },
                new[] { new ClearanceAxisCondition("X", -1, 1) }, new[] { new ClearanceRecoveryOutput("Vacuum", false) });
            _ = new ControlledClearanceProfile("test", plan, complete, limits);
            var shared = new ControlledClearancePlan(new[] { output }, new[] { "Zone:Rotary" });
            ExpectMessage(() => new ControlledClearanceProfile("test", shared, complete, limits), "共享区域缺少设备声明");
            _ = new ControlledClearanceProfile("test", shared, complete, limits,
                new[] { new ClearanceSharedFeedback("Zone:Rotary", new[] { new ClearanceInputCondition("DoorSafe", true) }) });
            ExpectMessage(() => new ControlledClearanceProfile("test", shared, complete, limits,
                new[] { new ClearanceSharedFeedback("Zone:Rotary", new[] { new ClearanceInputCondition("DoorSafe", false) }) }),
                "共享区域反馈未包含在恢复条件中");
            var missingOutput = new ControlledClearanceRecovery(complete.Inputs, complete.Axes, new ClearanceRecoveryOutput[0]);
            ExpectMessage(() => new ControlledClearanceProfile("test", plan, missingOutput, limits), "遗漏清料输出");
            var input = new[] { new ClearanceInputCondition("Sensor", true) };
            var move = new ClearanceAbsoluteMove("动作", "X", 5, 0.01, Profile(), input,
                new[] { new ClearanceAxisCondition("X", -1, 1) }, TimeSpan.FromSeconds(5), maintainInputs: input);
            var movePlan = new ControlledClearancePlan(new[] { move });
            var moveRecovery = new ControlledClearanceRecovery(input, new[] { new ClearanceAxisCondition("X", -1, 6) }, new ClearanceRecoveryOutput[0]);
            ExpectMessage(() => new ControlledClearanceProfile("test", movePlan, moveRecovery,
                new[] { new ClearanceAxisLimit("X", -10, 10, 1, 100, 100) }), "目标、容差或速度");
            ExpectMessage(() => new ControlledClearanceProfile("test", movePlan, moveRecovery,
                new[] { new ClearanceAxisLimit("X", -1, 5, 10, 100, 100) }), "安全边界");
            var mutableLimits = new List<ClearanceAxisLimit> { new ClearanceAxisLimit("X", -10, 10, 10, 100, 100) };
            var frozen = new ControlledClearanceProfile("test", movePlan, moveRecovery, mutableLimits);
            mutableLimits.Clear();
            Ensure(frozen.AxisLimits.Count == 1, "注册前复制的设备轴边界被外部列表修改。");
            var extraRecovery = new ControlledClearanceRecovery(input,
                new[] { new ClearanceAxisCondition("X", -1, 6), new ClearanceAxisCondition("Y", -1, 1) },
                new[] { new ClearanceRecoveryOutput("Indicator", false) });
            var extra = new ControlledClearanceProfile("extra", movePlan, extraRecovery,
                new[] { new ClearanceAxisLimit("X", -10, 10, 10, 100, 100), new ClearanceAxisLimit("Y", -10, 10, 10, 100, 100) });
            Ensure(extra.RequiredResources.Contains("Axis:Y") && extra.RequiredResources.Contains("Output:Indicator"),
                "恢复条件新增轴或输出未进入独占资源范围。");
            var guardedMove = new ClearanceAbsoluteMove("动作", "X", 5, 0.01, Profile(), input,
                new[] { new ClearanceAxisCondition("X", -1, 1) }, TimeSpan.FromSeconds(5), maintainInputs: input,
                maintainOutputs: new[] { new ClearanceOutputCondition("焊接启动", false) });
            var guardedPlan = new ControlledClearancePlan(new[] { guardedMove });
            Ensure(guardedPlan.RequiredResources.Contains("Output:焊接启动"), "维持输出未进入计划独占资源。");
            ExpectMessage(() => new ControlledClearanceProfile("test", guardedPlan, moveRecovery,
                new[] { new ClearanceAxisLimit("X", -10, 10, 10, 100, 100) }), "遗漏清料输出");
            var guardedRecovery = new ControlledClearanceRecovery(input, new[] { new ClearanceAxisCondition("X", -1, 6) },
                new[] { new ClearanceRecoveryOutput("焊接启动", false) });
            _ = new ControlledClearanceProfile("test", guardedPlan, guardedRecovery,
                new[] { new ClearanceAxisLimit("X", -10, 10, 10, 100, 100) });
        }

        /// <summary>安全监控无法启动时仍须建立待确认会话；旧监控退出前不得恢复。</summary>
        private static async Task ClearanceMonitorStartupFailureCanBeInspectedAsync()
        {
            string root = Path.Combine(Path.GetTempPath(), "mcf-clearance-monitor-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var oldMonitor = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                var axis = new FakeAxis("X");
                var device = CreateDevice(axis, new FakeLogicalIo { InputValue = true }, out _, out MachineRuntime runtime,
                    journal: new TaskResourceJournal(root, "M1"));
                runtime.SetClearanceRecordCallbacks(() => { }, () => { }, value => { });
                runtime.ConfigureControlledClearanceProfiles(new[] { TestClearanceProfile() });
                await runtime.ConnectAsync(CancellationToken.None);
                await runtime.ResetAsync(CancellationToken.None);
                device.ClaimMachine(new object());
                var blocked = device.Resources.Enqueue("old", "T", "工位", "n", "动作", "e", new[] { "Axis:X" });
                Ensure(SpinWait.SpinUntil(() => device.Resources.TryAcquire(blocked, out _), 5000), "资源记录未完成。");
                device.Resources.Block(blocked, "待确认");
                const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                typeof(MachineRuntime).GetField("_safetyMonitorTask", flags).SetValue(runtime, oldMonitor.Task);
                ExpectMessage(() => runtime.StartControlledClearanceAsync("test", CancellationToken.None)
                    .GetAwaiter().GetResult(), "急停状态监控尚未结束");
                Ensure(SpinWait.SpinUntil(() => runtime.IsClearanceStopSettled, 5000) && axis.MoveCalls == 0,
                    "监控启动失败后没有建立可核查的停止会话，或错误发出动作。");
                ExpectMessage(() => runtime.ConfirmControlledClearanceRecoveryAsync("现场已核实", CancellationToken.None)
                    .GetAwaiter().GetResult(), "急停监控仍在收尾");
                Ensure(runtime.IsClearing && !device.Resources.Recover(blocked), "旧监控未结束却释放清料隔离。");
                oldMonitor.TrySetResult(true);
                await runtime.ConfirmControlledClearanceRecoveryAsync("现场已核实", CancellationToken.None);
                Ensure(!runtime.IsClearing && device.ReadResources().Count == 1, "旧监控结束后没有完成核查或丢失原隔离。");
            }
            finally { oldMonitor.TrySetResult(true); Directory.Delete(root, true); }
        }

        /// <summary>记录回调即使部分写入后失败，也不得继续发动作或放行旧生产。</summary>
        private static async Task ClearanceRecordFailureBlocksStartAsync()
        {
            var axis = new FakeAxis("X");
            CreateDevice(axis, new FakeLogicalIo { InputValue = true }, out _, out MachineRuntime runtime);
            runtime.SetClearanceRecordCallbacks(() => { throw new IOException("记录失败"); }, () => { }, value => { });
            runtime.ConfigureControlledClearanceProfiles(new[] { TestClearanceProfile() });
            await runtime.ConnectAsync(CancellationToken.None);
            await runtime.ResetAsync(CancellationToken.None);
            ExpectMessage(() => runtime.StartControlledClearanceAsync("test", CancellationToken.None)
                .GetAwaiter().GetResult(), "记录失败");
            Ensure(axis.MoveCalls == 0 && !string.IsNullOrWhiteSpace(runtime.RecoveryBlockReason), "记录异常后错误允许动作或旧生产。");
        }

        /// <summary>以未放行停止验证清料隔离；失败时保留真实记录目录，避免删除异常掩盖业务首因。</summary>
        private static async Task PendingClearanceCannotRecoverAsync()
        {
            string root = Path.Combine(Path.GetTempPath(), "mcf-clearance-pending-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var stopGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool caseSucceeded = false;
            try
            {
                var axis = new FakeAxis("X") { IgnoresCancellation = true, StopGate = stopGate };
                // 此超时也覆盖启动时真实资源记录落盘，因此仅本例留出五秒；停止迟到由未放行的 stopGate 决定。
                var device = CreateDevice(axis, new FakeLogicalIo { InputValue = true }, out _, out MachineRuntime runtime,
                    journal: new TaskResourceJournal(root, "M1"), pauseTimeout: TimeSpan.FromSeconds(5));
                runtime.SetClearanceRecordCallbacks(() => { }, () => { }, value => { });
                runtime.ConfigureControlledClearanceProfiles(new[] { TestClearanceProfile() });
                await runtime.ConnectAsync(CancellationToken.None);
                await runtime.ResetAsync(CancellationToken.None);
                device.ClaimMachine(new object());
                var blocked = device.Resources.Enqueue("old", "T", "工位", "n", "动作", "e", new[] { "Axis:X" });
                Ensure(SpinWait.SpinUntil(() => device.Resources.TryAcquire(blocked, out _), 5000), "资源记录未完成。");
                device.Resources.Block(blocked, "待确认");
                await runtime.StartControlledClearanceAsync("test", CancellationToken.None);
                Ensure(SpinWait.SpinUntil(() => axis.Moving, 5000), "清料轴命令未启动。");
                bool pauseTimedOut = false;
                try { await runtime.PauseAsync(CancellationToken.None); }
                catch (TimeoutException) { pauseTimedOut = true; }
                Ensure(pauseTimedOut, "停止门未放行时暂停没有超时，未覆盖迟到停止场景。");
                Ensure(axis.StopCalls > 0, "暂停超时前没有实际调用轴停止。");
                Ensure(!stopGate.Task.IsCompleted, "核查迟到停止前停止门已被放行。");
                ExpectMessage(() => runtime.ConfirmControlledClearanceRecoveryAsync("确认", CancellationToken.None)
                    .GetAwaiter().GetResult(), "仍在执行或收尾");
                Ensure(runtime.IsClearing && !device.Resources.Recover(blocked), "迟到停止时丢失隔离。");
                stopGate.TrySetResult(true);
                try { await runtime.ClearanceCompletion; } catch (InvalidOperationException) { }
                await runtime.ConfirmControlledClearanceRecoveryAsync("现场已核实", CancellationToken.None);
                Ensure(!runtime.IsClearing && device.ReadResources().Count == 1, "迟到停止结束后恢复未保留原隔离。");
                caseSucceeded = true;
            }
            finally
            {
                stopGate.TrySetResult(true);
                FinishClearanceCheckDirectory(root, caseSucceeded);
            }
        }

        /// <summary>真实 .guard 句柄仍打开时，失败收尾必须保留原异常和目录，不能用删除错误覆盖业务首因。</summary>
        private static void ClearanceFailureKeepsOpenGuardAndOriginalException()
        {
            string root = Path.Combine(Path.GetTempPath(), "mcf-clearance-cleanup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string guardPath = Path.Combine(root, "cleanup.guard");
            var originalError = new InvalidOperationException("清料失败收尾检查的原始异常哨兵。");
            try
            {
                using (var guard = new FileStream(guardPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                {
                    Exception observedError = null;
                    try
                    {
                        try { throw originalError; }
                        finally { FinishClearanceCheckDirectory(root, caseSucceeded: false); }
                    }
                    catch (Exception error) { observedError = error; }
                    Ensure(ReferenceEquals(observedError, originalError), "清料失败收尾用目录删除异常覆盖了原始业务异常。");
                    Ensure(Directory.Exists(root) && File.Exists(guardPath), "清料失败收尾删除了仍有打开句柄的诊断目录或记录。");
                }
            }
            finally
            {
                // using 已关闭本负控持有的唯一句柄；只清理它自己的临时目录，不干预实际失败用例的证据。
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
            Console.WriteLine("[通过] 清料失败收尾：真实 .guard 句柄未关闭时保留原始异常与记录目录。");
        }

        /// <summary>清料检查全部成功才一次删除目录；失败时只记录路径，保留可能仍被后台写入的证据。</summary>
        private static void FinishClearanceCheckDirectory(string root, bool caseSucceeded)
        {
            if (caseSucceeded) Directory.Delete(root, true);
            else Console.Error.WriteLine("[清料检查诊断] 用例失败，保留记录目录供排查：" + root);
        }

        /// <summary>C# 工位暂停并结算后共用清料状态，清料后不能续跑原工步。</summary>
        private static async Task StationFlowClearanceKeepsRecoveryBlockedAsync()
        {
            var axis = new FakeAxis("X");
            CreateDevice(axis, new FakeLogicalIo { InputValue = true }, out _, out MachineRuntime runtime, withStationFlow: true);
            int before = 0, observed = 0;
            runtime.SetClearanceRecordCallbacks(() => before++, () => observed++);
            runtime.ConfigureControlledClearanceProfiles(new[] { TestClearanceProfile() });
            await runtime.ConnectAsync(CancellationToken.None);
            await runtime.ResetAsync(CancellationToken.None);
            await runtime.StartAsync(CancellationToken.None);
            await runtime.PauseAsync(CancellationToken.None);
            Ensure(runtime.State == MachineRunState.Paused, "C# 工位未进入暂停，不能测试清料。");
            await runtime.StartControlledClearanceAsync("test", CancellationToken.None);
            Ensure(SpinWait.SpinUntil(() => axis.Moving, 5000), "C# 工位暂停后未开始清料动作。");
            axis.Complete();
            await runtime.ClearanceCompletion;
            Ensure(before == 1 && observed == 1 && runtime.State == MachineRunState.ResetRequired,
                "C# 清料未写证据或错误标记为就绪。");
            await runtime.ResetAsync(CancellationToken.None);
            ExpectMessage(() => runtime.StartAsync(CancellationToken.None).GetAwaiter().GetResult(), "恢复信息未确认");
        }

        /// <summary>持久记录和隔离锁存同时失败时，两者都必须向调用方报告。</summary>
        private static async Task ClearanceIsolationErrorIsNotHiddenAsync()
        {
            var axis = new FakeAxis("X");
            CreateDevice(axis, new FakeLogicalIo { InputValue = true }, out _, out MachineRuntime runtime);
            runtime.SetClearanceRecordCallbacks(() => { }, () => { });
            runtime.ConfigureControlledClearanceProfiles(new[] { TestClearanceProfile() });
            await runtime.ConnectAsync(CancellationToken.None);
            await runtime.ResetAsync(CancellationToken.None);
            await runtime.AcquireTaskEngineControlAsync(new FailingClearanceOwner(), CancellationToken.None);
            try
            {
                await runtime.StartControlledClearanceAsync("test", CancellationToken.None);
                throw new InvalidOperationException("记录和锁存失败时不应开始清料。");
            }
            catch (AggregateException error)
            {
                string text = error.ToString();
                Ensure(text.Contains("记录失败") && text.Contains("隔离失败"), "隔离失败或原始错误被隐藏。");
                Ensure(axis.MoveCalls == 0 && runtime.State == MachineRunState.Alarmed, "失败后仍发出轴命令或未锁存报警。");
            }
        }

        private sealed class FailingClearanceOwner : IControlledClearanceResources
        {
            public IControlledClearanceReservation ReserveClearance(IReadOnlyCollection<string> names) => new FailingClearanceReservation();
        }

        private sealed class FailingClearanceReservation : IControlledClearanceReservation
        {
            public Task Ready => Task.FromException(new InvalidOperationException("记录失败"));
            public void MarkUnconfirmed(string reason) { throw new InvalidOperationException("隔离失败"); }
            public void Complete() { throw new InvalidOperationException("不应完成"); }
            public void ConfirmRecovery() { throw new InvalidOperationException("不应恢复"); }
        }

        private static async Task MachineClearanceLifecycleScenarioAsync(string scenario)
        {
            string root = Path.Combine(Path.GetTempPath(), "mcf-clearance-lifecycle-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var axis = new FakeAxis("X");
                var io = new FakeLogicalIo { InputValue = true };
                var safety = new FakeSafetyStatus();
                var device = CreateDevice(axis, io, out _, out MachineRuntime runtime,
                    journal: new TaskResourceJournal(root, "M1"), safety: safety, pauseTimeout: TimeSpan.FromMilliseconds(500));
                int before = 0, after = 0;
                MotionControl.Framework.Contracts.Alarms.MachineAlarmDefinition testAlarm = null;
                bool failInspection = scenario == "核查写入失败";
                bool changeDuringInspection = scenario == "核查后变化";
                var inspections = new List<string>();
                bool reentrantCallbackFinished = false;
                var recordEntered = new ManualResetEventSlim(false);
                var finishRecord = new ManualResetEventSlim(false);
                runtime.SetClearanceRecordCallbacks(() => before++, () =>
                {
                    recordEntered.Set();
                    if (scenario.StartsWith("记录时") && !finishRecord.Wait(TimeSpan.FromSeconds(5)))
                        throw new TimeoutException("测试记录未放行。");
                    after++;
                }, value =>
                {
                    if (failInspection) throw new IOException("核查记录写入失败");
                    inspections.Add(value);
                    if (changeDuringInspection) { changeDuringInspection = false; io.InputValue = false; }
                });
                runtime.ConfigureControlledClearanceProfiles(new[] { TestClearanceProfile(true, scenario.StartsWith("记录时")) });
                await runtime.ConnectAsync(CancellationToken.None);
                await runtime.ResetAsync(CancellationToken.None);
                bool successScenario = scenario == "完成" || scenario == "状态订阅异常";
                int receivedStatus = 0;
                if (scenario.EndsWith("订阅异常"))
                {
                    runtime.StatusChanged += (sender, args) => throw new InvalidOperationException("模拟状态订阅者异常");
                    runtime.StatusChanged += (sender, args) => Interlocked.Increment(ref receivedStatus);
                    runtime.PreparationChanged += (sender, args) => throw new InvalidOperationException("模拟准备订阅者异常");
                }
                device.ClaimMachine(new object());
                var blocked = device.Resources.Enqueue("old", "T", "工位", "n", "动作", "e", new[] { "Axis:X" });
                Ensure(SpinWait.SpinUntil(() => device.Resources.TryAcquire(blocked, out _), 5000), "资源记录未完成。");
                device.Resources.Block(blocked, "待确认");
                ExpectMessage(() => runtime.StartControlledClearanceAsync("missing", CancellationToken.None).GetAwaiter().GetResult(), "未注册");
                ExpectMessage(() => runtime.ConfigureControlledClearanceProfiles(new[] { TestClearanceProfile() }), "只能在控制器断开");
                await runtime.StartControlledClearanceAsync("test", CancellationToken.None);
                Ensure(SpinWait.SpinUntil(() => axis.Moving, 5000), "清料轴命令未启动。");
                Ensure(runtime.IsClearing && runtime.IsPreparing && before == 1, "清料未占用整机状态或未先记录。");
                Ensure(!device.Resources.Recover(blocked), "清料期间释放了旧隔离。");
                if (scenario == "取消重入" || scenario == "取消回调失败")
                {
                    const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                    var source = (CancellationTokenSource)typeof(MachineRuntime).GetField("_preparationCancellation", flags).GetValue(runtime);
                    source.Token.Register(() =>
                    {
                        if (scenario == "取消回调失败") throw new InvalidOperationException("模拟取消回调失败");
                        Task probe = Task.Run(() => runtime.IsClearing);
                        if (!probe.Wait(TimeSpan.FromSeconds(2))) throw new TimeoutException("取消回调重入运行时死锁。");
                        reentrantCallbackFinished = true;
                    });
                }
                if (scenario.StartsWith("记录时"))
                {
                    axis.Complete();
                    Ensure(recordEntered.Wait(TimeSpan.FromSeconds(5)), "未进入结束记录。");
                    Task stop = null;
                    if (scenario == "记录时停止") stop = runtime.StopControlledClearanceAsync();
                    else
                    {
                        testAlarm = new MotionControl.Framework.Contracts.Alarms.MachineAlarmDefinition(
                            "CLEARANCE_TEST", "清料测试", MotionControl.Framework.Contracts.Alarms.MachineAlarmKind.StopMachine);
                        runtime.AlarmService.ReportCondition(testAlarm, "测试", true, "记录期间报警");
                    }
                    Ensure(SpinWait.SpinUntil(() => axis.StopCalls > 0, 5000), "记录期间未立即发出停止。");
                    Ensure(!device.Resources.Recover(blocked), "结束记录期间提前释放隔离。");
                    finishRecord.Set();
                    if (stop != null) try { await stop; } catch (InvalidOperationException) { }
                    try { await runtime.ClearanceCompletion; } catch (InvalidOperationException) { }
                    Ensure(runtime.IsClearing && runtime.IsPreparing && !device.Resources.Recover(blocked),
                        "记录期间中断却提交成功或释放隔离。");
                }
                else if (successScenario)
                {
                    axis.Complete();
                    await runtime.ClearanceCompletion;
                    Ensure(after == 1 && !runtime.IsClearing && runtime.State == MachineRunState.ResetRequired,
                        "成功清料没有正确结算，或错误标记为可生产。");
                    Ensure(!string.IsNullOrWhiteSpace(runtime.RecoveryBlockReason) && device.ReadResources().Count == 1,
                        "清料完成错误放开现场恢复或原隔离。");
                }
                else
                {
                    if (scenario == "急停")
                    {
                        safety.Active = true;
                        Ensure(SpinWait.SpinUntil(() => runtime.State == MachineRunState.Alarmed && axis.StopCalls > 0, 5000),
                            "急停没有触发清料受控停止。");
                    }
                    else
                    {
                        try { await runtime.PauseAsync(CancellationToken.None); }
                        catch (InvalidOperationException) { }
                    }
                    try { await runtime.ClearanceCompletion; } catch (InvalidOperationException) { }
                    Ensure(axis.StopCalls > 0 && after == 0 && runtime.IsClearing && runtime.IsPreparing,
                        "中断时未停止，或错误写入清料成功/释放占用。");
                    if (scenario == "取消重入") Ensure(reentrantCallbackFinished, "重入取消回调未完成。");
                    ExpectMessage(() => runtime.DisconnectAsync(CancellationToken.None).GetAwaiter().GetResult(), "不能断开");
                    Ensure(!device.Resources.Recover(blocked), "中断后原隔离被错误释放。");
                }
                if (!successScenario)
                {
                    Ensure(SpinWait.SpinUntil(() => runtime.IsClearanceStopSettled, 5000),
                        "清料停止、取消回调或原始设备调用仍未完成，不能开始核查断言。");
                    Ensure(runtime.IsClearing && !device.Resources.Recover(blocked), "停止结算后原隔离被释放。");
                    safety.Active = false;
                    if (testAlarm != null) runtime.AlarmService.ReportCondition(testAlarm, "测试", false, "报警条件已解除");
                    if (scenario == "取消回调失败")
                    {
                        ExpectMessage(() => runtime.ConfirmControlledClearanceRecoveryAsync("现场已核实", CancellationToken.None)
                            .GetAwaiter().GetResult(), "清料取消回调失败或被取消");
                        Ensure(runtime.IsClearing && inspections.Count == 0 && !device.Resources.Recover(blocked), "取消回调失败却解除了隔离。");
                        return;
                    }
                    io.InputValue = false;
                    ExpectMessage(() => runtime.ConfirmControlledClearanceRecoveryAsync("现场已核实", CancellationToken.None)
                        .GetAwaiter().GetResult(), "恢复输入不满足");
                    Ensure(runtime.IsClearing && inspections.Count == 0 && !device.Resources.Recover(blocked),
                        "核查条件失败却释放隔离或写入确认。");
                    io.InputValue = true;
                    if (scenario == "停止")
                    {
                        io.FlipAfterRead = true;
                        ExpectMessage(() => runtime.ConfirmControlledClearanceRecoveryAsync("现场已核实", CancellationToken.None)
                            .GetAwaiter().GetResult(), "恢复输入不满足");
                        io.FlipAfterRead = false;
                        io.InputValue = true;
                        await io.WriteOutputAsync("Vacuum", true, CancellationToken.None);
                        ExpectMessage(() => runtime.ConfirmControlledClearanceRecoveryAsync("现场已核实", CancellationToken.None)
                            .GetAwaiter().GetResult(), "恢复输出不满足");
                        await io.WriteOutputAsync("Vacuum", false, CancellationToken.None);
                    }
                    if (failInspection)
                    {
                        ExpectMessage(() => runtime.ConfirmControlledClearanceRecoveryAsync("现场已核实", CancellationToken.None)
                            .GetAwaiter().GetResult(), "核查记录写入失败");
                        Ensure(runtime.IsClearing && !device.Resources.Recover(blocked), "核查记录写入失败却释放隔离。");
                        failInspection = false;
                    }
                    if (changeDuringInspection)
                    {
                        ExpectMessage(() => runtime.ConfirmControlledClearanceRecoveryAsync("现场已核实", CancellationToken.None)
                            .GetAwaiter().GetResult(), "恢复输入不满足");
                        Ensure(runtime.IsClearing && !device.Resources.Recover(blocked), "记录后条件变化却释放隔离。");
                        io.InputValue = true;
                    }
                    await runtime.ConfirmControlledClearanceRecoveryAsync("现场已核实", CancellationToken.None);
                    Ensure(!runtime.IsClearing && !runtime.IsPreparing && runtime.State == MachineRunState.ResetRequired,
                        "有效核查后未结束清料独占。");
                    Ensure(inspections.Count == (scenario == "核查后变化" ? 2 : 1)
                        && inspections[0].Contains("现场已核实") && inspections[0].Contains("DI Sensor=True")
                        && inspections[0].Contains("DO Vacuum=")
                        && device.ReadResources().Count == 1 && !string.IsNullOrWhiteSpace(runtime.RecoveryBlockReason),
                        "恢复说明未记录或错误解除原隔离/恢复门禁。");
                }
                if (scenario.EndsWith("订阅异常")) Ensure(receivedStatus > 0, "异常订阅者阻断了后续状态通知。");
            }
            finally { Directory.Delete(root, true); }
        }

        /// <summary>设备端必须持权并持久化新增保护范围；失败不能释放原隔离。</summary>
        private static void MachineClearanceReservationChecks()
        {
            string root = Path.Combine(Path.GetTempPath(), "mcf-clearance-device-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var journal = new TaskResourceJournal(root, "M1");
                var device = CreateDevice(new FakeAxis("X"), new FakeLogicalIo(), out _, out _, journal: journal);
                var owner = new object();
                device.ClaimMachine(owner);
                var request = device.Resources.Enqueue("old", "T", "工位", "n", "动作", "e", new[] { "Axis:X" });
                Ensure(SpinWait.SpinUntil(() => device.Resources.TryAcquire(request, out _), 5000), "资源持久记录未完成。");
                device.Resources.Block(request, "待确认");
                IControlledClearanceReservation reservation = ((IControlledClearanceResources)device)
                    .ReserveClearance(new[] { "Output:Vacuum" });
                reservation.Ready.GetAwaiter().GetResult();
                Ensure(TaskResourceJournal.Inspect(root, "M1").Contains("Output:Vacuum"), "新增输出未持久化。");
                Ensure(!device.Resources.Recover(request) && device.Resources.IsReserved("Output:Vacuum"), "清料凭证未保持独占。");
                reservation.MarkUnconfirmed("输出反馈不明");
                ExpectMessage(() => reservation.Complete(), "待确认");
                Ensure(!device.Resources.Recover(request) && device.Resources.IsReserved("Output:Vacuum"), "失败后错误释放清料保护。");
            }
            finally { Directory.Delete(root, true); }
        }

        /// <summary>隔离记录不可省略或释放，清料动作未收尾时不可让出独占许可。</summary>
        private static void ClearanceLeaseRetainsIsolation()
        {
            var resources = new TaskResourceCoordinator(new[] { "Axis:X", "Output:夹紧", "Zone:旋转", "Axis:Y" });
            var axis = resources.Enqueue("old-1", "T", "工位一", "n1", "定位", "e1", new[] { "Axis:X", "Zone:旋转" });
            var output = resources.Enqueue("old-2", "T", "工位二", "n2", "夹紧", "e2", new[] { "Output:夹紧" });
            Ensure(resources.TryAcquire(axis, out _) && resources.TryAcquire(output, out _), "测试资源未取得。");
            resources.Block(axis, "轴状态待确认");
            resources.Block(output, "夹具状态待确认");
            ExpectMessage(() => resources.BeginClearance(new[] { axis.Id }, new[] { "Axis:X", "Zone:旋转" }), "未完整选择");
            ExpectMessage(() => resources.BeginClearance(new[] { axis.Id, output.Id }, new[] { "Axis:X", "Output:夹紧" }), "未包含");
            var lease = resources.BeginClearance(new[] { axis.Id, output.Id }, new[] { "Axis:X", "Output:夹紧", "Zone:旋转", "Axis:Y" });
            Ensure(resources.Read().Count(item => item.State == TaskResourceState.Blocked) == 2, "开始清料不应移除原隔离。");
            Ensure(resources.IsReserved("Axis:Y"), "清料新增声明资源未独占。");
            ExpectMessage(() => resources.BeginClearance(new[] { axis.Id, output.Id }, new[] { "Axis:X", "Output:夹紧", "Zone:旋转" }), "已有受控清料");
            ExpectMessage(() => resources.Enqueue("other", "T", "工位", "n", "动作", "e", new[] { "Axis:Y" }), "不能登记普通");
            Ensure(Enumerable.Range(0, 20).AsParallel().All(_ => !resources.Recover(axis)), "清料时并发恢复释放了原隔离。");
            ExpectMessage(() => resources.BeginClearanceAction(lease, null, "Axis:X"), "未登记");
            var sequence = resources.BeginClearanceSequence(lease);
            ExpectMessage(() => resources.EndClearance(lease), "序列尚未结束");
            Ensure(Enumerable.Range(0, 20).AsParallel().All(_ =>
            {
                try { resources.EndClearance(lease); return false; } catch (InvalidOperationException) { return true; }
            }), "动作间隙允许并发归还清料租约。");
            ExpectMessage(() => resources.BeginClearanceAction(lease, sequence, "Output:未声明"), "未声明");
            var action = resources.BeginClearanceAction(lease, sequence, "Axis:X");
            ExpectMessage(() => resources.BeginClearanceAction(lease, sequence, "axis:x"), "未结算动作");
            var otherAction = resources.BeginClearanceAction(lease, sequence, "Axis:Y");
            ExpectMessage(() => sequence.Dispose(), "仍在执行或收尾");
            ExpectMessage(() => resources.EndClearance(lease), "仍在执行或收尾");
            action.Dispose();
            action.Dispose();
            ExpectMessage(() => resources.EndClearance(lease), "仍在执行或收尾");
            otherAction.Dispose();
            ExpectMessage(() => resources.EndClearance(lease), "序列尚未结束");
            sequence.Dispose();
            resources.EndClearance(lease);
            Ensure(!resources.IsReserved("Axis:Y") && resources.Read().Count(item => item.State == TaskResourceState.Blocked) == 2,
                "清料结束应仅释放租约并保留原隔离。");
            ExpectMessage(() => resources.BeginClearanceAction(lease, sequence, "Axis:X"), "已经失效");
            Ensure(resources.Recover(axis) && resources.Recover(output), "结束清料后原有显式资源恢复不再可用。");

            // 有任何旧运行申请未收尾时，不允许取得清料许可。
            var held = resources.Enqueue("active", "T", "工位", "n", "动作", "e", new[] { "Axis:X" });
            Ensure(resources.TryAcquire(held, out _), "测试占用未取得。");
            ExpectMessage(() => resources.BeginClearance(new[] { "unknown" }, new[] { "Axis:X" }), "仍有资源");
            resources.Release(held);

            // 清料动作结果未知时，即便设备调用返回，额外资源也不能因归还凭证而放行。
            var uncertainRequest = resources.Enqueue("uncertain", "T", "工位", "n", "动作", "e", new[] { "Axis:X" });
            Ensure(resources.TryAcquire(uncertainRequest, out _), "测试资源未取得。");
            resources.Block(uncertainRequest, "旧动作待确认");
            var uncertainLease = resources.BeginClearance(new[] { uncertainRequest.Id }, new[] { "Axis:X", "Axis:Y" });
            using (var uncertainSequence = resources.BeginClearanceSequence(uncertainLease))
            using (resources.BeginClearanceAction(uncertainLease, uncertainSequence, "Axis:Y")) resources.MarkClearanceUnconfirmed(uncertainLease, "停止结果未知");
            ExpectMessage(() => resources.EndClearance(uncertainLease), "结果未确认");
            Ensure(resources.IsReserved("Axis:Y") && !resources.Recover(uncertainRequest), "不确定动作丢失额外资源隔离。");

            var failedJournal = new TaskResourceCoordinator(new[] { "Axis:X" });
            var failedRequest = failedJournal.Enqueue("old", "T", "工位", "n", "动作", "e", new[] { "Axis:X" });
            Ensure(failedJournal.TryAcquire(failedRequest, out _), "测试资源未取得。 ");
            failedJournal.Block(failedRequest, "旧动作待确认");
            var failedLease = failedJournal.BeginClearance(new[] { failedRequest.Id }, new[] { "Axis:X" });
            failedLease.JournalWrite = Task.FromException(new IOException("模拟资源记录写入失败"));
            var failedSequence = failedJournal.BeginClearanceSequence(failedLease);
            ExpectMessage(() => failedJournal.BeginClearanceAction(failedLease, failedSequence, "Axis:X"), "记录未确认保存");
            failedSequence.Dispose();
            ExpectMessage(() => failedJournal.EndClearance(failedLease), "记录未确认保存");
            Ensure(!failedJournal.Recover(failedRequest), "清料记录写入失败却释放了隔离。 ");

            // 正式设备的新增资源必须先写入独立证据，模拟重启仍能看到该资源。
            string root = Path.Combine(Path.GetTempPath(), "mcf-clearance-lease-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var journal = new TaskResourceJournal(root, "M1");
                var persisted = new TaskResourceCoordinator(new[] { "Axis:X", "Axis:Y" }, journal);
                var request = persisted.Enqueue("old", "T", "工位", "n", "动作", "e", new[] { "Axis:X" });
                Ensure(SpinWait.SpinUntil(() => persisted.TryAcquire(request, out _), 5000), "原资源记录未完成。 ");
                persisted.Block(request, "待确认");
                var persistedLease = persisted.BeginClearance(new[] { request.Id }, new[] { "Axis:X", "Axis:Y" });
                persistedLease.JournalWrite.GetAwaiter().GetResult();
                string[] guards = Directory.GetFiles(Path.Combine(root, "Runtime", "ResourceGuards"), "*.guard", SearchOption.AllDirectories);
                Ensure(guards.Any(file => File.ReadAllText(file).Contains("Axis:Y")), "清料新增资源没有留下重启证据。 ");
                string restartWarning = TaskResourceJournal.Inspect(root, "M1");
                Ensure(restartWarning.Contains("Axis:X") && restartWarning.Contains("Axis:Y"), "重启提示遗漏了清料新增资源。 ");
                string corrupt = Path.Combine(Path.GetDirectoryName(guards[0]), "zz-corrupt.guard");
                File.WriteAllText(corrupt, "{");
                bool rejected = false;
                try { TaskResourceJournal.Inspect(root, "M1"); } catch { rejected = true; }
                Ensure(rejected, "已有正常记录时错误忽略了另一条损坏证据。 ");
                File.Delete(corrupt);
                using (var persistedSequence = persisted.BeginClearanceSequence(persistedLease))
                using (persisted.BeginClearanceAction(persistedLease, persistedSequence, "Axis:Y")) { }
                persisted.EndClearance(persistedLease);
                Ensure(TaskResourceJournal.Inspect(root, "M1") != null, "清料结束错误清除了重启证据。 ");
                Ensure(persisted.Recover(request), "清料结束后原隔离未能核查解除。 ");
                persisted.SettleJournalWritesAsync().GetAwaiter().GetResult();
                journal.ClearSafeSession();
                Ensure(TaskResourceJournal.Inspect(root, "M1") == null, "整会话正常收尾后未能清理清料证据。 ");
            }
            finally { Directory.Delete(root, true); }
        }
    }
}
