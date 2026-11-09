# PLCopen 重构第一批：停止模式语义与 UI 保护

日期：2026-11-09  
基线：`master` 提交 `5a4d038ec113d5af9dfd8a7f3e5fb133c06c8784`  
分支：`codex/plcopen-refactor-phase1-20261109`

## 本批范围

本批只处理可由源码直接证实的停止模式歧义，并建立不会误改用户确认 UI 布局的自动化基线。**不变更四个受保护页面或主窗口的 XAML，不更改任何 UI 按钮、状态显示、动作准入，不合并到 master。**

## R01：VirtualAxis 停止模式不再静默降级

- **问题**：原 `VirtualAxis.StopAsync(AxisStopMode stopMode, ...)` 忽略 `stopMode`，总是向软件控制器发送 `stop`。控制器只有一种 `stop` 命令，无法证明具有 `Abort` 对应的独立中止语义。
- **修改入口**：`src/Adapters/MotionCards/Virtual/Adapter/VirtualAxis.cs` 的 `StopAsync`。
- **修改后**：`Controlled` 沿用已有停止与反馈检查；`Abort` 在发送命令前显式抛出 `NotSupportedException`；未知枚举在发送命令前抛出 `ArgumentOutOfRangeException`。不对真实硬件作任何变化。
- **标准边界**：对齐 PLCopen 区分停止、运动中止与错误的设计思想，但 `AxisStopMode.Abort` 并不声明等同 IEC 功能块 `MC_Stop`、`MC_Halt` 或安全急停。未来应结合厂商行为和反馈再设计完整的运动状态模型。
- **兼容性影响**：已有 `Controlled` 路径不变；以前向 Virtual 发送 `Abort` 的代码可能从“被当成 stop 执行”变为“显式不支持”。这是防止错误语义的有意行为变化，须关注具体调用方及运行场景。
- **新增静态与运行检查入口**：`tools/MotionControl.VirtualAdapter.Checks/Program.cs` 的 `Run` 增加 `Abort` 和非法枚举拒绝断言。此程序依赖已启动的独立软件控制器，未在本次无 Windows/.NET 环境中运行，不能称为已通过。

## UI 保护：布局文件基线守卫

新增 `tools/Verify-ProtectedUi.ps1`，并在 `.github/workflows/windows-checks.yml` 中于构建前调用。

依据固定基线 Git Blob SHA 检查以下文件：
- `AxisMappingConfigurationView.xaml`
- `IoMappingConfigurationView.xaml`
- `ManualAxisControl.xaml`
- `IoMonitorView.xaml`
- `MainWindow.xaml`

任何文件内容变化（包括空白、布局和文案）均使构建工作流失败。后续若获得用户明确批准，应在独立 PR 中先审核 UI 差异及基线更新，而不是绕过检查。

**局限**：上述守卫只校验 XAML，不证明相应 `.xaml.cs`、Host、Runtime 内部行为未变化；必须继续做正常 Host 真正的屏幕验收、安全门禁、断连与设备切换回归。此脚本不替代 PLCopen 状态机测试或真实运动卡验证。

## 本轮验证实际情况

- **已完成**：源码定位、变更提交、回归断言代码、UI 基线脚本及 CI 调用编排。
- **尚未完成**：Windows Host 编译、完整 `Verify-V18.ps1`、独立 VirtualController 运行检查、界面屏幕核对、ZMotion 仿真与实体运动卡测试。
- **未改动**：五个受保护 XAML 文件及四页代码后台、`MachineRuntime`、`IAxis` 对外签名。
- **合并建议**：先核对 GitHub Actions 的 Windows 构建结果；VirtualAdapter.Checks 还需要真实启动软件控制器再单独验证，然后才能决定合并。

## 下一批计划（尚未修改）

1. 梳理 PLCopen 轴状态与现有 `AxisStatus` 的映射边界：源缺失时返回“未知”，不要凭 `Moving`、`ServoEnabled` 猜测 `Homing` 等状态。
2. 对比 `MachineRuntime` 与 `TaskMachineController` 的控制权、安全准入和停止收尾责任；只抽取已经证明重复的规则。
3. 为四个受保护页面建立正式 Host 的截图/交互回归基线，验证 UI 后台更改没有引入旧数据或门禁退化。

本项目以 C# 初级开发者能按调用顺序阅读为标准，不因 PLCopen 而引入反射式注册、复杂泛型管线或不必要的层次。
