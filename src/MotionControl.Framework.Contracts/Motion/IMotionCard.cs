using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MotionControl.Framework.Contracts.Motion
{
    /// <summary>一张已经按设备硬件配置建立的物理运动控制卡实例。</summary>
    public interface IMotionCard
    {
        /// <summary>来自本机硬件配置的稳定控制卡标识。</summary>
        string ControllerId { get; }

        bool IsConnected { get; }

        /// <summary>
        /// 应用硬件配置后，此卡提供的逻辑轴列表。连接前也必须可读取，供 Runtime 启动时检查名称。
        /// </summary>
        IReadOnlyCollection<IAxis> Axes { get; }

        Task ConnectAsync(CancellationToken cancellationToken);

        Task DisconnectAsync(CancellationToken cancellationToken);
    }
}
