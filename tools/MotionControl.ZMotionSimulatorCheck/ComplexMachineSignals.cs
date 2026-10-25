using System;
using System.Threading;

namespace MotionControl.ZMotionSimulatorCheck
{
    internal sealed class ComplexMachineSignals
    {
        /// <summary>A、B 定位完成标记。</summary>
        private int _aPositionReady, _bPositionReady;
        /// <summary>A、B 检测完成标记。</summary>
        private int _aInspectionDone, _bInspectionDone;
        /// <summary>搬运完成标记。</summary>
        private int _transferDone;
        /// <summary>当前占用共用 Z 轴的流程数量。</summary>
        private int _sharedAxisOwners;
        /// <summary>当前正在申请共用 Z 轴的流程数量。</summary>
        private int _sharedAxisWaiters;
        /// <summary>本轮是否真实观察到共用轴同时存在持有者和等待者。</summary>
        private int _sharedAxisContentionObserved;
        /// <summary>最后一个检测流程接受的共用 Z 轴目标位置。</summary>
        private double _lastSharedAxisTarget;

        /// <summary>两个定位任务均完成后才允许两个检测任务争用共用轴。</summary>
        public bool BothPositionsReady => Volatile.Read(ref _aPositionReady) == 1
            && Volatile.Read(ref _bPositionReady) == 1;
        /// <summary>两个检测任务均完成后才允许搬运任务报告交接完成。</summary>
        public bool BothInspectionsDone => Volatile.Read(ref _aInspectionDone) == 1
            && Volatile.Read(ref _bInspectionDone) == 1;
        /// <summary>送料任务等待的搬运交接结果。</summary>
        public bool TransferDone => Volatile.Read(ref _transferDone) == 1;
        /// <summary>当前正在进入共用 Z 轴动作段的检测任务数量，暂停后应回到 0。</summary>
        public int SharedAxisOwners => Volatile.Read(ref _sharedAxisOwners);
        /// <summary>当前等待共用 Z 轴资源的检测任务数量。</summary>
        public int SharedAxisWaiters => Volatile.Read(ref _sharedAxisWaiters);
        /// <summary>本轮运行中是否曾实际观察到共用轴争用。</summary>
        public bool SharedAxisContentionObserved => Volatile.Read(ref _sharedAxisContentionObserved) == 1;
        /// <summary>最后完成检测的任务在共用轴上留下的目标位置。</summary>
        public double LastSharedAxisTarget => _lastSharedAxisTarget;

        /// <summary>记录对应定位任务已经使两根轴都到位。</summary>
        public void MarkPositionReady(bool stationA)
        {
            if (stationA) Interlocked.Exchange(ref _aPositionReady, 1);
            else Interlocked.Exchange(ref _bPositionReady, 1);
        }

        /// <summary>检查任何时刻只有一个检测任务进入共用 Z 轴动作区。</summary>
        public void EnterSharedAxis()
        {
            int ownerCount = Interlocked.Increment(ref _sharedAxisOwners);
            if (ownerCount != 1)
            {
                Interlocked.Decrement(ref _sharedAxisOwners);
                throw new InvalidOperationException("共用 Z 轴同时分配给了两个检测任务。");
            }

            if (SharedAxisWaiters > 0)
                Interlocked.Exchange(ref _sharedAxisContentionObserved, 1);
        }

        /// <summary>记录一个检测任务开始申请包含共用 Z 轴的完整轴组。</summary>
        public void BeginSharedAxisRequest()
        {
            Interlocked.Increment(ref _sharedAxisWaiters);
            if (SharedAxisOwners > 0)
                Interlocked.Exchange(ref _sharedAxisContentionObserved, 1);
        }

        /// <summary>在轴组申请成功、取消或失败后移除对应的等待记录。</summary>
        public void EndSharedAxisRequest()
        {
            int waiterCount = Interlocked.Decrement(ref _sharedAxisWaiters);
            if (waiterCount < 0)
            {
                Interlocked.Exchange(ref _sharedAxisWaiters, 0);
                throw new InvalidOperationException("共用 Z 轴等待计数出现负数。");
            }
        }

        /// <summary>新一轮争用验证前清除历史记录，并确认上一轮资源已全部归还。</summary>
        public void ResetSharedAxisContentionObservation()
        {
            if (SharedAxisOwners != 0 || SharedAxisWaiters != 0)
                throw new InvalidOperationException("共用 Z 轴仍被占用或仍有任务等待，不能清除争用记录。");

            Interlocked.Exchange(ref _sharedAxisContentionObserved, 0);
        }

        /// <summary>检测任务的轴都停稳后归还共用 Z 轴记录。</summary>
        public void LeaveSharedAxis() { Interlocked.Decrement(ref _sharedAxisOwners); }

        /// <summary>记录检测任务完成和共用 Z 轴最后的目标位置。</summary>
        public void MarkInspectionDone(bool stationA, double sharedAxisTarget)
        {
            _lastSharedAxisTarget = sharedAxisTarget;
            if (stationA) Interlocked.Exchange(ref _aInspectionDone, 1);
            else Interlocked.Exchange(ref _bInspectionDone, 1);
        }

        /// <summary>记录搬运任务已等到两侧检测结果并完成交接。</summary>
        public void MarkTransferDone() { Interlocked.Exchange(ref _transferDone, 1); }
    }
}
