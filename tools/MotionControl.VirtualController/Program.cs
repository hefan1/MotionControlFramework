using System;
using System.IO;
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
            if (args.Length > 1 || (args.Length == 1 && (!int.TryParse(args[0], out port) || port < 1 || port > 65535)))
            {
                Console.Error.WriteLine("用法：MotionControl.VirtualController.exe [本机端口]");
                return 2;
            }

            try
            {
                var state = new ControllerState();
                var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
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

        /// <summary>解析请求并返回结果；错误携带原因，不以成功掩盖。</summary>
        private static void HandleClient(TcpClient client, ControllerState state)
        {
            using (client)
            using (NetworkStream stream = client.GetStream())
            using (var reader = new StreamReader(stream, new UTF8Encoding(false)))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true })
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
