using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.Motion;

namespace MotionControl.ZMotionSimulatorCheck
{
    /// <summary>模拟转盘上料工位使用 X/Z 轴和共享转运 X 轴取放工件。</summary>
    internal sealed class RotaryLoadFlow : IStationFlow
    {
        /// <summary>上料工位的 X、Z 轴与被其他工位共用的转运轴。</summary>
        private readonly IAxis _xAxis, _zAxis, _sharedTransferAxis;
        /// <summary>申请上料工位涉及的全部逻辑轴。</summary>
        private readonly IAxisResourceCoordinator _axisResources;
        /// <summary>记录上料完成、转盘周期和共享轴占用。</summary>
        private readonly RotaryAssemblySignals _signals;
        /// <summary>仿真使用的低速运动参数。</summary>
        private readonly AxisMotionProfile _profile = new AxisMotionProfile(5, 10, 10);
        /// <summary>上料工位的等待、取料与安全退回位置。</summary>
        private double _xClear, _zClear, _zSafe, _xApproach, _xPick, _zPick;
        /// <summary>共享转运轴的等待和上料位置。</summary>
        private double _sharedClear, _sharedPick;
        /// <summary>已经完成的周期编号；暂停时保留。</summary>
        private int _finishedCycle = -1;
        /// <summary>上料工位等待共享轴资源的累计时间。</summary>
        private long _resourceWaitMilliseconds;

        /// <summary>创建带共享转运轴的上料工位流程。</summary>
        public RotaryLoadFlow(IAxis xAxis, IAxis zAxis, IAxis sharedTransferAxis,
            IAxisResourceCoordinator axisResources, RotaryAssemblySignals signals,
            double xClear, double zClear, double zSafe, double xPick, double zPick,
            double sharedClear, double sharedPick)
        {
            _xAxis = xAxis;
            _zAxis = zAxis;
            _sharedTransferAxis = sharedTransferAxis;
            _axisResources = axisResources;
            _signals = signals;
            _xClear = xClear;
            _zClear = zClear;
            _zSafe = zSafe;
            _xPick = xPick;
            _zPick = zPick;
            _sharedClear = sharedClear;
            _sharedPick = sharedPick;
            CurrentStepName = "等待启动";
        }

        /// <summary>上料工位在运行监控中的名称。</summary>
        public string StationName { get { return "转盘上料"; } }
        /// <summary>当前上料步骤或共享资源等待说明。</summary>
        public string CurrentStepName { get; private set; }
        /// <summary>上料工位等待共享转运轴的累计毫秒数。</summary>
        public long ResourceWaitMilliseconds { get { return Interlocked.Read(ref _resourceWaitMilliseconds); } }

        /// <summary>清空本次仿真阶段之前累计的共享轴排队时间。</summary>
        public void ResetResourceWaitTime()
        {
            Interlocked.Exchange(ref _resourceWaitMilliseconds, 0);
        }

        /// <summary>连接并读取轴初始位置后，设置本轮上料和安全退回目标。</summary>
        public void SetTargets(double xClear, double zClear, double zSafe, double xApproach,
            double xPick, double zPick, double sharedClear, double sharedPick)
        {
            _xClear = xClear;
            _zClear = zClear;
            _zSafe = zSafe;
            _xApproach = xApproach;
            _xPick = xPick;
            _zPick = zPick;
            _sharedClear = sharedClear;
            _sharedPick = sharedPick;
        }

        /// <summary>先抬 Z 轴，再取放工件，最后让机构退回转盘分度等待位置。</summary>
        public async Task ExecuteCurrentStepAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int cycle = _signals.CurrentCycle;
            if (_finishedCycle >= cycle)
            {
                CurrentStepName = "上料机构已退回，等待转盘分度";
                await _signals.WaitForNextCycleAsync(cycle, cancellationToken).ConfigureAwait(false);
                return;
            }

            CurrentStepName = "上料 X/Z 轴预定位到共享转运等待位";
            using (await _axisResources.AcquireAxisGroupAsync(
                new[] { _xAxis.Name, _zAxis.Name }, cancellationToken).ConfigureAwait(false))
            {
                await Task.WhenAll(
                    _zAxis.MoveAbsoluteAsync(_zSafe, _profile, cancellationToken),
                    _xAxis.MoveAbsoluteAsync(_xApproach, _profile, cancellationToken)).ConfigureAwait(false);
            }

            CurrentStepName = "等待共享转运 X 轴";
            Stopwatch wait = Stopwatch.StartNew();
            using (await _axisResources.AcquireAxisGroupAsync(
                new[] { _xAxis.Name, _zAxis.Name, _sharedTransferAxis.Name }, cancellationToken).ConfigureAwait(false))
            {
                Interlocked.Add(ref _resourceWaitMilliseconds, wait.ElapsedMilliseconds);
                _signals.EnterSharedAxis("上料");
                try
                {
                    CurrentStepName = "共享转运轴执行上料";
                    await _zAxis.MoveAbsoluteAsync(_zSafe, _profile, cancellationToken).ConfigureAwait(false);
                    CurrentStepName = "上料 X 轴和共享转运轴同时进入取料位";
                    await Task.WhenAll(
                        _xAxis.MoveAbsoluteAsync(_xPick, _profile, cancellationToken),
                        _sharedTransferAxis.MoveAbsoluteAsync(_sharedPick, _profile, cancellationToken))
                        .ConfigureAwait(false);

                    CurrentStepName = "上料 Z 轴下降取料";
                    await _zAxis.MoveAbsoluteAsync(_zPick, _profile, cancellationToken).ConfigureAwait(false);

                    CurrentStepName = "上料 Z 轴退回安全位";
                    await _zAxis.MoveAbsoluteAsync(_zSafe, _profile, cancellationToken).ConfigureAwait(false);

                    CurrentStepName = "上料 X 轴和共享转运轴退回等待位";
                    await Task.WhenAll(
                        _xAxis.MoveAbsoluteAsync(_xClear, _profile, cancellationToken),
                        _sharedTransferAxis.MoveAbsoluteAsync(_sharedClear, _profile, cancellationToken))
                        .ConfigureAwait(false);

                    CurrentStepName = "上料 Z 轴回等待高度";
                    await _zAxis.MoveAbsoluteAsync(_zClear, _profile, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _signals.LeaveSharedAxis("上料");
                }
            }

            _signals.MarkLoadReady(cycle);
            _finishedCycle = cycle;
            CurrentStepName = "上料完成，等待转盘分度";
        }

        /// <summary>保留上料工位步骤，使暂停后从本轮上料流程重新确认动作。</summary>
        public Task ResetAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
