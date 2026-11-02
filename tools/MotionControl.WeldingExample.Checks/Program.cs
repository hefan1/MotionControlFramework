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
            try { RunAsync().GetAwaiter().GetResult(); CheckStaleFeedbackAsync().GetAwaiter().GetResult(); CheckFeedbackDuringMotionAsync().GetAwaiter().GetResult(); PreparationChecks.RunAsync().GetAwaiter().GetResult(); Console.WriteLine("代码工位并行、堵塞、暂停恢复、旧反馈拦截及设备准备检查通过；仅内部检查。"); return 0; }
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
                await WaitForAsync(() => flows[3].CurrentStepName.Contains("等待动作完成反馈")
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
            context.Io.ForcedHighInput = "压装完成";
            var flows = new WeldingExampleEquipmentDefinition().CreateStationFlows(context).ToArray();
            using (var cancel = new CancellationTokenSource())
            {
                Task[] workers = flows.Select(flow => RunStationAsync(flow, cancel.Token)).ToArray();
                await WaitForAsync(() => flows[1].CurrentStepName.Contains("等待动作前反馈复位"), TimeSpan.FromSeconds(8));
                await Task.Delay(250);
                if (await context.Io.ReadOutputAsync("压头动作", CancellationToken.None))
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
                if (await context.Io.ReadOutputAsync("压头动作", CancellationToken.None))
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
            /// <summary>四个测试轴及 0 到 1 的允许位置。</summary>
            public IReadOnlyList<AxisHardwareDefinition> AxisDefinitions { get { return new[] { "Load_X", "Press_Z", "Camera_X", "Unload_X" }.Select(name => new AxisHardwareDefinition { Name = name, MinimumPosition = 0, MaximumPosition = 1 }).ToArray(); } }
            /// <summary>四个测试输入名称。</summary>
            public IReadOnlyList<DigitalIoHardwareDefinition> InputDefinitions { get { return new[] { "上料到位", "压装完成", "检测完成", "下料到位" }.Select(name => new DigitalIoHardwareDefinition { Name = name }).ToArray(); } }
            /// <summary>四个测试输出名称。</summary>
            public IReadOnlyList<DigitalIoHardwareDefinition> OutputDefinitions { get { return new[] { "夹具夹紧", "压头动作", "相机触发", "下料电磁阀" }.Select(name => new DigitalIoHardwareDefinition { Name = name }).ToArray(); } }
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
            { if (Name == "Press_Z") Interlocked.Increment(ref _state.WeldAxisMoves); int now = Interlocked.Increment(ref _state.Moving); if (now > _state.SimultaneousMoves) _state.SimultaneousMoves = now; try { await Task.Delay(80, token); if (Name == "Press_Z" && position == 1 && _state.RaiseWeldFeedbackOnMove) _state.ForcedHighInput = "压装完成"; } finally { Interlocked.Decrement(ref _state.Moving); } }
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
            /// <summary>复检反馈是否保持无效。</summary>
            internal volatile bool BlockInspection = true;
            /// <summary>指定输入卡在有效，用于验证动作前不接受旧完成信号。</summary>
            internal volatile string ForcedHighInput;
            /// <summary>在焊接轴到达工作位时模拟反馈异常变为有效。</summary>
            internal volatile bool RaiseWeldFeedbackOnMove;
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
                    if (name == "上料到位") return Task.FromResult(_feed);
                    if (name == ForcedHighInput) return Task.FromResult(true);
                    string output = name == "压装完成" ? "压头动作" : name == "检测完成" ? "相机触发" : "下料电磁阀";
                    return Task.FromResult(!(name == "下料到位" && BlockInspection) && _outputs.ContainsKey(output) && _outputs[output]);
                }
            }
            /// <summary>返回当前输出。</summary>
            public Task<bool> ReadOutputAsync(string name, CancellationToken token) { lock (_sync) return Task.FromResult(_outputs.ContainsKey(name) && _outputs[name]); }
            /// <summary>设置当前输出。</summary>
            public Task WriteOutputAsync(string name, bool value, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                lock (_sync)
                {
                    bool previous = _outputs.ContainsKey(name) && _outputs[name];
                    _outputs[name] = value;
                    if (name == "夹具夹紧" && value && _feed) _gripped = true;
                    if (name == "夹具夹紧" && !value && previous && _gripped)
                    {
                        _gripped = false;
                        _feed = false;
                        Task.Run(async () => { await Task.Delay(250); lock (_sync) _feed = true; });
                    }
                }
                return Task.CompletedTask;
            }
        }
    }
}
