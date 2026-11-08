using MotionControl.Framework.Runtime.Machine;
using MotionControl.Framework.Wpf.Variables;

namespace MotionControl.Metadata.Checks
{
    /// <summary>检查定义维护门禁，明确区分冻结任务定义和合法当前值写入。</summary>
    internal static partial class Program
    {
        /// <summary>Ready 和待复位仍允许原人工写值，但有任务会话或后台装载时不能修改定义。</summary>
        private static void DefinitionEditingRequiresClosedTaskSession()
        {
            Check(DeviceVariableEditAccess.DefinitionBlockResourceKey(MachineRunState.Ready, false, false) == null,
                "无任务会话的 Ready 状态错误禁止定义维护。");
            Check(DeviceVariableEditAccess.DefinitionBlockResourceKey(MachineRunState.Ready, true, false)
                == "变量定义任务会话锁定", "已加载任务可被 Ready 状态绕过定义保护。");
            Check(DeviceVariableEditAccess.CanWriteCurrentValue(MachineRunState.Ready),
                "冻结任务定义意外取消了 Ready 状态合法的当前值写入。");
            Check(DeviceVariableEditAccess.DefinitionBlockResourceKey(MachineRunState.ResetRequired, true, false)
                == "变量定义任务会话锁定", "待复位的旧任务会话仍可修改变量含义。");
            Check(DeviceVariableEditAccess.CanWriteCurrentValue(MachineRunState.ResetRequired),
                "待复位状态原有当前值写入权限发生变化。");
            Check(DeviceVariableEditAccess.DefinitionBlockResourceKey(null, true, false)
                == "变量定义任务会话锁定", "后台目录装载尚未接管 Runtime 时未锁定定义。");
            Check(DeviceVariableEditAccess.DefinitionBlockResourceKey(MachineRunState.Ready, false, true)
                == "变量定义设备操作锁定", "设备准备或收尾活动中仍可修改定义。");
            Check(DeviceVariableEditAccess.DefinitionBlockResourceKey(MachineRunState.Running, false, false)
                == "变量状态运行时锁定" && !DeviceVariableEditAccess.CanWriteCurrentValue(MachineRunState.Running),
                "设备运行时原有定义和当前值写入门槛被放宽。");
            Check(!DeviceVariableEditAccess.CanWriteCurrentValue(MachineRunState.Paused)
                && !DeviceVariableEditAccess.CanWriteCurrentValue(MachineRunState.Alarmed),
                "暂停或报警时错误放行当前值人工写入。");
            Check(DeviceVariableEditAccess.DefinitionBlockResourceKey(null, false, false) == null
                && DeviceVariableEditAccess.CanWriteCurrentValue(MachineRunState.Disconnected),
                "关闭任务后脱机维护或原断连人工写值不再可用。");
        }
    }
}
