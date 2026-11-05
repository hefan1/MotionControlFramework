using System;
using System.Collections.Generic;
using System.IO;
using MotionControl.Framework.Runtime.Configuration;
using MotionControl.Framework.Wpf.Configuration;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Threading;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Contracts.IO;
using MotionControl.Framework.Runtime.Safety;
using MotionControl.Framework.Runtime.Alarms;
using MotionControl.Framework.Runtime.Machine;
using MotionControl.Host.Wpf;

namespace MotionControl.OperatorPages.Checks
{
    /// <summary>用真实 Host 刷新方法和纯内存连接验证异步急停读数的归属。</summary>
    internal static class AutomaticStartReadinessSessionChecks
    {
        /// <summary>驱动成功、读取失效、同对象重连及控制权离开后返回。</summary>
        internal static async Task RunAsync()
        {
            await CheckSafetyInputSaveAsync();
            var card = new MemoryCard();
            var safety = new PendingSafety();
            var inputs = new LogicalIoEmergencyStopStatus(safety, "DI-A");
            var alarms = new MachineAlarmService();
            var runtime = new MachineRuntime(new MotionCardGroup(new[] { card }), new[] { new IdleFlow() }, alarms, inputs);
            var window = new MainWindow();
            ((DispatcherTimer)typeof(MainWindow).GetField("_clockTimer", Fields).GetValue(window)).Stop();
            var start = (Button)window.FindName("MachineStartButton");
            var token = CancellationToken.None;
            try
            {
                await runtime.ConnectAsync(token);
                await runtime.ResetAsync(token);
                window.SetMachineRuntime(runtime);
                await Refresh(window);
                Require(start.IsEnabled, "同连接且急停明确释放时启动提示仍受阻。");

                var delayed = safety.DelayNext();
                Task first = Refresh(window);
                int readCount = safety.ReadCount;
                await Refresh(window);
                Require(!first.IsCompleted && !start.IsEnabled && readCount == safety.ReadCount, "页面重入或未知时允许启动。");
                Guid? previous = runtime.ConnectionSessionId;
                await runtime.DisconnectAsync(token);
                await runtime.ConnectAsync(token);
                await runtime.ResetAsync(token);
                Require(previous != runtime.ConnectionSessionId, "内存控制器重连未改变会话。");
                delayed.SetResult(true);
                await first;
                Require(!start.IsEnabled && !alarms.HasLatchedStopAlarm, "旧连接有效急停污染了新连接。");
                await Refresh(window);
                Require(start.IsEnabled, "新连接未能重新获得自己的就绪结论。");

                delayed = safety.DelayNext();
                first = Refresh(window);
                var owner = new object();
                await runtime.AcquireTaskEngineControlAsync(owner, token);
                await Refresh(window);
                var reason = typeof(MainWindow).GetField("_automaticStartBlockReason", Fields);
                string taskReason = (string)reason.GetValue(window);
                Require(!string.IsNullOrWhiteSpace(taskReason), "任务入口缺失没有明确提示。");
                delayed.SetException(new InvalidOperationException("旧入口读失败"));
                await first;
                Require((string)reason.GetValue(window) == taskReason && !alarms.HasLatchedStopAlarm, "旧失败覆盖了任务入口提示或报警。");
                runtime.ReleaseTaskEngineControl(owner);

                delayed = safety.DelayNext();
                first = Refresh(window);
                await runtime.AcquireTaskEngineControlAsync(owner, token);
                runtime.ReleaseTaskEngineControl(owner);
                delayed.SetResult(false);
                await first;
                Require(!start.IsEnabled, "控制权往返后复用了旧成功读数。");

                delayed = safety.DelayNext();
                first = Refresh(window);
                await runtime.SaveSafetyInputConfigurationAsync(() => inputs.UpdateEmergencyStopInputName("DI-B"), token);
                await runtime.SaveSafetyInputConfigurationAsync(() => inputs.UpdateEmergencyStopInputName("DI-A"), token);
                delayed.SetResult(true);
                await first;
                Require(!start.IsEnabled && !alarms.HasLatchedStopAlarm, "急停 DI 切换并恢复原名称后采用了旧读数。");

                delayed = safety.DelayNext();
                var manual = runtime.GetManualOperationBlockReasonAsync(token);
                var mapping = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                Task apply = runtime.RunConnectedConfigurationChangeAsync(() => mapping.Task, token);
                try
                {
                    int beforeMappingRead = safety.ReadCount;
                    Require(!string.IsNullOrWhiteSpace(await runtime.GetAutomaticStartBlockReasonAsync(token))
                        && safety.ReadCount == beforeMappingRead, "在线重映射期间仍读取安全条件。");
                    delayed.SetException(new InvalidOperationException("旧映射读取失败"));
                    Require(await manual == ManualOperationBlockReason.EmergencyStopStatusUnavailable
                        && !alarms.HasLatchedStopAlarm, "手动页面旧映射报警污染新来源。");
                }
                finally { delayed.TrySetResult(false); mapping.TrySetResult(true); }
                await apply;
                await Refresh(window);
                Require(start.IsEnabled, "在线变更结束后未重新接受当前来源。");

                delayed = safety.DelayNext();
                manual = runtime.GetManualOperationBlockReasonAsync(token);
                await runtime.DisconnectAsync(token);
                await runtime.ConnectAsync(token);
                await runtime.ResetAsync(token);
                delayed.SetResult(true);
                Require(await manual == ManualOperationBlockReason.EmergencyStopStatusUnavailable
                    && !alarms.HasLatchedStopAlarm, "重连后手动页面旧急停污染新会话。");

                // Start 正在等自己的驱动读取时，页面发现的同会话瞬时急停必须锁存。
                delayed = safety.DelayNext();
                first = Refresh(window);
                var startRead = safety.DelayNext();
                Task startCommand = runtime.StartAsync(token);
                try
                {
                    Require(!startCommand.IsCompleted, "未进入启动命令持锁的驱动等待。");
                    delayed.SetResult(true);
                    await first;
                    Require(alarms.HasLatchedStopAlarm, "启动持锁期间遗漏了页面观察到的急停。");
                }
                finally { delayed.TrySetResult(true); startRead.TrySetResult(false); }
                try { await startCommand; throw new Exception("急停锁存后启动未被禁止。"); }
                catch (InvalidOperationException) { }
                await runtime.ResetAsync(token);

                delayed = safety.DelayNext();
                first = Refresh(window);
                delayed.SetException(new InvalidOperationException("当前急停读取失败"));
                await first;
                Require(!start.IsEnabled && alarms.Alarms.Any(a => a.ConditionActive), "当前会话急停读取失败未保留停机报警。");
                await runtime.ResetAsync(token);
                await Refresh(window);
                Require(start.IsEnabled, "复位后当前会话有效反馈没有恢复就绪提示。");

                delayed = safety.DelayNext();
                first = Refresh(window);
                await runtime.DisconnectAsync(token);
                await runtime.ConnectAsync(token);
                await runtime.ResetAsync(token);
                var newRead = safety.DelayNext();
                Task<string> fresh = runtime.GetAutomaticStartBlockReasonAsync(token);
                newRead.SetResult(true);
                Require(!string.IsNullOrWhiteSpace(await fresh) && alarms.Alarms.Any(a => a.ConditionActive), "新连接真实急停未生效。");
                delayed.SetResult(false);
                await first;
                Require(alarms.Alarms.Any(a => a.ConditionActive) && !start.IsEnabled, "旧成功清除了新连接的急停条件。");

                await runtime.DisconnectAsync(token);
                Require(!string.IsNullOrWhiteSpace(await runtime.GetAutomaticStartBlockReasonAsync(token)),
                    "断开时返回了可启动的空原因。");
                var empty = new MachineRuntime(new MotionCardGroup(new[] { new MemoryCard() }),
                    new IStationFlow[0], new MachineAlarmService(), inputs);
                await empty.ConnectAsync(token);
                try
                {
                    await empty.ResetAsync(token);
                    window.SetMachineRuntime(empty);
                    await Refresh(window);
                    Require(((string)reason.GetValue(window)).Contains("尚未配置自动工位")
                        && !start.IsEnabled, "未配置工位显示为一直检查或允许启动。");
                }
                finally { await empty.DisconnectAsync(token); }
            }
            finally
            {
                window.Close();
                if (runtime.State != MachineRunState.Disconnected) await runtime.DisconnectAsync(token);
            }
        }

