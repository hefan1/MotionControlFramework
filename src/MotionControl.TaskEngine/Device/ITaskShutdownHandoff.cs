using System.Threading.Tasks;

namespace MotionControl.TaskEngine.Device
{
    /// <summary>任务设备可选的待确认资源持久退出交接；不代表现场或生产状态已恢复。</summary>
    public interface ITaskShutdownHandoff
    {
        /// <summary>复核并保留所有隔离记录、停轴及输出保持，再封存运行时；不得放行旧任务。</summary>
        Task PreserveBlockedForShutdownAsync(object owner);
    }
}
