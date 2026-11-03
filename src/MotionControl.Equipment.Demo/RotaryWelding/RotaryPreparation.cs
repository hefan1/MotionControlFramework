using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Equipment;
using MotionControl.Framework.Contracts.Motion;
using MotionControl.Framework.Contracts.Preparation;

namespace MotionControl.Equipment.Demo.RotaryWelding
{
    /// <summary>本软件测试设备的十三轴准备顺序；不清除载具或自动启动生产。</summary>
    internal sealed class RotaryPreparation : IStationPreparation
    {
        /// <summary>测试轴的回零顺序，先处理竖直轴，再处理水平轴，最后处理转台。</summary>
        private static readonly string[] HomeOrder = { "Feeder_Z", "Camera_Z", "Weld_Z", "Press_Z", "Reject_Z", "Feeder_X", "Load_X", "Transfer_Y", "Unload_X", "Buffer_X", "Buffer_Y", "Camera_X", "Conveyor_R" };
        /// <summary>转台准备角度和测试速度，单位分别为度与度/秒。</summary>
        private static readonly AxisMotionProfile TurnProfile = new AxisMotionProfile(90, 180, 180);
        /// <summary>当前设备逻辑轴、IO 和载具状态。</summary>
        private readonly IEquipmentContext _context;
        /// <summary>回零前只读检查当前会话载具。</summary>
        private readonly RotaryCarrierLine _line;

        /// <summary>绑定设备配置和载具线，不创建新的轴或 IO。</summary>
        internal RotaryPreparation(IEquipmentContext context, RotaryCarrierLine line) { _context = context; _line = line; }
        /// <summary>稳定工位标识。</summary>
        public string StationId { get { return "RotaryPrepare"; } }
        /// <summary>操作员看到的设备准备名称。</summary>
        public string StationName { get { return "双夹位设备准备"; } }
        /// <summary>完整设备准备不依赖其他准备项。</summary>
        public IReadOnlyCollection<string> Dependencies { get { return new string[0]; } }

        /// <summary>只读核对无当前会话载具、无上料、所有输出关闭且轴已停稳。</summary>
        public async Task ValidateStartAsync(CancellationToken token)
        {
            _line.EnsureCanPrepare();
            if (await _context.LogicalIo.ReadInputAsync("上料到位", token).ConfigureAwait(false))
                throw new InvalidOperationException("检测到上料，请先处理物料再准备。");
            foreach (string name in RotaryWeldingEquipmentDefinition.OutputNames)
            {
                // 纯运动验证不使用扫码设备，不应要求其输出已映射。
                if (!_line.TraceProducts && name == "扫码触发") continue;
                if (await _context.LogicalIo.ReadOutputAsync(name, token).ConfigureAwait(false))
                    throw new InvalidOperationException("输出未关闭，不能准备：" + name);
            }
            foreach (string clamp in new[] { "取料", "A", "B" })
            {
                bool closed = await _context.LogicalIo.ReadInputAsync(clamp + "夹紧到位", token).ConfigureAwait(false);
                bool open = await _context.LogicalIo.ReadInputAsync(clamp + "松开到位", token).ConfigureAwait(false);
                bool closedAgain = await _context.LogicalIo.ReadInputAsync(clamp + "夹紧到位", token).ConfigureAwait(false);
                bool openAgain = await _context.LogicalIo.ReadInputAsync(clamp + "松开到位", token).ConfigureAwait(false);
                if (closed || !open || closed != closedAgain || open != openAgain)
                    throw new InvalidOperationException(clamp + "夹具松开反馈未确认、冲突或不稳定，不能准备。");
            }
            foreach (string name in HomeOrder)
            {
                AxisStatus state = await _context.GetAxis(name).ReadStatusAsync(token).ConfigureAwait(false);
                if (!HasStoppedFeedback(state) || state.AlarmActive
                    || state.PositiveLimitActive != false || state.NegativeLimitActive != false)
                    throw new InvalidOperationException("轴位置或速度未知、未停稳、报警或限位状态未知：" + name);
            }
        }

        /// <summary>逐轴使能与回零，最后将旋转台移至 -90 度；异常时停止当前轴并保留错误。</summary>
        public async Task ExecuteAsync(IProgress<StationPreparationProgress> progress, CancellationToken token)
        {
            await ValidateStartAsync(token).ConfigureAwait(false);
            IAxis active = null;
            try
            {
                foreach (string name in HomeOrder)
                {
                    active = _context.GetAxis(name);
                    if (progress != null) progress.Report(new StationPreparationProgress(name + " 回零", "等待轴停稳并确认原点。"));
                    await active.EnableServoAsync(token).ConfigureAwait(false);
                    await active.HomeAsync(token).ConfigureAwait(false);
                    AxisStatus state = await active.ReadStatusAsync(token).ConfigureAwait(false);
                    if (!HasStoppedFeedback(state) || !state.ServoEnabled || !state.Homed || state.AlarmActive
                        || state.RequiresHomingAfterLimitAlarm || state.PositiveLimitActive != false || state.NegativeLimitActive != false)
                        throw new InvalidOperationException(name + " 回零后状态无效。");
                }
                active = _context.GetAxis("Conveyor_R");
                if (progress != null) progress.Report(new StationPreparationProgress("旋转台到初始夹位", "等待到达 -90°。"));
                await active.MoveAbsoluteAsync(-90, TurnProfile, token).ConfigureAwait(false);
                await ValidateReadyAsync(token).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                if (active != null)
                {
                    try
                    {
                        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                            await active.StopAsync(AxisStopMode.Controlled, timeout.Token).ConfigureAwait(false);
                    }
                    catch (Exception stopError) { throw new AggregateException("准备失败且停止轴失败。", error, stopError); }
                }
                throw;
            }
        }

        /// <summary>核验十三轴已回零、停稳，且处于各自初始位置。</summary>
        public async Task ValidateReadyAsync(CancellationToken token)
        {
            foreach (string name in HomeOrder)
            {
                AxisStatus state = await _context.GetAxis(name).ReadStatusAsync(token).ConfigureAwait(false);
                double expected = name == "Conveyor_R" ? -90 : 0;
                if (!HasStoppedFeedback(state) || !state.ServoEnabled || !state.Homed || state.AlarmActive || state.RequiresHomingAfterLimitAlarm
                    || state.PositiveLimitActive != false || state.NegativeLimitActive != false || Math.Abs(state.Position - expected) > 0.01)
                    throw new InvalidOperationException(name + " 不在准备完成位置或状态无效。");
            }
        }

        /// <summary>仅把位置、速度均可读且速度接近零的静止轴视为已停稳。</summary>
        private static bool HasStoppedFeedback(AxisStatus state)
        {
            return state != null && !state.Moving
                && !double.IsNaN(state.Position) && !double.IsInfinity(state.Position)
                && !double.IsNaN(state.Velocity) && !double.IsInfinity(state.Velocity)
                && Math.Abs(state.Velocity) <= 0.001;
        }
    }
}
