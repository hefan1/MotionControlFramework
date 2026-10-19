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

        /// <summary>控制卡当前是否已成功建立连接。</summary>
        bool IsConnected { get; }

        /// <summary>
        /// 应用硬件配置后，此卡提供的逻辑轴列表。连接前也必须可读取，供 Runtime 启动时检查名称。
        /// </summary>
        IReadOnlyCollection<IAxis> Axes { get; }

        /// <summary>建立控制卡连接；连接失败或操作被取消时报告错误，不返回假成功。</summary>
        Task ConnectAsync(CancellationToken cancellationToken);

        /// <summary>断开控制卡连接并释放适配器持有的厂商资源。</summary>
        Task DisconnectAsync(CancellationToken cancellationToken);
    }
}
