using System.Threading;
using System.Threading.Tasks;

namespace MotionControl.Framework.Contracts.Flow
{
    /// <summary>设备项目可选提供的启动前初始化步骤，例如回零、移动到安全位或初始化设备输出。</summary>
    public interface IEquipmentInitializationFlow
    {
        /// <summary>返回当前正在执行或等待恢复的初始化步骤名称，供故障信息定位使用。</summary>
        string CurrentStepName { get; }

        /// <summary>初始化所有步骤成功完成后返回 true。</summary>
        bool IsCompleted { get; }

        /// <summary>
        /// 执行一个初始化步骤并在完成后返回。步骤完成前不得推进；收到取消时保留当前步骤，
        /// 并让正在进行的轴动作按受控停止语义结束。
        /// </summary>
        Task ExecuteCurrentStepAsync(CancellationToken cancellationToken);

        /// <summary>清除初始化流程可复位的报警，但保留当前步骤和已完成状态。</summary>
        Task ResetAsync(CancellationToken cancellationToken);

        /// <summary>控制卡开始一个新的连接周期时，将初始化步骤恢复到第一步并清除完成状态。</summary>
        void ResetForNewConnection();
    }
}
