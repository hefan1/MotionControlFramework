using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Device;
using MotionControl.TaskEngine.Device.InMemory;
using MotionControl.TaskEngine.Machine;

namespace MotionControl.TaskEngine.Checks
{
    /// <summary>检查记录已落盘但调用返回失败时的同会话恢复。</summary>
    internal static partial class MachineChecks
    {
        /// <summary>同一节点故障重试须使用首次产品快照、首次采样时间和相同执行编号。</summary>
        private static async Task RecordCommitRecovery()
        {
            string directory = Path.Combine(Path.GetTempPath(), "motion-record-retry-" + Guid.NewGuid().ToString("N"));
            try
            {
                var sink = new FailAfterRecordSink(directory);
                var device = new InMemoryTaskDevice(TaskFixtures.Catalog(), sink);
                device.SetVariable("Vision.Result", "P1");
                var record = new WriteRecordNode { Id = "record", Name = "保存产品", Category = "生产", TimeoutMs = 5000,
                    Fields = new Dictionary<string, string> { ["product"] = "Vision.Result" } };
                var machine = Machine(device, new[] { TaskDefinition("RecordRetry", record) }, Station("record", "RecordRetry", StationTaskMode.Once));
                machine.Reset(); machine.Start();
                await Until(() => machine.GetSnapshot().State == TaskMachineState.Faulted, "首次写入后故障未锁存。");
                device.SetVariable("Vision.Result", "P2");
                machine.Reset(); machine.Start();
                await Until(() => State(machine, "record").CompletedCycles == 1, "故障复位后未完成记录节点。");
                CheckAssert.True(sink.Calls == 2 && sink.SameAttempt, "记录重试改变了编号、时间或产品字段。");
                CheckAssert.True(Directory.GetFiles(Path.Combine(directory, "Runtime", "TaskRecords"), "*.json").Length == 1,
                    "写入返回不确定后重试产生重复记录。");
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }

        /// <summary>首次写盘成功后模拟返回异常，后续请求由真实文件存储校验幂等。</summary>
        private sealed class FailAfterRecordSink : ITaskRecordSink
        {
            /// <summary>真实逐条文件存储。</summary>
            private readonly TaskFileRecordSink _file;
            /// <summary>首个执行编号、时间和产品值。</summary>
            private string _id, _value;
            /// <summary>首个快照的 UTC 时间。</summary>
            private DateTimeOffset _time;
            /// <summary>调用次数和重试是否一致。</summary>
            internal int Calls;
            /// <summary>重试是否保持首次内容。</summary>
            internal bool SameAttempt;
            /// <summary>使用临时目录创建记录存储。</summary>
            internal FailAfterRecordSink(string directory) { _file = new TaskFileRecordSink(directory, "Test01"); }
            /// <summary>首次写成功后报错，第二次验证原始内容并执行文件级幂等检查。</summary>
            public void CommitRecord(string executionId, string taskId, string nodeId, string category, DateTimeOffset recordedAtUtc, IReadOnlyDictionary<string, string> fields)
            {
                Calls++;
                if (Calls == 1) { _id = executionId; _time = recordedAtUtc; _value = fields["product"]; }
                else SameAttempt = executionId == _id && recordedAtUtc == _time && fields["product"] == _value && _value == "P1";
                _file.CommitRecord(executionId, taskId, nodeId, category, recordedAtUtc, fields);
                if (Calls == 1) throw new IOException("模拟发布成功后返回失败。");
            }
        }
    }
}
