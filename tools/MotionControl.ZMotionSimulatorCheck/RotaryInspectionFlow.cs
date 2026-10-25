using System;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.Motion;

namespace MotionControl.ZMotionSimulatorCheck
{
    /// <summary>模拟视觉/检测工位的 XY 定位、Z 轴检测和结果交接。</summary>
    internal sealed class RotaryInspectionFlow : IStationFlow
    {
        /// <summary>检测工位的 X、Y、Z 三根独立逻辑轴。</summary>
        private readonly IAxis _xAxis, _yAxis, _zAxis;
        /// <summary>整组申请检测工位三根轴，避免其他流程插入其中。</summary>
        private readonly IAxisResourceCoordinator _axisResources;
        /// <summary>记录视觉检测结果和转盘周期交接。</summary>
        private readonly RotaryAssemblySignals _signals;
        /// <summary>仿真使用的低速运动参数。</summary>
        private readonly AxisMotionProfile _profile = new AxisMotionProfile(5, 10, 10);
        /// <summary>检测工位的等待、XY 检测和 Z 安全位置。</summary>
        private double _xClear, _yClear, _zClear, _zSafe;
        /// <summary>检测工位的 XY 目标和 Z 轴检测位置。</summary>
        private double _xSnap, _ySnap, _zSnap;
        /// <summary>已经完成的周期编号；暂停时保留。</summary>
        private int _finishedCycle = -1;

        /// <summary>创建有三根轴的视觉检测工位流程。</summary>
        public RotaryInspectionFlow(IAxis xAxis, IAxis yAxis, IAxis zAxis,
            IAxisResourceCoordinator axisResources, RotaryAssemblySignals signals,
            double xClear, double yClear, double zClear, double zSafe,
            double xSnap, double ySnap, double zSnap)
        {
            _xAxis = xAxis;
            _yAxis = yAxis;
            _zAxis = zAxis;
            _axisResources = axisResources;
            _signals = signals;
            _xClear = xClear;
            _yClear = yClear;
            _zClear = zClear;
            _zSafe = zSafe;
            _xSnap = xSnap;
            _ySnap = ySnap;
            _zSnap = zSnap;
            CurrentStepName = "等待启动";
        }

        /// <summary>视觉检测工位在运行监控中的名称。</summary>
        public string StationName { get { return "视觉检测"; } }
        /// <summary>当前视觉定位、检测或等待说明。</summary>
        public string CurrentStepName { get; private set; }

        /// <summary>连接并读取轴初始位置后，设置视觉 XY、检测 Z 和安全退回目标。</summary>
        public void SetTargets(double xClear, double yClear, double zClear, double zSafe,
            double xSnap, double ySnap, double zSnap)
        {
            _xClear = xClear;
            _yClear = yClear;
            _zClear = zClear;
            _zSafe = zSafe;
            _xSnap = xSnap;
            _ySnap = ySnap;
            _zSnap = zSnap;
        }

        /// <summary>先将 Z 轴退到安全位置，再并行定位 XY，完成检测后让三轴退回。</summary>
        public async Task ExecuteCurrentStepAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int cycle = _signals.CurrentCycle;
            if (_finishedCycle >= cycle)
            {
                CurrentStepName = "视觉检测已完成，等待转盘分度";
                await _signals.WaitForNextCycleAsync(cycle, cancellationToken).ConfigureAwait(false);
                return;
            }

            bool passed;
            using (await _axisResources.AcquireAxisGroupAsync(
                new[] { _xAxis.Name, _yAxis.Name, _zAxis.Name }, cancellationToken).ConfigureAwait(false))
            {
                CurrentStepName = "检测 Z 轴先退到安全高度";
                await _zAxis.MoveAbsoluteAsync(_zSafe, _profile, cancellationToken).ConfigureAwait(false);

                CurrentStepName = "视觉 X/Y 轴同时定位";
                await Task.WhenAll(
                    _xAxis.MoveAbsoluteAsync(_xSnap, _profile, cancellationToken),
                    _yAxis.MoveAbsoluteAsync(_ySnap, _profile, cancellationToken)).ConfigureAwait(false);

                CurrentStepName = "检测 Z 轴下降到拍照位置";
                await _zAxis.MoveAbsoluteAsync(_zSnap, _profile, cancellationToken).ConfigureAwait(false);
                passed = _signals.GetInspectionResult(cycle);
                CurrentStepName = passed ? "视觉结果通过" : "视觉结果不通过";

                CurrentStepName = "检测 Z 轴退回安全高度";
                await _zAxis.MoveAbsoluteAsync(_zSafe, _profile, cancellationToken).ConfigureAwait(false);

                CurrentStepName = "视觉 X/Y 轴退回等待位置";
                await Task.WhenAll(
                    _xAxis.MoveAbsoluteAsync(_xClear, _profile, cancellationToken),
                    _yAxis.MoveAbsoluteAsync(_yClear, _profile, cancellationToken)).ConfigureAwait(false);

                CurrentStepName = "检测 Z 轴回等待高度";
                await _zAxis.MoveAbsoluteAsync(_zClear, _profile, cancellationToken).ConfigureAwait(false);
            }

            _signals.MarkInspectionReady(cycle, passed);
            _finishedCycle = cycle;
            CurrentStepName = "视觉结果已交接，等待转盘分度";
        }

        /// <summary>保留视觉检测工位步骤，使暂停后重新执行当前轮的检测流程。</summary>
        public Task ResetAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
