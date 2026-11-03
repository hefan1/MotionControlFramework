using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Alarms;
using MotionControl.Framework.Contracts.Flow;
using MotionControl.Framework.Contracts.IO;
using MotionControl.Framework.Contracts.Motion;

namespace MotionControl.Equipment.Demo.RotaryWelding
{
    /// <summary>双夹位设备中的独立工位；取消保留步骤、载具与区域占用。</summary>
    internal sealed partial class RotaryStationFlow : IStationFlow, IStationAxisRequirements, IStationCycleStatistics, IStationInputWaitState
    {
        /// <summary>直线测试轴运动参数，单位 mm/s；仅供软件模型测试。</summary>
        private static readonly AxisMotionProfile LinearProfile = new AxisMotionProfile(5, 50, 50);
        /// <summary>转轴测试速度，单位度/s；不代表现场参数。</summary>
        private static readonly AxisMotionProfile RotaryProfile = new AxisMotionProfile(90, 180, 180);
        /// <summary>保护工位步骤、产品和统计数据。</summary>
        private readonly object _sync = new object();
        /// <summary>设备中已经核验的逻辑轴。</summary>
        private readonly IDictionary<string, IAxis> _axes;
        /// <summary>逻辑 IO 与本机载具状态。</summary>
        private readonly ILogicalIo _io;
        /// <summary>载具与转台的会话状态。</summary>
        private readonly RotaryCarrierLine _line;
        /// <summary>等待超时上报的报警服务与报警定义。</summary>
        private readonly IMachineAlarmService _alarms;
        /// <summary>设备配置中预设的停机报警。</summary>
        private readonly MachineAlarmDefinition _alarm;
        /// <summary>当前步骤等待计时。</summary>
        private readonly Stopwatch _timer = new Stopwatch();
        /// <summary>工位当前动作编号；成功后才推进。</summary>
        private int _step;
        /// <summary>界面显示的当前动作。</summary>
        private string _description = "等待工位条件";
        /// <summary>当前等待输入；不等待时为空。</summary>
        private string _waitingInput;
        /// <summary>本次操作锁定的夹位。</summary>
        private int? _nest;
        /// <summary>扫码返回的产品编号或当前缓存产品。</summary>
        private string _product;
        /// <summary>缓存载具实际检测结果。</summary>
        private string _result;
        /// <summary>扫码异常后需要先撤销旧触发再读取同一件产品。</summary>
        private bool _scannerNeedsRetry;
        /// <summary>相机结果超时后需要清除旧结果，再检测当前载具。</summary>
        private bool _cameraNeedsRetry;
        /// <summary>已发出但返回结果未知的本会话输出命令；不可盲目重复触发。</summary>
        private string _pendingOutput;
        /// <summary>在途输出命令的期望值。</summary>
        private bool _pendingOutputValue;
        /// <summary>工位完成的完整产品数。</summary>
        private long _completed;

        /// <summary>绑定工位名、轴、IO 与同一设备的载具线。</summary>
        internal RotaryStationFlow(string name, IDictionary<string, IAxis> axes, ILogicalIo io, RotaryCarrierLine line,
            IMachineAlarmService alarms, MachineAlarmDefinition alarm)
        {
            StationName = name; _axes = axes; _io = io; _line = line; _alarms = alarms; _alarm = alarm;
        }

        /// <summary>操作员看到的当前工位名称。</summary>
        public string StationName { get; private set; }
        /// <summary>当前动作及已保留夹位、产品和检测结果。</summary>
        public string CurrentStepName
        {
            get
            {
                lock (_sync)
                {
                    string carrier = _line.DescribeNest(_nest);
                    return _description + (carrier == null ? "" : " · " + carrier)
                        + (_product == null ? "" : " · " + _product) + (_result == null ? "" : " · 检测" + _result);
                }
            }
        }
        /// <summary>提供操作员当前等待的逻辑输入。</summary>
        public string WaitingInputName { get { lock (_sync) return _waitingInput; } }
        /// <summary>整机启动前要求十三根样例轴均建立参考。</summary>
        public IReadOnlyCollection<string> RequiredHomedAxes { get { return RotaryWeldingEquipmentDefinition.AxisNames; } }
        /// <summary>当前工位本会话完成数。</summary>
        public long CompletedCycles { get { lock (_sync) return _completed; } }
        /// <summary>样例暂未统计节拍。</summary>
        public TimeSpan? LastCycleDuration { get { return null; } }
        /// <summary>设备准备使用与生产相同的载具状态。</summary>
        internal RotaryCarrierLine Line { get { return _line; } }

