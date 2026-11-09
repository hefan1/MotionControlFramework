namespace MotionControl.Framework.Contracts.Motion
{
    /// <summary>
    /// 逻辑轴可选的运动阶段，与 PLCopen Motion Control Part 1 的轴状态名称对照。
    /// 状态只允许由可信控制器或已确认的执行状态提供，不能仅凭 Moving 或 ServoEnabled 猜测。
    /// 此枚举本身不表示 PLCopen 功能块兼容或认证，也不是设备安全状态。
    /// </summary>
    public enum AxisMotionState
    {
        /// <summary>控制器未提供足够证据；不能当作静止、停稳或允许启动。</summary>
        Unknown = 0,

        /// <summary>轴驱动未使能；不表示硬件安全回路已断开。</summary>
        Disabled = 1,

        /// <summary>轴已使能且当前没有运动命令；不能替代反馈停稳检查。</summary>
        Standstill = 2,

        /// <summary>轴正在执行回零过程。</summary>
        Homing = 3,

        /// <summary>轴正在执行有限目标的定位运动。</summary>
        DiscreteMotion = 4,

        /// <summary>轴正在执行连续运动，例如软件控制器的连续点动。</summary>
        ContinuousMotion = 5,

        /// <summary>轴正在执行已被控制器明确识别的同步运动。</summary>
        SynchronizedMotion = 6,

        /// <summary>轴正在执行受控停止过程；不能仅凭收到停止命令设置此状态。</summary>
        Stopping = 7,

        /// <summary>控制器明确进入运动故障停止状态。</summary>
        ErrorStop = 8
    }
}
