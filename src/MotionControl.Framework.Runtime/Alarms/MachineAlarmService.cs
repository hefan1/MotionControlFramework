using System;
using System.Collections.Generic;
using System.Linq;
using MotionControl.Framework.Contracts.Alarms;

namespace MotionControl.Framework.Runtime.Alarms
{
    /// <summary>按设备报警规则保存当前报警，并处理条件恢复和人工复位。</summary>
    public sealed class MachineAlarmService : IMachineAlarmService, IMachineAlarmBatchService, IMachineAlarmConditionContextService
    {
        /// <summary>保护报警集合，避免流程线程与界面线程同时读写。</summary>
        private readonly object _sync = new object();

        /// <summary>按报警来源和报警代码保存当前报警记录。</summary>
        private readonly Dictionary<string, Dictionary<string, MachineAlarmRecord>> _alarmsBySource =
            new Dictionary<string, Dictionary<string, MachineAlarmRecord>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>每条报警当前仍有效的条件标识；旧接口使用独立的保留标识。</summary>
        private readonly Dictionary<string, Dictionary<string, Dictionary<string, string>>> _conditionsBySource =
            new Dictionary<string, Dictionary<string, Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>旧报警接口没有条件身份，其多次上报按同一个条件处理。</summary>
        private const string LegacyConditionKey = "legacy";

        /// <summary>新停机报警首次出现时触发，供整机运行时立即暂停流程。</summary>
        public event EventHandler<MachineAlarmRaisedEventArgs> StopAlarmRaised;

        /// <summary>报警新增、条件变化或清除后触发，供界面刷新报警列表。</summary>
        public event EventHandler AlarmsChanged;

        /// <summary>报警状态变化时只允许快速入队；磁盘操作必须在后台完成。</summary>
        internal event Action<AlarmHistoryEntry> HistoryRecorded;

        /// <summary>在报警锁内订阅历史，保证订阅与事件入队之间没有竞争。</summary>
        internal void AttachHistory(Action<AlarmHistoryEntry> handler)
        { lock (_sync) HistoryRecorded += handler; }

        /// <summary>等待正在入队的事件结束后解除订阅，供历史收尾使用。</summary>
        internal void DetachHistory(Action<AlarmHistoryEntry> handler)
        { lock (_sync) HistoryRecorded -= handler; }

        /// <summary>持报警锁生成完整事件，保证快速发生和恢复不会被快照轮询遗漏。</summary>
        private void RecordHistory(MachineAlarmRecord alarm, string action)
        {
            var handler = HistoryRecorded;
            if (handler == null) return;
            try { handler(AlarmHistoryEntry.Create(alarm, action)); }
            catch (Exception error) { System.Diagnostics.Trace.TraceError("报警历史入队失败：" + error); }
        }

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

            ReportConditions(new[] { definition }, sourceName, conditionActive, detail);
        }

        /// <summary>原子更新一组报警，避免界面或停机订阅者只看到同一故障的一部分。</summary>
        public void ReportConditions(
            IEnumerable<MachineAlarmDefinition> definitions,
            string sourceName,
            bool conditionActive,
            string detail)
        {
            UpdateConditions(definitions, sourceName, conditionActive, detail, LegacyConditionKey, null);
        }

        /// <summary>独立报告一个条件，避免其他条件恢复时误清该报警。</summary>
        public void ReportConditionGroup(IEnumerable<MachineAlarmDefinition> definitions, string sourceName,
            bool conditionActive, string detail, string conditionKey, string inputName)
        {
            if (string.IsNullOrWhiteSpace(conditionKey))
                throw new ArgumentException("报警条件标识不能为空。", nameof(conditionKey));
            UpdateConditions(definitions, sourceName, conditionActive, detail,
                "context:" + conditionKey.Trim(), string.IsNullOrWhiteSpace(inputName) ? null : inputName.Trim());
        }

