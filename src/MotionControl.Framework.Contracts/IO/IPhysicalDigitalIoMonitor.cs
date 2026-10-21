using System.Threading;
using System.Threading.Tasks;

namespace MotionControl.Framework.Contracts.IO
{
    /// <summary>为 IO 监控页提供按物理通道读写数字 IO 的可选能力。</summary>
    public interface IPhysicalDigitalIoMonitor
    {
        /// <summary>读取指定物理 DI 通道的原始电平；true 表示物理高电平。</summary>
        Task<bool> ReadDigitalInputChannelAsync(int channel, CancellationToken cancellationToken);

        /// <summary>读取指定物理 DO 通道的原始输出电平；true 表示控制卡输出高电平。</summary>
        Task<bool> ReadDigitalOutputChannelAsync(int channel, CancellationToken cancellationToken);

        /// <summary>设置指定物理 DO 通道的原始电平；true 表示写入高电平。</summary>
        Task WriteDigitalOutputChannelAsync(int channel, bool physicalHigh, CancellationToken cancellationToken);
    }
}
