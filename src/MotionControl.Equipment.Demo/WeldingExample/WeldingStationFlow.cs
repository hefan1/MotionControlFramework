using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.IO;
using MotionControl.Framework.Contracts.Motion;

namespace MotionControl.Equipment.Demo.WeldingExample
{
    /// <summary>夹具焊接测试线中的一个工位，按显式步骤执行并等待下游空位。</summary>
    internal sealed class WeldingStationFlow : IStationFlow, IStationAxisRequirements, IStationCycleStatistics, IStationInputWaitState
    {
        /// <summary>测试流程使用的绝对运动曲线，单位为逻辑轴工程单位。</summary>
        private static readonly AxisMotionProfile Profile = new AxisMotionProfile(5, 50, 50);
        /// <summary>等待输入的最大时长，单位毫秒。</summary>
        private const int TimeoutMilliseconds = 30000;
        /// <summary>工位在载具线中的顺序。</summary>
        private readonly int _index;
        /// <summary>工位中文名。</summary>
        private readonly string _stationName;
        /// <summary>本工位专用逻辑轴。</summary>
        private readonly IAxis _axis;
        /// <summary>设备逻辑 IO。</summary>
        private readonly ILogicalIo _io;
        /// <summary>完成反馈输入。</summary>
        private readonly string _input;
        /// <summary>动作输出。</summary>
        private readonly string _output;
        /// <summary>工位共享的载具数据。</summary>
        private readonly WeldingCarrierLine _line;
        /// <summary>设备停机报警服务。</summary>
        private readonly IMachineAlarmService _alarms;
        /// <summary>等待输入超时报警定义。</summary>
        private readonly MachineAlarmDefinition _timeoutAlarm;
        /// <summary>保护操作界面读取的步骤和完成数。</summary>
        private readonly object _sync = new object();
        /// <summary>当前步骤，单个工位由调度器串行执行。</summary>
        private int _step = 10;
        /// <summary>当前步骤的简明中文说明。</summary>
        private string _description = "等待载具";
        /// <summary>当前等待输入的计时器；暂停时清零，恢复时重新计时。</summary>
        private readonly Stopwatch _wait = new Stopwatch();
        /// <summary>工位已完整交出的载具数。</summary>
        private long _completed;

        /// <summary>绑定工位资源；资源及软限位由设备定义在构造前校验。</summary>
        internal WeldingStationFlow(int index, string stationName, IAxis axis, ILogicalIo io, string input,
            string output, WeldingCarrierLine line, IMachineAlarmService alarms, MachineAlarmDefinition timeoutAlarm)
        {
            _index = index;
            _stationName = stationName;
            _axis = axis;
            _io = io;
            _input = input;
            _output = output;
            _line = line;
            _alarms = alarms;
            _timeoutAlarm = timeoutAlarm;
            if (index == 0) _description = "等待上料到位";
        }

        /// <summary>运行页面显示的工位名称。</summary>
        public string StationName { get { return _stationName; } }
        /// <summary>运行页面显示动作及本工位的测试载具标识。</summary>
        public string CurrentStepName
        {
            get
            {
                string description;
                lock (_sync) description = _description;
                string carrier = _line.GetCarrier(_index);
                return carrier == null ? description : description + " · " + carrier;
            }
        }
        /// <summary>当前等待的逻辑输入，不等待时为空。</summary>
        public string WaitingInputName { get { lock (_sync) return _step == 10 && _index == 0 || _step == 25 || _step == 27 || _step == 40 || _step == 60 ? _input : null; } }
        /// <summary>启动前需要回零的专用逻辑轴。</summary>
        public IReadOnlyCollection<string> RequiredHomedAxes { get { return new[] { _axis.Name }; } }
        /// <summary>当前会话已交出的载具总数。</summary>
        public long CompletedCycles { get { lock (_sync) return _completed; } }
        /// <summary>此测试示例尚不统计周期时间。</summary>
        public TimeSpan? LastCycleDuration { get { return null; } }

