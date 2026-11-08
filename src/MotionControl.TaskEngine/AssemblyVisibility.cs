using System.Runtime.CompilerServices;

// 设备适配器需要读取协调器冻结的恢复条件；这些内部状态不开放给设备项目或界面修改。
[assembly: InternalsVisibleTo("MotionControl.TaskEngine.MachineDevice")]
[assembly: InternalsVisibleTo("MotionControl.MachineDevice.Checks")]
// 结构化诊断检查需要构造历史快照；仅向离线检查程序集开放内部构造方法。
[assembly: InternalsVisibleTo("MotionControl.StructuredDiagnostics.Checks")]
