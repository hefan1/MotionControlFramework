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
        {
            Definition = definition;
            SourceName = sourceName;
            Detail = detail;
            ConditionActive = conditionActive;
            FirstRaisedAtUtc = firstRaisedAtUtc;
            LastChangedAtUtc = lastChangedAtUtc;
        }

        public MachineAlarmDefinition Definition { get; private set; }

        public string SourceName { get; private set; }

        public string Detail { get; private set; }

        /// <summary>底层触发条件当前是否仍然存在。</summary>
        public bool ConditionActive { get; private set; }

        /// <summary>停机报警在人工复位前一直保留。</summary>
        public bool RequiresReset
        {
            get { return Definition.Kind == MachineAlarmKind.StopMachine; }
        }

        public DateTime FirstRaisedAtUtc { get; private set; }

        public DateTime LastChangedAtUtc { get; private set; }
    }
}
