namespace MotionControl.Framework.Contracts.Flow
{
    /// <summary>显式声明可在受控暂停后保存并恢复流程步骤的工位。</summary>
    public interface IRecoverableStationFlow
    {
        /// <summary>返回当前完成边界上的流程步骤编号，供设备恢复快照保存。</summary>
        int RecoveryStepNumber { get; }

        /// <summary>检查指定步骤是否属于本工位已知步骤，避免恢复到旧版或无效步骤。</summary>
        bool CanRestoreStep(int stepNumber);

        /// <summary>恢复到已校验的步骤起点；不应在此方法中发出运动或 IO 命令。</summary>
        void RestoreStep(int stepNumber);
    }
}