        /// <summary>验证并原子更新报警记录；同一记录涉及多个输入时取消单点定位。</summary>
        private void UpdateConditions(IEnumerable<MachineAlarmDefinition> definitions, string sourceName,
            bool conditionActive, string detail, string conditionKey, string inputName)
        {
            if (definitions == null)
            {
                throw new ArgumentNullException(nameof(definitions));
            }

            if (string.IsNullOrWhiteSpace(sourceName))
            {
                throw new ArgumentException("报警来源不能为空，例如工位名称或整机名称。", nameof(sourceName));
            }

            sourceName = sourceName.Trim();
            var definitionList = definitions.ToList();
            if (definitionList.Count == 0)
            {
                throw new ArgumentException("至少需要一条报警定义。", nameof(definitions));
            }

            var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (MachineAlarmDefinition definition in definitionList)
            {
                if (definition == null)
                {
                    throw new ArgumentException("报警定义列表不能包含空项。", nameof(definitions));
                }
                if (!seenCodes.Add(definition.Code))
                {
                    throw new ArgumentException("同一批报警代码不能重复：" + definition.Code + "。", nameof(definitions));
                }
            }

            var newStopAlarms = new List<MachineAlarmRecord>();
            var alarmsChanged = false;

            lock (_sync)
            {
                Dictionary<string, MachineAlarmRecord> sourceAlarms;
                bool sourceExists = _alarmsBySource.TryGetValue(sourceName, out sourceAlarms);

                // 先完整验证定义一致性，再改集合，避免一批更新一半后因定义冲突抛错。
                if (sourceExists)
                {
                    foreach (MachineAlarmDefinition definition in definitionList)
                    {
                        MachineAlarmRecord existingAlarm;
                        if (sourceAlarms.TryGetValue(definition.Code, out existingAlarm))
                        {
                            EnsureSameDefinition(existingAlarm.Definition, definition, sourceName);
                        }
                    }
                }

                if (!sourceExists && conditionActive)
                {
                    sourceAlarms = new Dictionary<string, MachineAlarmRecord>(StringComparer.OrdinalIgnoreCase);
                    _alarmsBySource.Add(sourceName, sourceAlarms);
                    _conditionsBySource.Add(sourceName, new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase));
                }

                if (sourceAlarms == null)
                {
                    return;
                }

                foreach (MachineAlarmDefinition definition in definitionList)
                {
                    Dictionary<string, Dictionary<string, string>> sourceConditions = _conditionsBySource[sourceName];
                    MachineAlarmRecord existingAlarm;
                    if (!sourceAlarms.TryGetValue(definition.Code, out existingAlarm))
                    {
                        if (!conditionActive)
                        {
                            continue;
                        }

                        var now = DateTime.UtcNow;
                        var newAlarm = new MachineAlarmRecord(
                            definition,
                            sourceName,
                            detail,
                            true,
                            now,
                            now,
                            inputName);
                        sourceAlarms.Add(definition.Code, newAlarm);
                        RecordHistory(newAlarm, "发生");
                        sourceConditions.Add(definition.Code, new Dictionary<string, string>(StringComparer.Ordinal) { { conditionKey, detail } });
                        alarmsChanged = true;
                        if (definition.Kind == MachineAlarmKind.StopMachine)
                        {
                            newStopAlarms.Add(newAlarm);
                        }
                        continue;
                    }

                    Dictionary<string, string> activeConditions = sourceConditions[definition.Code];
                    if (conditionActive)
                        activeConditions[conditionKey] = detail;
                    else if (!activeConditions.Remove(conditionKey))
                        continue;
                    bool anyConditionActive = activeConditions.Count > 0;
                    string currentDetail = conditionActive ? detail
                        : anyConditionActive ? activeConditions.Values.First() : existingAlarm.Detail;

                    if (!anyConditionActive && definition.Kind == MachineAlarmKind.NonStop)
                    {
                        RecordHistory(existingAlarm, "条件恢复并自动清除");
                        sourceAlarms.Remove(definition.Code);
                        sourceConditions.Remove(definition.Code);

                        alarmsChanged = true;
                    }
                    else
                    {
                        bool ambiguous = existingAlarm.InputAssociationAmbiguous
                            || (conditionActive && existingAlarm.RelatedInputName != null
                                && !string.Equals(existingAlarm.RelatedInputName, inputName, StringComparison.OrdinalIgnoreCase));
                        string associatedInput = ambiguous ? null : existingAlarm.RelatedInputName;
                        if (existingAlarm.ConditionActive != anyConditionActive
                            || !string.Equals(existingAlarm.Detail, currentDetail, StringComparison.Ordinal)
                            || ambiguous != existingAlarm.InputAssociationAmbiguous)
                        {
                            var changedAlarm = new MachineAlarmRecord(
                                existingAlarm.Definition,
                                existingAlarm.SourceName,
                                currentDetail,
                                anyConditionActive,
                                existingAlarm.FirstRaisedAtUtc,
                                DateTime.UtcNow,
                                associatedInput,
                                ambiguous);
                            sourceAlarms[definition.Code] = changedAlarm;
                            if (existingAlarm.ConditionActive != anyConditionActive)
                                RecordHistory(changedAlarm, anyConditionActive ? "条件再次成立" : "条件恢复，等待复位");
                            alarmsChanged = true;
                        }
                    }
                }

                if (sourceAlarms.Count == 0)
                {
                    _alarmsBySource.Remove(sourceName);
                    _conditionsBySource.Remove(sourceName);
                }
            }

            NotifyChanges(newStopAlarms, alarmsChanged);
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
                    RecordHistory(alarm, "报警复位");
                    var sourceAlarms = _alarmsBySource[alarm.SourceName];
                    sourceAlarms.Remove(alarm.Definition.Code);
                    _conditionsBySource[alarm.SourceName].Remove(alarm.Definition.Code);
                    if (sourceAlarms.Count == 0)
                    {
                        _alarmsBySource.Remove(alarm.SourceName);
                        _conditionsBySource.Remove(alarm.SourceName);
                    }
                }

                alarmsChanged = recoveredStopAlarms.Count > 0;
            }

            NotifyChanges(new MachineAlarmRecord[0], alarmsChanged);
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
        private void NotifyChanges(IReadOnlyCollection<MachineAlarmRecord> newStopAlarms, bool alarmsChanged)
        {
            var notificationErrors = new List<Exception>();

            EventHandler<MachineAlarmRaisedEventArgs> stopAlarmRaised = StopAlarmRaised;
            foreach (MachineAlarmRecord newStopAlarm in newStopAlarms)
            {
                if (stopAlarmRaised == null)
                {
                    break;
                }

                foreach (EventHandler<MachineAlarmRaisedEventArgs> handler in stopAlarmRaised.GetInvocationList())
                {
                    try
                    {
                        handler(this, new MachineAlarmRaisedEventArgs(newStopAlarm));
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
                    foreach (EventHandler handler in alarmsChangedHandler.GetInvocationList())
                    {
                        try
                        {
                            handler(this, EventArgs.Empty);
                        }
                        catch (Exception error)
                        {
                            notificationErrors.Add(error);
                        }
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
