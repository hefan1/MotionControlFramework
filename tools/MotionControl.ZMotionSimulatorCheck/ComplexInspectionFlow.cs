using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.Motion;

namespace MotionControl.ZMotionSimulatorCheck
{
    internal sealed class ComplexInspectionFlow : IStationFlow
    {
        /// <summary>本侧两根工具轴及共用工作 Z 轴。</summary>
        private readonly IAxis _toolOne, _toolTwo, _sharedZ;
        /// <summary>一次申请完整三轴组，直到动作停稳才释放。</summary>
        private readonly IAxisResourceCoordinator _axisResources;
        /// <summary>读取定位交接信号并记录共用轴占用。</summary>
        private readonly ComplexMachineSignals _signals;
        /// <summary>区分 A、B 两侧的检测结果。</summary>
        private readonly bool _stationA;
        /// <summary>检测轴组的仿真运动参数。</summary>
        private readonly AxisMotionProfile _profile = new AxisMotionProfile(5, 10, 10);
        /// <summary>10 为等待与检测，20 为完成待机。</summary>
        private int _step = 10;
        /// <summary>工具轴及共用 Z 轴的绝对目标位置。</summary>
        private double _toolOneTarget, _toolTwoTarget, _sharedZTarget;
        /// <summary>三轴全部到位后才为真。</summary>
        private int _completed;

        /// <summary>建立一个等待定位结果并使用共用 Z 轴的检测任务。</summary>
        public ComplexInspectionFlow(string name, IAxis toolOne, IAxis toolTwo, IAxis sharedZ,
            IAxisResourceCoordinator axisResources, ComplexMachineSignals signals, bool stationA)
        {
            StationName = name;
            _toolOne = toolOne; _toolTwo = toolTwo; _sharedZ = sharedZ;
            _axisResources = axisResources; _signals = signals; _stationA = stationA;
            CurrentStepName = "等待两侧定位完成";
        }

        /// <summary>任务在整机工位列表中的名称。</summary>
        public string StationName { get; }
        /// <summary>当前的定位交接或轴组动作说明。</summary>
        public string CurrentStepName { get; private set; }
        /// <summary>本侧检测的三根轴是否均到位。</summary>
        public bool Completed => Volatile.Read(ref _completed) == 1;
        /// <summary>等待共用 Z 轴占用凭证花费的毫秒数。</summary>
        public long ResourceWaitMilliseconds { get; private set; }

        /// <summary>保存三个轴的绝对目标位置。</summary>
        public void SetTargets(double toolOne, double toolTwo, double sharedZ)
        { _toolOneTarget = toolOne; _toolTwoTarget = toolTwo; _sharedZTarget = sharedZ; }

        /// <summary>先等两侧定位，再申请完整轴组，防止两个检测任务同时驱动共用 Z。</summary>
        public async Task ExecuteCurrentStepAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_step == 20) { await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false); return; }
            if (_step != 10) throw new InvalidOperationException(StationName + " 的步骤编号无效：" + _step);

            CurrentStepName = "等待 A/B 定位交接";
            while (!_signals.BothPositionsReady)
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);

            CurrentStepName = "申请两根工具轴和共用 Z 轴";
            Stopwatch wait = Stopwatch.StartNew();
            using (await _axisResources.AcquireAxisGroupAsync(
                new[] { _toolOne.Name, _toolTwo.Name, _sharedZ.Name }, cancellationToken).ConfigureAwait(false))
            {
                ResourceWaitMilliseconds = wait.ElapsedMilliseconds;
                _signals.EnterSharedAxis();
                try
                {
                    CurrentStepName = "三轴同时动作，等待全部到位";
                    await Task.WhenAll(
                        _toolOne.MoveAbsoluteAsync(_toolOneTarget, _profile, cancellationToken),
                        _toolTwo.MoveAbsoluteAsync(_toolTwoTarget, _profile, cancellationToken),
                        _sharedZ.MoveAbsoluteAsync(_sharedZTarget, _profile, cancellationToken)).ConfigureAwait(false);
                    _signals.MarkInspectionDone(_stationA, _sharedZTarget);
                }
                finally { _signals.LeaveSharedAxis(); }
            }
            Interlocked.Exchange(ref _completed, 1);
            _step = 20;
            CurrentStepName = "检测轴组到位，已交接搬运任务";
        }

        /// <summary>复位保留当前检测步骤；被取消的轴组重新申请资源后定位到原目标。</summary>
        public Task ResetAsync(CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    }
}
