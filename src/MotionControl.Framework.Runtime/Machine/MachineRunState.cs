namespace MotionControl.Framework.Runtime.Machine
{
    /// <summary>单台设备的运行状态。</summary>
    public enum MachineRunState
    {
        /// <summary>运动卡未连接。</summary>
        Disconnected,

        /// <summary>运动卡已连接，必须先执行复位。</summary>
        ResetRequired,

        /// <summary>复位完成，允许启动。</summary>
        Ready,

        /// <summary>工位流程正在并行运行。</summary>
        Running,

        /// <summary>正在等待工位完成当前步骤并安全停下。</summary>
        Pausing,

        /// <summary>流程已暂停，保留当前步骤；再次启动前必须复位。</summary>
        Paused,

        /// <summary>运行或复位出现故障，需要处理并复位。</summary>
        Alarmed
    }
}
