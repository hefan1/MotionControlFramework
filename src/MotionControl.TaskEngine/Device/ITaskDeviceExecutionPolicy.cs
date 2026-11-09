namespace MotionControl.TaskEngine.Device
{
    /// <summary>
    /// 仅供框架内已知的纯内存任务设备声明执行规则。
    /// 未实现本接口的设备一律使用严格模式：复位检查轴就绪，同步暂停/停机报警入口不允许跳过停稳收尾。
    /// 不把这些选项放入用户配置，也不作为厂商能力声明或 PLCopen 认证依据。
    /// </summary>
    internal interface ITaskDeviceExecutionPolicy
    {
        /// <summary>复位任务时是否检查需要定位的轴已使能并完成回零。</summary>
        bool RequiresAxisReadinessOnReset { get; }

        /// <summary>是否允许同步改变暂停/停机报警状态而不等待物理运动停止；仅无物理命令的内存设备可启用。</summary>
        bool AllowsSynchronousStateChangeWithoutStopSettlement { get; }
    }
}
