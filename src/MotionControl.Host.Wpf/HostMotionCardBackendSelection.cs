using System;
using System.Diagnostics;
using System.IO;
using MotionControl.Framework.Runtime.Configuration;

namespace MotionControl.Host.Wpf
{
    /// <summary>根据单独的工程配置选择运动卡后端，不改动设备的卡与轴映射。</summary>
    internal static class HostMotionCardBackendSelection
    {
        /// <summary>后端选择文件；缺省时始终选择原有正运动连接。</summary>
        internal const string ConfigurationPath = "Parameters/ControllerBackend.json";

        /// <summary>读取后端选择并显式创建连接；错误配置不回退到其他控制器。</summary>
        internal static IMotionCardHostConnection Create(MachineConfigurationStore store, string machineId)
        {
            string fullPath = Path.Combine(store.GetMachineDirectory(machineId),
                ConfigurationPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
            {
                Trace.TraceInformation("运动卡后端：ZMotion（默认）。");
                return new ZMotionAxisMappingCardConnection(store, machineId);
            }

            HostMotionCardBackendSettings settings = store.LoadJson<HostMotionCardBackendSettings>(machineId, ConfigurationPath);
            if (settings == null || string.IsNullOrWhiteSpace(settings.Backend))
                throw new InvalidOperationException("运动卡后端配置缺少 backend，不能启动设备连接。");

            if (string.Equals(settings.Backend.Trim(), "ZMotion", StringComparison.OrdinalIgnoreCase))
            {
                Trace.TraceInformation("运动卡后端：ZMotion（工程配置）。");
                return new ZMotionAxisMappingCardConnection(store, machineId);
            }

            if (string.Equals(settings.Backend.Trim(), "Virtual", StringComparison.OrdinalIgnoreCase))
            {
                if (settings.Port < 1 || settings.Port > 65535)
                    throw new InvalidOperationException("运动卡后端端口必须在 1 到 65535 之间。");
                Trace.TraceInformation("运动卡后端：Virtual，本机端口 " + settings.Port + "。");
                return new VirtualAxisMappingCardConnection(store, machineId, settings.Port);
            }

            throw new InvalidOperationException("未知运动卡后端 '" + settings.Backend + "'；支持 ZMotion 和 Virtual，已拒绝启动。");
        }
    }

    /// <summary>工程侧的控制器后端选择；虚拟连接始终限定本机。</summary>
    internal sealed class HostMotionCardBackendSettings
    {
        /// <summary>显式选择 ZMotion 或 Virtual。</summary>
        public string Backend { get; set; }

        /// <summary>独立控制器本机 TCP 端口，默认 18520。</summary>
        public int Port { get; set; } = 18520;
    }
}
