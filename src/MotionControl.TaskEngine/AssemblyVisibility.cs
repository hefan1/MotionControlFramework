using System.Runtime.CompilerServices;

// 设备适配器需要读取协调器冻结的恢复条件；这些内部状态不开放给设备项目或界面修改。
[assembly: InternalsVisibleTo("MotionControl.TaskEngine.MachineDevice")]
[assembly: InternalsVisibleTo("MotionControl.MachineDevice.Checks")]
