using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Configuration;
using MotionControl.Framework.Contracts.Motion;

namespace MotionControl.Adapter.MotionCard.Virtual
{
    /// <summary>将框架逻辑轴动作转换为软件控制器的轴命令。</summary>
    internal sealed class VirtualAxis : IAxis
    {
        /// <summary>所属软件控制卡。</summary>
        private readonly VirtualMotionCard _card;
        /// <summary>轴的设备映射、限位和到位容差。</summary>
        private readonly AxisHardwareDefinition _definition;
        /// <summary>创建逻辑轴，物理轴号由设备配置指定。</summary>
        public VirtualAxis(VirtualMotionCard card, AxisHardwareDefinition definition)
        {
            _card = card ?? throw new ArgumentNullException(nameof(card));
            _definition = definition ?? throw new ArgumentNullException(nameof(definition));
            if (string.IsNullOrWhiteSpace(definition.Name)) throw new ArgumentException("轴名称不能为空。");
        }
        /// <summary>设备流程使用的逻辑轴名称。</summary>
        public string Name { get { return _definition.Name; } }
        /// <summary>发送带当前物理轴号的命令。</summary>
        private Task<Dictionary<string, object>> SendAsync(string command, CancellationToken token, double position = 0, double speed = 0, bool value = false)
        {
            return _card.SendAsync(new Dictionary<string, object> { ["command"] = command, ["axis"] = _definition.AxisNumber, ["position"] = position, ["speed"] = speed, ["value"] = value }, token);
        }
        /// <summary>读取布尔字段，未提供时保留未知状态。</summary>
        private static bool? OptionalBoolean(Dictionary<string, object> data, string key)
        {
            return data.TryGetValue(key, out var value) && value != null ? (bool?)Convert.ToBoolean(value) : null;
        }
        /// <summary>读取控制器返回的独立轴反馈快照。</summary>
        public async Task<AxisStatus> ReadStatusAsync(CancellationToken cancellationToken)
        {
            var response = await SendAsync("readAxis", cancellationToken).ConfigureAwait(false);
            var state = (Dictionary<string, object>)response["axisState"];
            return new AxisStatus(
                Convert.ToDouble(state["position"]), Convert.ToDouble(state["velocity"]),
                Convert.ToBoolean(state["servoEnabled"]), Convert.ToBoolean(state["moving"]), Convert.ToBoolean(state["homed"]),
                OptionalBoolean(state, "positiveLimitActive"), OptionalBoolean(state, "negativeLimitActive"),
                OptionalBoolean(state, "positiveSoftLimitActive"), OptionalBoolean(state, "negativeSoftLimitActive"),
                Convert.ToBoolean(state["alarmActive"]), state.TryGetValue("alarmMessage", out var message) ? Convert.ToString(message) : null,
                Convert.ToBoolean(state["requiresHomingAfterLimitAlarm"]),
                state.TryGetValue("commandPosition", out var position) && position != null ? (double?)Convert.ToDouble(position) : null,
                OptionalBoolean(state, "originInputActive"), "软件控制器");
        }
        /// <summary>使能轴伺服。</summary>
        public async Task EnableServoAsync(CancellationToken cancellationToken) { await SendAsync("servo", cancellationToken, value: true).ConfigureAwait(false); }
        /// <summary>关闭轴伺服，控制器负责拒绝运动中的关闭。</summary>
        public async Task DisableServoAsync(CancellationToken cancellationToken) { await SendAsync("servo", cancellationToken, value: false).ConfigureAwait(false); }
        /// <summary>发出回零命令并等到控制器确认完成。</summary>
        public async Task HomeAsync(CancellationToken cancellationToken)
        {
            // 此软件控制器固定回到零位，不允许静默跨越已配置的软件行程。
            ValidateTarget(0);
            await SendAsync("home", cancellationToken).ConfigureAwait(false);
            await WaitUntilStoppedAsync(null, true, cancellationToken).ConfigureAwait(false);
        }
        /// <summary>限制目标在设备已启用的软件限位范围内。</summary>
        private void ValidateTarget(double position)
        {
            if (double.IsNaN(position) || double.IsInfinity(position)) throw new ArgumentOutOfRangeException(nameof(position));
            if (_definition.SoftLimitEnabled == true && (position < _definition.MinimumPosition || position > _definition.MaximumPosition))
                throw new InvalidOperationException("轴 " + Name + " 的目标位置超出软件限位。");
        }
        /// <summary>发出绝对定位命令，等待实际反馈到位；取消时发送停止。</summary>
        public async Task MoveAbsoluteAsync(double position, AxisMotionProfile profile, CancellationToken cancellationToken)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            ValidateTarget(position);
            await SendAsync("move", cancellationToken, position, profile.Speed).ConfigureAwait(false);
            await WaitUntilStoppedAsync(position, false, cancellationToken).ConfigureAwait(false);
        }
        /// <summary>根据实时反馈换算相对位移，使用相同定位与停止语义。</summary>
        public async Task MoveRelativeAsync(double distance, AxisMotionProfile profile, CancellationToken cancellationToken)
        {
            AxisStatus status = await ReadStatusAsync(cancellationToken).ConfigureAwait(false);
            await MoveAbsoluteAsync(status.Position + distance, profile, cancellationToken).ConfigureAwait(false);
        }
        /// <summary>启动指定方向的连续点动。</summary>
        public async Task StartJogAsync(MotionDirection direction, AxisMotionProfile profile, CancellationToken cancellationToken)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (direction != MotionDirection.Positive && direction != MotionDirection.Negative) throw new ArgumentOutOfRangeException(nameof(direction));
            if (_definition.SoftLimitEnabled == true)
                throw new NotSupportedException("软件控制器尚不支持点动的软件限位，已拒绝该轴点动。");
            await SendAsync("jog", cancellationToken, speed: profile.Speed * (int)direction).ConfigureAwait(false);
        }
        /// <summary>停止轴运动并检查控制器确实报告停止。</summary>
        public async Task StopAsync(AxisStopMode stopMode, CancellationToken cancellationToken)
        {
            await SendAsync("stop", cancellationToken).ConfigureAwait(false);
            AxisStatus status = await ReadStatusAsync(cancellationToken).ConfigureAwait(false);
            if (status.Moving) throw new InvalidOperationException("轴 " + Name + " 停止命令后仍在运动。");
        }
        /// <summary>请求清除轴报警，控制器负责检查报警前置条件。</summary>
        public async Task ResetAlarmAsync(CancellationToken cancellationToken) { await SendAsync("resetAlarm", cancellationToken).ConfigureAwait(false); }
        /// <summary>轮询完成状态；取消时使用独立令牌停车，避免取消令牌阻止停止命令。</summary>
        private async Task WaitUntilStoppedAsync(double? target, bool homing, CancellationToken token)
        {
            var deadline = DateTime.UtcNow.AddMinutes(2);
            try
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    AxisStatus status = await ReadStatusAsync(token).ConfigureAwait(false);
                    if (status.AlarmActive) throw new InvalidOperationException("轴 " + Name + " 报警：" + status.AlarmMessage);
                    if (!status.Moving)
                    {
                        if (homing && !status.Homed) throw new InvalidOperationException("轴 " + Name + " 回零未完成。");
                        if (target.HasValue && Math.Abs(status.Position - target.Value) > _definition.PositionTolerance)
                            throw new InvalidOperationException("轴 " + Name + " 停止但未到目标位置。");
                        return;
                    }
                    if (DateTime.UtcNow >= deadline) throw new TimeoutException("轴 " + Name + " 运动等待超时。");
                    await Task.Delay(30, token).ConfigureAwait(false);
                }
            }
            catch (Exception operationError)
            {
                // 等待失败也必须尝试停车，不能让超时后的轴继续运行。
                try { await StopAsync(AxisStopMode.Controlled, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception stopError) { throw new AggregateException("运动等待失败且停车失败。", operationError, stopError); }
                throw;
            }
        }
    }
}
