using System;
using System.Threading;
using System.Threading.Tasks;
using MotionControl.Framework.Contracts.Motion;

namespace MotionControl.Adapter.MotionCard.ZMotion
{
    /// <summary>正运动控制卡上的一个逻辑轴，负责将框架动作交给所属卡实例。</summary>
    public sealed class ZMotionAxis : IAxis
    {
        /// <summary>所属的正运动卡实例；轴操作通过该实例调用控制器。</summary>
        private readonly ZMotionCard _card;

        /// <summary>记录控制器是否已完成该轴的回零。</summary>
        private bool _isHomed;

        /// <summary>硬限位报警后标记该轴必须重新回零才能定位。</summary>
        private bool _requiresHomingAfterLimitAlarm;

        /// <summary>绑定所属运动卡和设备配置中的逻辑轴参数。</summary>
        internal ZMotionAxis(ZMotionCard card, ZMotionAxisConfiguration configuration)
        {
            _card = card;
            Configuration = configuration;
        }

        /// <summary>设备流程使用的逻辑轴名称。</summary>
        public string Name
        {
            get { return Configuration.Name; }
        }

        /// <summary>正运动控制器上的物理轴通道号。</summary>
        public int AxisNumber
        {
            get { return Configuration.AxisNumber; }
        }

        /// <summary>该轴对应的设备参数，包括工程单位、限位和回零设置。</summary>
        internal ZMotionAxisConfiguration Configuration { get; private set; }

        /// <summary>更新同一物理通道的逻辑配置；坐标单位或换算比例变化时清除回零状态。</summary>
        internal void UpdateConfiguration(ZMotionAxisConfiguration configuration)
        {
            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            configuration.Validate();
            if (configuration.AxisNumber != AxisNumber
                || !string.Equals(
                    configuration.ControllerId,
                    Configuration.ControllerId,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "在线更新只能修改同一控制卡上的同一物理轴配置。轴号或控制卡标识发生变化时必须重新连接。");
            }

            bool coordinateMeaningChanged = !string.Equals(
                Configuration.EngineeringUnit,
                configuration.EngineeringUnit,
                StringComparison.Ordinal)
                || Configuration.UnitsPerEngineeringUnit != configuration.UnitsPerEngineeringUnit;
            Configuration = configuration;
            if (coordinateMeaningChanged)
            {
                ClearHomedState();
            }
        }

        /// <summary>记录本次控制器连接期间是否已完成回零。</summary>
        internal bool IsHomed
        {
            get { return _isHomed; }
        }

        /// <summary>硬限位报警复位后，是否要求先重新回零才能执行定位。</summary>
        internal bool RequiresHomingAfterLimitAlarm
        {
            get { return _requiresHomingAfterLimitAlarm; }
        }

        /// <summary>读取该逻辑轴的实时位置、伺服、运动和报警状态。</summary>
        public Task<AxisStatus> ReadStatusAsync(CancellationToken cancellationToken)
        {
            return _card.ReadAxisStatusAsync(this, cancellationToken);
        }

        /// <summary>使能该轴的伺服驱动。</summary>
        public Task EnableServoAsync(CancellationToken cancellationToken)
        {
            return _card.EnableServoAsync(this, true, cancellationToken);
        }

        /// <summary>关闭该轴的伺服驱动。</summary>
        public Task DisableServoAsync(CancellationToken cancellationToken)
        {
            return _card.EnableServoAsync(this, false, cancellationToken);
        }

        /// <summary>按设备轴配置调用正运动控制器的 DATUM 回零，并等待控制器报告完成。</summary>
        public Task HomeAsync(CancellationToken cancellationToken)
        {
            return _card.HomeAsync(this, cancellationToken);
        }

