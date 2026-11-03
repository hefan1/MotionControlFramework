using System.Threading.Tasks;

namespace MotionControl.Framework.Contracts.Alarms
{
    /// <summary>等待已经提交的停机报警交接给同步事件订阅者。</summary>
    public interface IMachineStopAlarmNotificationDrain
    {
        /// <summary>等待当前已登记的停机通知完成；通知失败时返回异常。</summary>
        Task WaitForStopNotificationDispatchAsync();
    }
}
