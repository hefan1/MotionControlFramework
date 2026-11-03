using System.Collections.Generic;
using System.Threading.Tasks;

namespace MotionControl.Framework.Runtime.Machine
{
    /// <summary>已接管设备的执行入口提供清料资源保护；Runtime 不依赖具体任务执行器。</summary>
    internal interface IControlledClearanceResources
    {
        /// <summary>继承全部待确认资源并独占声明范围；动作前必须等待返回凭证的持久化任务。</summary>
        IControlledClearanceReservation ReserveClearance(IReadOnlyCollection<string> requiredResources);
    }

    /// <summary>清料资源凭证。失败后必须锁存，不能以释放对象代替现场核查。</summary>
    internal interface IControlledClearanceReservation
    {
        /// <summary>完整保护范围写入持久证据的任务，失败前禁止动作。</summary>
        Task Ready { get; }
        /// <summary>有任何动作或收尾结果未知时锁存全部保护范围。</summary>
        void MarkUnconfirmed(string reason);
        /// <summary>只有全部动作及其物理反馈均确认成功时才能结束清料租约；原隔离仍保留。</summary>
        void Complete();
        /// <summary>经设备条件与确认记录复核后结束失败清料租约，原隔离仍保留。</summary>
        void ConfirmRecovery();
    }
}
