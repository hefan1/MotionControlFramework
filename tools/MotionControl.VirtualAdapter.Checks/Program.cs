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

namespace MotionControl.VirtualAdapter.Checks
{
    /// <summary>对独立控制器运行轴、IO 和取消运动的适配器往返检查。</summary>
    internal static class Program
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
                    RunControllerApi(int.Parse(args[1]));
                else
                    Run(args);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.GetType().FullName);
                Console.Error.WriteLine(ex.Message);
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
                if (Math.Abs(afterRelative.Position - 0.5) > definition.PositionTolerance || afterRelative.Moving)
                    throw new Exception("相对定位后的真实轴反馈不符合目标。");

                axis.StartJogAsync(MotionDirection.Positive, profile, token).GetAwaiter().GetResult();
                if (!axis.ReadStatusAsync(token).GetAwaiter().GetResult().Moving)
                    throw new Exception("控制器 API 未确认连续点动已启动。");
                bool abortRejected = false;
                try { axis.StopAsync(AxisStopMode.Abort, token).GetAwaiter().GetResult(); }
                catch (NotSupportedException) { abortRejected = true; }
                if (!abortRejected)
                    throw new Exception("不支持的 Abort 停止模式没有明确拒绝。");
                if (!axis.ReadStatusAsync(token).GetAwaiter().GetResult().Moving)
                    throw new Exception("拒绝 Abort 后意外发送了停止命令。");

                axis.StopAsync(AxisStopMode.Controlled, token).GetAwaiter().GetResult();
                AxisStatus stopped = axis.ReadStatusAsync(token).GetAwaiter().GetResult();
                if (stopped.Moving || double.IsNaN(stopped.Velocity)
                    || double.IsInfinity(stopped.Velocity) || Math.Abs(stopped.Velocity) > 0.001)
                    throw new Exception("Controlled 停止后真实反馈仍未确认停稳。");
                axis.DisableServoAsync(token).GetAwaiter().GetResult();
                Console.WriteLine("[通过] 独立软件控制器 API：回零、绝对/相对定位、点动、Abort 拒绝、Controlled 停稳和断使能");
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
