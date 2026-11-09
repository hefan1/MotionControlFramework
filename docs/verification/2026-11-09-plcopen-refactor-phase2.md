# PLCopen 重构第二批：唯一停稳规则、实际控制器 API 与无表头监控检查

日期：2026-11-09  
工作分支：`codex/plcopen-refactor-phase2-20261109`；继承第一批 `codex/plcopen-refactor-phase1-20261109`，暂不合并 `master`。

## 本批目标和用户要求

框架初期以 PLCopen Motion Control 的运动语义为基准，**现有的软件控制器程序和正式适配器 API 是运动验证入口，不新增 FakeAxis、InMemoryAxis 或第二套运动模型**。用户确认的轴映射、IO 映射、轴手动、IO 监控四页及主窗口骨架完全保护，本次未触碰其 XAML 或事件后台。

规范参考：https://www.plcopen.org/standards/motion-control/ （Part 1 单轴与状态；Part 4 协调运动；Part 5 回零）。

## R02：统一停稳反馈判定

- 原问题：`MachineRuntime.StopAutomaticProductionAsync` 已调用 `AxisStopFeedback.IsConfirmedStopped`，但 `MachineTaskDevice.Motion.cs` 中的动作完成、独立停稳查询和收尾轮询，分别重复实现 `Moving`、速度有限性以及 0.001 工程单位/秒容差。
- 修改文件：`src/MotionControl.TaskEngine.MachineDevice/MachineTaskDevice.Motion.cs`。
- 修改方式：三处直接复用已存在的 `AxisStopFeedback.IsConfirmedStopped(status)`，保留原位置信息有效性、轴报警、回零完成、收尾超时与故障文本。没有引入新接口、管理器、状态单例或第二个停止状态机。
- 安全边界：该谓词表示**当前读取结果足以供软件确认停稳**，不是安全回路、机械制动或失效安全状态认证。

## R03：修正过时 WPF 任务监控测试，不改 UI

- 原问题：`TaskMonitor.Checks/Program.cs` 仍调用 `StationsGrid.Columns[0].Header.ToString()`；当前 `TaskMonitorView.xaml` 的工位列表已使用一个 `DataGridTemplateColumn` 和 `HeadersVisibility=None`，无表头导致空引用。第一批 Windows CI 的 Host 编译成功，但因此有 1/15 检查项目失败。
- 修改：将旧表头断言改为验证现有无表头模板、工位 ID 身份及中英资源切换；不删除测试、不修改 WPF 页面。
- 注意：修复前的 CI 失败是实际记录，第二批需要等待新 CI 确认，不提前宣称全部通过。

## R04：使用已有软件控制器做端到端 API 检查

- `tools/MotionControl.VirtualAdapter.Checks/Program.cs`：增加 `--controller-api <端口>`，仅通过 `VirtualMotionCard` / `IAxis` 调用控制器 API，依次进行连接、使能、回零、绝对定位、相对定位、点动、拒绝不支持的 Abort、Controlled 停止、停稳反馈、断使能、断开连接。每次异常收尾都尝试停止测试轴并释放租约。
- `tools/Verify-ControllerApi.ps1`：在 Windows 构建并启动**仓库已有** `MotionControl.VirtualController.exe`，随机选择本地隔离端口，先只读核对服务身份及协议版本，运行上述 API 检查，再核对轴已停止并仅清理本脚本启动的进程。
- `.github/workflows/windows-checks.yml`：在原有 V1.8 内部检查后执行上述 API 检查；即使旧检查失败也保留 API 检查结果。
- 不修改控制器仿真轴的物理运动模型，也不额外创建假运动卡或假控制器。软件控制器简化停止物理过程（命令处理为瞬时速度清零），本检查**不证明 PLCopen MC_Stop 减速斜坡**，不证明厂商 SDK 或实物机械制动。

## PLCopen 单轴状态的可用数据与缺口

| PLCopen 概念 | 当前项目事实 | 本轮处理 |
| --- | --- | --- |
| 伺服使能、回零、绝对/相对定位、点动 | `IAxis` 已有对应异步方法，适配器读 `AxisStatus` | 直接复用，不复制接口 |
| 轴是否在动、速度和位置 | `AxisStatus` 提供 `Moving`、`Velocity`、`Position` | 复用唯一停稳谓词，不用纯布尔断定安全 |
| Disabled / Standstill / ErrorStop | 仅有使能及报警等部分证据，不代表完整 PLCopen 状态机 | 暂不推出伪状态 |
| Homing / DiscreteMotion / ContinuousMotion / Stopping | 当前通用快照缺少全部已受理动作的类型及可靠的执行状态数据 | 不从文案、`Moving` 推算，待厂商 API 提供可信依据 |
| MC_Stop / MC_Halt / Abort | 项目目前只有 `AxisStopMode.Controlled/Abort`，与 PLCopen FB 语义并非一一映射 | 维持真实能力边界，不声称认证 |

下一阶段的重点是查证 ZMotion SDK 能否提供可靠轴状态与运动命令执行阶段，不能为了填满 PLCopen 枚举而假造数据。C# 工位与任务列表流程的控制权判定沿用 `MachineRuntime`，先核对再减少确证重复逻辑，不强行合并调度器。

## 验证与合并边界

本批已提交源码、脚本和中文说明；**Windows CI 实际结果以 GitHub Actions 运行记录为准**，未完成时不得写作通过。本批不进行真实控制卡、ZDevelop 屏幕或正式 Host 的人工 UI 验收；这三者仍须单独记录。

受保护布局由第一批 `Verify-ProtectedUi.ps1` 持续检查，但 XAML 字节一致并不代替交互与动作门禁回归。

修改范围不含 `MainWindow.xaml` 或四个受保护页面的 XAML / code-behind；所有新增代码要求 C# 初级工程师能顺序阅读，方法与成员保持中文注释。
