namespace MotionControl.Framework.Contracts.Preparation
{
    /// <summary>工位准备的可见状态；不会由报警复位直接变为准备完成。</summary>
    public enum StationPreparationState
    {
        /// <summary>本连接周期内尚未完成准备或资格已失效。</summary>
        NotPrepared,
        /// <summary>已列入本次准备，等待前序工位。</summary>
        Waiting,
        /// <summary>正在检查或执行本工位准备步骤。</summary>
        Preparing,
        /// <summary>准备动作完成且现场验收通过。</summary>
        Ready,
        /// <summary>操作被停止，未完成准备。</summary>
        Stopped,
        /// <summary>准备失败，必须处理原因。</summary>
        Failed
    }

    /// <summary>提供给操作页的不可变工位准备状态。</summary>
    public sealed class StationPreparationSnapshot
    {
        /// <summary>创建独立快照，避免界面直接修改准备资格。</summary>
        public StationPreparationSnapshot(string stationId, string stationName,
            StationPreparationState state, string action, string detail)
        { StationId = stationId; StationName = stationName; State = state; Action = action; Detail = detail; }
        /// <summary>设备定义的工位标识。</summary>
        public string StationId { get; }
        /// <summary>操作员使用的工位名称。</summary>
        public string StationName { get; }
        /// <summary>当前准备阶段。</summary>
        public StationPreparationState State { get; }
        /// <summary>当前动作或最近结果。</summary>
        public string Action { get; }
        /// <summary>阻塞原因或操作建议。</summary>
        public string Detail { get; }
        /// <summary>仅完成并通过验收时为真。</summary>
        public bool IsPrepared { get { return State == StationPreparationState.Ready; } }
    }
}
