using System.Threading.Tasks;

namespace MotionControl.TaskEngine.Device
{
    /// <summary>有持久资源记录的设备须在关闭会话前完成安全清理。</summary>
    public interface ITaskResourceJournalCleanup
    {
        /// <summary>校验动作及资源已收尾后异步清理；失败必须保留设备控制权。</summary>
        Task CompleteResourceJournalAsync();
    }
}
