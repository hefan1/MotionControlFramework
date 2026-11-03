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
        /// <summary>复检下料工位的独立下料轴；其他工位为空。</summary>
        private readonly IAxis _unloadAxis;
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
        /// <summary>相机完成或结果等待超时；报警复位后需安全复位相机再重触发。</summary>
        private bool _cameraNeedsRetry;
        /// <summary>相机结果上一轮的唯一候选值。</summary>
        private string _cameraCandidate;
        /// <summary>当前相机结果连续相同的采样次数。</summary>
        private int _cameraCandidateSamples;
        /// <summary>夹具当前等待的目标状态，用于区分夹紧和松开采样。</summary>
        private bool? _clampExpected;
        /// <summary>夹具到位与输出一致的连续采样次数。</summary>
        private int _clampSamples;

        /// <summary>绑定工位资源；资源及软限位由设备定义在构造前校验。</summary>
        internal WeldingStationFlow(int index, string stationName, IAxis axis, ILogicalIo io, string input,
            string output, WeldingCarrierLine line, IMachineAlarmService alarms, MachineAlarmDefinition timeoutAlarm, IAxis unloadAxis)
        {
            _index = index;
            _stationName = stationName;
            _axis = axis;
            _unloadAxis = unloadAxis;
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
                string text = carrier == null ? description : description + " · " + carrier;
                string lastResult = _index == 3 ? _line.GetLastInspectionResult() : null;
                return lastResult == null || (carrier != null && carrier.Contains(" · 检测"))
                    ? text : text + " · 上次检测 " + lastResult;
            }
        }
        /// <summary>当前等待的逻辑输入，不等待时为空。</summary>
        public string WaitingInputName
        {
            get
            {
                lock (_sync)
                {
                    if (_index == 0 && (_step == 10 || _step == 20 || _step == 60)) return "上料到位";
                    if (_step == 12 || _step == 55 || (_step == 30 && _index == 0)) return "夹具松开到位";
                    if (_step == 35) return "夹具夹紧到位";
                    if (_step == 25 || _step == 27 || _step == 40 || (_step == 60 && _index != 0)) return _input;
                    if (_step == 42) return "相机触发反馈";
                    if (_step == 43 || _step == 46 || _step == 91) return "检测完成";
                    if (_step == 72 || _step == 74 || (_step == 70 && _index == 3)) return "下料到位";
                    return null;
                }
            }
        }
        /// <summary>启动前需要回零的专用逻辑轴。</summary>
        public IReadOnlyCollection<string> RequiredHomedAxes { get { return _unloadAxis == null ? new[] { _axis.Name } : new[] { _axis.Name, _unloadAxis.Name }; } }
        /// <summary>当前会话已交出的载具总数。</summary>
        public long CompletedCycles { get { lock (_sync) return _completed; } }
        /// <summary>此测试示例尚不统计周期时间。</summary>
        public TimeSpan? LastCycleDuration { get { return null; } }
        /// <summary>供设备准备工厂核对同一设备实例的工位顺序。</summary>
        internal int StationIndex { get { return _index; } }
        /// <summary>与本工位共用的在制品状态；准备操作只能检查，不能清空。</summary>
        internal WeldingCarrierLine CarrierLine { get { return _line; } }

        /// <summary>执行当前一步；暂停取消保留步骤和载具，绝对定位恢复后重新发送原目标。</summary>
        public async Task ExecuteCurrentStepAsync(CancellationToken cancellationToken)
        {
            int step;
            lock (_sync) step = _step;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                _line.MarkProductionStarted();
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
                        SetStep(_index == 0 ? 12 : 25, _index == 0 ? "确认夹具松开到位" : "等待动作前反馈复位");
                        return;
                    case 12:
                        if (await WaitClampAsync(false, cancellationToken).ConfigureAwait(false)) SetStep(20, "轴到工作位");
                        return;
                    case 20:
                        if (_index == 0 && !await WaitLoadMotionReadyAsync(cancellationToken).ConfigureAwait(false)) return;
                        await _axis.MoveAbsoluteAsync(1, Profile, cancellationToken).ConfigureAwait(false);
                        SetStep(_index == 0 ? 30 : 27, _index == 0 ? "启动工位动作" : "动作前再次确认反馈复位");
                        return;
                    case 25:
                        // 完成信号必须先复位，防止上一周期残留或传感器卡住时误判本次动作完成。
                        if (await WaitInputAsync(false, cancellationToken).ConfigureAwait(false))
                            SetStep(_index == 3 ? 26 : 20, _index == 3 ? "确认相机结果已复位" : "轴到工作位");
                        return;
                    case 26:
                        if (await WaitCameraClearAsync(cancellationToken).ConfigureAwait(false)) SetStep(20, "轴到工作位");
                        return;
                    case 27:
                        // 轴运动过程中反馈也可能异常变为有效；输出前重新核对，不能误判动作完成。
                        if (await WaitInputAsync(false, cancellationToken).ConfigureAwait(false))
                            SetStep(_index == 3 ? 28 : 30, _index == 3 ? "拍照前再次确认结果复位" : "启动工位动作");
                        return;
                    case 28:
                        if (await WaitCameraClearAsync(cancellationToken).ConfigureAwait(false)) SetStep(30, "启动工位动作");
                        return;
                    case 30:
                        // 动作前重新检查刚才的反馈；此前检查不能当作当前反馈。
                        if (_index == 0 && !await WaitClampAsync(false, cancellationToken).ConfigureAwait(false)) return;
                        if ((_index == 1 || _index == 2) && !await WaitInputAsync(false, cancellationToken).ConfigureAwait(false)) return;
                        if (_index == 3 && !await WaitCameraClearAsync(cancellationToken).ConfigureAwait(false)) return;
                        await _io.WriteOutputAsync(_output, true, cancellationToken).ConfigureAwait(false);
                        SetStep(_index == 0 ? 35 : 40, _index == 0 ? "等待夹具夹紧到位" : "等待动作完成反馈");
                        return;
                    case 35:
                        if (await WaitClampAsync(true, cancellationToken).ConfigureAwait(false)) SetStep(50, "释放夹具");
                        return;
                    case 40:
                        if (await WaitInputAsync(true, cancellationToken).ConfigureAwait(false))
                            SetStep(_index == 3 ? 41 : 50, _index == 3 ? "撤销相机触发" : "关闭工位动作");
                        return;
                    case 41:
                        await _io.WriteOutputAsync("相机触发", false, cancellationToken).ConfigureAwait(false);
                        SetStep(42, "等待相机触发反馈复位");
                        return;
                    case 42:
                        if (await WaitNamedInputAsync("相机触发反馈", false, cancellationToken).ConfigureAwait(false)) SetStep(43, "等待检测完成");
                        return;
                    case 43:
                        if (await WaitNamedInputAsync("检测完成", true, cancellationToken).ConfigureAwait(false)) SetStep(44, "读取检测结果");
                        return;
                    case 44:
                        if (await ReadCameraResultAsync(cancellationToken).ConfigureAwait(false)) SetStep(45, "请求相机结果复位");
                        return;
                    case 45:
                        await _io.WriteOutputAsync("相机结果复位", true, cancellationToken).ConfigureAwait(false);
                        SetStep(46, "等待检测完成复位");
                        return;
                    case 46:
                        if (await WaitNamedInputAsync("检测完成", false, cancellationToken).ConfigureAwait(false)) SetStep(47, "等待检测结果复位");
                        return;
                    case 47:
                        if (await WaitCameraResultsClearAsync(cancellationToken).ConfigureAwait(false)) SetStep(48, "撤销相机结果复位");
                        return;
                    case 48:
                        await _io.WriteOutputAsync("相机结果复位", false, cancellationToken).ConfigureAwait(false);
                        SetStep(49, "相机轴返回等待位");
                        return;
                    case 49:
                        await _axis.MoveAbsoluteAsync(0, Profile, cancellationToken).ConfigureAwait(false);
                        SetStep(70, "下料轴到工作位");
                        return;
                    case 50:
                        await _io.WriteOutputAsync(_output, false, cancellationToken).ConfigureAwait(false);
                        SetStep(_index == 0 ? 55 : 60, _index == 0 ? "等待夹具松开到位" : "等待完成反馈复位");
                        return;
                    case 55:
                        if (await WaitClampAsync(false, cancellationToken).ConfigureAwait(false)) SetStep(60, "等待上料信号复位");
                        return;
                    case 60:
                        if (await WaitInputAsync(false, cancellationToken).ConfigureAwait(false)) SetStep(70, "轴返回等待位");
                        return;
                    case 70:
                        if (_index == 0 && !await WaitLoadReturnReadyAsync(cancellationToken).ConfigureAwait(false)) return;
                        if (_index == 3)
                        {
                            if (!await WaitNamedInputAsync("下料到位", false, cancellationToken).ConfigureAwait(false)) return;
                            if (await _io.ReadOutputAsync("下料电磁阀", cancellationToken).ConfigureAwait(false))
                                throw new InvalidOperationException("下料输出仍有效，不能移动下料轴。");
                        }
                        await (_index == 3 ? _unloadAxis.MoveAbsoluteAsync(1, Profile, cancellationToken)
                            : _axis.MoveAbsoluteAsync(0, Profile, cancellationToken)).ConfigureAwait(false);
                        SetStep(_index == 3 ? 71 : 80, _index == 3 ? "启动下料" : "等待下游空位");
                        return;
                    case 71:
                        if (!await WaitNamedInputAsync("下料到位", false, cancellationToken).ConfigureAwait(false)) return;
                        await _io.WriteOutputAsync("下料电磁阀", true, cancellationToken).ConfigureAwait(false);
                        SetStep(72, "等待下料到位");
                        return;
                    case 72:
                        if (await WaitNamedInputAsync("下料到位", true, cancellationToken).ConfigureAwait(false)) SetStep(73, "关闭下料输出");
                        return;
                    case 73:
                        await _io.WriteOutputAsync("下料电磁阀", false, cancellationToken).ConfigureAwait(false);
                        SetStep(74, "等待下料反馈复位");
                        return;
                    case 74:
                        if (await WaitNamedInputAsync("下料到位", false, cancellationToken).ConfigureAwait(false)) SetStep(75, "下料轴返回等待位");
                        return;
                    case 75:
                        await _unloadAxis.MoveAbsoluteAsync(0, Profile, cancellationToken).ConfigureAwait(false);
                        SetStep(80, "完成复检并放行");
                        return;
                    case 90:
                        if (await _io.ReadOutputAsync("相机触发", cancellationToken).ConfigureAwait(false)
                            || await _io.ReadOutputAsync("相机结果复位", cancellationToken).ConfigureAwait(false))
                            throw new InvalidOperationException("相机触发或复位输出仍有效，不能自动重试，请先检查。");
                        await _io.WriteOutputAsync("相机结果复位", true, cancellationToken).ConfigureAwait(false);
                        SetStep(91, "等待旧检测完成复位");
                        return;
                    case 91:
                        if (await WaitNamedInputAsync("检测完成", false, cancellationToken).ConfigureAwait(false)) SetStep(92, "等待旧检测结果复位");
                        return;
                    case 92:
                        if (await WaitCameraResultsClearAsync(cancellationToken).ConfigureAwait(false)) SetStep(93, "撤销旧结果复位");
                        return;
                    case 93:
                        await _io.WriteOutputAsync("相机结果复位", false, cancellationToken).ConfigureAwait(false);
                        SetStep(28, "重试拍照前确认相机已复位");
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
                _cameraCandidate = null;
                _cameraCandidateSamples = 0;
                _clampExpected = null;
                _clampSamples = 0;
                throw;
            }
        }

        /// <summary>等待逻辑输入达到期望，超时发出停机报警，未知或读失败不按无效输入放行。</summary>
        private async Task<bool> WaitInputAsync(bool expected, CancellationToken token)
        {
            return await WaitNamedInputAsync(_input, expected, token).ConfigureAwait(false);
        }

        /// <summary>按逻辑名称等待输入，设备读取异常不会当作无效输入。</summary>
        private async Task<bool> WaitNamedInputAsync(string name, bool expected, CancellationToken token)
        {
            bool value = await _io.ReadInputAsync(name, token).ConfigureAwait(false);
            return await WaitStateAsync(value == expected, name + (expected ? "有效" : "复位"), token).ConfigureAwait(false);
        }

        /// <summary>夹具目标输出与到位连续两次一致后才放行；两路同时有效时立即拒绝。</summary>
        private async Task<bool> WaitClampAsync(bool clamped, CancellationToken token)
        {
            bool stable = await SampleClampAsync(clamped, token).ConfigureAwait(false);
            return await WaitStateAsync(stable, clamped ? "夹具夹紧到位" : "夹具松开到位", token).ConfigureAwait(false);
        }

        /// <summary>上料运动前同时核对物料和夹具稳定松开，等待期间共用同一超时。</summary>
        private async Task<bool> WaitLoadMotionReadyAsync(CancellationToken token)
        {
            bool material = await _io.ReadInputAsync("上料到位", token).ConfigureAwait(false);
            bool released = await SampleClampAsync(false, token).ConfigureAwait(false);
            return await WaitStateAsync(material && released, "上料到位且夹具稳定松开", token).ConfigureAwait(false);
        }

        /// <summary>上料轴返回前再次核对无料且夹具稳定松开；等待期间共用同一超时。</summary>
        private async Task<bool> WaitLoadReturnReadyAsync(CancellationToken token)
        {
            bool material = await _io.ReadInputAsync("上料到位", token).ConfigureAwait(false);
            bool released = await SampleClampAsync(false, token).ConfigureAwait(false);
            return await WaitStateAsync(!material && released, "上料信号复位且夹具稳定松开", token).ConfigureAwait(false);
        }

        /// <summary>检查夹具输出和两路到位；输出不符或相互冲突时拒绝，未到位时重新累计。</summary>
        private async Task<bool> SampleClampAsync(bool clamped, CancellationToken token)
        {
            try
            {
                bool output = await _io.ReadOutputAsync("夹具夹紧", token).ConfigureAwait(false);
                bool close = await _io.ReadInputAsync("夹具夹紧到位", token).ConfigureAwait(false);
                bool open = await _io.ReadInputAsync("夹具松开到位", token).ConfigureAwait(false);
                bool outputAfter = await _io.ReadOutputAsync("夹具夹紧", token).ConfigureAwait(false);
                if (output != clamped || outputAfter != clamped)
                    throw new InvalidOperationException("夹具输出状态与当前动作不一致，请检查输出及现场。");
                if (close && open) throw new InvalidOperationException("夹具夹紧到位与松开到位同时有效，请检查传感器。");
                bool matches = clamped ? close && !open : open && !close;
                if (_clampExpected != clamped || !matches) { _clampExpected = clamped; _clampSamples = 0; }
                if (matches) _clampSamples++;
                return _clampSamples >= 2;
            }
            catch
            {
                _clampExpected = null;
                _clampSamples = 0;
                throw;
            }
        }

        /// <summary>拍照前检查触发、完成、结果和复位输出，避免把上次结果当成本次结果。</summary>
        private async Task<bool> WaitCameraClearAsync(CancellationToken token)
        {
            bool ack = await _io.ReadInputAsync("相机触发反馈", token).ConfigureAwait(false);
            bool done = await _io.ReadInputAsync("检测完成", token).ConfigureAwait(false);
            bool ok = await _io.ReadInputAsync("检测OK", token).ConfigureAwait(false);
            bool ng = await _io.ReadInputAsync("检测NG", token).ConfigureAwait(false);
            bool trigger = await _io.ReadOutputAsync("相机触发", token).ConfigureAwait(false);
            bool reset = await _io.ReadOutputAsync("相机结果复位", token).ConfigureAwait(false);
            if (ok && ng) throw new InvalidOperationException("检测OK与检测NG同时有效，请检查相机结果信号。");
            return await WaitStateAsync(!ack && !done && !ok && !ng && !trigger && !reset, "相机触发及上次检测结果复位", token).ConfigureAwait(false);
        }

        /// <summary>相机完成保持有效且两次采样结果一致时才接受唯一结果；无结果等待，结果冲突立即拒绝。</summary>
        private async Task<bool> ReadCameraResultAsync(CancellationToken token)
        {
            if (!await _io.ReadInputAsync("检测完成", token).ConfigureAwait(false))
                throw new InvalidOperationException("相机完成信号在读取结果前已复位，无法确认结果。");
            bool ok = await _io.ReadInputAsync("检测OK", token).ConfigureAwait(false);
            bool ng = await _io.ReadInputAsync("检测NG", token).ConfigureAwait(false);
            if (!await _io.ReadInputAsync("检测完成", token).ConfigureAwait(false))
                throw new InvalidOperationException("相机完成信号在读取结果期间已复位，无法确认结果。");
            if (ok && ng) throw new InvalidOperationException("检测OK与检测NG同时有效，不能放行载具。");
            string candidate = ok ? "OK" : ng ? "NG" : null;
            if (candidate == null) { _cameraCandidate = null; _cameraCandidateSamples = 0; }
            else if (candidate == _cameraCandidate) _cameraCandidateSamples++;
            else { _cameraCandidate = candidate; _cameraCandidateSamples = 1; }
            if (!await WaitStateAsync(_cameraCandidateSamples >= 2, "相机稳定检测结果", token).ConfigureAwait(false)) return false;
            _line.SetInspectionResult(candidate);
            return true;
        }

        /// <summary>复位握手后等待两个结果位均无效。</summary>
        private async Task<bool> WaitCameraResultsClearAsync(CancellationToken token)
        {
            bool ok = await _io.ReadInputAsync("检测OK", token).ConfigureAwait(false);
            bool ng = await _io.ReadInputAsync("检测NG", token).ConfigureAwait(false);
            if (ok && ng) throw new InvalidOperationException("检测OK与检测NG同时有效，请检查相机结果信号。");
            return await WaitStateAsync(!ok && !ng, "相机检测结果复位", token).ConfigureAwait(false);
        }

        /// <summary>通用等待计时只保留当前步骤，超时上报停机报警并保留步骤。</summary>
        private async Task<bool> WaitStateAsync(bool matches, string detailName, CancellationToken token)
        {
            if (!_wait.IsRunning) _wait.Restart();
            if (matches)
            {
                _wait.Reset();
                return true;
            }
            if (_wait.ElapsedMilliseconds >= TimeoutMilliseconds)
            {
                lock (_sync)
                {
                    if (_index == 3 && (_step == 43 || _step == 44)) _cameraNeedsRetry = true;
                }
                string detail = "等待“" + detailName + "”超时，请检查工位和输出。";
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
            lock (_sync)
            {
                _step = step; _description = description;
                _clampExpected = null; _clampSamples = 0;
                if (step == 44) { _cameraCandidate = null; _cameraCandidateSamples = 0; }
            }
        }

        /// <summary>保留当前载具和步骤；现场确认后由整机复位和启动续跑。</summary>
        public Task ResetAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _wait.Reset();
            lock (_sync)
            {
                _clampExpected = null;
                _clampSamples = 0;
                _cameraCandidate = null;
                _cameraCandidateSamples = 0;
                if (_cameraNeedsRetry && (_step == 43 || _step == 44))
                {
                    _step = 90;
                    _description = "复位相机并重新检测";
                    _cameraNeedsRetry = false;
                }
            }
            return Task.CompletedTask;
        }
    }
}
