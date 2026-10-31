using System.Collections.Generic;

namespace MotionControl.Framework.Contracts.Alarms
{
    /// <summary>可选能力：分别跟踪同一报警下的多个条件，并记录明确关联的逻辑输入。</summary>
    public interface IMachineAlarmConditionContextService
    {
        /// <summary>条件标识在同一报警来源内唯一且在报告、恢复时保持一致；输入未知时为空。</summary>
        void ReportConditionGroup(IEnumerable<MachineAlarmDefinition> definitions, string sourceName,
            bool conditionActive, string detail, string conditionKey, string inputName);
    }
}
