namespace MotionControl.TaskEngine.Simulation
{
    /// <summary>内存轴状态；不代表实际控制卡、编码器或安全回路。</summary>
    public sealed class SimulatedAxisSnapshot
    {
        /// <summary>逻辑轴名称。</summary>
        public string Name { get; }
        /// <summary>当前仿真位置，使用目录内工程单位。</summary>
        public double Position { get; }
        /// <summary>是否已由测试场景设置为使能。</summary>
        public bool Enabled { get; }
        /// <summary>仿真位置参考是否有效。</summary>
        public bool Homed { get; }
        /// <summary>当前是否被一个仿真动作占用，暂停时仍保留占用。</summary>
        public bool IsBusy { get; }
        /// <summary>场景注入的报警，为空表示无报警。</summary>
        public string Alarm { get; }
        /// <summary>已接受的移动或回零命令次数，供重复执行检查使用。</summary>
        public int AcceptedCommands { get; }

        /// <summary>复制轴状态，防止外部直接改变正在执行的动作。</summary>
        internal SimulatedAxisSnapshot(string name, double position, bool enabled, bool homed,
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
