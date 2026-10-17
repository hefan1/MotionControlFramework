# 仿真优先的框架验证实施计划

> **For Codex:** 按本计划逐项实现，并在每项后检查变更范围。

**目标：** 让 ZDevelop 离线仿真器通过正运动适配器与单机 Runtime 完成可重复的连接、轴/IO 和启停流程验证。

**架构：** Equipment 模板集中保存控制卡连接、轴参数和 IO 映射；正运动配置只保存连接器专属参数，适配器接收已经分开的配置对象。新增独立命令行验证工具，不把仿真验证代码塞进 WPF 主窗口。Runtime 为暂停等待增加超时和未退出流程保护。

**技术栈：** .NET Framework 4.8、C# 7.3、WPF 现有解决方案、Visual Studio Build Tools MSBuild、正运动 ZDevelop 离线仿真器。

---

### 任务 1：明确配置唯一来源

**文件：**
- 修改 `src/MotionControl.Equipment.Template/HardwareConfig/AxisParameters.json`
- 修改 `src/MotionControl.Equipment.Template/HardwareConfig/IoMapping.json`
- 修改 `src/Adapters/MotionCards/ZMotion/Adapter/Samples/ZDevelopSimulation.json`
- 修改正运动适配器配置类、卡构造函数和使用说明

设备模板中的轴参数文件保存轴逻辑名、控制器标识、物理轴号、单位换算、软限位和点动参数；IO 文件保存逻辑点名、控制器标识、物理通道和有效电平。ZDevelop 样例文件只保存仿真器连接信息，避免轴和 IO 在厂商配置里再维护一份。

### 任务 2：增加仿真器联调工具

**文件：**
- 新建 `tools/MotionControl.ZMotionSimulatorCheck/`
- 修改 `MotionControlFramework.sln`
- 更新根 README 与正运动适配器说明

命令行工具读取模板配置并使用生产适配器实例，检查控制器身份、应用轴参数、读取轴状态与 DI/DO；可显式执行仿真轴往返定位和 DO 读写恢复。随后用同一适配器运行 Runtime 连接、复位、启动、暂停、再次复位和断开流程。默认不连接实体卡，仍由适配器的本机地址和 `VPLC5xx-Simu` 身份校验限制目标。

### 任务 3：给暂停增加超时边界

**文件：**
- 修改 `src/MotionControl.Framework.Runtime/FlowScheduling/StationFlowScheduler.cs`
- 修改 `src/MotionControl.Framework.Runtime/Machine/MachineRuntime.cs`
- 更新 Runtime 与架构说明

暂停超时后把设备置为报警状态并报告工位仍未退出；只要流程任务还在运行，就拒绝复位、断卡和再次启动。允许在报警状态再次请求暂停，以便流程响应取消后完成回收。

### 任务 4：编译和运行验证

- 使用安装的 .NET SDK MSBuild 构建完整解决方案。
- 启动 ZDevelop 仿真器并运行仿真检查工具，记录实际连接、身份、状态、运动和 IO 结果。
- 启动 WPF Host 确认外壳可运行。
- 只暂存本次相关文件，检查暂存差异后用中文提交说明提交本地 Git。
