using System.Threading;
using System.Threading.Tasks;

namespace MotionControl.Framework.Contracts.Safety
{
    /// <summary>读取机器安全输入的当前状态；安全停机仍由独立硬件安全回路完成。</summary>
    public interface IMachineSafetyStatus
    {
        /// <summary>读取急停回路是否处于触发状态；true 表示急停已按下或安全回路断开。</summary>
        Task<bool> ReadEmergencyStopActiveAsync(CancellationToken cancellationToken);
    }
}
