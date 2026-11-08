using MotionControl.Framework.Runtime.Machine;

namespace MotionControl.Framework.Wpf.Variables
{
    /// <summary>分别判断定义维护与当前值写入，防止已加载任务继续使用旧的变量含义。</summary>
    internal static class DeviceVariableEditAccess
    {
        /// <summary>保留原当前值写入的整机状态门槛；变量本身的人工写入权限仍由页面和存储检查。</summary>
        internal static bool CanWriteCurrentValue(MachineRunState? state)
        {
            return !state.HasValue || state == MachineRunState.Disconnected
                || state == MachineRunState.ResetRequired || state == MachineRunState.Ready;
        }

        /// <summary>返回不能维护定义的明确原因；任务装载期间也必须锁定，避免新目录与新存储交叉。</summary>
        internal static string DefinitionBlockResourceKey(MachineRunState? state, bool taskSessionOrCommand,
            bool deviceOperationActive)
        {
            if (taskSessionOrCommand) return "变量定义任务会话锁定";
            if (deviceOperationActive) return "变量定义设备操作锁定";
            return CanWriteCurrentValue(state) ? null : "变量状态运行时锁定";
        }
    }
}
