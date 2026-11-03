using System.Collections.Generic;
namespace MotionControl.TaskEngine.Definitions
{
    /// <summary>将同一变量快照中的字段作为一条记录持久化。</summary>
    public sealed class WriteRecordNode : TaskNode
    {
        /// <summary>记录步骤类型。</summary>
        public override string Kind => "WriteRecord";
        /// <summary>记录类别，由设备项目自行定义。</summary>
        public string Category { get; set; }
        /// <summary>记录字段名到字符串变量名的映射。</summary>
        public Dictionary<string, string> Fields { get; set; } = new Dictionary<string, string>();
    }
}
