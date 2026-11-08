using System.Collections.Generic;
using Newtonsoft.Json;

namespace MotionControl.TaskEngine.Editing
{
    /// <summary>任务应用的固定清单与内容证据；恢复时必须与备份目录中的清单完全一致。</summary>
    internal sealed class TaskApplyJournal
    {
        /// <summary>版本 2 增加内容哈希；旧版无哈希记录不能安全自动恢复。</summary>
        [JsonProperty(Required = Required.Always)]
        public int SchemaVersion { get; set; } = 2;
        /// <summary>所属设备，恢复时必须一致。</summary>
        [JsonProperty(Required = Required.Always)]
        public string MachineId { get; set; }
        /// <summary>备份目录的固定 GUID 后缀。</summary>
        [JsonProperty(Required = Required.Always)]
        public string BackupId { get; set; }
        /// <summary>完整受影响文件清单，包含新增、改写、保留和删除项。</summary>
        [JsonProperty(Required = Required.Always)]
        public List<TaskApplyFile> Files { get; set; } = new List<TaskApplyFile>();
    }

    /// <summary>单个运行文件应用前后的字节身份；哈希不是文件时间或 JSON 字段摘要。</summary>
    internal sealed class TaskApplyFile
    {
        /// <summary>受限的 Tasks 文件或固定设备清单路径。</summary>
        [JsonProperty(Required = Required.Always)]
        public string RelativePath { get; set; }
        /// <summary>应用前文件是否存在；不存在时原内容和备份哈希必须为空。</summary>
        [JsonProperty(Required = Required.Always)]
        public bool Existed { get; set; }
        /// <summary>本次应用后文件是否应存在；false 表示明确删除，不是缺少哈希。</summary>
        [JsonProperty(Required = Required.Always)]
        public bool AppliedExists { get; set; }
        /// <summary>应用前原始字节的 SHA256，包含原有 BOM 与换行。</summary>
        public string OriginalSha256 { get; set; }
        /// <summary>写入备份后重新读取所得的 SHA256，必须与原始字节一致。</summary>
        public string BackupSha256 { get; set; }
        /// <summary>本次实际准备写入字节的 SHA256；删除文件时为空。</summary>
        public string AppliedSha256 { get; set; }
    }
}
