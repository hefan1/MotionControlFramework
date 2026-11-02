using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Framework.Runtime.Configuration;
using Newtonsoft.Json;

namespace MotionControl.Framework.Runtime.Alarms
{
    /// <summary>后台历史收尾的实际结果；失败时待写事件不代表已经持久化。</summary>
    public sealed class AlarmHistoryCloseResult
    {
        /// <summary>后台写入线程是否已经结束。</summary>
        public bool WriterStopped { get; internal set; }
        /// <summary>收尾检查时仍在内存队列中的事件数量；线程未结束时只是瞬时值。</summary>
        public int PendingCount { get; internal set; }
        /// <summary>最后一次写入错误或收尾超时说明。</summary>
        public string Error { get; internal set; }
        /// <summary>仅当线程退出且没有待写事件时才视为收尾完成。</summary>
        public bool IsComplete { get { return WriterStopped && PendingCount == 0; } }
    }

    /// <summary>一次报警状态变化；历史记录仅用于查询，不参与当前报警或设备控制。</summary>
    public sealed class AlarmHistoryEntry
    {
        /// <summary>状态变化的 UTC 时间。</summary>
        public DateTime TimeUtc { get; set; }
        /// <summary>本次报警的首次发生时间，用于区分重复报警。</summary>
        public DateTime FirstRaisedAtUtc { get; set; }
        /// <summary>发生、条件恢复或报警复位。</summary>
        public string Action { get; set; }
        /// <summary>报警来源，例如工位名称。</summary>
        public string Source { get; set; }
        /// <summary>设备报警代码。</summary>
        public string Code { get; set; }
        /// <summary>报警内容。</summary>
        public string Message { get; set; }
        /// <summary>停机或非停机。</summary>
        public string Kind { get; set; }
        /// <summary>发生时或最近状态变化的设备上下文。</summary>
        public string Detail { get; set; }
        /// <summary>明确关联的逻辑输入，未知时为空。</summary>
        public string Input { get; set; }
        /// <summary>按实际报警快照生成历史事件。</summary>
        internal static AlarmHistoryEntry Create(MachineAlarmRecord alarm, string action)
        {
            return new AlarmHistoryEntry { TimeUtc = DateTime.UtcNow, FirstRaisedAtUtc = alarm.FirstRaisedAtUtc,
                Action = action, Source = alarm.SourceName, Code = alarm.Definition.Code,
                Message = alarm.Definition.Message, Kind = alarm.RequiresReset ? "停机" : "非停机",
                Detail = alarm.Detail, Input = alarm.RelatedInputName };
        }
    }

    /// <summary>将报警状态变化后台追加到设备目录；记录失败不会阻塞停机控制。</summary>
    public sealed class AlarmHistoryJournal : IDisposable
    {
        /// <summary>报警来源。</summary>
        private readonly MachineAlarmService _service;
        /// <summary>按日期拆分的日志目录。</summary>
        private readonly string _directory;
        /// <summary>保持事件顺序，写失败时保留队头以便重试。</summary>
        private readonly ConcurrentQueue<AlarmHistoryEntry> _pending = new ConcurrentQueue<AlarmHistoryEntry>();
        /// <summary>唤醒后台写入线程。</summary>
        private AutoResetEvent _signal = new AutoResetEvent(false);
        /// <summary>后台写入线程。</summary>
        private Thread _writer;
        /// <summary>关闭时停止接收新事件。</summary>
        private volatile bool _closing;
        /// <summary>保护重复收尾，避免两个调用交错解除订阅。</summary>
        private readonly object _closeSync = new object();
        /// <summary>标记线程结束后已释放的唤醒句柄，避免反复连接泄漏系统资源。</summary>
        private bool _signalDisposed;
        /// <summary>最近一次后台错误，跨线程安全读取。</summary>
        private volatile string _lastError;
        /// <summary>最近写入错误，可在界面提示历史可能不完整。</summary>
        public string LastError { get { return _lastError; } }

        /// <summary>绑定设备报警服务并启动后台写入，不修改当前报警。</summary>
        public AlarmHistoryJournal(MachineAlarmService service, MachineConfigurationStore store, string machineId)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            if (store == null) throw new ArgumentNullException(nameof(store));
            _directory = Path.Combine(store.GetMachineDirectory(machineId), "Runtime", "AlarmHistory");
            _writer = new Thread(WriteLoop) { IsBackground = true, Name = "AlarmHistoryWriter" };
            _service.AttachHistory(Enqueue);
            _writer.Start();
        }

        /// <summary>在报警线程只放入队列，不读写文件。</summary>
        private void Enqueue(AlarmHistoryEntry entry) { _pending.Enqueue(entry); _signal.Set(); }

