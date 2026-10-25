using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.Motion;

namespace MotionControl.ZMotionSimulatorCheck
{
    /// <summary>模拟良品/不良品出料工位，并与上料工位争用同一根转运轴。</summary>
    internal sealed class RotaryUnloadFlow : IStationFlow
    {
        /// <summary>出料工位的 X、Z 轴与上料工位共用的转运轴。</summary>
        private readonly IAxis _xAxis, _zAxis, _sharedTransferAxis;
        /// <summary>申请出料工位涉及的全部逻辑轴。</summary>
        private readonly IAxisResourceCoordinator _axisResources;
        /// <summary>读取前一轮视觉结果并记录出料分流结果。</summary>
        private readonly RotaryAssemblySignals _signals;
        /// <summary>仿真使用的低速运动参数。</summary>
        private readonly AxisMotionProfile _profile = new AxisMotionProfile(5, 10, 10);
        /// <summary>出料机构的等待、Z 安全、良品和不良品位置。</summary>
        private double _xClear, _zClear, _zSafe, _zPick;
        /// <summary>出料的良品/不良品等待位置与实际放料位置。</summary>
        private double _goodApproach, _rejectApproach, _goodPosition, _rejectPosition;
        /// <summary>共享转运轴的等待、良品和不良品位置。</summary>
        private double _sharedClear, _sharedGoodPosition, _sharedRejectPosition;
        /// <summary>已经完成的周期编号；暂停时保留。</summary>
        private int _finishedCycle = -1;
        /// <summary>出料工位等待共享轴资源的累计时间。</summary>
        private long _resourceWaitMilliseconds;

        /// <summary>创建具有结果分流和共享转运轴的出料工位流程。</summary>
        public RotaryUnloadFlow(IAxis xAxis, IAxis zAxis, IAxis sharedTransferAxis,
            IAxisResourceCoordinator axisResources, RotaryAssemblySignals signals,
            double xClear, double zClear, double zSafe, double zPick,
            double goodPosition, double rejectPosition, double sharedClear,
            double sharedGoodPosition, double sharedRejectPosition)
        {
            _xAxis = xAxis;
            _zAxis = zAxis;
            _sharedTransferAxis = sharedTransferAxis;
            _axisResources = axisResources;
            _signals = signals;
            _xClear = xClear;
            _zClear = zClear;
            _zSafe = zSafe;
            _zPick = zPick;
            _goodPosition = goodPosition;
            _rejectPosition = rejectPosition;
            _sharedClear = sharedClear;
            _sharedGoodPosition = sharedGoodPosition;
            _sharedRejectPosition = sharedRejectPosition;
            CurrentStepName = "等待启动";
        }

        /// <summary>出料工位在运行监控中的名称。</summary>
        public string StationName { get { return "良品与不良品出料"; } }
        /// <summary>当前出料步骤、结果分支或共享资源等待说明。</summary>
        public string CurrentStepName { get; private set; }
        /// <summary>出料工位等待共享转运轴的累计毫秒数。</summary>
        public long ResourceWaitMilliseconds { get { return Interlocked.Read(ref _resourceWaitMilliseconds); } }

        /// <summary>清空本次仿真阶段之前累计的共享轴排队时间。</summary>
        public void ResetResourceWaitTime()
        {
            Interlocked.Exchange(ref _resourceWaitMilliseconds, 0);
        }

        /// <summary>连接并读取轴初始位置后，设置良品/不良品下料路径和安全退回目标。</summary>
        public void SetTargets(double xClear, double zClear, double zSafe, double zPick,
            double goodApproach, double rejectApproach, double goodPosition, double rejectPosition, double sharedClear,
            double sharedGoodPosition, double sharedRejectPosition)
        {
            _xClear = xClear;
            _zClear = zClear;
            _zSafe = zSafe;
            _zPick = zPick;
            _goodApproach = goodApproach;
            _rejectApproach = rejectApproach;
            _goodPosition = goodPosition;
            _rejectPosition = rejectPosition;
            _sharedClear = sharedClear;
            _sharedGoodPosition = sharedGoodPosition;
            _sharedRejectPosition = sharedRejectPosition;
        }

