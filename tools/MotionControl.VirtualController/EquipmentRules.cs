namespace MotionControl.VirtualController
{
    /// <summary>单个输入跟随某个输出的延迟反馈规则。</summary>
    internal sealed class FeedbackRule
    {
        /// <summary>需要改变的输入物理通道。</summary>
        public int Input;
        /// <summary>监听的输出物理通道。</summary>
        public int Output;
        /// <summary>反馈改变的延迟，单位毫秒。</summary>
        public int DelayMs;
        /// <summary>输出高电平时输入是否应为高电平。</summary>
        public bool ActiveHigh;
        /// <summary>故障时保持当前输入，不再跟随输出。</summary>
        public bool Stuck;
        /// <summary>用于让过期的延迟任务失效。</summary>
        public int Version;
    }

    /// <summary>用于上料工位循环测试的供料与夹具规则。</summary>
    internal sealed class FeederRule
    {
        /// <summary>是否启用自动供料。</summary>
        public bool Enabled;
        /// <summary>上料到位输入通道。</summary>
        public int Input;
        /// <summary>夹具动作输出通道。</summary>
        public int Output;
        /// <summary>启动后上料到位延迟，单位毫秒。</summary>
        public int FeedDelayMs;
        /// <summary>取走物料后下一件到位延迟，单位毫秒。</summary>
        public int EmptyDelayMs;
        /// <summary>已经观察到夹具打开后的夹紧动作。</summary>
        public bool Gripped;
        /// <summary>是否注入缺料故障。</summary>
        public bool Fault;
        /// <summary>用于让过期的延迟供料任务失效。</summary>
        public int Version;
        /// <summary>上料轴物理轴号；-1 表示旧的无安全位门禁模式。</summary>
        public int SafeAxis = -1;
        /// <summary>同一安全位置条件下还必须停稳的其他物理轴；旧配置默认为空。</summary>
        public int[] AdditionalSafeAxes = new int[0];
        /// <summary>上料轴已回零后的逻辑安全位置。</summary>
        public double SafePosition;
        /// <summary>允许轴偏离逻辑安全位置的最大距离。</summary>
        public double SafeTolerance;
        /// <summary>是否有一件等待安全位和延时的物料。</summary>
        public bool FeedPending;
        /// <summary>本次待供料应连续满足条件的时间，单位毫秒。</summary>
        public int PendingDelayMs;
        /// <summary>安全位持续满足的起始单调时间，空表示条件尚不满足。</summary>
        public double? SafeSinceMilliseconds;
    }
}
