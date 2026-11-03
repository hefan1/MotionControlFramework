using System.Collections.Generic;
using System.Linq;
using System;
using System.Threading.Tasks;

namespace MotionControl.TaskEngine.Resources
{
    /// <summary>未接入运行入口的内部清料资源凭证；只保留声明范围，不授权设备动作。</summary>
    internal sealed class TaskClearanceLease
    {
        /// <summary>设备明确声明的独占资源名称，包含原隔离资源。</summary>
        internal IReadOnlyList<string> Resources { get; }
        /// <summary>已登记但尚未完成收尾的清料动作数，由协调器锁保护。</summary>
        internal int InFlight;
        /// <summary>完整清料序列的独占登记，动作间隙也必须保持有效。</summary>
        internal TaskResourceCoordinator.ClearanceSequence Sequence;
        /// <summary>登记中的资源；同一轴或输出禁止重复并发动作。</summary>
        internal readonly HashSet<string> ActiveResources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>完整清料资源记录的写入任务，完成前不能开始设备动作。</summary>
        internal Task JournalWrite = Task.CompletedTask;
        /// <summary>动作或停止的结果无法确认时永久锁存，不能通过归还租约绕过隔离。</summary>
        internal string UnconfirmedReason;

        /// <summary>冻结资源列表，防止调用方修改原集合。</summary>
        internal TaskClearanceLease(IEnumerable<string> resources)
        {
            Resources = resources.ToList().AsReadOnly();
        }
    }
}
