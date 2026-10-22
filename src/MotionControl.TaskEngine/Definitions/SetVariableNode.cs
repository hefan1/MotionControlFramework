namespace MotionControl.TaskEngine.Definitions
{
    /// <summary>向现有变量赋值的定义；不新增变量，不在校验时修改运行值。</summary>
    public sealed class SetVariableNode : TaskNode
    {
        /// <summary>变量赋值的固定类型。</summary>
        public override string Kind => "SetVariable";
        /// <summary>目标设备变量名称。</summary>
        public string VariableName { get; set; }
        /// <summary>赋值来源；允许同类型或当前整数类型到 double 的赋值，不做隐式截断。</summary>
        public TaskValueBinding Value { get; set; }
    }
}
