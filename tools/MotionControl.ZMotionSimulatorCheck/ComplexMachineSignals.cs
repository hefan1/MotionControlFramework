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
            if (Interlocked.Increment(ref _sharedAxisOwners) != 1)
            {
                Interlocked.Decrement(ref _sharedAxisOwners);
                throw new InvalidOperationException("共用 Z 轴同时分配给了两个检测任务。");
            }
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
