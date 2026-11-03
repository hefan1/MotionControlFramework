using System.Collections.Generic;

namespace MotionControl.TaskEngine.Definitions
{
    /// <summary>读取锁存的二进制输入并写入字符串变量；输入保持和有效性由外部设备提供。</summary>
    public sealed class ReadInputWordNode : TaskNode
    {
        /// <summary>读取多位输入步骤类型。</summary>
        public override string Kind => "ReadInputWord";
        /// <summary>数据位逻辑输入名称，按低位到高位排列。</summary>
        public List<string> InputNames { get; set; } = new List<string>();
        /// <summary>指示数据已锁存且可读取的输入。</summary>
        public string ValidInputName { get; set; }
        /// <summary>指示本次设备读取成功的输入。</summary>
        public string SuccessInputName { get; set; }
        /// <summary>接收格式化结果的字符串设备变量。</summary>
        public string TargetVariable { get; set; }
        /// <summary>数字前固定添加的文本。</summary>
        public string Prefix { get; set; } = "";
        /// <summary>数据持续无效或读取失败时，等待超时关联的报警。</summary>
        public List<string> TimeoutAlarmCodes { get; set; } = new List<string>();
    }
}
