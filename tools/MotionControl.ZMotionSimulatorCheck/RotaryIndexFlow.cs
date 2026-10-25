using System;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.Motion;

namespace MotionControl.ZMotionSimulatorCheck
{
    /// <summary>等待所有工位退回安全等待位置后，驱动转盘分度轴进入下一轮。</summary>
    internal sealed class RotaryIndexFlow : IStationFlow
    {
        /// <summary>单根转盘分度轴。</summary>
        private readonly IAxis _indexAxis;
        /// <summary>转盘分度轴资源协调入口。</summary>
        private readonly IAxisResourceCoordinator _axisResources;
        /// <summary>检查所有工位是否完成，并推进转盘周期。</summary>
        private readonly RotaryAssemblySignals _signals;
        /// <summary>仿真使用的低速分度参数。</summary>
        private readonly AxisMotionProfile _profile = new AxisMotionProfile(5, 10, 10);
        /// <summary>首轮和第二轮分度目标位置。</summary>
        private double _firstIndexPosition, _secondIndexPosition;
        /// <summary>已经处理的工位周期编号；暂停后保留。</summary>
        private int _finishedCycle = -1;

        /// <summary>创建等待五个工位完成的转盘分度流程。</summary>
        public RotaryIndexFlow(IAxis indexAxis, IAxisResourceCoordinator axisResources,
            RotaryAssemblySignals signals, double firstIndexPosition, double secondIndexPosition)
        {
            _indexAxis = indexAxis;
            _axisResources = axisResources;
            _signals = signals;
            _firstIndexPosition = firstIndexPosition;
            _secondIndexPosition = secondIndexPosition;
            CurrentStepName = "等待启动";
        }

        /// <summary>转盘分度工位在运行监控中的名称。</summary>
        public string StationName { get { return "转盘分度"; } }
        /// <summary>当前等待工位退位、分度或结束的步骤说明。</summary>
        public string CurrentStepName { get; private set; }

        /// <summary>连接并读取分度轴初始位置后，设置前两次分度的安全目标。</summary>
        public void SetTargets(double firstIndexPosition, double secondIndexPosition)
        {
            _firstIndexPosition = firstIndexPosition;
            _secondIndexPosition = secondIndexPosition;
        }

        /// <summary>工位都完成并退回后分度；最后一轮只结束流程，不再多转一圈。</summary>
        public async Task ExecuteCurrentStepAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int cycle = _signals.CurrentCycle;
            if (_finishedCycle >= cycle)
            {
                CurrentStepName = "分度流程完成，等待整机停止";
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                return;
            }

            CurrentStepName = "等待五个工位完成并退回安全等待位";
            while (!_signals.AreAllStationsReady(cycle))
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }

            if (cycle == RotaryAssemblySignals.CycleCount - 1)
            {
                _signals.MarkFinalCycleCompleted(cycle);
                _finishedCycle = cycle;
                CurrentStepName = "三轮加工完成，等待整机停止";
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                return;
            }

            double target = cycle == 0 ? _firstIndexPosition : _secondIndexPosition;
            using (await _axisResources.AcquireAxisAsync(_indexAxis.Name, cancellationToken).ConfigureAwait(false))
            {
                CurrentStepName = "转盘分度到下一工位";
                await _indexAxis.MoveAbsoluteAsync(target, _profile, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            _signals.AdvanceAfterIndex(cycle);
            _finishedCycle = cycle;
            CurrentStepName = "分度完成，启动下一轮工位";
        }

        /// <summary>保留当前分度等待条件，复位后继续等待工位就绪。</summary>
        public Task ResetAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