        /// <summary>按发生顺序追加；失败保留未写入记录并周期重试。</summary>
        private void WriteLoop()
        {
            while (true)
            {
                AlarmHistoryEntry entry;
                if (!_pending.TryPeek(out entry))
                {
                    if (_closing) return;
                    _signal.WaitOne(1000);
                    continue;
                }
                try
                {
                    Directory.CreateDirectory(_directory);
                    string path = Path.Combine(_directory, entry.TimeUtc.ToLocalTime().ToString("yyyy-MM-dd") + ".jsonl");
                    File.AppendAllText(path, JsonConvert.SerializeObject(entry) + Environment.NewLine, new UTF8Encoding(false));
                    _pending.TryDequeue(out entry);
                    _lastError = null;
                }
                catch (Exception error)
                {
                    _lastError = "报警历史写入失败：" + error.Message;
                    Trace.TraceError(LastError);
                    if (_closing) return;
                    _signal.WaitOne(3000);
                }
            }
        }

        /// <summary>查询最近日期的记录；内存只保留指定条数，文件损坏或读取失败明确报告。</summary>
        public IReadOnlyList<AlarmHistoryEntry> ReadRecent(int days, int maximum)
        {
            if (days < 1 || maximum < 1) throw new ArgumentOutOfRangeException("查询范围必须大于零。");
            // 按时间保留最新 N 条；时间相同的记录按文件读取顺序保留最近项。
            var entries = new SortedDictionary<DateTime, Queue<AlarmHistoryEntry>>();
            int count = 0;
            DateTime today = DateTime.Now.Date;
            for (int day = 0; day < days; day++)
            {
                string file = Path.Combine(_directory, today.AddDays(-day).ToString("yyyy-MM-dd") + ".jsonl");
                // 只跳过明确不存在的文件；无权限或 I/O 错误不能显示成“无报警”。
                try { File.GetAttributes(file); }
                catch (FileNotFoundException) { continue; }
                catch (DirectoryNotFoundException) { continue; }
                foreach (string line in File.ReadLines(file, Encoding.UTF8))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var item = JsonConvert.DeserializeObject<AlarmHistoryEntry>(line);
                    if (item == null) throw new InvalidDataException("报警历史存在空记录：" + file);
                    Queue<AlarmHistoryEntry> sameTime;
                    if (!entries.TryGetValue(item.TimeUtc, out sameTime))
                        entries.Add(item.TimeUtc, sameTime = new Queue<AlarmHistoryEntry>());
                    sameTime.Enqueue(item);
                    count++;
                    if (count > maximum)
                    {
                        DateTime earliest = entries.First().Key;
                        Queue<AlarmHistoryEntry> oldest = entries[earliest];
                        oldest.Dequeue(); count--;
                        if (oldest.Count == 0) entries.Remove(earliest);
                    }
                }
            }
            return entries.Reverse().SelectMany(pair => pair.Value.Reverse()).ToList();
        }

        /// <summary>停止接收并等待收尾，返回待写数量和错误；超时不代表后台已经结束。</summary>
        public AlarmHistoryCloseResult Close(TimeSpan timeout)
        {
            if (timeout < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
            lock (_closeSync)
            {
                if (!_closing)
                {
                    _service.DetachHistory(Enqueue);
                    _closing = true; _signal.Set();
                }
                bool stopped = _writer.Join(timeout);
                if (stopped && !_signalDisposed) { _signal.Dispose(); _signalDisposed = true; }
                int pending = _pending.Count;
                string error = LastError;
                if (!stopped && string.IsNullOrEmpty(error)) error = "报警历史收尾超时，后台写入仍在进行。";
                if (stopped && pending > 0 && string.IsNullOrEmpty(error)) error = "报警历史有未写入的记录。";
                var result = new AlarmHistoryCloseResult { WriterStopped = stopped, PendingCount = pending, Error = error };
                if (!result.IsComplete) Trace.TraceError("报警历史未完整保存：" + result.Error + " 待写条数：" + pending);
                return result;
            }
        }

        /// <summary>处理写入原因后重试尚在内存中的记录；仅在关闭订阅后使用。</summary>
        public AlarmHistoryCloseResult RetryPending(TimeSpan timeout)
        {
            lock (_closeSync)
            {
                if (!_closing) throw new InvalidOperationException("请先结束报警历史写入，再重试待写记录。");
                if (!_writer.IsAlive && !_pending.IsEmpty)
                {
                    if (_signalDisposed) { _signal = new AutoResetEvent(false); _signalDisposed = false; }
                    _writer = new Thread(WriteLoop) { IsBackground = true, Name = "AlarmHistoryRetry" };
                    _writer.Start();
                }
                return Close(timeout);
            }
        }

        /// <summary>读取尚未确认写入的内存快照，供调用方保留或诊断；线程运行时可能变化。</summary>
        public IReadOnlyList<AlarmHistoryEntry> GetPendingSnapshot() { return _pending.ToArray(); }

        /// <summary>兼容标准释放；需要向人员报告结果时应使用 Close。</summary>
        public void Dispose() { Close(TimeSpan.FromSeconds(3)); }
    }
}