        /// <summary>以工程单位移动到指定绝对位置，并等待轴停止。</summary>
        public async Task MoveAbsoluteAsync(
            double position,
            AxisMotionProfile profile,
            CancellationToken cancellationToken)
        {
            ValidateFinite(position, nameof(position));
            if (profile == null)
            {
                throw new ArgumentNullException(nameof(profile));
            }

            double targetPosition = await _card.StartMoveAsync(
                this,
                position,
                profile,
                true,
                cancellationToken).ConfigureAwait(false);
            await WaitForCompletionAndStopOnFailureAsync(targetPosition, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>以工程单位相对当前位置移动，并等待轴停止。</summary>
        public async Task MoveRelativeAsync(
            double distance,
            AxisMotionProfile profile,
            CancellationToken cancellationToken)
        {
            ValidateFinite(distance, nameof(distance));
            if (profile == null)
            {
                throw new ArgumentNullException(nameof(profile));
            }

            double targetPosition = await _card.StartMoveAsync(
                this,
                distance,
                profile,
                false,
                cancellationToken).ConfigureAwait(false);
            await WaitForCompletionAndStopOnFailureAsync(targetPosition, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>按指定方向和运动曲线启动连续点动。</summary>
        public Task StartJogAsync(
            MotionDirection direction,
            AxisMotionProfile profile,
            CancellationToken cancellationToken)
        {
            if (direction != MotionDirection.Positive && direction != MotionDirection.Negative)
            {
                throw new ArgumentOutOfRangeException(nameof(direction), "点动方向无效。");
            }

            if (profile == null)
            {
                throw new ArgumentNullException(nameof(profile));
            }

            return _card.StartJogAsync(this, direction, profile, cancellationToken);
        }

        /// <summary>按指定停止方式停止轴，并等待控制卡确认轴已空闲。</summary>
        public async Task StopAsync(AxisStopMode stopMode, CancellationToken cancellationToken)
        {
            if (stopMode != AxisStopMode.Controlled && stopMode != AxisStopMode.Abort)
            {
                throw new ArgumentOutOfRangeException(nameof(stopMode), "轴停止方式无效。");
            }

            await _card.StopAxisAsync(this, stopMode, cancellationToken).ConfigureAwait(false);
            await _card.WaitForAxisIdleAsync(
                this,
                _card.StopConfirmationTimeout,
                cancellationToken).ConfigureAwait(false);
        }

        /// <summary>按正运动控制卡的当前状态清除该轴报警；有效限位仍触发时不会复位。</summary>
        public Task ResetAlarmAsync(CancellationToken cancellationToken)
        {
            return _card.ResetAxisAlarmAsync(this, cancellationToken);
        }

        /// <summary>记录控制器已报告该轴回零完成。</summary>
        internal void MarkHomed()
        {
            _isHomed = true;
            _requiresHomingAfterLimitAlarm = false;
        }

        /// <summary>硬限位报警恢复后，清除回零标记并要求完成回零。</summary>
        internal void RequireHomingAfterLimitAlarm()
        {
            _isHomed = false;
            _requiresHomingAfterLimitAlarm = true;
        }

        /// <summary>清除该轴的回零标记；断开或重连后调用。</summary>
        internal void ClearHomedState()
        {
            _isHomed = false;
        }

        /// <summary>运动命令已下发后，无论取消、超时还是反馈/报警错误，都先尝试受控停止。</summary>
        private async Task WaitForCompletionAndStopOnFailureAsync(
            double targetPosition,
            CancellationToken cancellationToken)
        {
            try
            {
                await _card.WaitForAxisMoveCompletedAsync(
                    this,
                    targetPosition,
                    _card.MotionTimeoutMilliseconds,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception motionError)
            {
                // 命令可能已启动，不能因状态读取错误而直接退出，留下轴继续运动。
                // 停止失败时由 StopAfterInterruptedMoveAsync 同时保留原故障与停止故障。
                await StopAfterInterruptedMoveAsync(motionError).ConfigureAwait(false);
                throw;
            }
        }

        /// <summary>在运动被取消或超时后执行受控停止，并确认轴已停稳。</summary>
        private async Task StopAfterInterruptedMoveAsync(Exception originalError)
        {
            try
            {
                await _card.StopAxisAsync(this, AxisStopMode.Controlled, CancellationToken.None)
                    .ConfigureAwait(false);
                using (var stopTimeout = new CancellationTokenSource(_card.StopConfirmationTimeout))
                {
                    await _card.WaitForAxisIdleAsync(
                        this,
                        _card.StopConfirmationTimeout,
                        stopTimeout.Token).ConfigureAwait(false);
                }
            }
            catch (Exception stopError)
            {
                throw new AggregateException(
                    "正运动逻辑轴 '" + Name + "' 的运动被取消或超时，且受控停止未能确认完成。",
                    originalError,
                    stopError);
            }
        }

        /// <summary>拒绝 NaN 和无穷大，避免把无效运动参数传给控制卡。</summary>
        private static void ValidateFinite(double value, string parameterName)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(parameterName, "运动参数必须是有限数值。");
            }
        }
    }
}
