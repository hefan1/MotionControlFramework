using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MotionControl.Framework.Contracts.IO
{
    /// <summary>为 IO 监控页提供可选的多通道读取能力，减少控制卡通讯请求数量。</summary>
    public interface IPhysicalDigitalIoBatchMonitor
    {
        /// <summary>批量读取物理 DI；返回值按通道号索引，true 表示物理高电平。</summary>
        Task<IReadOnlyDictionary<int, bool>> ReadDigitalInputChannelsAsync(
            IReadOnlyCollection<int> channels,
            CancellationToken cancellationToken);

        /// <summary>批量读取物理 DO；返回值按通道号索引，true 表示控制卡输出高电平。</summary>
        Task<IReadOnlyDictionary<int, bool>> ReadDigitalOutputChannelsAsync(
            IReadOnlyCollection<int> channels,
            CancellationToken cancellationToken);
    }
}
