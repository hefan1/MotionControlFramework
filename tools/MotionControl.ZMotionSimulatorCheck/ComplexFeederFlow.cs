using System;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.Motion;

namespace MotionControl.ZMotionSimulatorCheck
{
    internal sealed class ComplexFeederFlow : IStationFlow
    {
        /// <summary>送料任务使用的独立逻辑轴。</summary>
        private readonly IAxis _axis;
        /// <summary>送料轴资源占用入口。</summary>
        private readonly IAxisResourceCoordinator _axisResources;
        /// <summary>搬运任务给出的完成标记。</summary>
        private readonly ComplexMachineSignals _signals;
        /// <summary>送料轴的仿真运动参数。</summary>
        private readonly AxisMotionProfile _profile = new AxisMotionProfile(5, 10, 10);
        /// <summary>10 为送料定位，20 为等待搬运，30 为完成待机。</summary>
        private int _step = 10;
        /// <summary>送料轴的绝对目标位置。</summary>
        private double _target;
        /// <summary>送料轴到位且搬运交接完成后才为真。</summary>
        private int _completed;

        /// <summary>建立一条独立的送料流程。</summary>
        public ComplexFeederFlow(IAxis axis, IAxisResourceCoordinator axisResources,
            ComplexMachineSignals signals)
        { _axis = axis; _axisResources = axisResources; _signals = signals;
          StationName = "送料任务"; CurrentStepName = "等待启动"; }

        /// <summary>任务在整机工位列表中的名称。</summary>
        public string StationName { get; }
        /// <summary>当前送料步骤及等待原因。</summary>
        public string CurrentStepName { get; private set; }
        /// <summary>送料与搬运两项条件均完成。</summary>
        public bool Completed => Volatile.Read(ref _completed) == 1;

        /// <summary>保存连接后计算的送料轴绝对目标。</summary>
        public void SetTarget(double target) { _target = target; }

        /// <summary>送料轴独立定位，等待搬运交接后结束本轮任务。</summary>
        public async Task ExecuteCurrentStepAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_step == 10)
            {
                CurrentStepName = "送料轴定位";
                using (await _axisResources.AcquireAxisAsync(_axis.Name, cancellationToken).ConfigureAwait(false))
                    await _axis.MoveAbsoluteAsync(_target, _profile, cancellationToken).ConfigureAwait(false);
                _step = 20;
                CurrentStepName = "等待搬运交接";
                return;
            }
            if (_step == 20)
            {
                while (!_signals.TransferDone)
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                Interlocked.Exchange(ref _completed, 1);
                _step = 30;
                CurrentStepName = "送料与搬运交接完成";
                return;
            }
            if (_step == 30) { await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false); return; }
            throw new InvalidOperationException("送料任务的步骤编号无效：" + _step);
        }

        /// <summary>复位保留送料当前步骤，避免重做已完成的轴定位。</summary>
        public Task ResetAsync(CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    }
}
