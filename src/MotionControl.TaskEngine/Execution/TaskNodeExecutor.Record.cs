using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Device;
namespace MotionControl.TaskEngine.Execution
{
    /// <summary>记录在锁外写盘，暂停需等待在途写入结束。</summary>
    internal sealed partial class TaskNodeExecutor
    {
        /// <summary>在运行许可下采集单次快照，失败或恢复时保持同一编号及内容。</summary>
        private async Task WriteRecordAsync(TaskExecutionScope scope, WriteRecordNode node)
        {
            var sink = _device as ITaskRecordSink;
            if (sink == null) throw new NotSupportedException("任务设备未配置记录存储，不能继续生产。");
            await scope.StepAsync(() =>
            {
                if (scope.RecordFields != null) return;
                var snapshot = _device.ReadVariables();
                var fields = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var field in node.Fields)
                {
                    var value = snapshot.FirstOrDefault(item => string.Equals(item.Name, field.Value, StringComparison.OrdinalIgnoreCase));
                    if (value == null || !value.HasValue || string.IsNullOrWhiteSpace(value.CurrentValue))
                        throw new InvalidOperationException("记录来源尚无有效值：" + field.Value);
                    fields.Add(field.Key, value.CurrentValue);
                }
                scope.RecordTimeUtc = DateTimeOffset.UtcNow;
                scope.RecordFields = fields;
            }).ConfigureAwait(false);
            await SampleDeviceAsync(scope, () => { sink.CommitRecord(scope.ExecutionId, scope.TaskId, node.Id, node.Category, scope.RecordTimeUtc.Value, scope.RecordFields); return true; }, v => { }).ConfigureAwait(false);
            // 已持久化的记录允许暂停期间完成；恢复后才放行后续动作。
            await scope.StepAsync(() =>
                scope.Diagnostics = new TaskStepDiagnostics("已保存本次记录", scope.RecordFields.Select(item =>
                    new TaskObservedValue(item.Key, "记录值", item.Value, true, quality: StationValueQuality.Good)))).ConfigureAwait(false);
        }
    }
}
