namespace MotionControl.Framework.Contracts.Flow
{
    /// <summary>
    /// 当前独占设备命令权的执行入口。同一时刻只允许一个入口启动设备动作；
    /// 由整机运行时在启动前检查，避免两套调度同时控制同一批轴。
    /// </summary>
    public enum MachineControlOwner
    {
        /// <summary>没有任何执行入口，只能手动调试；设备未配置工位流程且未交给任务入口时使用。</summary>
        None = 0,

        /// <summary>设备工程中用 C# 编写的 IStationFlow 工位流程入口。</summary>
        StationFlows = 1,

        /// <summary>图形化任务定义执行入口，由 TaskEngine 按任务文件驱动设备。</summary>
        TaskEngine = 2
    }
}
