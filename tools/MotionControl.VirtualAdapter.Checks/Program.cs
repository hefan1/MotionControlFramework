using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Adapter.MotionCard.Virtual;
using MotionControl.Framework.Contracts.Configuration;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Runtime.Alarms;
using MotionControl.Framework.Runtime.Machine;
using MotionControl.Framework.Runtime.Safety;

namespace MotionControl.VirtualAdapter.Checks
{
    /// <summary>对独立控制器运行轴、IO 和取消运动的适配器往返检查。</summary>
    internal static partial class Program
    {
        /// <summary>每个检查进程使用独立的临时诊断目录，不写用户设备日志。</summary>
        private static readonly string DiagnosticsRoot = Path.Combine(Path.GetTempPath(), "MotionControl.VirtualAdapter.Checks-" + Guid.NewGuid().ToString("N"));
        /// <summary>使用隔离端口执行检查，调用方需先启动控制器。</summary>
        private static void Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--abrupt-exit")
            {
                StartMotionAndExit(int.Parse(args[1]));
                return;
            }
            try
            {
                // 只通过现有软件控制器 API 检查运动语义，不创建内存假轴。
                if (args.Length == 2 && args[0] == "--controller-api")
                {
                    int port = int.Parse(args[1]);
                    RunControllerApi(port);
                    CheckRuntimeTaskSession(port);
                    CheckTaskCommandOperationsAsync(port).GetAwaiter().GetResult();
                }
                else if (args.Length == 3 && args[0] == "--multi-controller-api")
                {
                    // 两个独立控制器进程上的 IMotionCard 真实调用，不创建假轴。
                    RunMultiControllerApi(int.Parse(args[1]), int.Parse(args[2]));
                }
                else
                    Run(args);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.GetType().FullName);
                Console.Error.WriteLine(ex.ToString());
                Environment.ExitCode = 1;
            }
            finally
            {
                // 只删除此检查进程自己创建、位于系统临时目录的结果。
                string root = Path.GetFullPath(DiagnosticsRoot);
                string temporary = Path.GetFullPath(Path.GetTempPath());
                if (root.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) && Directory.Exists(root))
                    Directory.Delete(root, true);
            }
        }
        /// <summary>验证真实控制器往返行为。</summary>
        private static void Run(string[] args)
        {
            int port = args.Length == 0 ? 18521 : int.Parse(args[0]);
            var definition = new AxisHardwareDefinition { Name = "X", ControllerId = "Test", AxisNumber = 1, PositionTolerance = 0.01, SoftLimitEnabled = true, MinimumPosition = -10, MaximumPosition = 10 };
            var input = new DigitalIoHardwareDefinition { Name = "Input", Channel = 4, ActiveHigh = false };
            var materialPresent = new DigitalIoHardwareDefinition { Name = "A夹位有料", Channel = 69, ActiveHigh = true };
            var materialEmpty = new DigitalIoHardwareDefinition { Name = "A夹位无料", Channel = 70, ActiveHigh = true };
            var output = new DigitalIoHardwareDefinition { Name = "Output", Channel = 0, ActiveHigh = false };
            var card = new VirtualMotionCard("Test", new[] { definition }, new[] { input, materialPresent, materialEmpty }, new[] { output }, port, DiagnosticsRoot);
            var token = CancellationToken.None;
            card.ConnectAsync(token).GetAwaiter().GetResult();
            AssertMaterialInputsThroughAdapter(card, port, token);
            SendRaw(port, "{\"command\":\"setInput\",\"channel\":4,\"value\":true}");
            if (card.ReadInputAsync("Input", token).GetAwaiter().GetResult()) throw new Exception("低有效 DI 极性错误");
            SendRaw(port, "{\"command\":\"setInput\",\"channel\":4,\"value\":false}");
            if (!card.ReadInputAsync("Input", token).GetAwaiter().GetResult()) throw new Exception("低有效 DI 状态错误");
            if (!SendRaw(port, "{\"command\":\"invalidCommand\"}").Contains("\"ok\":false")) throw new Exception("未知命令未返回失败");
            var axis = card.Axes.Single();
            axis.EnableServoAsync(token).GetAwaiter().GetResult();
            axis.HomeAsync(token).GetAwaiter().GetResult();
            // Abort 没有独立控制器命令，必须在发送任何停车命令前拒绝。
            bool abortRejected = false;
            try { axis.StopAsync(AxisStopMode.Abort, token).GetAwaiter().GetResult(); }
            catch (NotSupportedException) { abortRejected = true; }
            if (!abortRejected) throw new Exception("软件控制器未拒绝不支持的 Abort 停止模式");
            bool invalidStopRejected = false;
            try { axis.StopAsync((AxisStopMode)999, token).GetAwaiter().GetResult(); }
            catch (ArgumentOutOfRangeException) { invalidStopRejected = true; }
            if (!invalidStopRejected) throw new Exception("软件控制器未拒绝未知停止模式");
            bool jogRejected = false;
            try { axis.StartJogAsync(MotionDirection.Positive, new AxisMotionProfile(10, 10, 10), token).GetAwaiter().GetResult(); }
            catch (NotSupportedException) { jogRejected = true; }
            if (!jogRejected) throw new Exception("启用软件限位时未拒绝不受控点动");
            axis.MoveAbsoluteAsync(1, new AxisMotionProfile(10, 10, 10), token).GetAwaiter().GetResult();
            if (Math.Abs(axis.ReadStatusAsync(token).GetAwaiter().GetResult().Position - 1) > 0.01) throw new Exception("轴位置反馈错误");
            definition.MinimumPosition = 0.5;
            bool homeRejected = false;
            try { axis.HomeAsync(token).GetAwaiter().GetResult(); }
            catch (InvalidOperationException) { homeRejected = true; }
            if (!homeRejected) throw new Exception("零位超出软件限位时未拒绝回零");
            definition.MinimumPosition = -10;
            card.WriteOutputAsync("Output", false, token).GetAwaiter().GetResult();
            if (card.ReadOutputAsync("Output", token).GetAwaiter().GetResult()) throw new Exception("低有效输出转换错误");
            card.WriteOutputAsync("Output", true, token).GetAwaiter().GetResult();
            if (!card.ReadOutputAsync("Output", token).GetAwaiter().GetResult()) throw new Exception("输出未触发");
            if (card.ReadDigitalOutputChannelsAsync(new[] { 0, 1 }, token).GetAwaiter().GetResult()[0]) throw new Exception("批量物理输出电平错误");
            using (var cancel = new CancellationTokenSource(150))
            {
                try { axis.MoveAbsoluteAsync(9, new AxisMotionProfile(1, 1, 1), cancel.Token).GetAwaiter().GetResult(); throw new Exception("运动取消未生效"); }
                catch (OperationCanceledException) { }
            }
            AxisStatus stopped = axis.ReadStatusAsync(token).GetAwaiter().GetResult();
            if (stopped.Moving) throw new Exception("取消后轴仍在运动");
            if (stopped.Position <= 1 || stopped.Position >= 9) throw new Exception("停止位置不在运动路径内");
            card.WriteDigitalOutputChannelAsync(0, false, token).GetAwaiter().GetResult();
            var outputGuarantee = card as IOutputStatePreservingDisconnect;
            if (outputGuarantee == null || !outputGuarantee.PreservesDigitalOutputsOnDisconnect)
                throw new Exception("软件控制器未声明断连后保持输出");
            card.DisconnectAsync(token).GetAwaiter().GetResult();
            string retainedOutput = SendRaw(port, "{\"command\":\"readOutput\",\"channel\":0}");
            if (!retainedOutput.Contains("\"value\":false")) throw new Exception("释放会话后输出电平发生变化");
            AssertLeaseStopsAfterExit(port);
            AssertLeaseStopsAfterLostReply(port);
            var unreachable = new VirtualMotionCard("Offline", new[] { definition }, new[] { input }, new[] { output }, 18522, DiagnosticsRoot);
            bool connectionFailed = false;
            try { unreachable.ConnectAsync(token).GetAwaiter().GetResult(); }
            catch (IOException) { connectionFailed = true; }
            if (!connectionFailed || unreachable.IsConnected) throw new Exception("离线控制器未正确报告连接失败");
            AssertRejectedIdentity(definition, input, output, "{\"ok\":true}");
            AssertRejectedIdentity(definition, input, output, "{\"ok\":true,\"controllerType\":\"Other\",\"protocolVersion\":1}");
            AssertRejectedIdentity(definition, input, output, "{\"ok\":true,\"controllerType\":\"MotionControl.VirtualController\",\"protocolVersion\":1}");
            AssertHeartbeatDiagnostic(false);
            AssertHeartbeatDiagnostic(true);
            Console.WriteLine("虚拟控制器适配器往返检查通过。");
        }

        /// <summary>
        /// 连接已有的独立软件控制器，仅通过实际 IMotionCard/IAxis API 验证轴命令及反馈。
        /// 不创建 FakeAxis、内存运动模型或新的控制器协议。
        /// </summary>
        private static void RunControllerApi(int port)
        {
            var definition = new AxisHardwareDefinition
            {
                Name = "ApiAxis", ControllerId = "ApiCheck", AxisNumber = 1,
                PositionTolerance = 0.01, SoftLimitEnabled = false
            };
            var card = new VirtualMotionCard("ApiCheck", new[] { definition },
                new DigitalIoHardwareDefinition[0], new DigitalIoHardwareDefinition[0], port, DiagnosticsRoot);
            IAxis axis = card.Axes.Single();
            var token = CancellationToken.None;
            var profile = new AxisMotionProfile(10, 20, 20);
            try
            {
                card.ConnectAsync(token).GetAwaiter().GetResult();
                axis.EnableServoAsync(token).GetAwaiter().GetResult();
                axis.HomeAsync(token).GetAwaiter().GetResult();
                AxisStatus homed = axis.ReadStatusAsync(token).GetAwaiter().GetResult();
                if (!homed.Homed || homed.Moving)
                    throw new Exception("控制器 API 未确认回零完成。");

                axis.MoveAbsoluteAsync(1, profile, token).GetAwaiter().GetResult();
                AxisStatus atTarget = axis.ReadStatusAsync(token).GetAwaiter().GetResult();
                if (Math.Abs(atTarget.Position - 1) > definition.PositionTolerance || atTarget.Moving)
                    throw new Exception("绝对定位后的真实轴反馈不符合目标。");
                axis.MoveRelativeAsync(-0.5, profile, token).GetAwaiter().GetResult();
                AxisStatus afterRelative = axis.ReadStatusAsync(token).GetAwaiter().GetResult();
                if (Math.Abs(afterRelative.Position - 0.5) > definition.PositionTolerance
                    || !afterRelative.HasStoppedMotionFeedback())
                    throw new Exception("相对定位后的真实轴反馈未确认停稳或不符合目标。");

                // 注入控制器真实 API 读取失败：命令已经发出，必须停车，且不能把操作当成 Done。
                string readFault = SendRaw(port,
                    "{\"command\":\"setCommandFault\",\"targetCommand\":\"readAxis\",\"targetIndex\":1,\"mode\":\"reject\",\"delayMs\":0}");
                if (!readFault.Contains("\"ok\":true"))
                    throw new Exception("独立控制器未接受一次性轴状态读取故障。");
                bool moveRejectedOnFeedbackError = false;
                try
                {
                    axis.MoveAbsoluteAsync(10, new AxisMotionProfile(1, 1, 1), token).GetAwaiter().GetResult();
                }
                catch (InvalidOperationException error)
                {
                    moveRejectedOnFeedbackError = error.Message.Contains("拒绝 readAxis");
                }
                if (!moveRejectedOnFeedbackError)
                    throw new Exception("定位期间控制器状态读取失败被错误当成动作完成。");
                AxisStatus afterFailedMove = axis.ReadStatusAsync(token).GetAwaiter().GetResult();
                if (!afterFailedMove.HasStoppedMotionFeedback()
                    || afterFailedMove.Position >= 10 - definition.PositionTolerance)
                    throw new Exception("定位反馈失败后未能通过控制器真实回读确认停车。");

                axis.StartJogAsync(MotionDirection.Positive, profile, token).GetAwaiter().GetResult();
                AxisStatus jogging = axis.ReadStatusAsync(token).GetAwaiter().GetResult();
                if (!jogging.Moving || jogging.HasStoppedMotionFeedback())
                    throw new Exception("点动中的实际反馈被错误标记为停稳。");
                bool abortRejected = false;
                try { axis.StopAsync(AxisStopMode.Abort, token).GetAwaiter().GetResult(); }
                catch (NotSupportedException) { abortRejected = true; }
                if (!abortRejected)
                    throw new Exception("不支持的 Abort 停止模式没有明确拒绝。");
                if (!axis.ReadStatusAsync(token).GetAwaiter().GetResult().Moving)
                    throw new Exception("拒绝 Abort 后意外发送了停止命令。");

                axis.StopAsync(AxisStopMode.Controlled, token).GetAwaiter().GetResult();
                AxisStatus stopped = axis.ReadStatusAsync(token).GetAwaiter().GetResult();
                if (!stopped.HasStoppedMotionFeedback())
                    throw new Exception("Controlled 停止后真实反馈仍未确认停稳。");

                // 控制器停止请求已执行但回读失败，API 不得报告“停止确认成功”。
                axis.StartJogAsync(MotionDirection.Positive, profile, token).GetAwaiter().GetResult();
                string stopFeedbackFault = SendRaw(port,
                    "{\"command\":\"setCommandFault\",\"targetCommand\":\"readAxis\",\"targetIndex\":1,\"mode\":\"reject\",\"delayMs\":0}");
                if (!stopFeedbackFault.Contains("\"ok\":true"))
                    throw new Exception("控制器未接受停止反馈读取故障。");
                bool stopFeedbackRejected = false;
                try { axis.StopAsync(AxisStopMode.Controlled, token).GetAwaiter().GetResult(); }
                catch (InvalidOperationException error) { stopFeedbackRejected = error.Message.Contains("拒绝 readAxis"); }
                if (!stopFeedbackRejected)
                    throw new Exception("停车后状态读取失败仍被错误标记为停止确认成功。");
                if (!axis.ReadStatusAsync(token).GetAwaiter().GetResult().HasStoppedMotionFeedback())
                    throw new Exception("停止反馈故障后无法从真实控制器确认轴停稳。");
                axis.DisableServoAsync(token).GetAwaiter().GetResult();
                Console.WriteLine("[通过] 独立软件控制器 API：定位、点动、停止、异常回读、真实停稳和断使能");
            }
            finally
            {
                if (card.IsConnected)
                {
                    // 即使中间断言失败，也先停止该隔离测试轴再释放连接租约。
                    try { axis.StopAsync(AxisStopMode.Controlled, CancellationToken.None).GetAwaiter().GetResult(); }
                    finally { card.DisconnectAsync(CancellationToken.None).GetAwaiter().GetResult(); }
                }
            }
        }

        /// <summary>
        /// 连接现有软件控制器并通过真实 MotionCardGroup/Runtime 检查任务控制权交接。
        /// 本检查只读取轴、报警和连接状态，不创建虚构的轴状态，也不发运动命令。
        /// </summary>
        private static void CheckRuntimeTaskSession(int port)
        {
            var axis = new AxisHardwareDefinition
            {
                Name = "SessionAxis", ControllerId = "SessionCheck", AxisNumber = 2,
                SoftLimitEnabled = false, PositionTolerance = 0.01
            };
            var stopInput = new DigitalIoHardwareDefinition
            {
                Name = "SessionEmergencyStop", ControllerId = "SessionCheck",
                Channel = 42, ActiveHigh = true
            };
            var card = new VirtualMotionCard("SessionCheck", new[] { axis },
                new[] { stopInput }, new DigitalIoHardwareDefinition[0], port,
                Path.Combine(DiagnosticsRoot, "runtime-session"));
            var group = new MotionCardGroup(new IMotionCard[] { card });
            var alarms = new MachineAlarmService();
            var safety = new LogicalIoEmergencyStopStatus(card, "SessionEmergencyStop");
            var runtime = new MachineRuntime(group, new IStationFlow[0], alarms, safety);
            var token = CancellationToken.None;
            object currentOwner = null;
            try
            {
                runtime.ConnectAsync(token).GetAwaiter().GetResult();
                runtime.ResetAsync(token).GetAwaiter().GetResult();
                Guid originalSession = runtime.ConnectionSessionId.Value;
                currentOwner = new object();
                runtime.AcquireTaskEngineControlAsync(currentOwner, token).GetAwaiter().GetResult();
                CheckOldTaskFinishRejected(runtime, originalSession, new object(), "不是当前任务持权设备");
                CheckOldTaskFinishRejected(runtime, Guid.NewGuid(), currentOwner, "不是当前连接会话");
                runtime.FinishTaskEngineRunAsync(originalSession, currentOwner, false, null).GetAwaiter().GetResult();
                runtime.ReleaseTaskEngineControl(currentOwner);
                currentOwner = null;
                runtime.DisconnectAsync(token).GetAwaiter().GetResult();

                // 使用同一台真实软件控制器重连，旧会话不能结束新连接的任务状态。
                runtime.ConnectAsync(token).GetAwaiter().GetResult();
                Guid newSession = runtime.ConnectionSessionId.Value;
                if (newSession == originalSession) throw new Exception("重连没有生成新的运行时会话身份。");
                currentOwner = new object();
                runtime.AcquireTaskEngineControlAsync(currentOwner, token).GetAwaiter().GetResult();
                CheckOldTaskFinishRejected(runtime, originalSession, currentOwner, "重连前旧会话");
                runtime.FinishTaskEngineRunAsync(newSession, currentOwner, false, null).GetAwaiter().GetResult();
                runtime.ReleaseTaskEngineControl(currentOwner);
                currentOwner = null;
                runtime.DisconnectAsync(token).GetAwaiter().GetResult();
                Console.WriteLine("[通过] 独立软件控制器 Runtime：持权身份拒绝、跨连接迟到状态拒绝与正常收尾");
            }
            finally
            {
                // 本检查没有使能或运动。失败时仍尝试归还该测试会话并断开控制器。
                if (currentOwner != null) runtime.ReleaseTaskEngineControl(currentOwner);
                if (card.IsConnected) runtime.DisconnectAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
        }

        /// <summary>错误身份不能修改 Runtime 状态；只接受明确的连接或持权拒绝原因。</summary>
        private static void CheckOldTaskFinishRejected(MachineRuntime runtime, Guid session,
            object owner, string scenario)
        {
            MachineRunState stateBefore = runtime.State;
            bool rejected = false;
            try { runtime.FinishTaskEngineRunAsync(session, owner, false, null).GetAwaiter().GetResult(); }
            catch (InvalidOperationException error)
            {
                rejected = error.Message.Contains("连接会话已变化")
                    || error.Message.Contains("未持有本机控制权");
            }
            if (!rejected || runtime.State != stateBefore)
                throw new Exception("旧任务完成回调未被安全拒绝：" + scenario);
        }

        /// <summary>通过正式虚拟卡适配器读取独立物料输入，核对未知、变化、冲突和输出隔离。</summary>
        private static void AssertMaterialInputsThroughAdapter(VirtualMotionCard card, int port, CancellationToken token)
        {
            string configured = SendRaw(port, "{\"command\":\"configureMaterialSensors\",\"name\":\"A\",\"presentInputChannel\":69,\"emptyInputChannel\":70}");
            if (!configured.Contains("\"ok\":true")) throw new Exception("未能配置独立物料反馈。");
            foreach (var scenario in new[] {
                (State: "unknown", Present: false, Empty: false),
                (State: "present", Present: true, Empty: false),
                (State: "empty", Present: false, Empty: true),
                (State: "conflict", Present: true, Empty: true) })
            {
                string response = SendRaw(port, "{\"command\":\"setMaterialSensorState\",\"name\":\"A\",\"state\":\"" + scenario.State + "\"}");
                if (!response.Contains("\"ok\":true")) throw new Exception("设置物料测试状态失败：" + scenario.State);
                bool present = card.ReadInputAsync("A夹位有料", token).GetAwaiter().GetResult();
                bool empty = card.ReadInputAsync("A夹位无料", token).GetAwaiter().GetResult();
                if (present != scenario.Present || empty != scenario.Empty) throw new Exception("适配器物料反馈不一致：" + scenario.State);
            }
            card.WriteOutputAsync("Output", false, token).GetAwaiter().GetResult();
            if (!card.ReadDigitalOutputChannelsAsync(new[] { 0 }, token).GetAwaiter().GetResult()[0])
                throw new Exception("未能在测试中改变物理输出电平。");
            if (!card.ReadInputAsync("A夹位有料", token).GetAwaiter().GetResult()
                || !card.ReadInputAsync("A夹位无料", token).GetAwaiter().GetResult())
                throw new Exception("输出变化错误改写独立物料反馈。");
            card.WriteOutputAsync("Output", true, token).GetAwaiter().GetResult();
        }
        /// <summary>启动运动后直接退出，模拟 Host 未执行任何断开清理。</summary>
        private static void StartMotionAndExit(int port)
        {
            var axisDefinition = new AxisHardwareDefinition { Name = "X", ControllerId = "Test", AxisNumber = 1, SoftLimitEnabled = false };
            var card = new VirtualMotionCard("Test", new[] { axisDefinition }, new DigitalIoHardwareDefinition[0], new DigitalIoHardwareDefinition[0], port, DiagnosticsRoot);
            var token = CancellationToken.None;
            card.ConnectAsync(token).GetAwaiter().GetResult();
            var axis = card.Axes.Single();
            axis.StartJogAsync(MotionDirection.Positive, new AxisMotionProfile(1, 1, 1), token).GetAwaiter().GetResult();
            Environment.Exit(0);
        }
        /// <summary>验证进程异常退出后控制器的租约看门狗主动停车。</summary>
        private static void AssertLeaseStopsAfterExit(int port)
        {
            string executable = Process.GetCurrentProcess().MainModule.FileName;
            using (var process = Process.Start(new ProcessStartInfo(executable, "--abrupt-exit " + port) { UseShellExecute = false, CreateNoWindow = true }))
            {
                if (!process.WaitForExit(5000) || process.ExitCode != 0) throw new Exception("异常退出测试进程失败");
            }
            string moving = SendRaw(port, "{\"command\":\"readAxis\",\"axis\":1}");
            if (!moving.Contains("\"moving\":true")) throw new Exception("退出前未成功发出点动");
            Thread.Sleep(3000);
            string stopped = SendRaw(port, "{\"command\":\"readAxis\",\"axis\":1}");
            if (!stopped.Contains("\"moving\":false")) throw new Exception("租约过期后轴仍在运动");
        }
        /// <summary>代理放行定位命令后丢弃响应，检查通信失败后的租约停车。</summary>
        private static void AssertLeaseStopsAfterLostReply(int port)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int proxyPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task.Run(() =>
            {
                try
                {
                    while (true)
                    {
                        TcpClient incoming = listener.AcceptTcpClient();
                        Task.Run(() =>
                        {
                            using (incoming)
                            using (var input = incoming.GetStream())
                            using (var reader = new StreamReader(input, Encoding.UTF8))
                            using (var writer = new StreamWriter(input, new UTF8Encoding(false), 1024, true) { AutoFlush = true })
                            {
                                string request = reader.ReadLine();
                                string response = SendRaw(port, request);
                                if (!request.Contains("\"command\":\"move\"")) writer.WriteLine(response);
                            }
                        });
                    }
                }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
            });
            try
            {
                var definition = new AxisHardwareDefinition { Name = "X", ControllerId = "Test", AxisNumber = 1, PositionTolerance = 0.01 };
                string lostReplyDirectory = Path.Combine(DiagnosticsRoot, "lost-reply");
                var card = new VirtualMotionCard("Test", new[] { definition }, new DigitalIoHardwareDefinition[0], new DigitalIoHardwareDefinition[0], proxyPort, lostReplyDirectory);
                card.ConnectAsync(CancellationToken.None).GetAwaiter().GetResult();
                bool failed = false;
                try { card.Axes.Single().MoveAbsoluteAsync(20, new AxisMotionProfile(1, 1, 1), CancellationToken.None).GetAwaiter().GetResult(); }
                catch (IOException) { failed = true; }
                if (!failed || card.IsConnected) throw new Exception("丢失定位响应未报告通信失败");
                string diagnostic = card.FirstConnectionFailure;
                if (string.IsNullOrWhiteSpace(diagnostic) || !diagnostic.Contains("通信/move") || !diagnostic.Contains("System.IO.IOException"))
                    throw new Exception("丢失响应未保留首次传输错误与操作上下文");
                if (!File.ReadAllText(card.ConnectionDiagnosticsPath).Contains("通信/move")) throw new Exception("传输故障未写入隔离诊断目录");
                if (!SendRaw(port, "{\"command\":\"readAxis\",\"axis\":1}").Contains("\"moving\":true"))
                    throw new Exception("代理没有放行定位指令");
                Thread.Sleep(3000);
                if (!SendRaw(port, "{\"command\":\"readAxis\",\"axis\":1}").Contains("\"moving\":false"))
                    throw new Exception("丢失定位响应后看门狗未停车");
            }
            finally { listener.Stop(); }
        }

        /// <summary>模拟心跳被控制器拒绝；检查真实原因、日志错误和离线状态均可观察。</summary>
        private static void AssertHeartbeatDiagnostic(bool blockLogDirectory)
        {
            string directory = Path.Combine(DiagnosticsRoot, blockLogDirectory ? "not-a-directory" : "heartbeat");
            if (blockLogDirectory)
            {
                Directory.CreateDirectory(DiagnosticsRoot);
                File.WriteAllText(directory, "作为文件阻止创建诊断目录");
            }
            else
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "software-controller-connection.log"), new string('x', 1024 * 1024));
            }
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task server = Task.Run(() =>
            {
                try
                {
                    while (true)
                    {
                        using (var client = listener.AcceptTcpClient())
                        using (var stream = client.GetStream())
                        using (var reader = new StreamReader(stream, Encoding.UTF8))
                        using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, true) { AutoFlush = true })
                        {
                            string request = reader.ReadLine();
                            if (request.Contains("\"command\":\"ping\""))
                                writer.WriteLine("{\"ok\":true,\"controllerType\":\"MotionControl.VirtualController\",\"protocolVersion\":2}");
                            else if (request.Contains("\"command\":\"acquireLease\""))
                                writer.WriteLine("{\"ok\":true,\"leaseId\":\"TEST_LEASE_SECRET\"}");
                            else if (request.Contains("\"command\":\"heartbeat\""))
                                writer.WriteLine("{\"ok\":false,\"error\":\"测试心跳故障\"}");
                            else writer.WriteLine("{\"ok\":false,\"error\":\"不支持的测试请求\"}");
                        }
                    }
                }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
            });
            try
            {
                var card = new VirtualMotionCard("Test", new AxisHardwareDefinition[0], new DigitalIoHardwareDefinition[0],
                    new DigitalIoHardwareDefinition[0], port, directory);
                card.ConnectAsync(CancellationToken.None).GetAwaiter().GetResult();
                if (!SpinWait.SpinUntil(() => !card.IsConnected && card.FirstConnectionFailure != null, 5000))
                    throw new Exception("心跳失败未及时标记离线并保留原因");
                string failure = card.FirstConnectionFailure;
                if (!failure.Contains("心跳") || !failure.Contains("测试心跳故障") || failure.Contains("TEST_LEASE_SECRET"))
                    throw new Exception("心跳诊断缺少具体原因或泄漏会话编号");
                DateTimeOffset time;
                if (!DateTimeOffset.TryParse(failure.Split(' ')[0], out time) || Math.Abs((DateTimeOffset.Now - time).TotalMinutes) > 1)
                    throw new Exception("心跳诊断时间不正确");
                if (blockLogDirectory)
                {
                    if (!SpinWait.SpinUntil(() => !string.IsNullOrWhiteSpace(card.ConnectionDiagnosticsWriteFailure), 3000))
                        throw new Exception("诊断写入失败未报告");
                }
                else
                {
                    if (!SpinWait.SpinUntil(() => File.Exists(card.ConnectionDiagnosticsPath + ".previous")
                        && new FileInfo(card.ConnectionDiagnosticsPath).Length < 1024 * 1024, 3000)) throw new Exception("心跳诊断日志未完成轮转");
                    if (!File.ReadAllText(card.ConnectionDiagnosticsPath).Contains("测试心跳故障"))
                        throw new Exception("心跳故障未写入诊断日志");
                    if (!File.Exists(card.ConnectionDiagnosticsPath + ".previous")
                        || new FileInfo(card.ConnectionDiagnosticsPath).Length >= 1024 * 1024)
                        throw new Exception("诊断日志达到上限后未保留有界历史");
                    if (card.ConnectionDiagnosticsWriteFailure != null) throw new Exception("正常写入被误报为失败");
                }
            }
            finally
            {
                listener.Stop();
                server.GetAwaiter().GetResult();
            }
        }

        /// <summary>启动短暂的本地服务，确保错误身份或协议不会被当成控制器连接。</summary>
        private static void AssertRejectedIdentity(AxisHardwareDefinition axis, DigitalIoHardwareDefinition input, DigitalIoHardwareDefinition output, string reply)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                Task server = Task.Run(() =>
                {
                    using (var client = listener.AcceptTcpClient())
                    using (var stream = client.GetStream())
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, true) { AutoFlush = true })
                    {
                        reader.ReadLine();
                        writer.WriteLine(reply);
                    }
                });
                var card = new VirtualMotionCard("Test", new[] { axis }, new[] { input }, new[] { output }, port, DiagnosticsRoot);
                bool rejected = false;
                try { card.ConnectAsync(CancellationToken.None).GetAwaiter().GetResult(); }
                catch (InvalidOperationException) { rejected = true; }
                server.GetAwaiter().GetResult();
                if (!rejected || card.IsConnected) throw new Exception("错误服务身份或协议未被拒绝");
            }
            finally { listener.Stop(); }
        }
        /// <summary>通过控制器协议配置独立测试输入或验证未知指令。</summary>
        private static string SendRaw(int port, string request)
        {
            using (var client = new TcpClient("127.0.0.1", port))
            using (var stream = client.GetStream())
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, true) { AutoFlush = true })
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                writer.WriteLine(request);
                return reader.ReadLine();
            }
        }
    }
}
