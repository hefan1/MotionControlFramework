namespace MotionControl.TaskEngine.Definitions
{
    /// <summary>读取一次逻辑输入并写入已有布尔变量；未知输入不写值。</summary>
    public sealed class ReadInputNode : TaskNode
    {
        /// <summary>读取输入的固定类型。</summary>
        public override string Kind => "ReadInput";
        /// <summary>需要采样的现有逻辑 DI 名称。</summary>
        public string InputName { get; set; }
        /// <summary>接收本次采样的已有布尔设备变量名称。</summary>
        public string VariableName { get; set; }
    }
}
