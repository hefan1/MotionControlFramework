using System.Threading;
using System.Threading.Tasks;

namespace MotionControl.Framework.Contracts.Motion
{
    /// <summary>
    /// 设备中的一个逻辑轴。直线轴位置使用 mm、速度使用 mm/s；旋转轴位置使用度、
    /// 速度使用度/s。脉冲换算由运动卡适配器处理。
    /// </summary>
    public interface IAxis
    {
        /// <summary>设备中的逻辑名称，例如 "X" 或 "FeederLift"。</summary>
        string Name { get; }

        /// <summary>读取轴当前状态快照；位置和速度均使用该逻辑轴配置的工程单位。</summary>
        Task<AxisStatus> ReadStatusAsync(CancellationToken cancellationToken);

        /// <summary>使能当前轴的伺服；控制卡拒绝使能或调用被取消时报告错误。</summary>
        Task EnableServoAsync(CancellationToken cancellationToken);

        /// <summary>关闭当前轴的伺服；调用方应先确认轴已停止并处于允许断使能的状态。</summary>
        Task DisableServoAsync(CancellationToken cancellationToken);

        /// <summary>使用轴配置中的回零参数执行回零。</summary>
        Task HomeAsync(CancellationToken cancellationToken);

        /// <summary>
        /// 移动到绝对位置，轴到位后完成。取消令牌会请求受控停止；轴停下后，
        /// 此操作以取消状态结束。停止过程发生驱动错误时会抛出异常。
        /// </summary>
        Task MoveAbsoluteAsync(double position, AxisMotionProfile profile, CancellationToken cancellationToken);

        /// <summary>
        /// 从当前位置移动指定距离，轴到位后完成。取消令牌会请求受控停止；轴停下后，
        /// 此操作以取消状态结束。停止过程发生驱动错误时会抛出异常。
        /// </summary>
        Task MoveRelativeAsync(double distance, AxisMotionProfile profile, CancellationToken cancellationToken);

        /// <summary>
        /// 按指定运动曲线开始连续点动，命令被接受后即返回。点动请求结束时，调用方必须调用 StopAsync；
        /// 此处取消令牌只取消点动命令的启动。
        /// </summary>
        Task StartJogAsync(MotionDirection direction, AxisMotionProfile profile, CancellationToken cancellationToken);

        /// <summary>按指定的普通运动停止方式停止轴；急停不通过此方法处理。</summary>
        Task StopAsync(AxisStopMode stopMode, CancellationToken cancellationToken);

        /// <summary>
        /// 清除运动卡报告的轴报警。当前限位或报警输入仍有效时必须失败；该操作不会移动轴，
        /// 也不会替代设备级报警复位逻辑。
        /// </summary>
        Task ResetAlarmAsync(CancellationToken cancellationToken);
    }
}