        /// <summary>通过 Host 保存路径核对互斥、停稳、写盘失败与取消的文件及生效来源。</summary>
        private static async Task CheckSafetyInputSaveAsync()
        {
            string root = Path.Combine(Path.GetTempPath(), "SafetySave-" + Guid.NewGuid().ToString("N"));
            var store = new MachineConfigurationStore(root);
            var io = new PendingSafety();
            var source = new LogicalIoEmergencyStopStatus(io, "DI-A");
            var axis = new AxisMonitorAsyncChecks.SampleAxis("内存轴");
            var runtime = new MachineRuntime(new MotionCardGroup(new[] { new MemoryCard(axis) }),
                new[] { new IdleFlow() }, new MachineAlarmService(), source, controlInputIo: io);
            string directory = store.GetMachineDirectory("Fixture");
            string path = Path.Combine(directory, "HardwareConfig", "SafetyInterlock.json");
            var token = CancellationToken.None;

            // 所有测试均调用正式 Host 保存协调方法，仅使用临时目录和内存设备。
            Task<string> Save(MachineRuntime target, string name, CancellationToken ct, Func<bool> current = null)
            {
                var configuration = new SafetyInterlockConfigurationDocument { EmergencyStopInputName = name };
                var method = typeof(App).GetMethod("SaveSafetyInterlockConfigurationAsync", BindingFlags.Static | BindingFlags.NonPublic);
                return (Task<string>)method.Invoke(null, new object[] { store, "Fixture", target, target == null ? null : source,
                    configuration, current ?? (() => true), ct });
            }
            // 读数和磁盘必须同时保持预期名称。
            async Task Check(string name)
            {
                Require(File.ReadAllText(path).Contains(name), "文件未保留 " + name);
                await source.ReadEmergencyStopActiveAsync(token);
                Require(io.LastInputName == name, "生效输入未保留 " + name);
            }
            // 将一次轴读取设为已知状态，运动中及非有限速度均应被拦截。
            void SetAxis(bool moving, double velocity)
            {
                axis.Pending = new TaskCompletionSource<AxisStatus>();
                axis.Pending.SetResult(new AxisStatus(0, velocity, true, moving, true, false, false, false, false,
                    false, null, false));
            }

            try
            {
                await Save(null, "DI-A", token);
                SetAxis(false, 0);
                await runtime.ConnectAsync(token);
                var heldRead = io.DelayNext();
                var reset = runtime.ResetAsync(token);
                try
                {
                    await ExpectSaveRejectedAsync(Save(runtime, "DI-B", token));
                    await Check("DI-A");
                }
                finally { heldRead.TrySetResult(false); }
                // 测试轴禁止复位动作；只需确认慢读后命令正常释放互斥门。
                try { await reset; } catch (InvalidOperationException) { }

                SetAxis(true, 5);
                await ExpectSaveRejectedAsync(Save(runtime, "DI-B", token));
                SetAxis(false, double.NaN);
                await ExpectSaveRejectedAsync(Save(runtime, "DI-B", token));
                await Check("DI-A");

                axis.Pending = new TaskCompletionSource<AxisStatus>();
                axis.Pending.SetException(new IOException("模拟读取失败"));
                await ExpectSaveRejectedAsync(Save(runtime, "DI-B", token));
                SetAxis(false, 0);
                await ExpectSaveRejectedAsync(Save(runtime, "DI-B", new CancellationToken(true)));
                await ExpectSaveRejectedAsync(Save(runtime, "DI-B", token, () => false));
                await Check("DI-A");

                string lockedFile = path + ".locked";
                File.Move(path, lockedFile);
                Directory.CreateDirectory(path);
                try
                {
                    await ExpectSaveRejectedAsync(Save(runtime, "DI-B", token));
                    await source.ReadEmergencyStopActiveAsync(token);
                    Require(io.LastInputName == "DI-A", "写盘失败时改变了生效输入。");
                }
                finally { Directory.Delete(path); File.Move(lockedFile, path); }

                Require(await Save(runtime, "DI-B", token) == null, "空闲保存返回了意外警告。");
                await Check("DI-B");
                Require(Directory.EnumerateFiles(Path.GetDirectoryName(path), "*", SearchOption.AllDirectories)
                    .Any(p => p != path && File.ReadAllText(p).Contains("DI-A")), "成功替换没有保存旧版本备份。");

                // 注入旧按钮监控未结束，使文件安装之后的监控重启抛错。
                int checks = 0;
                var leftover = new TaskCompletionSource<bool>();
                bool InjectMonitorFailure()
                {
                    if (++checks == 2) typeof(MachineRuntime).GetField("_controlInputMonitorTask", Fields)
                        .SetValue(runtime, leftover.Task);
                    return true;
                }
                try
                {
                    string warning = await Save(runtime, "DI-C", token, InjectMonitorFailure);
                    Require(!string.IsNullOrWhiteSpace(warning) && runtime.State == MachineRunState.Alarmed,
                        "监控重启失败未区分已提交配置或未报警。");
                    await Check("DI-C");
                }
                finally { leftover.TrySetResult(true); }
            }
            finally
            {
                SetAxis(false, 0);
                if (runtime.State != MachineRunState.Disconnected) await runtime.DisconnectAsync(token);
                if (Directory.Exists(root) && Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                    Directory.Delete(root, true);
            }
        }

        /// <summary>确认保存被拒绝或取消；意外成功直接中止测试。</summary>
        private static async Task ExpectSaveRejectedAsync(Task operation)
        {
            try { await operation; }
            catch (InvalidOperationException) { return; }
            catch (OperationCanceledException) { return; }
            catch (IOException) { return; }
            throw new InvalidOperationException("应拒绝或取消的安全配置保存成功。");
        }

        /// <summary>正式刷新方法和窗口内部状态采用一次检查专用的反射入口。</summary>
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;

        /// <summary>驱动正式窗口已有的就绪检查，不运行设备流程。</summary>
        private static Task Refresh(MainWindow window)
        {
            return (Task)typeof(MainWindow).GetMethod("RefreshAutomaticStartReadinessAsync", Fields).Invoke(window, null);
        }

        /// <summary>报告具体失败的会话保护条件。</summary>
        private static void Require(bool valid, string description)
        {
            if (!valid) throw new InvalidOperationException(description);
        }

        /// <summary>下一次读取可挂起；后续复位读取使用自己的正常结果。</summary>
        private sealed class PendingSafety : ILogicalIo
        {
            /// <summary>等待交给下一次读取的受控任务。</summary>
            private TaskCompletionSource<bool> _next;
            /// <summary>读取次数，用于核对页面是否重入。</summary>
            public int ReadCount { get; private set; }
            /// <summary>记录最近读取的输入名称以确认当前生效的配置。</summary>
            internal string LastInputName;
            /// <summary>安排下一次读取挂起直到测试释放。</summary>
            public TaskCompletionSource<bool> DelayNext()
            {
                _next = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                return _next;
            }
            /// <summary>只读取内存布置的结果。</summary>
            public Task<bool> ReadInputAsync(string inputName, CancellationToken token)
            {
                LastInputName = inputName;
                ReadCount++;
                var pending = Interlocked.Exchange(ref _next, null);
                return pending == null ? Task.FromResult(false) : pending.Task;
            }
            /// <summary>禁止访问输出。</summary>
            public Task<bool> ReadOutputAsync(string outputName, CancellationToken token) { throw new InvalidOperationException(); }
            /// <summary>禁止写输出。</summary>
            public Task WriteOutputAsync(string outputName, bool value, CancellationToken token) { throw new InvalidOperationException(); }
        }

        /// <summary>仅模拟连接状态，无轴、IO 或驱动调用。</summary>
        private sealed class MemoryCard : IMotionCard
        {
            /// <summary>内存卡稳定标识。</summary>
            public string ControllerId { get { return "ReadinessMemory"; } }
            /// <summary>控制器替身的实际连接状态。</summary>
            public bool IsConnected { get; private set; }
            /// <summary>测试不配置运动轴。</summary>
            public IReadOnlyCollection<IAxis> Axes { get; private set; }
            /// <summary>按需附加内存轴，无真实驱动。</summary>
            internal MemoryCard(params IAxis[] axes) { Axes = axes; }
            /// <summary>只改变内存连接标志。</summary>
            public Task ConnectAsync(CancellationToken token) { IsConnected = true; return Task.CompletedTask; }
            /// <summary>只清除内存连接标志。</summary>
            public Task DisconnectAsync(CancellationToken token) { IsConnected = false; return Task.CompletedTask; }
        }

        /// <summary>允许内存复位，明确禁止执行任何工步。</summary>
        private sealed class IdleFlow : IStationFlow
        {
            /// <summary>工位测试名称。</summary>
            public string StationName { get { return "就绪检查工位"; } }
            /// <summary>未执行任何工步。</summary>
            public string CurrentStepName { get { return "待机"; } }
            /// <summary>测试禁止启动工位。</summary>
            public Task ExecuteCurrentStepAsync(CancellationToken token) { throw new InvalidOperationException("禁止执行"); }
            /// <summary>只确认测试工位初始状态。</summary>
            public Task ResetAsync(CancellationToken token) { return Task.CompletedTask; }
        }
    }
}