        /// <summary>执行当前工位的一步；成功前不丢失锁定的夹位和目标。</summary>
        public async Task ExecuteCurrentStepAsync(CancellationToken token)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                _line.EnsureRunningAllowed();
                switch (StationName)
                {
                    case "上料工位": await ExecuteLoadAsync(token).ConfigureAwait(false); break;
                    case "焊接工位": await ExecuteWeldAsync(token).ConfigureAwait(false); break;
                    case "压合工位": await ExecutePressAsync(token).ConfigureAwait(false); break;
                    case "旋转与下料": await ExecuteTransferAsync(token).ConfigureAwait(false); break;
                    case "复检分拣": await ExecuteInspectionAsync(token).ConfigureAwait(false); break;
                    default: throw new InvalidOperationException("未知双夹位工位：" + StationName);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { _timer.Reset(); throw; }
        }

        /// <summary>复位等待计时，保留当前步骤、产品和共享区状态。</summary>
        public Task ResetAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            _timer.Reset();
            lock (_sync)
            {
                if (StationName == "上料工位" && _scannerNeedsRetry)
                { _step = 80; _description = "复位扫码并重新读取"; _scannerNeedsRetry = false; }
                if (StationName == "复检分拣" && _cameraNeedsRetry)
                { _step = 80; _description = "复位相机并重新检测"; _cameraNeedsRetry = false; }
            }
            return Task.CompletedTask;
        }

        /// <summary>更新界面动作并重置当前等待计时。</summary>
        private void Next(int step, string description)
        {
            _line.RecordProgress(StationName, description);
            _timer.Reset();
            lock (_sync) { _step = step; _description = description; _waitingInput = null; }
        }

        /// <summary>设备条件未满足时保留步骤；超时定位工位并报告停机报警。</summary>
        private async Task<bool> WaitAsync(bool satisfied, string detail, CancellationToken token)
        {
            if (satisfied) { _timer.Reset(); lock (_sync) _waitingInput = null; return true; }
            lock (_sync) _waitingInput = detail;
            if (!_timer.IsRunning) _timer.Start();
            if (_timer.ElapsedMilliseconds >= 30000)
            {
                if (StationName == "上料工位" && (_step == 20 || _step == 21)) _scannerNeedsRetry = true;
                if (StationName == "复检分拣" && (_step == 19 || _step == 20)) _cameraNeedsRetry = true;
                string message = "等待“" + detail + "”超时，请核对当前工位及载具。";
                _alarms.ReportCondition(_alarm, StationName, true, message);
                _alarms.ReportCondition(_alarm, StationName, false, message);
                _timer.Reset();
                token.ThrowIfCancellationRequested();
            }
            await Task.Delay(50, token).ConfigureAwait(false);
            return false;
        }

        /// <summary>读取逻辑输入；读取失败不会按无效信号放行。</summary>
        private async Task<bool> WaitInputAsync(string name, bool expected, CancellationToken token)
        {
            return await WaitAsync(await _io.ReadInputAsync(name, token).ConfigureAwait(false) == expected,
                name + (expected ? "有效" : "复位"), token).ConfigureAwait(false);
        }

        /// <summary>核对夹具输出与互斥双到位，反馈冲突立即拒绝。</summary>
        private async Task<bool> WaitClampAsync(string prefix, bool closed, CancellationToken token)
        {
            return await WaitAsync(await SampleClampAsync(prefix, closed, token).ConfigureAwait(false),
                prefix + (closed ? "夹紧到位" : "松开到位"), token).ConfigureAwait(false);
        }

