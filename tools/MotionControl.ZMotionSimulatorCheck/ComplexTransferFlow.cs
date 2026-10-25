using System;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.Motion;

namespace MotionControl.ZMotionSimulatorCheck
{
    internal sealed class ComplexTransferFlow : IStationFlow
    {
        /// <summary>搬运 X/Z 两根轴。</summary>
        private readonly IAxis _x, _z;
        /// <summary>搬运轴组占用入口。</summary>
        private readonly IAxisResourceCoordinator _axisResources;
        /// <summary>两个检测任务提供的完成信号。</summary>
        private readonly ComplexMachineSignals _signals;
        /// <summary>搬运轴组的仿真运动参数。</summary>
        private readonly AxisMotionProfile _profile = new AxisMotionProfile(5, 10, 10);
        /// <summary>10 为预定位，20 为等检测，30 为完成待机。</summary>
        private int _step = 10;
        /// <summary>搬运 X/Z 的绝对目标位置。</summary>
        private double _xTarget, _zTarget;
        /// <summary>检测结果已交接给搬运流程后才为真。</summary>
        private int _completed;

        /// <summary>建立搬运任务，目标在连接后设置。</summary>
        public ComplexTransferFlow(IAxis x, IAxis z, IAxisResourceCoordinator axisResources,
            ComplexMachineSignals signals)
        { _x = x; _z = z; _axisResources = axisResources; _signals = signals;
          StationName = "搬运任务"; CurrentStepName = "等待启动"; }

        /// <summary>任务在整机工位列表中的名称。</summary>
        public string StationName { get; }
        /// <summary>当前搬运步骤及等待原因。</summary>
        public string CurrentStepName { get; private set; }
        /// <summary>搬运已经收到两个检测任务的完成信号。</summary>
        public bool Completed => Volatile.Read(ref _completed) == 1;

        /// <summary>保存搬运 X/Z 的绝对预定位目标。</summary>
        public void SetTargets(double xTarget, double zTarget)
        { _xTarget = xTarget; _zTarget = zTarget; }

        /// <summary>先并行预定位两轴，再等待双侧检测完成并通知送料任务。</summary>
        public async Task ExecuteCurrentStepAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_step == 10)
            {
                CurrentStepName = "搬运 X/Z 同时预定位";
                using (await _axisResources.AcquireAxisGroupAsync(
                    new[] { _x.Name, _z.Name }, cancellationToken).ConfigureAwait(false))
                {
                    await Task.WhenAll(
                        _x.MoveAbsoluteAsync(_xTarget, _profile, cancellationToken),
                        _z.MoveAbsoluteAsync(_zTarget, _profile, cancellationToken)).ConfigureAwait(false);
                }
                _step = 20;
                CurrentStepName = "等待 A/B 检测完成";
                return;
            }
            if (_step == 20)
            {
                while (!_signals.BothInspectionsDone)
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                _signals.MarkTransferDone();
                Interlocked.Exchange(ref _completed, 1);
                _step = 30;
                CurrentStepName = "检测完成，搬运交接完成";
                return;
            }
            if (_step == 30) { await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false); return; }
            throw new InvalidOperationException("搬运任务的步骤编号无效：" + _step);
        }

        /// <summary>复位保留已完成的搬运预定位，不重复执行已经到位的步骤。</summary>
        public Task ResetAsync(CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    }
}
