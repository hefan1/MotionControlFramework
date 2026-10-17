using System;
using System.Collections.Generic;

namespace MotionControl.Framework.Contracts.Alarms
{
    /// <summary>记录设备报警，并按停机/非停机规则处理条件变化。</summary>
    public interface IMachineAlarmService
    {
        /// <summary>当前显示的报警；恢复待复位的停机报警仍包含在集合中。</summary>
        IReadOnlyCollection<MachineAlarmRecord> Alarms { get; }

        /// <summary>报警列表新增、更新或清除时通知界面；事件在调用报告或复位的线程上触发。</summary>
        event EventHandler AlarmsChanged;

        /// <summary>存在尚未复位的停机报警时返回 true。</summary>
        bool HasLatchedStopAlarm { get; }

        /// <summary>有新的停机报警触发时通知整机运行时。</summary>
        event EventHandler<MachineAlarmRaisedEventArgs> StopAlarmRaised;

        /// <summary>报告某个报警条件当前是否成立。非停机报警在条件恢复时自动移除。</summary>
        void ReportCondition(
            MachineAlarmDefinition definition,
            string sourceName,
            bool conditionActive,
            string detail);

        /// <summary>清除条件已恢复的停机报警；条件仍成立时拒绝复位。</summary>
        void ResetStopAlarms();

        /// <summary>在执行轴和工位复位前，检查停机报警条件是否已经恢复。</summary>
        void EnsureStopAlarmConditionsRecovered();
    }
}
