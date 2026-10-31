using System.Collections.Generic;

namespace MotionControl.Framework.Contracts.Alarms
{
    /// <summary>可将同一故障条件关联的多条报警一次性发布到当前报警快照。</summary>
    public interface IMachineAlarmBatchService
    {
        /// <summary>原子更新同一来源下的一组报警，并在全部记录更新后统一通知订阅者。</summary>
        void ReportConditions(
            IEnumerable<MachineAlarmDefinition> definitions,
            string sourceName,
            bool conditionActive,
            string detail);
    }
}
