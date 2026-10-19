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

        /// <summary>报警代码、说明和停机类型定义。</summary>
        public MachineAlarmDefinition Definition { get; private set; }

        /// <summary>报告该报警的工位名称或整机名称。</summary>
        public string SourceName { get; private set; }

        /// <summary>设备项目提供的故障补充信息；没有补充信息时为空。</summary>
        public string Detail { get; private set; }

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
