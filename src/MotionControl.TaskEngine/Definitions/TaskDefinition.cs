namespace MotionControl.TaskEngine.Definitions
{
    /// <summary>一个可保存的任务定义；只描述要做什么，不保存运行进度。</summary>
    public sealed class TaskDefinition
    {
        /// <summary>文件格式版本；当前只接受版本 1。</summary>
        public int SchemaVersion { get; set; } = 1;
        /// <summary>稳定任务标识；重命名显示文字时保持不变。</summary>
        public string TaskId { get; set; }
        /// <summary>供现场人员阅读的任务名称。</summary>
        public string Name { get; set; }
        /// <summary>定义修订号；发布修改后的任务时递增，供以后恢复校验使用。</summary>
        public int Revision { get; set; } = 1;
        /// <summary>任务根节点，必须是顺序节点。</summary>
        public TaskNode Root { get; set; }
    }
}
