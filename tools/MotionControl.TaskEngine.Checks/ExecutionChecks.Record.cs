using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.TaskEngine.Definitions;
using MotionControl.TaskEngine.Device;
using MotionControl.TaskEngine.Device.InMemory;
using MotionControl.TaskEngine.Execution;
namespace MotionControl.TaskEngine.Checks
{
    /// <summary>逐件记录失败、暂停和幂等写入检查。</summary>
    internal static partial class ExecutionChecks
    {
        internal static int RunRecordCheck()
        {
            try { RecordPersistence().GetAwaiter().GetResult(); Console.WriteLine("[通过] 记录失败、暂停快照及文件幂等。"); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
            finally { foreach (TaskRun run in Runs) run.Cancel(); Runs.Clear(); }
        }
        private static async Task RecordPersistence()
        {
            string directory = Path.Combine(Path.GetTempPath(), "motion-record-check-" + Guid.NewGuid().ToString("N"));
            try
            {
                var file = new TaskFileRecordSink(directory, "M1");
                var id = Guid.NewGuid().ToString("N");
                var data = new Dictionary<string, string> { ["product"] = "P1", ["result"] = "OK" };
                DateTimeOffset recordedAt = new DateTimeOffset(2026, 11, 3, 8, 9, 10, TimeSpan.Zero);
                file.CommitRecord(id, "T", "N", "生产", recordedAt, data); file.CommitRecord(id, "T", "N", "生产", recordedAt, data);
                CheckAssert.True(Directory.GetFiles(Path.Combine(directory, "Runtime", "TaskRecords"), "*.json").Length == 1, "幂等重试产生重复文件。");
                string saved = File.ReadAllText(Path.Combine(directory, "Runtime", "TaskRecords", id + ".json"));
                CheckAssert.True(saved.Contains("\"recordedAtUtc\":\"2026-11-03T08:09:10.0000000+00:00\""), "持久化记录未保存可解析的 UTC 采样时间。");
                bool conflict = false;
                try { file.CommitRecord(id, "T", "N", "生产", recordedAt, new Dictionary<string, string> { ["product"] = "P2", ["result"] = "NG" }); }
                catch (InvalidDataException) { conflict = true; }
                CheckAssert.True(conflict, "同编号不同内容未拒绝。");
                conflict = false;
                try { file.CommitRecord(id, "T", "N", "生产", recordedAt.AddSeconds(1), data); }
                catch (InvalidDataException) { conflict = true; }
                CheckAssert.True(conflict, "同编号不同采样时间未拒绝。");
                var delayed = new TestRecordSink();
                var device = new InMemoryTaskDevice(TaskFixtures.Catalog(), delayed);
                device.SetVariable("Vision.Result", "P1");
                var record = new WriteRecordNode { Id = "record", Name = "记录", Category = "生产", TimeoutMs = 3000, Fields = new Dictionary<string, string> { ["product"] = "Vision.Result" } };
                DateTimeOffset beforeCapture = DateTimeOffset.UtcNow;
                TaskRun run = Start(TaskFixtures.Task(record, Output()), device);
                await Until(() => delayed.Started.IsSet, "记录写入未开始。");
                DateTimeOffset capturedTime = delayed.RecordedAt;
                CheckAssert.True(capturedTime.Offset == TimeSpan.Zero && capturedTime >= beforeCapture && capturedTime <= DateTimeOffset.UtcNow, "记录时间不是本次采样的 UTC 时间。");
                CheckAssert.True(run.Pause(), "写入期间无法暂停。");
                device.SetVariable("Vision.Result", "P2"); delayed.Release.Set();
                await Until(() => delayed.Finished.IsSet, "记录写入未结束。");
                await Task.Delay(100).ConfigureAwait(false);
                CheckAssert.True(device.ReadOutput("Vacuum") == null && delayed.Value == "P1", "暂停后错误放行或记录错配。");
                CheckAssert.True(run.Resume(), "不能从暂停恢复。"); Success(await Complete(run));
                CheckAssert.True(delayed.Calls == 1 && delayed.RecordedAt == capturedTime && device.ReadOutput("Vacuum") == true, "恢复后记录时间变化、重复记录或未继续。");
                var slow = new TestRecordSink();
                var timed = new InMemoryTaskDevice(TaskFixtures.Catalog(), slow); timed.SetVariable("Vision.Result", "P1");
                var timedRecord = new WriteRecordNode { Id = "timed", Name = "记录", Category = "生产", TimeoutMs = 80, Fields = new Dictionary<string, string> { ["product"] = "Vision.Result" } };
                TaskRun timedRun = Start(TaskFixtures.Task(timedRecord, Output()), timed);
                await Until(() => slow.Started.IsSet, "超时检查的记录写入未开始。");
                await Task.Delay(200).ConfigureAwait(false); slow.Release.Set();
                Failure(await Complete(timedRun), "超时");
                CheckAssert.True(timed.ReadOutput("Vacuum") == null, "记录超时后仍执行输出。");
                var failure = new TestRecordSink { Fail = true }; failure.Release.Set();
                var bad = new InMemoryTaskDevice(TaskFixtures.Catalog(), failure); bad.SetVariable("Vision.Result", "P1");
                Failure(await Complete(Start(TaskFixtures.Task(record, Output()), bad)), "记录写入失败");
                CheckAssert.True(bad.ReadOutput("Vacuum") == null, "记录失败后仍执行输出。");
                var unknown = new InMemoryTaskDevice(TaskFixtures.Catalog(), failure);
                Failure(await Complete(Start(TaskFixtures.Task(record, Output()), unknown)), "尚无有效值");
                CheckAssert.True(unknown.ReadOutput("Vacuum") == null, "未知记录字段后仍执行输出。");
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }
        private sealed class TestRecordSink : ITaskRecordSink
        {
            internal readonly ManualResetEventSlim Started = new ManualResetEventSlim();
            internal readonly ManualResetEventSlim Release = new ManualResetEventSlim();
            internal readonly ManualResetEventSlim Finished = new ManualResetEventSlim();
            internal bool Fail; internal int Calls; internal string Value;
            internal DateTimeOffset RecordedAt;
            public void CommitRecord(string executionId, string taskId, string nodeId, string category, DateTimeOffset recordedAtUtc, IReadOnlyDictionary<string, string> fields)
            {
                Calls++; Value = fields["product"]; RecordedAt = recordedAtUtc; Started.Set();
                if (!Release.Wait(5000)) throw new TimeoutException("检查器未释放记录写入。");
                Finished.Set(); if (Fail) throw new IOException("记录写入失败");
            }
        }
    }
}
