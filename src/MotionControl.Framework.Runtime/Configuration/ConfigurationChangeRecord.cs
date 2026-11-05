using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace MotionControl.Framework.Runtime.Configuration
{
    /// <summary>源服务完成单个操作阶段后提供的事实，缺失可选字段保持空值。</summary>
    public sealed class ConfigurationChangeRecord
    {
        /// <summary>记录格式，目前仅识别 1。</summary>
        [JsonProperty(Required = Required.Always)]
        public int FormatVersion { get; set; } = 1;
        /// <summary>本阶段记录的唯一标识；调用方应在记录前分配。</summary>
        [JsonProperty(Required = Required.Always)]
        public Guid RecordId { get; set; }
        /// <summary>动作所属设备工程，与记录目录核对。</summary>
        [JsonProperty(Required = Required.Always)]
        public string MachineId { get; set; }
        /// <summary>源动作产生结果的 UTC 时间，不取文件修改时间。</summary>
        [JsonProperty(Required = Required.Always)]
        public DateTimeOffset TimeUtc { get; set; }
        /// <summary>实际完成或失败的阶段。</summary>
        [JsonProperty(Required = Required.Always)]
        public ConfigurationChangeStage Stage { get; set; }
        /// <summary>该阶段源服务返回的结果。</summary>
        [JsonProperty(Required = Required.Always)]
        public ConfigurationChangeOutcome Outcome { get; set; }
        /// <summary>实际产生回执的源服务或入口。</summary>
        [JsonProperty(Required = Required.Always)]
        public string Source { get; set; }
        /// <summary>此动作指向的对象类型，如任务库或工位清单。</summary>
        [JsonProperty(Required = Required.Always)]
        public string ObjectType { get; set; }
        /// <summary>源服务使用的对象标识。</summary>
        [JsonProperty(Required = Required.Always)]
        public string ObjectId { get; set; }
        /// <summary>源服务提供的关联号，可用于串联同一次操作的不同阶段。</summary>
        public string OperationId { get; set; }
        /// <summary>源服务核实的旧版本或指纹，未知时为空。</summary>
        public string BeforeVersion { get; set; }
        /// <summary>此阶段核实的新版本或指纹，未知时为空。</summary>
        public string AfterVersion { get; set; }
        /// <summary>实际取得的操作者身份，未知时为空。</summary>
        public string Actor { get; set; }
        /// <summary>可选的实际差异字段路径。</summary>
        public string Field { get; set; }
        /// <summary>可选的变更前字段值。</summary>
        public string BeforeValue { get; set; }
        /// <summary>可选的变更后字段值。</summary>
        public string AfterValue { get; set; }
        /// <summary>按当时字段定义取得的单位。</summary>
        public string Unit { get; set; }
        /// <summary>源服务的结果说明；失败或拒绝时保留具体原因。</summary>
        public string Detail { get; set; }
    }

    /// <summary>分别记录保存、检查、应用和会话载入，无隐含后续阶段。</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum ConfigurationChangeStage
    {
        /// <summary>保存编辑草稿。</summary>
        SaveDraft = 1,
        /// <summary>检查定义。</summary>
        Check,
        /// <summary>写入配置文件。</summary>
        SaveFiles,
        /// <summary>应用配置或导入工程包。</summary>
        Apply,
        /// <summary>载入会话。</summary>
        LoadSession
    }

    /// <summary>源业务阶段的终态，与记录落盘结果独立。</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public enum ConfigurationChangeOutcome
    {
        /// <summary>源服务确认阶段成功。</summary>
        Succeeded = 1,
        /// <summary>执行过程中失败。</summary>
        Failed,
        /// <summary>阶段前置条件未满足。</summary>
        Rejected
    }
}
