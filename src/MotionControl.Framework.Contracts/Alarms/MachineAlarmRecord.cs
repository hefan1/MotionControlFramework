using System;

namespace MotionControl.Framework.Contracts.Alarms
{
    /// <summary>一个工位或整机报警的当前记录。</summary>
    public sealed class MachineAlarmRecord
    {
        /// <summary>创建包含报警来源、触发状态和发生时间的报警记录。</summary>
        public MachineAlarmRecord(
            MachineAlarmDefinition definition,
            string sourceName,
            string detail,
            bool conditionActive,
            DateTime firstRaisedAtUtc,
            DateTime lastChangedAtUtc)
            : this(definition, sourceName, detail, conditionActive, firstRaisedAtUtc, lastChangedAtUtc, null)
        {
        }

        /// <summary>创建带逻辑输入上下文的报警记录；无法确定单一输入时不提供定位。</summary>
        public MachineAlarmRecord(
            MachineAlarmDefinition definition,
            string sourceName,
            string detail,
            bool conditionActive,
            DateTime firstRaisedAtUtc,
            DateTime lastChangedAtUtc,
            string relatedInputName,
            bool inputAssociationAmbiguous = false)
        {
            Definition = definition;
            SourceName = sourceName;
            Detail = detail;
            ConditionActive = conditionActive;
            FirstRaisedAtUtc = firstRaisedAtUtc;
            LastChangedAtUtc = lastChangedAtUtc;
            RelatedInputName = inputAssociationAmbiguous ? null : relatedInputName;
            InputAssociationAmbiguous = inputAssociationAmbiguous;
        }

        /// <summary>报警代码、说明和停机类型定义。</summary>
        public MachineAlarmDefinition Definition { get; private set; }

        /// <summary>报告该报警的工位名称或整机名称。</summary>
        public string SourceName { get; private set; }

        /// <summary>设备项目提供的故障补充信息；没有补充信息时为空。</summary>
        public string Detail { get; private set; }

        /// <summary>报警明确关联的逻辑 DI；未知或多个信号共用同一报警记录时为空。</summary>
        public string RelatedInputName { get; private set; }

        /// <summary>同一报警记录已收到不同的关联输入，不能再定位为单个信号。</summary>
        public bool InputAssociationAmbiguous { get; private set; }

        /// <summary>底层触发条件当前是否仍然存在。</summary>
        public bool ConditionActive { get; private set; }

        /// <summary>停机报警在人工复位前一直保留。</summary>
        public bool RequiresReset
        {
            get { return Definition.Kind == MachineAlarmKind.StopMachine; }
        }

        /// <summary>本次报警记录首次触发的 UTC 时间。</summary>
        public DateTime FirstRaisedAtUtc { get; private set; }

        /// <summary>本次报警记录最近一次状态变化的 UTC 时间。</summary>
        public DateTime LastChangedAtUtc { get; private set; }
    }
}
