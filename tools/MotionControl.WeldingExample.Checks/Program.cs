using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Equipment.Demo.WeldingExample;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Framework.Contracts.Configuration;
using MotionControl.Framework.Contracts.Equipment;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.IO;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Runtime.Alarms;

namespace MotionControl.WeldingExample.Checks
{
    /// <summary>验证代码工位的并发载具、下游堵塞及暂停后的续跑。</summary>
    internal static class Program
    {
        /// <summary>以同步入口等待异步检查并返回进程状态。</summary>
        private static int Main()
        {
            try { RunAsync().GetAwaiter().GetResult(); CheckStaleFeedbackAsync().GetAwaiter().GetResult(); CheckFeedbackDuringMotionAsync().GetAwaiter().GetResult(); CheckClampConflictAsync().GetAwaiter().GetResult(); CheckLoadMotionClampRecheckAsync().GetAwaiter().GetResult(); CheckLoadReturnClampRecheckAsync().GetAwaiter().GetResult(); CheckCameraHandshakeAsync().GetAwaiter().GetResult(); CheckCameraConflictAsync().GetAwaiter().GetResult(); CheckCameraPauseAsync().GetAwaiter().GetResult(); Task.WhenAll(CheckCameraTimeoutRecoveryAsync(), CheckClampTimeoutRecoveryAsync()).GetAwaiter().GetResult(); PreparationChecks.RunAsync().GetAwaiter().GetResult(); Console.WriteLine("代码工位并行、堵塞、暂停恢复、夹具冲突、相机结果及设备准备检查通过；仅内部检查。"); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }

        /// <summary>模拟复检反馈堵塞、核对多载具及上游等待，解除后继续交接。</summary>
        private static async Task RunAsync()
        {
            var context = new FakeContext();
            var flows = new WeldingExampleEquipmentDefinition().CreateStationFlows(context).ToArray();
            using (var cancel = new CancellationTokenSource())
            {
                Task[] workers = flows.Select(flow => RunStationAsync(flow, cancel.Token)).ToArray();
                await WaitForAsync(() => flows[3].CurrentStepName.Contains("等待检测完成")
                    && flows[1].CurrentStepName.Contains("等待下游空位"), TimeSpan.FromSeconds(12));
                string[] occupied = flows.Select(flow => flow.CurrentStepName).Where(text => text.Contains("测试产品")).ToArray();
                if (occupied.Length < 3 || occupied.Select(text => text.Substring(text.IndexOf("测试产品", StringComparison.Ordinal))).Distinct().Count() != occupied.Length)
                    throw new InvalidOperationException("堵塞时没有观察到多个独立载具：" + string.Join(";", occupied));
                cancel.Cancel();
                try { await Task.WhenAll(workers); } catch (OperationCanceledException) { }
                string[] paused = flows.Select(flow => flow.CurrentStepName).ToArray();
                await Task.Delay(200);
                if (!paused.SequenceEqual(flows.Select(flow => flow.CurrentStepName)))
                    throw new InvalidOperationException("暂停后工位或载具状态仍然变化。");
            }
            context.Io.BlockInspection = false;
            using (var cancel = new CancellationTokenSource())
            {
                Task[] workers = flows.Select(flow => RunStationAsync(flow, cancel.Token)).ToArray();
                await WaitForAsync(() => ((IStationCycleStatistics)flows[3]).CompletedCycles >= 3, TimeSpan.FromSeconds(12));
                cancel.Cancel();
                try { await Task.WhenAll(workers); } catch (OperationCanceledException) { }
                if (context.Io.SimultaneousMoves < 2) throw new InvalidOperationException("没有观察到工位轴动作并行。");
            }
        }

        /// <summary>完成输入已卡在有效时禁止发出动作；信号复归后才能重新启动该工位。</summary>
        private static async Task CheckStaleFeedbackAsync()
        {
            var context = new FakeContext();
            context.Io.BlockInspection = false;
            context.Io.ForcedHighInput = "焊接完成";
            var flows = new WeldingExampleEquipmentDefinition().CreateStationFlows(context).ToArray();
            using (var cancel = new CancellationTokenSource())
            {
                Task[] workers = flows.Select(flow => RunStationAsync(flow, cancel.Token)).ToArray();
                await WaitForAsync(() => flows[1].CurrentStepName.Contains("等待动作前反馈复位"), TimeSpan.FromSeconds(8));
                await Task.Delay(250);
                if (await context.Io.ReadOutputAsync("焊接启动", CancellationToken.None))
                    throw new InvalidOperationException("旧完成反馈有效时仍发出了焊接动作。");
                if (context.Io.WeldAxisMoves != 0)
                    throw new InvalidOperationException("旧完成反馈有效时仍移动了焊接轴。");
                if (!flows[1].CurrentStepName.Contains("等待动作前反馈复位"))
                    throw new InvalidOperationException("旧完成反馈有效时错误进入下一步骤。");
                context.Io.ForcedHighInput = null;
                await WaitForAsync(() => ((IStationCycleStatistics)flows[1]).CompletedCycles >= 1, TimeSpan.FromSeconds(8));
                cancel.Cancel();
                try { await Task.WhenAll(workers); } catch (OperationCanceledException) { }
            }
        }

        /// <summary>轴运动期间完成信号异常有效时，不触发输出；反馈复归后继续。</summary>
        private static async Task CheckFeedbackDuringMotionAsync()
        {
            var context = new FakeContext();
            context.Io.BlockInspection = false;
            context.Io.RaiseWeldFeedbackOnMove = true;
            var flows = new WeldingExampleEquipmentDefinition().CreateStationFlows(context).ToArray();
            using (var cancel = new CancellationTokenSource())
            {
                Task[] workers = flows.Select(flow => RunStationAsync(flow, cancel.Token)).ToArray();
                await WaitForAsync(() => flows[1].CurrentStepName.Contains("动作前再次确认反馈复位"), TimeSpan.FromSeconds(8));
                await Task.Delay(250);
                if (await context.Io.ReadOutputAsync("焊接启动", CancellationToken.None))
                    throw new InvalidOperationException("运动期间反馈异常变有效后仍发出了焊接动作。");
                if (context.Io.WeldAxisMoves != 1 || !flows[1].CurrentStepName.Contains("动作前再次确认反馈复位"))
                    throw new InvalidOperationException("反馈异常时工位错误推进或重复移动。");
                context.Io.RaiseWeldFeedbackOnMove = false;
                context.Io.ForcedHighInput = null;
                await WaitForAsync(() => ((IStationCycleStatistics)flows[1]).CompletedCycles >= 1, TimeSpan.FromSeconds(8));
                cancel.Cancel();
                try { await Task.WhenAll(workers); } catch (OperationCanceledException) { }
            }
        }

        /// <summary>夹具两个到位信号同时有效时拒绝移动和夹紧。</summary>
        private static async Task CheckClampConflictAsync()
        {
            var context = new FakeContext();
            context.Io.ForceClampConflict = true;
            var flow = new WeldingExampleEquipmentDefinition().CreateStationFlows(context)[0];
            await flow.ExecuteCurrentStepAsync(CancellationToken.None);
            try { await flow.ExecuteCurrentStepAsync(CancellationToken.None); throw new InvalidOperationException("夹具冲突未拒绝。"); }
            catch (InvalidOperationException error)
            {
                if (!error.Message.Contains("同时有效")) throw;
            }
            if (context.Io.TotalMoves != 0 || await context.Io.ReadOutputAsync("夹具夹紧", CancellationToken.None))
                throw new InvalidOperationException("夹具反馈冲突时仍移动或夹紧。");
        }

        /// <summary>确认松开后若轴运动前发生冲突、输出异常或到位丢失，禁止运动并要求重新稳定采样。</summary>
        private static async Task CheckLoadMotionClampRecheckAsync()
        {
            var context = new FakeContext();
            var flow = new WeldingExampleEquipmentDefinition().CreateStationFlows(context)[0];
            await flow.ExecuteCurrentStepAsync(CancellationToken.None);
            await flow.ExecuteCurrentStepAsync(CancellationToken.None);
            await flow.ExecuteCurrentStepAsync(CancellationToken.None);
            if (!flow.CurrentStepName.Contains("轴到工作位")) throw new InvalidOperationException("没有进入上料轴运动前检查。");
            context.Io.ForceClampConflict = true;
            try { await flow.ExecuteCurrentStepAsync(CancellationToken.None); throw new InvalidOperationException("运动前夹具冲突未拒绝。"); }
            catch (InvalidOperationException error) { if (!error.Message.Contains("同时有效")) throw; }
            context.Io.ForceClampConflict = false;
            context.Io.ForceClampOutputMismatch = true;
            try { await flow.ExecuteCurrentStepAsync(CancellationToken.None); throw new InvalidOperationException("运动前夹具输出异常未拒绝。"); }
            catch (InvalidOperationException error) { if (!error.Message.Contains("输出状态")) throw; }
            context.Io.ForceClampOutputMismatch = false;
            context.Io.ForceClampNoFeedback = true;
            await flow.ExecuteCurrentStepAsync(CancellationToken.None);
            await flow.ExecuteCurrentStepAsync(CancellationToken.None);
            if (context.Io.TotalMoves != 0) throw new InvalidOperationException("夹具到位丢失时上料轴仍运动。");
            context.Io.ForceClampNoFeedback = false;
            await flow.ExecuteCurrentStepAsync(CancellationToken.None);
            if (context.Io.TotalMoves != 0) throw new InvalidOperationException("只有一次夹具到位采样就允许运动。");
            await flow.ExecuteCurrentStepAsync(CancellationToken.None);
            if (context.Io.TotalMoves != 1) throw new InvalidOperationException("夹具稳定松开后未执行上料轴运动。");
        }

        /// <summary>上料释放后等待无料期间若夹具状态改变，返回轴动作必须再次等待稳定松开。</summary>
        private static async Task CheckLoadReturnClampRecheckAsync()
        {
            var context = new FakeContext();
            context.Io.HoldFeedEmpty = true;
            var flow = new WeldingExampleEquipmentDefinition().CreateStationFlows(context)[0];
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (!flow.CurrentStepName.Contains("轴返回等待位"))
            {
                if (DateTime.UtcNow >= deadline) throw new TimeoutException("上料没有到达返回前检查。");
                await flow.ExecuteCurrentStepAsync(CancellationToken.None);
            }
            if (context.Io.TotalMoves != 1) throw new InvalidOperationException("上料轴不在返回前的预期位置。");
            context.Io.ForceClampConflict = true;
            try { await flow.ExecuteCurrentStepAsync(CancellationToken.None); throw new InvalidOperationException("返回前夹具冲突未拒绝。"); }
            catch (InvalidOperationException error) { if (!error.Message.Contains("同时有效")) throw; }
            context.Io.ForceClampConflict = false;
            context.Io.ForceClampOutputMismatch = true;
            try { await flow.ExecuteCurrentStepAsync(CancellationToken.None); throw new InvalidOperationException("返回前夹具输出异常未拒绝。"); }
            catch (InvalidOperationException error) { if (!error.Message.Contains("输出状态")) throw; }
            context.Io.ForceClampOutputMismatch = false;
            context.Io.ForceClampNoFeedback = true;
            await flow.ExecuteCurrentStepAsync(CancellationToken.None);
            if (context.Io.TotalMoves != 1) throw new InvalidOperationException("夹具到位丢失时上料轴仍返回。");
            context.Io.ForceClampNoFeedback = false;
            await flow.ExecuteCurrentStepAsync(CancellationToken.None);
            if (context.Io.TotalMoves != 1) throw new InvalidOperationException("仅一次松开到位采样就允许上料轴返回。");
            context.Io.ForceFeedHigh = true;
            await flow.ExecuteCurrentStepAsync(CancellationToken.None);
            if (context.Io.TotalMoves != 1) throw new InvalidOperationException("上料信号重新有效时轴仍返回。");
            context.Io.ForceFeedHigh = false;
            await flow.ExecuteCurrentStepAsync(CancellationToken.None);
            if (context.Io.TotalMoves != 2) throw new InvalidOperationException("无料且夹具稳定松开后未执行返回。");
        }

        /// <summary>相机完成但没有结果时不复位或下料，获得真实结果后才能继续。</summary>
        private static async Task CheckCameraHandshakeAsync()
        {
            var context = new FakeContext();
            context.Io.BlockInspection = false;
            context.Io.SuppressCameraResult = true;
            var flows = new WeldingExampleEquipmentDefinition().CreateStationFlows(context).ToArray();
            using (var cancel = new CancellationTokenSource())
            {
                Task[] workers = flows.Select(flow => RunStationAsync(flow, cancel.Token)).ToArray();
                try
                {
                    await WaitForAsync(() => flows[3].CurrentStepName.Contains("读取检测结果"), TimeSpan.FromSeconds(10));
                    if (await context.Io.ReadOutputAsync("相机结果复位", CancellationToken.None)
                        || await context.Io.ReadOutputAsync("下料电磁阀", CancellationToken.None))
                        throw new InvalidOperationException("未取得检测结果时错误复位或下料。");
                    context.Io.FlapCameraResult = true;
                    context.Io.SuppressCameraResult = false;
                    await Task.Delay(550);
                    if (await context.Io.ReadOutputAsync("相机结果复位", CancellationToken.None)
                        || await context.Io.ReadOutputAsync("下料电磁阀", CancellationToken.None))
                        throw new InvalidOperationException("检测结果连续变化时错误复位或下料。");
                    context.Io.FlapCameraResult = false;
                    await WaitForAsync(() => ((IStationCycleStatistics)flows[3]).CompletedCycles >= 1, TimeSpan.FromSeconds(10));
                    if (!flows[3].CurrentStepName.Contains("上次检测 OK") && !flows[3].CurrentStepName.Contains("检测OK"))
                        throw new InvalidOperationException("未显示相机返回的 OK 结果。");
                }
                finally { cancel.Cancel(); try { await Task.WhenAll(workers); } catch (OperationCanceledException) { } }
            }
        }

        /// <summary>相机同时返回 OK 和 NG 时不得复位结果或下料。</summary>
        private static async Task CheckCameraConflictAsync()
        {
            var context = new FakeContext();
            context.Io.BlockInspection = false;
            context.Io.ForceCameraConflict = true;
            var flows = new WeldingExampleEquipmentDefinition().CreateStationFlows(context).ToArray();
            using (var cancel = new CancellationTokenSource())
            {
                Task[] workers = flows.Select(flow => RunStationAsync(flow, cancel.Token)).ToArray();
                try
                {
                    await WaitForAsync(() => workers[3].IsFaulted, TimeSpan.FromSeconds(10));
                    if (!workers[3].Exception.ToString().Contains("同时有效")) throw new InvalidOperationException("未报告相机结果冲突。");
                    if (await context.Io.ReadOutputAsync("相机结果复位", CancellationToken.None)
                        || await context.Io.ReadOutputAsync("下料电磁阀", CancellationToken.None))
                        throw new InvalidOperationException("相机结果冲突时错误复位或下料。");
                }
                finally
                {
                    cancel.Cancel();
                    foreach (Task worker in workers) try { await worker; } catch (OperationCanceledException) { } catch (InvalidOperationException) { }
                }
            }
        }

        /// <summary>等待相机触发反馈时暂停保留输出和步骤，恢复后不重复发送触发。</summary>
        private static async Task CheckCameraPauseAsync()
        {
            var context = new FakeContext();
            context.Io.BlockInspection = false;
            context.Io.BlockCameraAck = true;
            var flows = new WeldingExampleEquipmentDefinition().CreateStationFlows(context).ToArray();
            using (var cancel = new CancellationTokenSource())
            {
                Task[] workers = flows.Select(flow => RunStationAsync(flow, cancel.Token)).ToArray();
                try
                {
                    await WaitForAsync(() => flows[3].CurrentStepName.Contains("等待动作完成反馈")
                        && context.Io.CameraTriggers == 1, TimeSpan.FromSeconds(10));
                }
                finally { cancel.Cancel(); try { await Task.WhenAll(workers); } catch (OperationCanceledException) { } }
            }
            if (!await context.Io.ReadOutputAsync("相机触发", CancellationToken.None))
                throw new InvalidOperationException("暂停时未保留相机触发状态。");
            context.Io.BlockCameraAck = false;
            using (var cancel = new CancellationTokenSource())
            {
                Task[] workers = flows.Select(flow => RunStationAsync(flow, cancel.Token)).ToArray();
                try
                {
                    await WaitForAsync(() => ((IStationCycleStatistics)flows[3]).CompletedCycles >= 1, TimeSpan.FromSeconds(10));
                    if (context.Io.CameraTriggers != 1) throw new InvalidOperationException("恢复前一周期时重复触发了相机。");
                }
                finally { cancel.Cancel(); try { await Task.WhenAll(workers); } catch (OperationCanceledException) { } }
            }
        }

        /// <summary>相机处理超时后解除故障；复位必须清除旧处理并重新触发后才放行载具。</summary>
        private static async Task CheckCameraTimeoutRecoveryAsync()
        {
            var context = new FakeContext();
            context.Io.BlockInspection = true;
            context.Io.RequireCameraResetAfterFault = true;
            var flows = new WeldingExampleEquipmentDefinition().CreateStationFlows(context).ToArray();
            string carrier;
            using (var cancel = new CancellationTokenSource())
            {
                Task[] workers = flows.Select(flow => RunStationAsync(flow, cancel.Token)).ToArray();
                try
                {
                    await WaitForAsync(() => flows[3].CurrentStepName.Contains("等待检测完成"), TimeSpan.FromSeconds(10));
                    carrier = flows[3].CurrentStepName.Substring(flows[3].CurrentStepName.IndexOf("测试产品", StringComparison.Ordinal));
                    await WaitForAsync(() => context.AlarmService.HasLatchedStopAlarm, TimeSpan.FromSeconds(35));
                }
                finally { cancel.Cancel(); try { await Task.WhenAll(workers); } catch (OperationCanceledException) { } }
            }
            if (!flows[3].CurrentStepName.Contains(carrier)) throw new InvalidOperationException("相机超时丢失载具信息。");
            context.Io.BlockInspection = false;
            context.AlarmService.ResetStopAlarms();
            foreach (IStationFlow flow in flows) await flow.ResetAsync(CancellationToken.None);
            using (var cancel = new CancellationTokenSource())
            {
                Task[] workers = flows.Select(flow => RunStationAsync(flow, cancel.Token)).ToArray();
                try
                {
                    await WaitForAsync(() => ((IStationCycleStatistics)flows[3]).CompletedCycles >= 1, TimeSpan.FromSeconds(10));
                    if (context.Io.CameraTriggers < 2 || context.Io.CameraResets < 2)
                        throw new InvalidOperationException("相机超时后未复位、重新触发并在完成后清除结果。");
                }
                finally { cancel.Cancel(); try { await Task.WhenAll(workers); } catch (OperationCanceledException) { } }
            }
        }

        /// <summary>夹具卡滞超时后解除故障，复位续跑应等待到位而不是重复夹紧。</summary>
        private static async Task CheckClampTimeoutRecoveryAsync()
        {
            var context = new FakeContext();
            context.Io.BlockInspection = false;
            context.Io.BlockClamp = true;
            var flows = new WeldingExampleEquipmentDefinition().CreateStationFlows(context).ToArray();
            using (var cancel = new CancellationTokenSource())
            {
                Task[] workers = flows.Select(flow => RunStationAsync(flow, cancel.Token)).ToArray();
                try
                {
                    await WaitForAsync(() => flows[0].CurrentStepName.Contains("等待夹具夹紧到位"), TimeSpan.FromSeconds(10));
                    await WaitForAsync(() => context.AlarmService.HasLatchedStopAlarm, TimeSpan.FromSeconds(35));
                }
                finally { cancel.Cancel(); try { await Task.WhenAll(workers); } catch (OperationCanceledException) { } }
            }
            if (!await context.Io.ReadOutputAsync("夹具夹紧", CancellationToken.None)) throw new InvalidOperationException("夹具故障时输出状态丢失。");
            context.Io.BlockClamp = false;
            context.AlarmService.ResetStopAlarms();
            foreach (IStationFlow flow in flows) await flow.ResetAsync(CancellationToken.None);
            using (var cancel = new CancellationTokenSource())
            {
                Task[] workers = flows.Select(flow => RunStationAsync(flow, cancel.Token)).ToArray();
                try { await WaitForAsync(() => ((IStationCycleStatistics)flows[0]).CompletedCycles >= 1, TimeSpan.FromSeconds(10)); }
                finally { cancel.Cancel(); try { await Task.WhenAll(workers); } catch (OperationCanceledException) { } }
            }
        }

        /// <summary>模拟调度器反复执行当前步骤。</summary>
        private static async Task RunStationAsync(IStationFlow flow, CancellationToken token)
        {
            while (true) { token.ThrowIfCancellationRequested(); await flow.ExecuteCurrentStepAsync(token); }
        }

        /// <summary>在限定时间内等待条件，超时明确失败。</summary>
        private static async Task WaitForAsync(Func<bool> predicate, TimeSpan timeout)
        {
            DateTime end = DateTime.UtcNow + timeout;
            while (!predicate()) { if (DateTime.UtcNow >= end) throw new TimeoutException("等待测试状态超时。"); await Task.Delay(25); }
        }

        /// <summary>为设备定义提供独立的测试轴、IO 和报警服务。</summary>
        private sealed class FakeContext : IEquipmentContext
        {
            /// <summary>模拟逻辑 IO 与轴动作计数。</summary>
            internal readonly FakeIo Io = new FakeIo();
            /// <summary>当前测试设备标识。</summary>
            public string EquipmentId { get { return "test"; } }
            /// <summary>五个测试轴及 0 到 1 的允许位置。</summary>
            public IReadOnlyList<AxisHardwareDefinition> AxisDefinitions { get { return new[] { "Load_X", "Weld_Z", "Press_Z", "Camera_X", "Unload_X" }.Select(name => new AxisHardwareDefinition { Name = name, MinimumPosition = 0, MaximumPosition = 1 }).ToArray(); } }
            /// <summary>样例使用的测试输入名称。</summary>
            public IReadOnlyList<DigitalIoHardwareDefinition> InputDefinitions { get { return new[] { "上料到位", "夹具夹紧到位", "夹具松开到位", "焊接完成", "压装完成", "相机触发反馈", "检测完成", "检测OK", "检测NG", "下料到位" }.Select(name => new DigitalIoHardwareDefinition { Name = name }).ToArray(); } }
            /// <summary>样例使用的测试输出名称。</summary>
            public IReadOnlyList<DigitalIoHardwareDefinition> OutputDefinitions { get { return new[] { "夹具夹紧", "焊接启动", "压头动作", "相机触发", "相机结果复位", "下料电磁阀" }.Select(name => new DigitalIoHardwareDefinition { Name = name }).ToArray(); } }
            /// <summary>测试 IO 实例。</summary>
            public ILogicalIo LogicalIo { get { return Io; } }
            /// <summary>测试报警服务。</summary>
            public IMachineAlarmService AlarmService { get; } = new MachineAlarmService();
            /// <summary>无文件依赖。</summary>
            public bool FileExists(string path) { return path == "Parameters/ControllerBackend.json"; }
            /// <summary>只提供软件控制器配置。</summary>
            public T LoadJson<T>(string path) where T : class { return new WeldingExampleEquipmentDefinition.WeldingTestBackend { Backend = "Virtual" } as T; }
            /// <summary>返回测试停机报警。</summary>
            public MachineAlarmDefinition ResolveAlarm(string code) { return new MachineAlarmDefinition(code, "测试超时", MachineAlarmKind.StopMachine); }
            /// <summary>创建指定逻辑轴。</summary>
            public IAxis GetAxis(string name) { return new FakeAxis(name, Io); }
        }

        /// <summary>只模拟步骤需要的绝对动作，追踪轴动作并发。</summary>
        private sealed class FakeAxis : IAxis
        {
            /// <summary>共享测试状态。</summary>
            private readonly FakeIo _state;
            /// <summary>绑定逻辑名称和共享状态。</summary>
            internal FakeAxis(string name, FakeIo state) { Name = name; _state = state; }
            /// <summary>逻辑轴名称。</summary>
            public string Name { get; private set; }
            /// <summary>等待一小段时间并记录并行数量。</summary>
            public async Task MoveAbsoluteAsync(double position, AxisMotionProfile profile, CancellationToken token)
            { Interlocked.Increment(ref _state.TotalMoves); if (Name == "Weld_Z") Interlocked.Increment(ref _state.WeldAxisMoves); int now = Interlocked.Increment(ref _state.Moving); if (now > _state.SimultaneousMoves) _state.SimultaneousMoves = now; try { await Task.Delay(80, token); if (Name == "Weld_Z" && position == 1 && _state.RaiseWeldFeedbackOnMove) _state.ForcedHighInput = "焊接完成"; } finally { Interlocked.Decrement(ref _state.Moving); } }
            /// <summary>本测试不读取轴状态。</summary>
            public Task<AxisStatus> ReadStatusAsync(CancellationToken token) { throw new NotSupportedException(); }
            /// <summary>本测试不使能轴。</summary>
            public Task EnableServoAsync(CancellationToken token) { throw new NotSupportedException(); }
            /// <summary>本测试不关闭伺服。</summary>
            public Task DisableServoAsync(CancellationToken token) { throw new NotSupportedException(); }
            /// <summary>本测试不回零。</summary>
            public Task HomeAsync(CancellationToken token) { throw new NotSupportedException(); }
            /// <summary>本测试不相对移动。</summary>
            public Task MoveRelativeAsync(double distance, AxisMotionProfile profile, CancellationToken token) { throw new NotSupportedException(); }
            /// <summary>本测试不点动。</summary>
            public Task StartJogAsync(MotionDirection direction, AxisMotionProfile profile, CancellationToken token) { throw new NotSupportedException(); }
            /// <summary>本测试不直接停止轴。</summary>
            public Task StopAsync(AxisStopMode mode, CancellationToken token) { throw new NotSupportedException(); }
            /// <summary>本测试不复位轴报警。</summary>
            public Task ResetAlarmAsync(CancellationToken token) { throw new NotSupportedException(); }
        }

        /// <summary>用输出电平提供独立反馈，复检反馈可阻塞。</summary>
        private sealed class FakeIo : ILogicalIo
        {
            /// <summary>保护输出值。</summary>
            private readonly object _sync = new object();
            /// <summary>当前输出电平。</summary>
            private readonly Dictionary<string, bool> _outputs = new Dictionary<string, bool>();
            /// <summary>供料输入与夹具已夹住状态。</summary>
            private bool _feed = true, _gripped;
            /// <summary>相机请求与结果锁存状态，由复位输出清除。</summary>
            private bool _cameraPending, _cameraDone;
            /// <summary>相机故障造成旧处理必须复位后才能再完成。</summary>
            private bool _cameraNeedsReset;
            /// <summary>当前抖动周期模拟的 OK 结果。</summary>
            private bool _flappingOk;
            /// <summary>复检反馈是否保持无效。</summary>
            internal volatile bool BlockInspection = true;
            /// <summary>指定输入卡在有效，用于验证动作前不接受旧完成信号。</summary>
            internal volatile string ForcedHighInput;
            /// <summary>在焊接轴到达工作位时模拟反馈异常变为有效。</summary>
            internal volatile bool RaiseWeldFeedbackOnMove;
            /// <summary>注入夹紧和松开到位同时有效的接线故障。</summary>
            internal volatile bool ForceClampConflict;
            /// <summary>模拟夹具两路到位都丢失。</summary>
            internal volatile bool ForceClampNoFeedback;
            /// <summary>模拟读取到的夹具输出与步骤目标不一致。</summary>
            internal volatile bool ForceClampOutputMismatch;
            /// <summary>保持供料位置无料，便于检查返回条件。</summary>
            internal volatile bool HoldFeedEmpty;
            /// <summary>强制上料到位重新有效，验证返回前复查。</summary>
            internal volatile bool ForceFeedHigh;
            /// <summary>相机完成但暂不提供任何结果，用于检查不得提前下料。</summary>
            internal volatile bool SuppressCameraResult;
            /// <summary>模拟相机同时返回两个互斥结果。</summary>
            internal volatile bool ForceCameraConflict;
            /// <summary>使相机结果在 OK 和 NG 之间交替，验证连续稳定采样。</summary>
            internal volatile bool FlapCameraResult;
            /// <summary>模拟相机没有确认触发。</summary>
            internal volatile bool BlockCameraAck;
            /// <summary>模拟夹具动作卡滞，两端都不到位。</summary>
            internal volatile bool BlockClamp;
            /// <summary>模拟相机故障解除后必须先复位旧处理。</summary>
            internal volatile bool RequireCameraResetAfterFault;
            /// <summary>相机触发输出上升沿次数。</summary>
            internal volatile int CameraTriggers;
            /// <summary>相机复位输出上升沿次数。</summary>
            internal volatile int CameraResets;
            /// <summary>所有轴接受的动作次数。</summary>
            internal int TotalMoves;
            /// <summary>当前并发轴动作数。</summary>
            internal int Moving;
            /// <summary>焊接轴已接收的测试运动次数。</summary>
            internal int WeldAxisMoves;
            /// <summary>观测到的最大并发轴动作数。</summary>
            internal int SimultaneousMoves;
            /// <summary>读取与输出对应的输入；扫码上料信号在夹具关闭时有效。</summary>
            public Task<bool> ReadInputAsync(string name, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                lock (_sync)
                {
                    if (name == "上料到位") return Task.FromResult(_feed || ForceFeedHigh);
                    if (name == ForcedHighInput) return Task.FromResult(true);
                    if (ForceClampConflict && (name == "夹具夹紧到位" || name == "夹具松开到位")) return Task.FromResult(true);
                    if (ForceClampNoFeedback && (name == "夹具夹紧到位" || name == "夹具松开到位")) return Task.FromResult(false);
                    bool active = _outputs.ContainsKey("夹具夹紧") && _outputs["夹具夹紧"];
                    if (BlockClamp && active && (name == "夹具夹紧到位" || name == "夹具松开到位")) return Task.FromResult(false);
                    if (name == "夹具夹紧到位") return Task.FromResult(active);
                    if (name == "夹具松开到位") return Task.FromResult(!active);
                    if (name == "相机触发反馈") return Task.FromResult(!BlockCameraAck && _outputs.ContainsKey("相机触发") && _outputs["相机触发"]);
                    if (_cameraPending && !BlockInspection && !_cameraNeedsReset) _cameraDone = true;
                    if (name == "检测完成") return Task.FromResult(_cameraDone);
                    if (name == "检测OK")
                    {
                        if (_cameraDone && FlapCameraResult) _flappingOk = !_flappingOk;
                        return Task.FromResult(_cameraDone && !SuppressCameraResult && (!FlapCameraResult || _flappingOk));
                    }
                    if (name == "检测NG") return Task.FromResult(_cameraDone && (ForceCameraConflict || (FlapCameraResult && !_flappingOk)));
                    string output = name == "焊接完成" ? "焊接启动" : name == "压装完成" ? "压头动作" : "下料电磁阀";
                    return Task.FromResult(_outputs.ContainsKey(output) && _outputs[output]);
                }
            }
            /// <summary>返回当前输出。</summary>
            public Task<bool> ReadOutputAsync(string name, CancellationToken token) { lock (_sync) return Task.FromResult(name == "夹具夹紧" && ForceClampOutputMismatch || _outputs.ContainsKey(name) && _outputs[name]); }
            /// <summary>设置当前输出。</summary>
            public Task WriteOutputAsync(string name, bool value, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                lock (_sync)
                {
                    bool previous = _outputs.ContainsKey(name) && _outputs[name];
                    _outputs[name] = value;
                    if (name == "相机触发" && value && !previous) { _cameraPending = true; if (RequireCameraResetAfterFault) _cameraNeedsReset = true; CameraTriggers++; }
                    if (name == "相机结果复位" && value) { _cameraPending = false; _cameraDone = false; _cameraNeedsReset = false; RequireCameraResetAfterFault = false; CameraResets++; }
                    if (name == "夹具夹紧" && value && _feed) _gripped = true;
                    if (name == "夹具夹紧" && !value && previous && _gripped)
                    {
                        _gripped = false;
                        _feed = false;
                        if (!HoldFeedEmpty) Task.Run(async () => { await Task.Delay(250); lock (_sync) _feed = true; });
                    }
                }
                return Task.CompletedTask;
            }
        }
    }
}
