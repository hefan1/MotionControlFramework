namespace MotionControl.TaskEngine.Device
{
    /// <summary>
    /// 任务执行时一根逻辑轴的状态快照。位置和速度使用该轴配置的工程单位；
    /// 只表达框架约定的运行状态，不暴露厂商状态位或错误码。
    /// </summary>
    public sealed class TaskAxisState
    {
        /// <summary>逻辑轴名称。</summary>
        public string Name { get; }
        /// <summary>当前反馈位置，使用该轴工程单位。</summary>
        public double Position { get; }
        /// <summary>伺服是否已使能。</summary>
        public bool Enabled { get; }
        /// <summary>位置参考是否有效，即是否已完成回零。</summary>
        public bool Homed { get; }
        /// <summary>是否仍有一个动作占用该轴；暂停期间保持占用。</summary>
        public bool IsBusy { get; }
        /// <summary>当前轴报警说明；为空表示没有轴报警。</summary>
        public string Alarm { get; }
        /// <summary>已接受的移动或回零命令次数，供重复执行和漏执行检查使用。</summary>
        public int AcceptedCommands { get; }

        /// <summary>创建不可变快照，避免外部修改正在执行的动作。</summary>
        public TaskAxisState(string name, double position, bool enabled, bool homed,
            bool busy, string alarm, int acceptedCommands)
        {
            Name = name;
            Position = position;
            Enabled = enabled;
            Homed = homed;
            IsBusy = busy;
            Alarm = alarm;
            AcceptedCommands = acceptedCommands;
        }
    }
}
