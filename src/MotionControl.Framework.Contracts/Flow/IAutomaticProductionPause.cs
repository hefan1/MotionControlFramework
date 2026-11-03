using System.Threading.Tasks;

namespace MotionControl.Framework.Contracts.Flow
{
    /// <summary>任务执行入口的受控暂停能力；任务生产启动前必须提供并能确认动作收尾。</summary>
    public interface IAutomaticProductionPause
    {
        /// <summary>当前任务已暂停且已登记动作和资源写入均已收尾时返回真。</summary>
        bool IsAutomaticPauseConfirmed { get; }

        /// <summary>受控暂停正在执行的生产并等待已登记的动作停止；失败时抛出原因。</summary>
        Task PauseAutomaticProductionAsync();
    }
}
