using System;
using System.Collections.Generic;
using System.Linq;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.TaskEngine.Definitions;

namespace MotionControl.TaskEngine.Execution
{
    /// <summary>把任务节点超时转换为报警库中的同源多报警记录。</summary>
    internal sealed partial class TaskNodeExecutor
    {
        /// <summary>超时同时上报全部关联报警；含停机报警时锁存故障并要求整机复位。</summary>
        private bool ReportTimeoutAlarms(TaskExecutionScope scope, IEnumerable<string> codes, string detail)
        {
            if (_alarmService == null) return true;

            MachineAlarmDefinition[] definitions = ResolveAlarmDefinitions(codes);
            ReportAlarmGroup(scope, definitions, true, detail);
            MachineAlarmDefinition[] stopAlarms = definitions
                .Where(definition => definition.Kind == MachineAlarmKind.StopMachine)
                .ToArray();
            if (stopAlarms.Length == 0) return false;

            // 超时是已发生事件；停机记录锁存，但输入恢复通过任务重进时的实时采样确认。
            ReportAlarmGroup(scope, stopAlarms, false, detail);
            return true;
        }

        /// <summary>等待条件恢复后清除相关记录；停机记录仍由整机复位统一清除。</summary>
        private void ClearTimeoutAlarms(TaskExecutionScope scope, IEnumerable<string> codes, string detail)
        {
            if (_alarmService == null) return;
            ReportAlarmGroup(scope, ResolveAlarmDefinitions(codes), false, detail);
        }

        /// <summary>只接受启动时校验过的报警代码，禁止运行中猜测或创建报警。</summary>
        private MachineAlarmDefinition[] ResolveAlarmDefinitions(IEnumerable<string> codes)
        {
            var result = new List<MachineAlarmDefinition>();
            foreach (string code in codes ?? Enumerable.Empty<string>())
            {
                var alarm = _device.Catalog.AlarmDefinitions.FirstOrDefault(item =>
                    string.Equals(item.Code, code, StringComparison.OrdinalIgnoreCase));
                if (alarm == null)
                    throw new InvalidOperationException("设备报警库中找不到任务引用的报警代码：" + code);
                result.Add(new MachineAlarmDefinition(alarm.Code, alarm.Message, alarm.Kind,
                    alarm.CheckGuidance, alarm.RecoveryGuidance));
            }

            if (result.Count == 0)
                throw new InvalidOperationException("等待超时没有关联报警，任务检查应在启动前拒绝此配置。");
            return result.ToArray();
        }

        /// <summary>正式报警服务原子更新多条记录；旧实现逐条兼容。</summary>
        private void ReportAlarmGroup(TaskExecutionScope scope, IEnumerable<MachineAlarmDefinition> definitions,
            bool active, string detail)
        {
            MachineAlarmDefinition[] group = definitions.ToArray();
            string sourceName = _station == null ? "任务 " + scope.TaskId : _station.Configuration.Name;
            var inputNode = scope.Node as WaitInputNode;
            var inputContext = _alarmService as IMachineAlarmConditionContextService;
            if (inputContext != null)
            {
                inputContext.ReportConditionGroup(group, sourceName, active, detail,
                    "task-node:" + scope.ExecutionId, inputNode?.InputName);
                return;
            }
            IMachineAlarmBatchService batch = _alarmService as IMachineAlarmBatchService;
            if (batch != null)
            {
                batch.ReportConditions(group, sourceName, active, detail);
                return;
            }

            var failures = new List<Exception>();
            foreach (MachineAlarmDefinition definition in group)
            {
                try { _alarmService.ReportCondition(definition, sourceName, active, detail); }
                catch (Exception error) { failures.Add(error); }
            }
            if (failures.Count == 1) throw new InvalidOperationException("更新任务超时报警失败。", failures[0]);
            if (failures.Count > 1) throw new AggregateException("更新多条任务超时报警失败。", failures);
        }
    }
}
