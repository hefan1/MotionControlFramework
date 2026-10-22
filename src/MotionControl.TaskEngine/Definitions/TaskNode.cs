namespace MotionControl.TaskEngine.Definitions
{
    /// <summary>任务树节点共同的标识信息；实际参数放在各具体节点类中。</summary>
    public abstract class TaskNode
    {
        /// <summary>固定的节点类型标识；JSON 读取器只接受明确支持的类型。</summary>
        public abstract string Kind { get; }
        /// <summary>任务内唯一的节点 ID；调整顺序或名称时不得重新生成。</summary>
        public string Id { get; set; }
        /// <summary>便于中文开发者和现场人员识别的步骤名称。</summary>
        public string Name { get; set; }
        /// <summary>节点及其子动作的总时限，单位毫秒；内存执行器使用扣除暂停的活动时间。</summary>
        public int TimeoutMs { get; set; } = 30000;
    }
}