        /// <summary>按前一轮检测结果选择良品或不良品位置，并在结束后退回安全位。</summary>
        public async Task ExecuteCurrentStepAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int cycle = _signals.CurrentCycle;
            if (_finishedCycle >= cycle)
            {
                CurrentStepName = "出料机构已退回，等待转盘分度";
                await _signals.WaitForNextCycleAsync(cycle, cancellationToken).ConfigureAwait(false);
                return;
            }

            bool passed = _signals.GetUnloadPassResult(cycle);
            double destination = passed ? _goodPosition : _rejectPosition;
            double approach = passed ? _goodApproach : _rejectApproach;
            double sharedDestination = passed ? _sharedGoodPosition : _sharedRejectPosition;
            CurrentStepName = "出料 X/Z 轴预定位到共享转运等待位";
            using (await _axisResources.AcquireAxisGroupAsync(
                new[] { _xAxis.Name, _zAxis.Name }, cancellationToken).ConfigureAwait(false))
            {
                await Task.WhenAll(
                    _zAxis.MoveAbsoluteAsync(_zSafe, _profile, cancellationToken),
                    _xAxis.MoveAbsoluteAsync(approach, _profile, cancellationToken)).ConfigureAwait(false);
            }

            CurrentStepName = "等待共享转运 X 轴";
            Stopwatch wait = Stopwatch.StartNew();
            using (await _axisResources.AcquireAxisGroupAsync(
                new[] { _xAxis.Name, _zAxis.Name, _sharedTransferAxis.Name }, cancellationToken).ConfigureAwait(false))
            {
                Interlocked.Add(ref _resourceWaitMilliseconds, wait.ElapsedMilliseconds);
                _signals.EnterSharedAxis("出料");
                try
                {
                    CurrentStepName = passed ? "共享转运轴执行良品下料" : "共享转运轴执行不良品分流";
                    await _zAxis.MoveAbsoluteAsync(_zSafe, _profile, cancellationToken).ConfigureAwait(false);

                    CurrentStepName = passed ? "出料 X/Z 轴到良品位置" : "出料 X/Z 轴到不良品位置";
                    await Task.WhenAll(
                        _xAxis.MoveAbsoluteAsync(destination, _profile, cancellationToken),
                        _sharedTransferAxis.MoveAbsoluteAsync(sharedDestination, _profile, cancellationToken))
                        .ConfigureAwait(false);

                    CurrentStepName = "出料 Z 轴取放工件";
                    await _zAxis.MoveAbsoluteAsync(_zPick, _profile, cancellationToken).ConfigureAwait(false);

                    CurrentStepName = "出料 Z 轴退回安全高度";
                    await _zAxis.MoveAbsoluteAsync(_zSafe, _profile, cancellationToken).ConfigureAwait(false);

                    CurrentStepName = "出料 X 轴和共享转运轴退回等待位";
                    await Task.WhenAll(
                        _xAxis.MoveAbsoluteAsync(_xClear, _profile, cancellationToken),
                        _sharedTransferAxis.MoveAbsoluteAsync(_sharedClear, _profile, cancellationToken))
                        .ConfigureAwait(false);

                    CurrentStepName = "出料 Z 轴回等待高度";
                    await _zAxis.MoveAbsoluteAsync(_zClear, _profile, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _signals.LeaveSharedAxis("出料");
                }
            }

            _signals.MarkUnloadReady(cycle, passed);
            _finishedCycle = cycle;
            CurrentStepName = passed ? "良品已出料，等待转盘分度" : "不良品已分流，等待转盘分度";
        }

        /// <summary>保留出料工位步骤，使暂停后重新执行当前轮的出料路径。</summary>
        public Task ResetAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
