using System;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MotionControl.VirtualController
{
    /// <summary>独立的软件控制器进程入口，供本机正式上位机连接。</summary>
    internal static class Program
    {
        /// <summary>默认只监听本机，避免向网络暴露测试控制接口。</summary>
        private const int DefaultPort = 18520;

        /// <summary>启动本地 TCP 服务并为每个连接处理一行或多行请求。</summary>
        private static int Main(string[] args)
        {
            int port = DefaultPort;
            string projectSession = null;
            if ((args.Length != 0 && args.Length != 1 && args.Length != 3)
                || (args.Length > 0 && (!int.TryParse(args[0], out port) || port < 1 || port > 65535))
                || (args.Length == 3 && (args[1] != "--project-session" || !IsProjectSession(args[2]))))
            {
                Console.Error.WriteLine("用法：MotionControl.VirtualController.exe [本机端口 [--project-session 项目会话编号]]");
                return 2;
            }
            if (args.Length == 3) projectSession = args[2];

            try
            {
                var state = new ControllerState(projectSession);
                var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                Task.Run(() => ListenForVerifiedHost(port, state));
                Console.WriteLine("虚拟控制器已启动，监听 127.0.0.1:" + port);
                Console.WriteLine("测试接口仅限本机；关闭窗口可停止本进程。");
                while (true)
                {
                    TcpClient client = listener.AcceptTcpClient();
                    Task.Run(() => HandleClient(client, state));
                }
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("控制器启动或监听失败：" + exception.Message);
                return 1;
            }
        }

        /// <summary>项目会话使用固定长度的小写十六进制摘要，不能将其他命令行内容当成编号。</summary>
        private static bool IsProjectSession(string value)
        {
            if (value == null || value.Length != 64) return false;
            foreach (char item in value)
                if (!((item >= '0' && item <= '9') || (item >= 'a' && item <= 'f'))) return false;
            return true;
        }

        /// <summary>解析请求并返回结果；错误携带原因，不以成功掩盖。</summary>
        private static void HandleClient(TcpClient client, ControllerState state)
        {
            using (client)
            using (NetworkStream stream = client.GetStream())
                HandleStream(stream, state);
        }

        /// <summary>提供本机管道供默认项目核验 Windows 返回的实际服务进程，原有 TCP 接口仍保留。</summary>
        private static void ListenForVerifiedHost(int port, ControllerState state)
        {
            string name = "MotionControl.VirtualController." + port;
            try
            {
                while (true)
                {
                    var pipe = LocalControllerPipe.Create(name);
                    try
                    {
                        pipe.WaitForConnection();
                        Task.Run(() => { using (pipe) HandleStream(pipe, state); });
                    }
                    catch { pipe.Dispose(); throw; }
                }
            }
            catch (Exception error) { Console.Error.WriteLine("软件控制器本机核验通道不可用：" + error.Message); }
        }

        /// <summary>TCP 和已验证的本机管道使用同一套请求、租约和状态处理。</summary>
        private static void HandleStream(Stream stream, ControllerState state)
        {
            using (var reader = new StreamReader(stream, new UTF8Encoding(false), false, 1024, true))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, true) { AutoFlush = true })
            {
                try
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        JObject response;
                        try
                        {
                            response = state.Execute(JObject.Parse(line));
                        }
                        catch (Exception exception)
                        {
                            response = new JObject { ["ok"] = false, ["error"] = exception.Message };
                        }

                        writer.WriteLine(response.ToString(Formatting.None));
                    }
                }
                catch (IOException)
                {
                    // 客户端中断连接不会改变已接收命令的执行状态。
                }
            }
        }
    }
}
