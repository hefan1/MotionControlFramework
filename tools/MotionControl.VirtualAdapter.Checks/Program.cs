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
        /// <summary>使用隔离端口执行检查，调用方需先启动控制器。</summary>
        private static void Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--abrupt-exit")
            {
                StartMotionAndExit(int.Parse(args[1]));
                return;
            }
            try { Run(args); }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.GetType().FullName);
                Console.Error.WriteLine(ex.Message);
                Environment.ExitCode = 1;
            }
        }
        /// <summary>验证真实控制器往返行为。</summary>
        private static void Run(string[] args)
        {
            int port = args.Length == 0 ? 18521 : int.Parse(args[0]);
            var definition = new AxisHardwareDefinition { Name = "X", ControllerId = "Test", AxisNumber = 1, PositionTolerance = 0.01, SoftLimitEnabled = true, MinimumPosition = -10, MaximumPosition = 10 };
            var input = new DigitalIoHardwareDefinition { Name = "Input", Channel = 4, ActiveHigh = false };
            var output = new DigitalIoHardwareDefinition { Name = "Output", Channel = 0, ActiveHigh = false };
            var card = new VirtualMotionCard("Test", new[] { definition }, new[] { input }, new[] { output }, port);
            var token = CancellationToken.None;
            card.ConnectAsync(token).GetAwaiter().GetResult();
            SendRaw(port, "{\"command\":\"setInput\",\"channel\":4,\"value\":true}");
            if (card.ReadInputAsync("Input", token).GetAwaiter().GetResult()) throw new Exception("低有效 DI 极性错误");
            SendRaw(port, "{\"command\":\"setInput\",\"channel\":4,\"value\":false}");
            if (!card.ReadInputAsync("Input", token).GetAwaiter().GetResult()) throw new Exception("低有效 DI 状态错误");
            if (!SendRaw(port, "{\"command\":\"invalidCommand\"}").Contains("\"ok\":false")) throw new Exception("未知命令未返回失败");
            var axis = card.Axes.Single();
            axis.EnableServoAsync(token).GetAwaiter().GetResult();
            axis.HomeAsync(token).GetAwaiter().GetResult();
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
            card.DisconnectAsync(token).GetAwaiter().GetResult();
            AssertLeaseStopsAfterExit(port);
            AssertLeaseStopsAfterLostReply(port);
            var unreachable = new VirtualMotionCard("Offline", new[] { definition }, new[] { input }, new[] { output }, 18522);
            bool connectionFailed = false;
            try { unreachable.ConnectAsync(token).GetAwaiter().GetResult(); }
            catch (IOException) { connectionFailed = true; }
            if (!connectionFailed || unreachable.IsConnected) throw new Exception("离线控制器未正确报告连接失败");
            AssertRejectedIdentity(definition, input, output, "{\"ok\":true}");
            AssertRejectedIdentity(definition, input, output, "{\"ok\":true,\"controllerType\":\"Other\",\"protocolVersion\":1}");
            AssertRejectedIdentity(definition, input, output, "{\"ok\":true,\"controllerType\":\"MotionControl.VirtualController\",\"protocolVersion\":1}");
            Console.WriteLine("虚拟控制器适配器往返检查通过。");
        }
        /// <summary>启动运动后直接退出，模拟 Host 未执行任何断开清理。</summary>
        private static void StartMotionAndExit(int port)
        {
            var axisDefinition = new AxisHardwareDefinition { Name = "X", ControllerId = "Test", AxisNumber = 1, SoftLimitEnabled = false };
            var card = new VirtualMotionCard("Test", new[] { axisDefinition }, new DigitalIoHardwareDefinition[0], new DigitalIoHardwareDefinition[0], port);
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
                var card = new VirtualMotionCard("Test", new[] { definition }, new DigitalIoHardwareDefinition[0], new DigitalIoHardwareDefinition[0], proxyPort);
                card.ConnectAsync(CancellationToken.None).GetAwaiter().GetResult();
                bool failed = false;
                try { card.Axes.Single().MoveAbsoluteAsync(20, new AxisMotionProfile(1, 1, 1), CancellationToken.None).GetAwaiter().GetResult(); }
                catch (IOException) { failed = true; }
                if (!failed || card.IsConnected) throw new Exception("丢失定位响应未报告通信失败");
                if (!SendRaw(port, "{\"command\":\"readAxis\",\"axis\":1}").Contains("\"moving\":true"))
                    throw new Exception("代理没有放行定位指令");
                Thread.Sleep(3000);
                if (!SendRaw(port, "{\"command\":\"readAxis\",\"axis\":1}").Contains("\"moving\":false"))
                    throw new Exception("丢失定位响应后看门狗未停车");
            }
            finally { listener.Stop(); }
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
                var card = new VirtualMotionCard("Test", new[] { axis }, new[] { input }, new[] { output }, port);
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
