using System;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace MotionControl.VirtualController
{
    /// <summary>创建仅供当前用户本机连接的控制器管道，拒绝远程客户端。</summary>
    internal static class LocalControllerPipe
    {
        /// <summary>管道允许服务端与客户端双向读写。</summary>
        private const uint AccessDuplex = 0x00000003;
        /// <summary>使用异步句柄以支持有限时间的读取。</summary>
        private const uint FileFlagOverlapped = 0x40000000;
        /// <summary>要求 Windows 拒绝远程客户端连接。</summary>
        private const uint RejectRemoteClients = 0x00000008;
        /// <summary>允许多个本机请求连接同时等待服务。</summary>
        private const uint UnlimitedInstances = 255;

        /// <summary>Windows 管道创建所需的安全描述符和句柄继承设置。</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct SecurityAttributes
        {
            /// <summary>此结构的字节数。</summary>
            public int Length;
            /// <summary>固定在内存中的安全描述符。</summary>
            public IntPtr Descriptor;
            /// <summary>不允许子进程继承管道句柄。</summary>
            public int InheritHandle;
        }

        /// <summary>通过 Windows 原生参数显式拒绝远程客户端。</summary>
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafePipeHandle CreateNamedPipeW(string name, uint openMode, uint pipeMode,
            uint maxInstances, uint outputBufferSize, uint inputBufferSize, uint defaultTimeout, ref SecurityAttributes attributes);

        /// <summary>创建只允许当前用户访问、且拒绝网络客户端的异步管道实例。</summary>
        internal static NamedPipeServerStream Create(string name)
        {
            SecurityIdentifier user;
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                user = identity.User ?? throw new InvalidOperationException("无法确认软件控制器的当前用户。");
            var security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
            security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
            byte[] descriptor = security.GetSecurityDescriptorBinaryForm();
            GCHandle pinned = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
            try
            {
                var attributes = new SecurityAttributes
                {
                    Length = Marshal.SizeOf(typeof(SecurityAttributes)),
                    Descriptor = pinned.AddrOfPinnedObject(),
                    InheritHandle = 0
                };
                SafePipeHandle handle = CreateNamedPipeW(@"\\.\pipe\" + name, AccessDuplex | FileFlagOverlapped,
                    RejectRemoteClients, UnlimitedInstances, 4096, 4096, 0, ref attributes);
                if (handle.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error();
                    handle.Dispose();
                    throw new IOException("无法创建本机软件控制器管道。", new Win32Exception(error));
                }
                try { return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle); }
                catch { handle.Dispose(); throw; }
            }
            finally { pinned.Free(); }
        }
    }
}
