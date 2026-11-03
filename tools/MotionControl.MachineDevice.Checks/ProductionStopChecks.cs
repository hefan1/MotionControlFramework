using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Runtime.Machine;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Machine;

namespace MotionControl.MachineDevice.Checks
{
    /// <summary>通过真实任务设备适配器检查 Runtime 停止入口，不连接控制器。</summary>
    internal static partial class Program
    {
        /// <summary>同步测试入口。</summary>
        private static void ProductionStopUsesTaskOwner()
        {
            ProductionStopUsesTaskOwnerAsync().GetAwaiter().GetResult();
        }

        /// <summary>运行中请求整机停止，验证任务确实停轴、保持输出，并显式核查后才能继续。</summary>
        private static async Task ProductionStopUsesTaskOwnerAsync()
        {
            var axis = new FakeAxis("X");
            var io = new FakeLogicalIo();
            var device = CreateDevice(axis, io, out _, out MachineRuntime runtime);
            var task = MoveTask(false);
            var configuration = new TaskMachineConfiguration { MachineId = "StopReview", Name = "停止检查",
                Stations = new List<StationTaskConfiguration> { new StationTaskConfiguration {
                    StationId = "S1", Name = "工位", TaskId = task.TaskId, Enabled = true, Mode = StationTaskMode.Once } } };
            runtime.UpdateControlInputNames(null, "Sensor", null);
            await runtime.ConnectAsync(CancellationToken.None);
            await Task.Delay(50);
            await runtime.ResetAsync(CancellationToken.None);
            var machine = new TaskMachineController(configuration, new[] { task }, device, runtime.AlarmService);
            await machine.ResetAsync();
            await io.WriteOutputAsync("Vacuum", true, CancellationToken.None);
            await runtime.BeginTaskEngineRunAsync(CancellationToken.None);
            machine.Start();
            runtime.ConfirmTaskEngineProductionStarted();
            await WaitForAsync(() => axis.Moving, "任务轴未开始运动。");
            await runtime.StopAutomaticProductionAsync(CancellationToken.None);
            Ensure(runtime.State == MachineRunState.Stopped && runtime.ProductionStopReviewRequired,
                "任务停止没有进入待核查状态。");
            Ensure(machine.GetSnapshot().State == TaskMachineState.Paused && !axis.Moving && axis.StopCalls > 0,
                "Runtime 状态已停止，但任务或轴没有真正响应停止。");
            Ensure(io.OutputValue && io.OutputWrites == 1, "停止时错误地改变保持输出。");
            ExpectMessage(() => runtime.ResetAsync(CancellationToken.None).GetAwaiter().GetResult(), "明确确认");
            await machine.ValidateResetPrerequisitesAsync();
            await runtime.ResetAsync(CancellationToken.None, true);
            await machine.ResetAsync();
            await runtime.BeginTaskEngineRunAsync(CancellationToken.None);
            machine.Start();
            runtime.ConfirmTaskEngineProductionStarted();
            await WaitForAsync(() => axis.Moving, "明确核查后旧工步未继续。");
            io.InputValue = true;
            await WaitForAsync(() => runtime.State == MachineRunState.Paused, "现场暂停输入没有暂停任务执行器。");
            Ensure(machine.GetSnapshot().State == TaskMachineState.Paused && !axis.Moving
                && !runtime.ProductionStopReviewRequired, "现场暂停输入没有停轴，或错误触发停止核查。");
            io.InputValue = false;
            await machine.ValidateResetPrerequisitesAsync();
            await runtime.ResetAsync(CancellationToken.None);
            await machine.ResetAsync();
            await runtime.BeginTaskEngineRunAsync(CancellationToken.None);
            machine.Start();
            runtime.ConfirmTaskEngineProductionStarted();
            await WaitForAsync(() => axis.Moving, "现场暂停复位后未继续任务。");
            axis.Complete();
            await WaitForAsync(() => machine.GetSnapshot().Stations[0].CompletedCycles == 1, "恢复后的步骤没有完成。");
            await runtime.StopAutomaticProductionAsync(CancellationToken.None);
            await machine.CloseAsync();
            await runtime.DisconnectAsync(CancellationToken.None);
        }

        /// <summary>同步执行任务轴停止故障注入。</summary>
        private static void ProductionStopFailureIsNotConfirmed()
        {
            ProductionStopFailureIsNotConfirmedAsync().GetAwaiter().GetResult();
        }

        /// <summary>任务轴停止失败时不发布已停止，保留故障及旧任务保护。</summary>
        private static async Task ProductionStopFailureIsNotConfirmedAsync()
        {
            var axis = new FakeAxis("X") { StopError = new InvalidOperationException("停止命令失败") };
            var device = CreateDevice(axis, new FakeLogicalIo(), out _, out MachineRuntime runtime);
            var task = MoveTask(false);
            var configuration = new TaskMachineConfiguration { MachineId = "StopFailure", Name = "停止失败检查",
                Stations = new List<StationTaskConfiguration> { new StationTaskConfiguration {
                    StationId = "S1", Name = "工位", TaskId = task.TaskId, Enabled = true, Mode = StationTaskMode.Once } } };
            await runtime.ConnectAsync(CancellationToken.None);
            await runtime.ResetAsync(CancellationToken.None);
            var machine = new TaskMachineController(configuration, new[] { task }, device, runtime.AlarmService);
            await machine.ResetAsync();
            await runtime.BeginTaskEngineRunAsync(CancellationToken.None);
            machine.Start();
            runtime.ConfirmTaskEngineProductionStarted();
            await WaitForAsync(() => axis.Moving, "故障检查轴未开始运动。");
            bool rejected = false;
            try { await runtime.StopAutomaticProductionAsync(CancellationToken.None); }
            catch (InvalidOperationException) { rejected = true; }
            Ensure(rejected && runtime.State == MachineRunState.Alarmed && runtime.ProductionStopReviewRequired,
                "轴停止失败被错误地报告为已停止或清除了核查要求。");
            Ensure(machine.GetSnapshot().State == TaskMachineState.Faulted && axis.StopCalls > 0,
                "任务执行器没有锁存停止故障。");
            axis.StopError = null;
            await axis.StopAsync(AxisStopMode.Controlled, CancellationToken.None);
            await machine.AbortAsync();
        }
    }
}
