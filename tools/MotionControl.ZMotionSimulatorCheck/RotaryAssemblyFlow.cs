using System;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.Motion;

namespace MotionControl.ZMotionSimulatorCheck
{
    /// <summary>模拟伺服压装工位，按先退安全位、再定位、最后下压的顺序运行。</summary>
    internal sealed class RotaryAssemblyFlow : IStationFlow
    {
        /// <summary>压装工位的定位轴和压头轴。</summary>
        private readonly IAxis _positionAxis, _pressAxis;
        /// <summary>保护压装工位两根轴的整组占用。</summary>
        private readonly IAxisResourceCoordinator _axisResources;
        /// <summary>读取送料到位并通知送料工位可以退回。</summary>
        private readonly RotaryAssemblySignals _signals;
        /// <summary>仿真使用的低速运动参数。</summary>
        private readonly AxisMotionProfile _profile = new AxisMotionProfile(5, 10, 10);
        /// <summary>定位、压头安全、下压和压装定位目标。</summary>
        private double _positionClear, _positionWork, _pressSafe, _pressWork;
        /// <summary>已经完成的周期编号；暂停时保留。</summary>
        private int _finishedCycle = -1;

        /// <summary>创建使用定位轴与压头轴的压装工位流程。</summary>
        public RotaryAssemblyFlow(IAxis positionAxis, IAxis pressAxis,
            IAxisResourceCoordinator axisResources, RotaryAssemblySignals signals,
            double positionClear, double positionWork, double pressSafe, double pressWork)
        {
            _positionAxis = positionAxis;
            _pressAxis = pressAxis;
            _axisResources = axisResources;
            _signals = signals;
            _positionClear = positionClear;
            _positionWork = positionWork;
            _pressSafe = pressSafe;
            _pressWork = pressWork;
            CurrentStepName = "等待启动";
        }

        /// <summary>压装工位在运行监控中的名称。</summary>
        public string StationName { get { return "伺服压装"; } }
        /// <summary>当前压装步骤或等待的送料条件。</summary>
        public string CurrentStepName { get; private set; }

        /// <summary>连接并读取轴初始位置后，设置定位轴和压头轴的安全及工作目标。</summary>
        public void SetTargets(double positionClear, double positionWork, double pressSafe, double pressWork)
        {
            _positionClear = positionClear;
            _positionWork = positionWork;
            _pressSafe = pressSafe;
            _pressWork = pressWork;
        }

        /// <summary>等待送料组件到位，完成压装后让定位轴和压头都退回安全等待位。</summary>
        public async Task ExecuteCurrentStepAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int cycle = _signals.CurrentCycle;
            if (_finishedCycle >= cycle)
            {
                CurrentStepName = "压装工位已退回，等待转盘分度";
                await _signals.WaitForNextCycleAsync(cycle, cancellationToken).ConfigureAwait(false);
                return;
            }

            using (await _axisResources.AcquireAxisGroupAsync(
                new[] { _positionAxis.Name, _pressAxis.Name }, cancellationToken).ConfigureAwait(false))
            {
                CurrentStepName = "压头先退到安全高度";
                await _pressAxis.MoveAbsoluteAsync(_pressSafe, _profile, cancellationToken).ConfigureAwait(false);

                CurrentStepName = "等待伺服送料到位";
                while (!_signals.IsComponentReady(cycle))
                {
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                }

                CurrentStepName = "定位轴进入压装位置";
                await _positionAxis.MoveAbsoluteAsync(_positionWork, _profile, cancellationToken).ConfigureAwait(false);

                CurrentStepName = "压头下压并完成压装";
                await _pressAxis.MoveAbsoluteAsync(_pressWork, _profile, cancellationToken).ConfigureAwait(false);
                _signals.MarkComponentConsumed(cycle);

                CurrentStepName = "压头回安全高度";
                await _pressAxis.MoveAbsoluteAsync(_pressSafe, _profile, cancellationToken).ConfigureAwait(false);
                CurrentStepName = "定位轴退回等待位置";
                await _positionAxis.MoveAbsoluteAsync(_positionClear, _profile, cancellationToken).ConfigureAwait(false);
            }

            _signals.MarkAssemblyReady(cycle);
            _finishedCycle = cycle;
            CurrentStepName = "压装完成，等待转盘分度";
        }

        /// <summary>保留当前压装周期，复位后按可读的工艺步骤重新确认执行。</summary>
        public Task ResetAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
