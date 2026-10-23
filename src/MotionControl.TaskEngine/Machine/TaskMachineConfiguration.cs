using System.Collections.Generic;
using Newtonsoft.Json;

namespace MotionControl.TaskEngine.Machine
{
    /// <summary>一台设备的自动运行清单，任务正文仍单独保存在任务定义文件中。</summary>
    public sealed class TaskMachineConfiguration
    {
        /// <summary>配置格式版本，当前只接受 1。</summary>
        public int SchemaVersion { get; set; } = 1;
        /// <summary>设备实例标识，与保存目录一致。</summary>
        public string MachineId { get; set; }
        /// <summary>供界面显示的设备名称。</summary>
        public string Name { get; set; }
        /// <summary>修改已有清单时递增，防止静默覆盖。</summary>
        public int Revision { get; set; } = 1;
        /// <summary>可选启动初始化任务；成功后才放行各工位，暂停不重复初始化。</summary>
        public string InitializationTaskId { get; set; }
        /// <summary>本设备的工位列表，启动后互相独立推进。</summary>
        public List<StationTaskConfiguration> Stations { get; set; } = new List<StationTaskConfiguration>();
    }

    /// <summary>工位怎样参加自动生产，独立于它引用的任务正文。</summary>
    public sealed class StationTaskConfiguration
    {
        /// <summary>本设备内唯一的工位标识。</summary>
        public string StationId { get; set; }
        /// <summary>工位中文名称。</summary>
        public string Name { get; set; }
        /// <summary>要执行的任务定义标识。</summary>
        public string TaskId { get; set; }
        /// <summary>是否参加本次自动运行；运行清单启动后冻结。</summary>
        [JsonProperty(Required = Required.Always)]
        public bool Enabled { get; set; } = true;
        /// <summary>必须明确选择单次、独立循环或请求触发。</summary>
        [JsonProperty(Required = Required.Always)]
        public StationTaskMode Mode { get; set; }
        /// <summary>周期之间的最小轮询间隔，1–60000 毫秒，默认 20，避免空任务占满线程。</summary>
        public int CycleIntervalMs { get; set; } = 20;
        /// <summary>请求触发模式使用的 bool 变量，请求方写，必须保持到完成信号。</summary>
        public string RequestVariable { get; set; }
        /// <summary>本工位唯一写入的 bool 完成变量；请求方清请求后本工位才清完成。</summary>
        public string CompletedVariable { get; set; }
    }

    /// <summary>工位的周期组织方式。</summary>
    public enum StationTaskMode
    {
        /// <summary>本次生产会话只执行一次。</summary>
        Once,
        /// <summary>各周期独立连续运行，不等待其他工位一起结束。</summary>
        Cyclic,
        /// <summary>每轮请求握手只执行一次，完成信号复归后才接受下一轮。</summary>
        OnRequest
    }
}
