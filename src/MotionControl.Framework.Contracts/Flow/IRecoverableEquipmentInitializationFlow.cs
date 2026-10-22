using System.Threading;
using System.Threading.Tasks;

namespace MotionControl.Framework.Contracts.Flow
{
    /// <summary>允许设备在软件重启后检查现场状态，并安全跳过已完成的启动初始化。</summary>
    public interface IRecoverableEquipmentInitializationFlow
    {
        /// <summary>
        /// 控制卡重新连接后读取当前轴、传感器和设备状态；只有现场状态确实满足生产条件时才返回 true。
        /// 不得仅凭恢复文件返回 true，也不得在此处把未回零的轴标记成已回零。
        /// 返回 false 时 Runtime 会照常执行完整初始化流程。
        /// </summary>
        Task<bool> CanSkipAfterRestartAsync(CancellationToken cancellationToken);
    }
}
