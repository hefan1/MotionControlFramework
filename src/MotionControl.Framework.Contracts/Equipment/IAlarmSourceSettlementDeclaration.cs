namespace MotionControl.Framework.Contracts.Equipment
{
    /// <summary>设备项目对报警来源生命周期的明确声明；未实现时框架不能假设来源已经停止。</summary>
    public interface IAlarmSourceSettlementDeclaration
    {
        /// <summary>声明设备报警只由已受 Runtime 管理并等待收尾的流程、准备或任务发布，不存在独立后台发布者。</summary>
        bool AllAlarmSourcesOwnedByTrackedFlows { get; }
    }
}
