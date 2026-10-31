namespace MotionControl.Framework.Contracts.Flow
{
    /// <summary>可提供当前等待 DI 信号的工位流程诊断信息。</summary>
    public interface IStationInputWaitState
    {
        /// <summary>当前步骤等待的逻辑 DI 名称；当前步骤不等待 DI 时返回 null。</summary>
        string WaitingInputName { get; }
    }
}
