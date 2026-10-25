using System;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.Motion;

namespace MotionControl.ZMotionSimulatorCheck
{
    /// <summary>模拟振动送料后的伺服推料机构，并与压装工位完成取料握手。</summary>
    internal sealed class RotaryFeederFlow : IStationFlow
    {
        /// <summary>送料机构使用的逻辑轴。</summary>
        private readonly IAxis _feederAxis;
        /// <summary>保护送料逻辑轴不被其他流程同时驱动。</summary>
        private readonly IAxisResourceCoordinator _axisResources;
        /// <summary>记录送料到位、压装取料和当前分度周期。</summary>
        private readonly RotaryAssemblySignals _signals;
        /// <summary>仿真使用的低速运动参数。</summary>
        private readonly AxisMotionProfile _profile = new AxisMotionProfile(5, 10, 10);
        /// <summary>送料轴的等待位置和推料位置。</summary>
        private double _clearPosition, _feedPosition;
        /// <summary>已经完成的周期编号；暂停期间不清除。</summary>
        private int _finishedCycle = -1;

        /// <summary>创建独立的送料工位流程。</summary>
        public RotaryFeederFlow(IAxis feederAxis, IAxisResourceCoordinator axisResources,
            RotaryAssemblySignals signals, double clearPosition, double feedPosition)
        {
            _feederAxis = feederAxis;
            _axisResources = axisResources;
            _signals = signals;
            _clearPosition = clearPosition;
            _feedPosition = feedPosition;
            CurrentStepName = "等待启动";
        }

        /// <summary>送料工位在运行监控中的名称。</summary>
        public string StationName { get { return "伺服送料"; } }
        /// <summary>当前送料动作或等待的握手条件。</summary>
        public string CurrentStepName { get; private set; }

        /// <summary>连接并读取送料轴初始位置后，设置等待位置和送料目标。</summary>
        public void SetTargets(double clearPosition, double feedPosition)
        {
            _clearPosition = clearPosition;
            _feedPosition = feedPosition;
        }

        /// <summary>送料轴送出组件，等待压装工位取走后再退回安全等待位。</summary>
        public async Task ExecuteCurrentStepAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int cycle = _signals.CurrentCycle;
            if (_finishedCycle >= cycle)
            {
                CurrentStepName = "送料工位已退回，等待转盘分度";
                await _signals.WaitForNextCycleAsync(cycle, cancellationToken).ConfigureAwait(false);
                return;
            }

            using (await _axisResources.AcquireAxisAsync(_feederAxis.Name, cancellationToken).ConfigureAwait(false))
            {
                CurrentStepName = "送料轴送出组件";
                await _feederAxis.MoveAbsoluteAsync(_feedPosition, _profile, cancellationToken).ConfigureAwait(false);
                _signals.MarkComponentReady(cycle);

                CurrentStepName = "等待压装工位取走组件";
                while (!_signals.IsComponentConsumed(cycle))
                {
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                }

                CurrentStepName = "送料轴退回等待位置";
                await _feederAxis.MoveAbsoluteAsync(_clearPosition, _profile, cancellationToken).ConfigureAwait(false);
            }

            _signals.MarkFeederReady(cycle);
            _finishedCycle = cycle;
            CurrentStepName = "送料完成，等待转盘分度";
        }

        /// <summary>保留送料步骤，使暂停后从当前周期重新确认动作和握手条件。</summary>
        public Task ResetAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
