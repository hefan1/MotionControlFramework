using System;
using System.Collections.Generic;
using System.Linq;
using MotionControl.Framework.Contracts.Alarms;

namespace MotionControl.Framework.Runtime.Alarms
{
    /// <summary>按设备报警规则保存当前报警，并处理条件恢复和人工复位。</summary>
    public sealed class MachineAlarmService : IMachineAlarmService
    {
        /// <summary>保护报警集合，避免流程线程与界面线程同时读写。</summary>
        private readonly object _sync = new object();

        /// <summary>按报警来源和报警代码保存当前报警记录。</summary>
        private readonly Dictionary<string, Dictionary<string, MachineAlarmRecord>> _alarmsBySource =
            new Dictionary<string, Dictionary<string, MachineAlarmRecord>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>新停机报警首次出现时触发，供整机运行时立即暂停流程。</summary>
        public event EventHandler<MachineAlarmRaisedEventArgs> StopAlarmRaised;

        /// <summary>报警新增、条件变化或清除后触发，供界面刷新报警列表。</summary>
        public event EventHandler AlarmsChanged;

        /// <summary>返回按报警来源和代码排序的当前报警只读快照。</summary>
        public IReadOnlyCollection<MachineAlarmRecord> Alarms
        {
            get
            {
                lock (_sync)
                {
                    return _alarmsBySource
                        .SelectMany(source => source.Value.Values)
                        .OrderBy(alarm => alarm.SourceName, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(alarm => alarm.Definition.Code, StringComparer.OrdinalIgnoreCase)
                        .ToList()
                        .AsReadOnly();
                }
            }
        }

        /// <summary>存在需要人工复位的停机报警时返回 true。</summary>
        public bool HasLatchedStopAlarm
        {
            get
            {
                lock (_sync)
                {
                    return _alarmsBySource
                        .SelectMany(source => source.Value.Values)
                        .Any(alarm => alarm.Definition.Kind == MachineAlarmKind.StopMachine);
                }
            }
        }

        /// <summary>报告报警条件变化；非停机报警在条件恢复时自动清除。</summary>
        public void ReportCondition(
            MachineAlarmDefinition definition,
            string sourceName,
            bool conditionActive,
            string detail)
        {
            if (definition == null)
            {
                throw new ArgumentNullException(nameof(definition));
            }

            if (string.IsNullOrWhiteSpace(sourceName))
            {
                throw new ArgumentException("报警来源不能为空，例如工位名称或整机名称。", nameof(sourceName));
            }

            sourceName = sourceName.Trim();

            MachineAlarmRecord changedAlarm = null;
            var alarmsChanged = false;
            var newStopAlarm = false;

            lock (_sync)
            {
                Dictionary<string, MachineAlarmRecord> sourceAlarms;
                if (!_alarmsBySource.TryGetValue(sourceName, out sourceAlarms))
                {
                    if (!conditionActive)
                    {
                        return;
                    }

                    sourceAlarms = new Dictionary<string, MachineAlarmRecord>(StringComparer.OrdinalIgnoreCase);
                    _alarmsBySource.Add(sourceName, sourceAlarms);
                }

                MachineAlarmRecord existingAlarm;
                if (!sourceAlarms.TryGetValue(definition.Code, out existingAlarm))
                {
                    if (!conditionActive)
                    {
                        return;
                    }

                    var now = DateTime.UtcNow;
                    changedAlarm = new MachineAlarmRecord(
                        definition,
                        sourceName,
                        detail,
                        true,
                        now,
                        now);
                    sourceAlarms.Add(definition.Code, changedAlarm);
                    alarmsChanged = true;
                    newStopAlarm = definition.Kind == MachineAlarmKind.StopMachine;
                }
                else
                {
                    EnsureSameDefinition(existingAlarm.Definition, definition, sourceName);

                    if (!conditionActive && definition.Kind == MachineAlarmKind.NonStop)
                    {
                        sourceAlarms.Remove(definition.Code);
                        if (sourceAlarms.Count == 0)
                        {
                            _alarmsBySource.Remove(sourceName);
                        }

                        alarmsChanged = true;
                    }
                    else if (existingAlarm.ConditionActive != conditionActive
                        || (conditionActive && !string.Equals(existingAlarm.Detail, detail, StringComparison.Ordinal)))
                    {
                        changedAlarm = new MachineAlarmRecord(
                            existingAlarm.Definition,
                            existingAlarm.SourceName,
                            conditionActive ? detail : existingAlarm.Detail,
                            conditionActive,
                            existingAlarm.FirstRaisedAtUtc,
                            DateTime.UtcNow);
                        sourceAlarms[definition.Code] = changedAlarm;
                        alarmsChanged = true;
                    }
                }
            }

            NotifyChanges(newStopAlarm ? changedAlarm : null, alarmsChanged);
        }

        /// <summary>停机报警条件全部恢复后，清除已锁存的停机报警。</summary>
        public void ResetStopAlarms()
        {
            var alarmsChanged = false;

            lock (_sync)
            {
                EnsureStopAlarmConditionsRecoveredCore();

                var recoveredStopAlarms = _alarmsBySource
                    .SelectMany(source => source.Value.Values)
                    .Where(alarm => alarm.Definition.Kind == MachineAlarmKind.StopMachine)
                    .ToList();

                foreach (var alarm in recoveredStopAlarms)
                {
                    var sourceAlarms = _alarmsBySource[alarm.SourceName];
                    sourceAlarms.Remove(alarm.Definition.Code);
                    if (sourceAlarms.Count == 0)
                    {
                        _alarmsBySource.Remove(alarm.SourceName);
                    }
                }

                alarmsChanged = recoveredStopAlarms.Count > 0;
            }

            NotifyChanges(null, alarmsChanged);
        }

        /// <summary>检查所有停机报警条件，仍有条件成立时拒绝复位。</summary>
        public void EnsureStopAlarmConditionsRecovered()
        {
            lock (_sync)
            {
                EnsureStopAlarmConditionsRecoveredCore();
            }
        }

        /// <summary>确认同一报警代码在运行期间没有改变类别、说明或操作指导。</summary>
        private static void EnsureSameDefinition(
            MachineAlarmDefinition existingDefinition,
            MachineAlarmDefinition reportedDefinition,
            string sourceName)
        {
            if (existingDefinition.Kind != reportedDefinition.Kind
                || !string.Equals(existingDefinition.Message, reportedDefinition.Message, StringComparison.Ordinal)
                || !string.Equals(existingDefinition.CheckGuidance, reportedDefinition.CheckGuidance, StringComparison.Ordinal)
                || !string.Equals(existingDefinition.RecoveryGuidance, reportedDefinition.RecoveryGuidance, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "报警 '" + sourceName + "/" + reportedDefinition.Code
                    + "' 在运行期间使用了不一致的类别、说明或操作指导。");
            }
        }

        /// <summary>在报警锁内确认所有停机报警条件均已恢复。</summary>
        private void EnsureStopAlarmConditionsRecoveredCore()
        {
            var activeStopAlarms = _alarmsBySource
                .SelectMany(source => source.Value.Values)
                .Where(alarm => alarm.Definition.Kind == MachineAlarmKind.StopMachine && alarm.ConditionActive)
                .ToList();

            if (activeStopAlarms.Count > 0)
            {
                var alarmNames = activeStopAlarms
                    .Select(alarm => alarm.SourceName + "/" + alarm.Definition.Code)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase);
                throw new InvalidOperationException(
                    "停机报警条件仍成立，无法复位：" + string.Join("、", alarmNames) + "。");
            }
        }

        /// <summary>触发报警事件并汇总订阅者处理失败的信息。</summary>
        private void NotifyChanges(MachineAlarmRecord newStopAlarm, bool alarmsChanged)
        {
            var notificationErrors = new List<Exception>();

            if (newStopAlarm != null)
            {
                var stopAlarmRaised = StopAlarmRaised;
                if (stopAlarmRaised != null)
                {
                    try
                    {
                        stopAlarmRaised(this, new MachineAlarmRaisedEventArgs(newStopAlarm));
                    }
                    catch (Exception error)
                    {
                        notificationErrors.Add(error);
                    }
                }
            }

            if (alarmsChanged)
            {
                var alarmsChangedHandler = AlarmsChanged;
                if (alarmsChangedHandler != null)
                {
                    try
                    {
                        alarmsChangedHandler(this, EventArgs.Empty);
                    }
                    catch (Exception error)
                    {
                        notificationErrors.Add(error);
                    }
                }
            }

            if (notificationErrors.Count == 1)
            {
                throw new InvalidOperationException("通知报警变化时发生错误。", notificationErrors[0]);
            }

            if (notificationErrors.Count > 1)
            {
                throw new AggregateException("通知整机和界面报警变化时都发生错误。", notificationErrors);
            }
        }
    }
}
