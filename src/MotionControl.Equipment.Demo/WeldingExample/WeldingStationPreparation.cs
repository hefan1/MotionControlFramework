using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.IO;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Contracts.Preparation;

namespace MotionControl.Equipment.Demo.WeldingExample
{
    /// <summary>焊接测试设备的单工位准备顺序；只使用通用轴和逻辑 IO，不包含厂商回零指令。</summary>
    internal sealed class WeldingStationPreparation : IStationPreparation
    {
        /// <summary>待机位置为零毫米，速度为每秒一毫米；实际工程须由机械工艺配置。</summary>
        private static readonly AxisMotionProfile WaitingPositionProfile = new AxisMotionProfile(1, 10, 10);
        /// <summary>本软件测试设备允许的到位误差，单位毫米。</summary>
        private const double PositionTolerance = 0.01;
        /// <summary>准备前确认反馈连续无效的时间，单位毫秒。</summary>
        private const int StableFeedbackMilliseconds = 1500;
        /// <summary>单次准备的最大时长；包括稳定检查、回零和到待机位。</summary>
        private static readonly TimeSpan PreparationTimeout = TimeSpan.FromSeconds(20);
        /// <summary>本工位专用轴。</summary>
        private readonly IAxis _axis;
        /// <summary>设备逻辑 IO。</summary>
        private readonly ILogicalIo _io;
        /// <summary>工位动作完成输入。</summary>
        private readonly string _input;
        /// <summary>工位动作输出；准备期间只检查，不擅自释放机构。</summary>
        private readonly string _output;
        /// <summary>当前设备载具及生产历史。</summary>
        private readonly WeldingCarrierLine _line;
        /// <summary>压合准备要求焊接已让位；焊接工位没有该前置工位。</summary>
        private readonly WeldingStationPreparation _precedingStation;

        /// <summary>绑定已校验的设备资源及具体工艺依赖。</summary>
        internal WeldingStationPreparation(string stationId, string stationName, IAxis axis, ILogicalIo io,
            string input, string output, WeldingCarrierLine line, WeldingStationPreparation precedingStation)
        {
            StationId = stationId;
            StationName = stationName;
            _axis = axis;
            _io = io;
            _input = input;
            _output = output;
            _line = line;
            _precedingStation = precedingStation;
        }

        /// <summary>工位稳定标识，供通用准备协调器匹配依赖。</summary>
        public string StationId { get; private set; }
        /// <summary>操作员看到的工位名称。</summary>
        public string StationName { get; private set; }
        /// <summary>只有压合依赖焊接；具体设备顺序保留在示例内。</summary>
        public IReadOnlyCollection<string> Dependencies
        {
            get { return _precedingStation == null ? new string[0] : new[] { _precedingStation.StationId }; }
        }

        /// <summary>只读检查无在制品、未进入生产、输出关闭、反馈复位和轴已停止；允许轴尚未使能或回零。</summary>
        public async Task ValidateStartAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            _line.EnsureCanPrepare();
            if (await _io.ReadInputAsync("上料到位", token).ConfigureAwait(false))
                throw new InvalidOperationException("上料位置检测到物料，请先处理来料再执行工位回零。");
            await EnsureOutputsAndFeedbackSafeAsync(token).ConfigureAwait(false);
            EnsureAxisStopped(await _axis.ReadStatusAsync(token).ConfigureAwait(false));
        }

