using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MotionControl.Adapter.MotionCard.Virtual
{
    /// <summary>为默认项目连接本机软件控制器，并在发送每条命令前核验管道的实际服务进程。</summary>
    internal static class VerifiedLocalControllerPipe
    {
        /// <summary>由 Windows 返回已连接管道对端的实际进程号，不能依赖服务自己声明的进程号。</summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

        /// <summary>打开已验证可执行文件路径的本机管道；失败时在任何命令发送前关闭管道。</summary>
        internal static NamedPipeClientStream Connect(int port, string expectedExecutable)
        {
            var pipe = new NamedPipeClientStream(".", "MotionControl.VirtualController." + port.ToString(CultureInfo.InvariantCulture),
                PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                pipe.Connect(2000);
                if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint processId) || processId == 0)
                    throw new IOException("无法确认软件控制器的实际进程。", new Win32Exception(Marshal.GetLastWin32Error()));
                using (Process owner = Process.GetProcessById(checked((int)processId)))
                {
                    string actualPath = Path.GetFullPath(owner.MainModule.FileName);
                    if (!string.Equals(actualPath, expectedExecutable, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("连接的服务不是本程序自带的软件控制器，未发送命令。");
                }
                return pipe;
            }
            catch (Exception error)
            {
                pipe.Dispose();
                if (error is IOException) throw;
                throw new IOException("无法确认本机软件控制器身份，未发送命令。", error);
            }
        }
    }
}
