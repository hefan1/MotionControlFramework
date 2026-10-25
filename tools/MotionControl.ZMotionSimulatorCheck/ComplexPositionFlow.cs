using System;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.Motion;

namespace MotionControl.ZMotionSimulatorCheck
{
    internal sealed class ComplexPositionFlow : IStationFlow
    {
        /// <summary>此工位的两根独立逻辑轴。</summary>
        private readonly IAxis _firstAxis, _secondAxis;
        /// <summary>整组占用两根轴，防止其他任务插入动作。</summary>
        private readonly IAxisResourceCoordinator _axisResources;
        /// <summary>交接给检测流程的完成信号。</summary>
        private readonly ComplexMachineSignals _signals;
        /// <summary>区分 A、B 两侧的交接标记。</summary>
        private readonly bool _stationA;
        /// <summary>两根轴使用的仿真速度、加速度和减速度。</summary>
        private readonly AxisMotionProfile _profile = new AxisMotionProfile(5, 10, 10);
        /// <summary>10 为两轴定位，20 为完成后等待整机暂停。</summary>
        private int _step = 10;
        /// <summary>两根轴的绝对目标位置。</summary>
        private double _firstTarget, _secondTarget;
        /// <summary>两根轴均到位后才为真。</summary>
        private int _completed;

        /// <summary>建立一个实际使用两根轴的定位任务。</summary>
        public ComplexPositionFlow(string name, IAxis firstAxis, IAxis secondAxis,
            IAxisResourceCoordinator axisResources, ComplexMachineSignals signals, bool stationA)
        {
            StationName = name;
            _firstAxis = firstAxis; _secondAxis = secondAxis;
            _axisResources = axisResources; _signals = signals; _stationA = stationA;
            CurrentStepName = "等待启动";
        }

        /// <summary>任务在整机工位列表中的名称。</summary>
        public string StationName { get; }
        /// <summary>当前正在定位或等待的步骤。</summary>
        public string CurrentStepName { get; private set; }
        /// <summary>两根轴是否均已到位。</summary>
        public bool Completed => Volatile.Read(ref _completed) == 1;

        /// <summary>保存两轴目标，目标由连接后读取的真实仿真位置计算。</summary>
        public void SetTargets(double firstTarget, double secondTarget)
        { _firstTarget = firstTarget; _secondTarget = secondTarget; }

        /// <summary>同组发出两根轴的定位命令，并在全部到位后通知检测任务。</summary>
        public async Task ExecuteCurrentStepAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_step == 20) { await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false); return; }
            if (_step != 10) throw new InvalidOperationException(StationName + " 的步骤编号无效：" + _step);

            CurrentStepName = "两轴同时定位，等待全部到位";
            using (await _axisResources.AcquireAxisGroupAsync(
                new[] { _firstAxis.Name, _secondAxis.Name }, cancellationToken).ConfigureAwait(false))
            {
                await Task.WhenAll(
                    _firstAxis.MoveAbsoluteAsync(_firstTarget, _profile, cancellationToken),
                    _secondAxis.MoveAbsoluteAsync(_secondTarget, _profile, cancellationToken)).ConfigureAwait(false);
            }
            _signals.MarkPositionReady(_stationA);
            Interlocked.Exchange(ref _completed, 1);
            _step = 20;
            CurrentStepName = "两轴到位，等待后续任务";
        }

        /// <summary>复位保留中断步骤和已完成标记，未完成定位仍用原绝对目标重试。</summary>
        public Task ResetAsync(CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    }
}
