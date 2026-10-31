using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Configuration;
using MotionControl.Framework.Contracts.IO;

namespace MotionControl.Framework.Runtime.Configuration
{
    /// <summary>核对在线替换 DO 映射前，旧映射是否仍控制着有效输出。</summary>
    public static class IoOutputMappingSafety
    {
        /// <summary>旧输出被删除、重命名、改通道或改极性时，必须先读取并确认该输出无效。</summary>
        public static async Task EnsureChangedOutputsInactiveAsync(ILogicalIo io,
            IEnumerable<DigitalIoHardwareDefinition> oldOutputs,
            IEnumerable<DigitalIoHardwareDefinition> newOutputs,
            CancellationToken cancellationToken)
        {
            if (io == null) throw new ArgumentNullException(nameof(io));
            if (oldOutputs == null) throw new ArgumentNullException(nameof(oldOutputs));
            if (newOutputs == null) throw new ArgumentNullException(nameof(newOutputs));

            var newByName = newOutputs.ToDictionary(point => point.Name, StringComparer.OrdinalIgnoreCase);
            foreach (DigitalIoHardwareDefinition oldPoint in oldOutputs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DigitalIoHardwareDefinition replacement;
                if (newByName.TryGetValue(oldPoint.Name, out replacement)
                    && string.Equals(oldPoint.ControllerId, replacement.ControllerId, StringComparison.OrdinalIgnoreCase)
                    && oldPoint.Channel == replacement.Channel && oldPoint.ActiveHigh == replacement.ActiveHigh)
                    continue;

                bool active;
                try { active = await io.ReadOutputAsync(oldPoint.Name, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    throw new InvalidOperationException("无法确认原输出 '" + oldPoint.Name
                        + "' 的状态，不能修改或删除该映射；请先检查输出和连接。", error);
                }
                if (active)
                    throw new InvalidOperationException("原输出 '" + oldPoint.Name
                        + "' 当前有效，不能修改或删除该映射；请按设备流程先关闭输出并确认机构安全。");
            }
        }
    }
}
