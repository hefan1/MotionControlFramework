using System.Threading;
using System.Threading.Tasks;

namespace MotionControl.Framework.Contracts.Flow
{
    /// <summary>设备项目中一个独立工位的代码流程。</summary>
    public interface IStationFlow
    {
        /// <summary>工位名称，在本设备内唯一。</summary>
        string StationName { get; }

        /// <summary>当前正在执行或等待恢复的步骤名称，供运行状态和报警定位使用。</summary>
        string CurrentStepName { get; }

        /// <summary>
        /// 执行当前一步并在完成后返回。步骤完成前不得更新到下一步；收到取消时保留当前步骤，
        /// 并让正在进行的轴动作按受控停止语义结束。
        /// </summary>
        Task ExecuteCurrentStepAsync(CancellationToken cancellationToken);

        /// <summary>清除工位可复位的报警状态，但保留 CurrentStepName。</summary>
        Task ResetAsync(CancellationToken cancellationToken);
    }
}