        /// <summary>执行当前一步；暂停取消保留步骤和载具，绝对定位恢复后重新发送原目标。</summary>
        public async Task ExecuteCurrentStepAsync(CancellationToken cancellationToken)
        {
            int step;
            lock (_sync) step = _step;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (step)
                {
                    case 10:
                        if (_index == 0)
                        {
                            if (!await WaitInputAsync(true, cancellationToken).ConfigureAwait(false)) return;
                            _line.StartProduct();
                        }
                        else if (_line.GetCarrier(_index) == null)
                        {
                            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                            return;
                        }
                        SetStep(_index == 0 ? 20 : 25, _index == 0 ? "轴到工作位" : "等待动作前反馈复位");
                        return;
                    case 20:
                        await _axis.MoveAbsoluteAsync(1, Profile, cancellationToken).ConfigureAwait(false);
                        SetStep(_index == 0 ? 30 : 27, _index == 0 ? "启动工位动作" : "动作前再次确认反馈复位");
                        return;
                    case 25:
                        // 完成信号必须先复位，防止上一周期残留或传感器卡住时误判本次动作完成。
                        if (await WaitInputAsync(false, cancellationToken).ConfigureAwait(false))
                            SetStep(20, "轴到工作位");
                        return;
                    case 27:
                        // 轴运动过程中反馈也可能异常变为有效；输出前重新核对，不能误判动作完成。
                        if (await WaitInputAsync(false, cancellationToken).ConfigureAwait(false))
                            SetStep(30, "启动工位动作");
                        return;
                    case 30:
                        await _io.WriteOutputAsync(_output, true, cancellationToken).ConfigureAwait(false);
                        SetStep(_index == 0 ? 35 : 40, _index == 0 ? "保持夹具动作" : "等待动作完成反馈");
                        return;
                    case 35:
                        await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                        SetStep(50, "关闭工位动作");
                        return;
                    case 40:
                        if (await WaitInputAsync(true, cancellationToken).ConfigureAwait(false))
                            SetStep(50, "关闭工位动作");
                        return;
                    case 50:
                        await _io.WriteOutputAsync(_output, false, cancellationToken).ConfigureAwait(false);
                        SetStep(60, _index == 0 ? "等待上料信号复位" : "等待完成反馈复位");
                        return;
                    case 60:
                        if (await WaitInputAsync(false, cancellationToken).ConfigureAwait(false)) SetStep(70, "轴返回等待位");
                        return;
                    case 70:
                        await _axis.MoveAbsoluteAsync(0, Profile, cancellationToken).ConfigureAwait(false);
                        SetStep(80, _index == 3 ? "完成复检并放行" : "等待下游空位");
                        return;
                    case 80:
                        if (!_line.TryTransfer(_index))
                        {
                            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                            return;
                        }
                        lock (_sync) _completed++;
                        SetStep(10, _index == 0 ? "等待上料到位" : "等待载具");
                        return;
                    default:
                        throw new InvalidOperationException("未知工位步骤：" + step + "。");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _wait.Reset();
                throw;
            }
        }

        /// <summary>等待逻辑输入达到期望，超时发出停机报警，未知或读失败不按无效输入放行。</summary>
        private async Task<bool> WaitInputAsync(bool expected, CancellationToken token)
        {
            if (!_wait.IsRunning) _wait.Restart();
            if (await _io.ReadInputAsync(_input, token).ConfigureAwait(false) == expected)
            {
                _wait.Reset();
                return true;
            }
            if (_wait.ElapsedMilliseconds >= TimeoutMilliseconds)
            {
                string detail = "等待“" + _input + "”" + (expected ? "有效" : "复位") + "超时，请检查工位和输出。";
                _alarms.ReportCondition(_timeoutAlarm, StationName, true, detail);
                _alarms.ReportCondition(_timeoutAlarm, StationName, false, detail);
                _wait.Reset();
                token.ThrowIfCancellationRequested();
            }
            await Task.Delay(100, token).ConfigureAwait(false);
            return false;
        }

        /// <summary>原子更新当前动作说明。</summary>
        private void SetStep(int step, string description)
        {
            lock (_sync) { _step = step; _description = description; }
        }

        /// <summary>保留当前载具和步骤；现场确认后由整机复位和启动续跑。</summary>
        public Task ResetAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _wait.Reset();
            return Task.CompletedTask;
        }
    }
}