        /// <summary>采样两次并确认输出与双到位一致；只返回结果，不重置等待计时。</summary>
        private async Task<bool> SampleClampAsync(string prefix, bool closed, CancellationToken token)
        {
            bool output = await _io.ReadOutputAsync(prefix + "夹紧", token).ConfigureAwait(false);
            bool close = await _io.ReadInputAsync(prefix + "夹紧到位", token).ConfigureAwait(false);
            bool open = await _io.ReadInputAsync(prefix + "松开到位", token).ConfigureAwait(false);
            bool closeAgain = await _io.ReadInputAsync(prefix + "夹紧到位", token).ConfigureAwait(false);
            bool openAgain = await _io.ReadInputAsync(prefix + "松开到位", token).ConfigureAwait(false);
            bool outputAgain = await _io.ReadOutputAsync(prefix + "夹紧", token).ConfigureAwait(false);
            if ((close && open) || (closeAgain && openAgain)) throw new InvalidOperationException(prefix + "夹具夹紧与松开反馈同时有效。");
            if (output != closed || outputAgain != closed) throw new InvalidOperationException(prefix + "夹具输出与预期不一致。");
            bool stable = close == closeAgain && open == openAgain && (closed ? close && !open : open && !close);
            return stable;
        }

        /// <summary>完成信号保持有效期间读取独立合格结果，避免把旧结果当作本次合格。</summary>
        private async Task<bool> WaitQualifiedAsync(string completion, string qualified, string output, string clamp, CancellationToken token)
        {
            bool clamped = await SampleClampAsync(clamp, true, token).ConfigureAwait(false);
            bool before = await _io.ReadInputAsync(completion, token).ConfigureAwait(false);
            bool result = await _io.ReadInputAsync(qualified, token).ConfigureAwait(false);
            bool after = await _io.ReadInputAsync(completion, token).ConfigureAwait(false);
            if (!before || !after || !await _io.ReadOutputAsync(output, token).ConfigureAwait(false))
                throw new InvalidOperationException(completion + "或动作输出在读取结果期间失效，不能确认加工结果。");
            return await WaitAsync(clamped && result, clamp + "夹紧及" + qualified, token).ConfigureAwait(false);
        }

        /// <summary>输出前一次等待夹具与上一周期反馈同时满足，防止分开计时掩盖异常。</summary>
        private async Task<bool> WaitProcessStartAsync(string clamp, string completion, string qualified, string output, CancellationToken token)
        {
            bool clamped = await SampleClampAsync(clamp, true, token).ConfigureAwait(false);
            bool done = await _io.ReadInputAsync(completion, token).ConfigureAwait(false);
            bool result = await _io.ReadInputAsync(qualified, token).ConfigureAwait(false);
            if (await _io.ReadOutputAsync(output, token).ConfigureAwait(false))
                throw new InvalidOperationException(output + "已有效，动作是否已执行未知，请人工核查。");
            return await WaitAsync(clamped && !done && !result, clamp + "夹紧且加工反馈已复位", token).ConfigureAwait(false);
        }

        /// <summary>同一等待期间同时核对夹具及加工完成反馈。</summary>
        private async Task<bool> WaitProcessCompleteAsync(string clamp, string completion, CancellationToken token)
        {
            bool clamped = await SampleClampAsync(clamp, true, token).ConfigureAwait(false);
            bool done = await _io.ReadInputAsync(completion, token).ConfigureAwait(false);
            return await WaitAsync(clamped && done, clamp + "夹紧及" + completion, token).ConfigureAwait(false);
        }

        /// <summary>旋转前同时确认空夹位松开及已焊夹位夹紧。</summary>
        private async Task<bool> WaitRotationClampsAsync(int nextNest, CancellationToken token)
        {
            string empty = nextNest == 0 ? "A" : "B";
            string full = nextNest == 0 ? "B" : "A";
            bool emptyOpen = await SampleClampAsync(empty, false, token).ConfigureAwait(false);
            bool fullClosed = await SampleClampAsync(full, true, token).ConfigureAwait(false);
            return await WaitAsync(emptyOpen && fullClosed, "空夹位松开且已焊夹位夹紧", token).ConfigureAwait(false);
        }

