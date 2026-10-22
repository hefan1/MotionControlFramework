namespace MotionControl.TaskEngine.Definitions
{
    /// <summary>设置一个逻辑 DO 的目标状态；此类只保存定义，不会操作输出。</summary>
    public sealed class SetOutputNode : TaskNode
    {
        /// <summary>设置输出的固定类型。</summary>
        public override string Kind => "SetOutput";
        /// <summary>现有逻辑 DO 名称。</summary>
        public string OutputName { get; set; }
        /// <summary>目标逻辑状态，信号极性由适配器负责。</summary>
        public bool Value { get; set; }
    }
}
