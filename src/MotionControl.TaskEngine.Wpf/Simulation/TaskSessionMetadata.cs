using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace MotionControl.TaskEngine.Wpf.Simulation
{
    /// <summary>正式发布的任务会话来源；修订取自执行器构造时的只读快照，不代表完整工程版本。</summary>
    public sealed class TaskSessionMetadata
    {
        /// <summary>每次成功发布任务会话时生成的独立标识，不复用控制器连接会话。</summary>
        public Guid SessionId { get; }
        /// <summary>任务会话完成发布前置检查时的 UTC 时间。</summary>
        public DateTimeOffset PublishedAtUtc { get; }
        /// <summary>该任务会话实际所属的设备标识。</summary>
        public string MachineId { get; }
        /// <summary>装载任务时使用的来源目录；来源未提供时为空。</summary>
        public string Source { get; }
        /// <summary>执行器使用的设备工位与任务安排清单的修订。</summary>
        public int MachineConfigurationRevision { get; }
        /// <summary>与执行器同源的任务标识及修订，不随磁盘或显示对象变化。</summary>
        public IReadOnlyDictionary<string, int> TaskRevisions { get; }

        /// <summary>只复制已冻结修订，不读取磁盘或执行设备命令。</summary>
        internal TaskSessionMetadata(string machineId, string source, int machineRevision, IReadOnlyDictionary<string, int> library)
        {
            if (string.IsNullOrWhiteSpace(machineId)) throw new ArgumentException("任务会话缺少设备标识。", nameof(machineId));
            if (library == null) throw new ArgumentNullException(nameof(library));
            var revisions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var task in library) revisions.Add(task.Key, task.Value);
            MachineConfigurationRevision = machineRevision;
            SessionId = Guid.NewGuid();
            PublishedAtUtc = DateTimeOffset.UtcNow;
            MachineId = machineId;
            Source = source;
            TaskRevisions = new ReadOnlyDictionary<string, int>(revisions);
        }

        /// <summary>生成页面顶部可见的会话身份和任务数量，不将任务修订称作工程版本。</summary>
        public string FormatSummary(bool english)
        {
            string time = PublishedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            return english
                ? $"Task session {SessionId} · loaded {time} (local) · {TaskRevisions.Count} task snapshots · project version not provided"
                : $"任务会话 {SessionId} · 加载 {time}（本地） · {TaskRevisions.Count} 个任务快照 · 工程版本未提供";
        }

        /// <summary>显示真实来源与最多 100 项冻结修订；超出部分明确标记为未显示。</summary>
        public string FormatDetails(bool english)
        {
            var tasks = TaskRevisions.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase).ToArray();
            string versions = string.Join("、", tasks.Take(100).Select(item => item.Key + " R" + item.Value));
            if (tasks.Length == 0) versions = english ? "no tasks" : "无任务";
            if (tasks.Length > 100) versions += english ? $"; entries 101–{tasks.Length} are not displayed" : $"；第 101–{tasks.Length} 项未显示";
            string source = string.IsNullOrWhiteSpace(Source) ? (english ? "not provided" : "未提供") : Source;
            return FormatSummary(english)
                + (english ? "\nEquipment: " : "\n设备：") + MachineId
                + (english ? " · Task source: " : " · 任务来源：") + source
                + (english ? "\nEquipment task manifest R" : "\n设备任务清单 R") + MachineConfigurationRevision
                + (english ? "\nFrozen task revisions: " : "\n会话冻结任务修订：") + versions;
        }
    }
}