        /// <summary>同会话续跑前检查执行中的加工轴仍停在预期工作位置。</summary>
        private async Task EnsureAtAsync(string name, double expected, CancellationToken token)
        {
            AxisStatus state = await _axes[name].ReadStatusAsync(token).ConfigureAwait(false);
            if (!state.ServoEnabled || !state.Homed || state.Moving || state.AlarmActive || state.RequiresHomingAfterLimitAlarm
                || state.PositiveLimitActive != false || state.NegativeLimitActive != false || Math.Abs(state.Position - expected) > 0.01)
                throw new InvalidOperationException(name + " 未停在当前步骤要求的位置，禁止继续。");
        }

        /// <summary>移动前核对指定让位轴已回零、停止且处于零位。</summary>
        private async Task EnsureSafeAsync(CancellationToken token, params string[] names)
        {
            foreach (string name in names)
            {
                AxisStatus state = await _axes[name].ReadStatusAsync(token).ConfigureAwait(false);
                if (!state.ServoEnabled || !state.Homed || state.Moving || state.AlarmActive || state.RequiresHomingAfterLimitAlarm
                    || state.PositiveLimitActive != false || state.NegativeLimitActive != false || Math.Abs(state.Position) > 0.01)
                    throw new InvalidOperationException(name + " 未在确认的零位让位状态，禁止当前动作。");
            }
        }

        /// <summary>使用逻辑轴执行单轴绝对定位，取消由轴适配器完成受控停止。</summary>
        private Task MoveAsync(string name, double target, CancellationToken token)
        {
            _line.EnsureRunningAllowed();
            return _axes[name].MoveAbsoluteAsync(target, name == "Conveyor_R" ? RotaryProfile : LinearProfile, token);
        }

        /// <summary>发出设备输出前确认会话证据仍可写；证据失败时不再发出新动作。</summary>
        private async Task WriteOutputAsync(string name, bool value, CancellationToken token)
        {
            _line.EnsureRunningAllowed();
            if (_pendingOutput != null)
            {
                if (_pendingOutput != name || _pendingOutputValue != value)
                    throw new InvalidOperationException("上一次输出命令尚未确认，禁止发出其他输出：" + _pendingOutput);
                bool actual = await _io.ReadOutputAsync(name, token).ConfigureAwait(false);
                if (actual != value)
                    throw new InvalidOperationException("输出命令返回不确定且当前状态不一致，请人工核对：" + name);
                // 只确认输出状态，不代表动作成功；下一工步仍须等待独立反馈。
                _pendingOutput = null;
                return;
            }
            _pendingOutput = name;
            _pendingOutputValue = value;
            await _io.WriteOutputAsync(name, value, token).ConfigureAwait(false);
            _pendingOutput = null;
        }

        /// <summary>由设备侧扫码数据位读取低位在前的十六位编码。</summary>
        private async Task<int> ReadScannerCodeAsync(CancellationToken token)
        {
            int value = 0;
            for (int bit = 0; bit < 16; bit++)
                if (await _io.ReadInputAsync("扫码数据" + bit.ToString("D2"), token).ConfigureAwait(false)) value |= 1 << bit;
            return value;
        }

        /// <summary>确认相机上一周期的完成及结果均已复归。</summary>
        private async Task<bool> CameraClearAsync(CancellationToken token)
        {
            bool ack = await _io.ReadInputAsync("相机触发反馈", token).ConfigureAwait(false);
            bool done = await _io.ReadInputAsync("检测完成", token).ConfigureAwait(false);
            bool ok = await _io.ReadInputAsync("检测OK", token).ConfigureAwait(false);
            bool ng = await _io.ReadInputAsync("检测NG", token).ConfigureAwait(false);
            if (ok && ng) throw new InvalidOperationException("相机 OK 与 NG 同时有效。");
            return await WaitAsync(!ack && !done && !ok && !ng, "相机完成及结果复位", token).ConfigureAwait(false);
        }
    }
}