        /// <summary>每次重试重新核对现场，从稳定反馈检查开始；不会重置生产步骤或载具。</summary>
        public async Task ExecuteAsync(IProgress<StationPreparationProgress> progress, CancellationToken token)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(PreparationTimeout);
                CancellationToken operationToken = timeout.Token;
                try
                {
                    Report(progress, "检查回零条件", "确认无物料，动作输出关闭，完成反馈已复位。");
                    await ValidateStartAsync(operationToken).ConfigureAwait(false);
                    await EnsurePrecedingStationReadyAsync(operationToken).ConfigureAwait(false);

                    Report(progress, "确认反馈稳定", "完成反馈须连续保持复位 1.5 秒。");
                    var stable = Stopwatch.StartNew();
                    while (stable.ElapsedMilliseconds < StableFeedbackMilliseconds)
                    {
                        await ValidateStartAsync(operationToken).ConfigureAwait(false);
                        await EnsurePrecedingStationReadyAsync(operationToken).ConfigureAwait(false);
                        await Task.Delay(100, operationToken).ConfigureAwait(false);
                    }

                    Report(progress, "使能 " + _axis.Name, "检查伺服和轴报警。");
                    await _axis.EnableServoAsync(operationToken).ConfigureAwait(false);
                    AxisStatus enabled = await _axis.ReadStatusAsync(operationToken).ConfigureAwait(false);
                    EnsureAxisStopped(enabled);
                    if (!enabled.ServoEnabled) throw new InvalidOperationException(_axis.Name + " 伺服未使能。");
                    await ValidateStartAsync(operationToken).ConfigureAwait(false);
                    await EnsurePrecedingStationReadyAsync(operationToken).ConfigureAwait(false);

                    Report(progress, _axis.Name + " 回零", "等待控制器确认坐标参考及轴停止。");
                    await _axis.HomeAsync(operationToken).ConfigureAwait(false);
                    AxisStatus homed = await _axis.ReadStatusAsync(operationToken).ConfigureAwait(false);
                    EnsureAxisStopped(homed);
                    if (!homed.Homed || homed.RequiresHomingAfterLimitAlarm)
                        throw new InvalidOperationException(_axis.Name + " 未建立有效回零参考。");

                    await ValidateStartAsync(operationToken).ConfigureAwait(false);
                    await EnsurePrecedingStationReadyAsync(operationToken).ConfigureAwait(false);
                    Report(progress, _axis.Name + " 返回待机位", "等待实际位置到达 0 mm。");
                    await _axis.MoveAbsoluteAsync(0, WaitingPositionProfile, operationToken).ConfigureAwait(false);
                    await ValidateReadyAsync(operationToken).ConfigureAwait(false);
                    Report(progress, "准备完成", "轴已回零并停在待机位，等待启动生产。");
                }
                catch (Exception operationError)
                {
                    try
                    {
                        using (var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                        {
                            await _axis.StopAsync(AxisStopMode.Controlled, stopTimeout.Token).ConfigureAwait(false);
                            AxisStatus stopped = await _axis.ReadStatusAsync(stopTimeout.Token).ConfigureAwait(false);
                            if (stopped.Moving || Math.Abs(stopped.Velocity) > 0.001)
                                throw new InvalidOperationException(_axis.Name + " 准备中止后尚未确认停稳。");
                        }
                    }
                    catch (Exception stopError)
                    {
                        throw new AggregateException(StationName + " 准备失败且停止确认失败。", operationError, stopError);
                    }
                    if (operationError is OperationCanceledException && !token.IsCancellationRequested)
                        throw new TimeoutException(StationName + " 准备超过 20 秒，已请求轴停止。", operationError);
                    throw;
                }
            }
        }

        /// <summary>实时验收轴坐标、停稳、待机位及反馈；准备后允许上料信号到达。</summary>
        public async Task ValidateReadyAsync(CancellationToken token)
        {
            await EnsurePrecedingStationReadyAsync(token).ConfigureAwait(false);
            await EnsureOutputsAndFeedbackSafeAsync(token).ConfigureAwait(false);
            AxisStatus status = await _axis.ReadStatusAsync(token).ConfigureAwait(false);
            EnsureAxisStopped(status);
            if (!status.ServoEnabled || !status.Homed || status.RequiresHomingAfterLimitAlarm)
                throw new InvalidOperationException(StationName + " 的轴参考或伺服状态已失效，请重新核对设备准备。");
            if (Math.Abs(status.Position) > PositionTolerance)
                throw new InvalidOperationException(StationName + " 未在待机位：" + _axis.Name + " 当前 " + status.Position + " mm。");
        }

        /// <summary>回零过程中检查前置工位仍停在待机位置，不只信任历史完成标记。</summary>
        private async Task EnsurePrecedingStationReadyAsync(CancellationToken token)
        {
            if (_precedingStation == null) return;
            try { await _precedingStation.ValidateReadyAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                throw new InvalidOperationException("等待" + _precedingStation.StationName + "让位：" + error.Message, error);
            }
        }

        /// <summary>只读取动作输出及完成反馈，任何读失败向上报告，不默认安全或擅自关闭夹具。</summary>
        private async Task EnsureOutputsAndFeedbackSafeAsync(CancellationToken token)
        {
            if (await _io.ReadOutputAsync(_output, token).ConfigureAwait(false))
                throw new InvalidOperationException("请先核对并关闭“" + _output + "”，工位回零不会强制释放机构。");
            if (await _io.ReadInputAsync(_input, token).ConfigureAwait(false))
                throw new InvalidOperationException("“" + _input + "”仍有效，请检查机构及反馈复位状态。");
        }

        /// <summary>核对有限的位置速度、停稳、可读的硬限位以及当前报警。</summary>
        private void EnsureAxisStopped(AxisStatus status)
        {
            if (double.IsNaN(status.Position) || double.IsInfinity(status.Position)
                || double.IsNaN(status.Velocity) || double.IsInfinity(status.Velocity))
                throw new InvalidOperationException(_axis.Name + " 位置或速度状态无效。");
            if (status.Moving || Math.Abs(status.Velocity) > 0.001)
                throw new InvalidOperationException(_axis.Name + " 仍在运动，请先停止。");
            if (status.AlarmActive)
                throw new InvalidOperationException(_axis.Name + " 存在轴报警：" + status.AlarmMessage);
            if (status.PositiveLimitActive != false || status.NegativeLimitActive != false)
                throw new InvalidOperationException(_axis.Name + " 硬限位触发或状态不可读，请先检查。");
        }

        /// <summary>将当前工艺动作与说明交给通用界面显示。</summary>
        private static void Report(IProgress<StationPreparationProgress> progress, string action, string detail)
        {
            if (progress != null) progress.Report(new StationPreparationProgress(action, detail));
        }
    }
}
