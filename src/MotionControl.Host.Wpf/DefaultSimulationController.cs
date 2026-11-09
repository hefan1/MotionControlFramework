using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace MotionControl.Host.Wpf
{
    /// <summary>为本机默认仿真项目启动或复用本程序自带的软件控制器，不改动其轴和 IO 状态。</summary>
    internal static class DefaultSimulationController
    {
        /// <summary>确认端口是本程序的软件控制器；端口空闲时启动隐藏的独立进程。</summary>
        internal static void EnsureRunning(int port, string executablePath, string projectSession)
        {
            if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
            if (string.IsNullOrWhiteSpace(projectSession) || projectSession.Length != 64)
                throw new ArgumentException("缺少有效的项目控制器会话编号。", nameof(projectSession));
            string expectedPath = Path.GetFullPath(executablePath);
            if (!File.Exists(expectedPath)) throw new FileNotFoundException("缺少本程序自带的软件控制器，请重新构建。", expectedPath);

            // 通过独占绑定判断端口是否空闲；沙盒中监听表可能不完整，空端口的连接请求也可能超时。
            var probe = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
            bool portFree;
            try { probe.Start(); portFree = true; }
            catch (SocketException) { portFree = false; }
            finally { probe.Stop(); }

            JObject identity = null;
            Process started = null;
            if (portFree)
            {
                started = Process.Start(new ProcessStartInfo(expectedPath, port.ToString(CultureInfo.InvariantCulture) + " --project-session " + projectSession)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = Path.GetDirectoryName(expectedPath)
                });
                if (started == null) throw new InvalidOperationException("软件控制器进程未能启动。");
                Thread.Sleep(300);
            }

            try
            {
                DateTime deadline = DateTime.UtcNow.AddSeconds(6);
                while (identity == null && DateTime.UtcNow < deadline)
                {
                    if (started != null && started.HasExited) throw new InvalidOperationException("软件控制器启动后退出，请检查端口。");
                    identity = TryReadIdentity(port);
                    if (identity != null) break;
                    Thread.Sleep(100);
                }
                if (identity == null) throw new TimeoutException("软件控制器没有在预定时间内启动。");
                int processId = (int?)identity["processId"] ?? 0;
                if ((bool?)identity["ok"] != true || (string)identity["controllerType"] != "MotionControl.VirtualController"
                    || (int?)identity["protocolVersion"] != 2 || processId <= 0)
                    throw new InvalidOperationException("测试端口的软件控制器身份或进程号不受支持。");
                if (!string.Equals((string)identity["projectSession"], projectSession, StringComparison.Ordinal))
                    throw new InvalidOperationException("端口上的软件控制器属于其他项目，未接管该服务。");
                if (started != null && (started.HasExited || started.Id != processId))
                    throw new InvalidOperationException("端口响应不属于本次启动的控制器，未接管该服务。");
                using (Process owner = Process.GetProcessById(processId))
                {
                    string actualPath = Path.GetFullPath(owner.MainModule.FileName);
                    if (!string.Equals(actualPath, expectedPath, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("端口上的软件控制器不是本程序自带版本，未接管该服务。");
                }
            }
            finally { started?.Dispose(); }
        }

        /// <summary>只发送身份查询；明确拒绝连接表示尚未监听，其他故障不能伪装成空闲。</summary>
        private static JObject TryReadIdentity(int port)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    IAsyncResult pending = client.BeginConnect(IPAddress.Loopback, port, null, null);
                    using (WaitHandle wait = pending.AsyncWaitHandle)
                        if (!wait.WaitOne(TimeSpan.FromSeconds(2))) throw new TimeoutException("读取软件控制器身份超时。");
                    client.EndConnect(pending);
                    using (NetworkStream stream = client.GetStream())
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true })
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        stream.ReadTimeout = 2000;
                        writer.WriteLine("{\"command\":\"ping\"}");
                        string line = reader.ReadLine();
                        if (line == null) throw new InvalidOperationException("软件控制器没有返回身份。");
                        return JObject.Parse(line);
                    }
                }
            }
            catch (SocketException error) when (error.SocketErrorCode == SocketError.ConnectionRefused) { return null; }
        }
    }
}
