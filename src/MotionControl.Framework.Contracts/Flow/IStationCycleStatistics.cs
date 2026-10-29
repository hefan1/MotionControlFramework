namespace MotionControl.Framework.Contracts.Flow
{
    /// <summary>可选的只读能力，报告工位完整循环的数量和最近一次周期。</summary>
    public interface IStationCycleStatistics
    {
        /// <summary>当前运行时生命周期内完成的完整循环数。</summary>
        long CompletedCycles { get; }

        /// <summary>最近一个完整循环的耗时；没有可测量的完整循环时为空。</summary>
        System.TimeSpan? LastCycleDuration { get; }
    }
}
