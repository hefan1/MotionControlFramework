# PLCopen 架构重构第四批：任务执行与运行时异步交接的会话隔离

日期：2026-11-09  
上游分支：`codex/plcopen-refactor-phase3-20261109`，基线提交 `1337ff5a2aa13ff14bbe6af24d3ca080023a6abf`  
本批分支：`codex/plcopen-refactor-phase4-20261109`

## 已确认的前置状态

第三批 GitHub Actions [42627488610](https://github.com/hefan1/MotionControlFramework/actions/runs/42627488610) 最终为 **success**：受保护 XAML 检查、正式 Host 构建、全部内部检查，以及仓库已有独立软件控制器的实际 `IMotionCard` / `IAxis` API 检查均通过。此前 42618930617 曾因过时测试调用编译失败，WPF 离屏测试还被 30 分钟超时取消，后续已修复并以新运行结果为准，不应把旧失败记录抹掉。

## 代码审查发现的职责与风险

框架有两种自动流程入口，其职责需要保留：
- `MachineRuntime`：设备连接身份、全机控制权、急停与报警、停止状态的权威来源；
- `TaskMachineController`：任务定义、各工位步骤、任务故障和动作收尾；
- `TaskMonitorLauncher`：从操作员按钮触发任务命令，并把任务结果提交给 Runtime。

它们**不应合并为一个巨大控制器**。实际风险是异步调用发起后，连接或任务会话可能改变，但部分任务页代码继续使用可变的 `_runtime`、`_session` 字段。旧会话的迟到暂停、复位、停止结果因此可能针对新连接提交。另一个问题是任务侧 `PauseAsync` 失败时，旧页面路径未明确通过原连接向 Runtime 锁存故障，容易造成任务侧故障和整机显示状态不一致。

## R07：Runtime 正式任务完成接口带原连接和所有者身份

修改 `src/MotionControl.Framework.Runtime/Machine/MachineRuntime.TaskEngine.cs`：

- 保留既有 `FinishTaskEngineRunAsync(bool, Exception)`，不破坏旧源码调用。
- 增加 `FinishTaskEngineRunAsync(Guid expectedSession, object deviceOwner, bool faulted, Exception error)` 供正式任务使用，复用原有 `EnsureTaskDeviceBinding`，不新建权限管理器。
- 在停止急停监控前、完成异步监控收尾后分别核对连接及持权设备。不匹配时保守拒绝，**不把旧任务的完成结果写入当前运行时**。
- 这是一项 C# 上位机运行时的会话安全约束，并不等同 PLCopen `MC_Stop` 或 `MC_Halt` 功能块，也不取代任何硬件安全回路。

修改 `MachineRuntime.cs` 的任务执行入口暂停：在原 TaskEngine 受控暂停前捕获 Runtime 连接 ID 和持权实例，后续完成与故障收尾都使用新增带身份的结束接口；原异常和无法完成收尾时的异常一起保留。

## R08：任务页面异步操作不读取换代的可变上下文

修改 `src/MotionControl.Host.Wpf/Tasks/TaskMonitorLauncher.cs`：

- 增加小而明确的 `EnsureCurrentTaskCommandContext` 方法：按发起时保存的任务对象、Runtime 对象、设备对象、连接会话和 `_machineContextVersion` 核对操作归属。
- 顶部按钮和任务页面的复位原先存在两套基本相同的顺序。合并为 `ResetTaskCoreAsync`，复用原有“先校验任务条件 → 按需复位 Runtime → 复位任务”的顺序，每个异步阶段返回后复查上下文。
- 启动命令在 Runtime 建立安全状态后、真正启动任务前再次确认会话；若失败，向**原 Runtime 和原设备**提交故障，而非采用新的可变字段。
- 暂停命令捕获原任务、Runtime、连接。如果任务受控停轴抛错或结果仍为 Faulted，使用原会话将 Runtime 锁定为 Alarmed，并保留原始停止错误与失败收尾错误；不报告假成功。
- 停止命令首先允许旧任务尝试终止自身运行，即使刚发生失联；只有向 Runtime 提交结果或执行正式关闭前，才要求原连接仍有效。若无法证明一致，则保留现场与恢复证据，不转移到新设备。

## 真实软件控制器 API 回归

修改已有 `tools/MotionControl.VirtualAdapter.Checks`，**没有新增 FakeAxis、InMemoryAxis、内存运动设备或新控制器进程**：

- 复用 `Verify-ControllerApi.ps1` 已启动的独立 `MotionControl.VirtualController`，经 `VirtualMotionCard` / `MotionCardGroup` / `MachineRuntime` 真正连接并重连。
- 验证错误设备所有者和旧连接 ID 的任务完成回调被拒绝，正确身份仍能正常收尾。
- 原动作验证轴 1 与本轮运行时会话验证轴 2 均在测试后通过控制器 `readAxis` API 核对停稳。会话用例不向轴 2 发运动命令。
- 实际运行效果须以本批 Windows CI 成功与具体输出为准，代码和脚本已提交不代表测试已通过。

## 四个受保护页面与固定主窗口

本批不修改 `AxisMappingConfigurationView.xaml`、`IoMappingConfigurationView.xaml`、`ManualAxisControl.xaml`、`IoMonitorView.xaml` 或 `MainWindow.xaml`，也不修改这些页面的 `.xaml.cs`。原有顶部、左侧、底部骨架不动。已有 `Verify-ProtectedUi.ps1` 持续按 Blob SHA 检查五个布局文件，但**文件未变化仍不等同正式 Host 屏幕验收**。

## 已知范围与后续步骤

- 两条流程执行器仍独立；`MachineRuntime` 负责整机安全准入和持权，`TaskMachineController` 负责任务内部动作收尾，避免重复建设第三套状态机。
- 本批主要改善异步提交时的身份复核与代码复用，并没有实现完整 PLCopen 单轴状态机，也没有适配真实正运动、雷赛或 ADLINK 实卡。
- `FinishTaskEngineRunAsync(bool, Exception)` 为兼容调用仍保留，不应再在正式 Host 的新任务操作中使用。
- 最终验收仍要求 Windows 完整构建、已有内部检查、实际软件控制器 API 验证，以及后续独立的正式 Host 屏幕/断连/重连场景验证；CI 通过不代表实物设备通过。
- 后续如需修改受保护页面操作，请独立提出理由并取得明确同意，不能靠架构重构顺带改变按钮、布局或门禁。
