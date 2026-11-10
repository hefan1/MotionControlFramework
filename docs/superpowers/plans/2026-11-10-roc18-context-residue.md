# ROC-18 设备与工艺页面来源残留排查实施计划

> 执行方式：总协调在本会话按 `superpowers:executing-plans` 实施；两个独立代理先分别审查页面与任务来源并编写互不重叠的回归，最终由新代理复核完整差异。

**目标：** 完成 14 个主页面的来源盘点，修复有代码和失败回归证据的旧检查结论、旧准备错误残留；不把配置文件保存误认为运行会话已经切换。

**架构：** 保留现有 Host、Runtime、任务工作区及页面结构。任务配置使用现有完整来源签名，后台串行观察其他任务文件变化，并在发布结果前核对工作区和观察代次。设备准备页只清理不再属于当前上下文的页面提示，Runtime 的报警、恢复及安全记录保持原有职责。

**技术：** C# 7.3、.NET Framework 4.8、WPF；已有 Windows CI 与检查项目；中文注释。

**规格依据：** Linear ROC-18 原始目标及验收清单、仓库 `AGENTS.md`、`docs/verification/2026-11-08-v18-context-isolation.md`、`2026-11-09-v18-task-session.md` 和 `2026-11-09-v18-progress-handoff.md`。历史验证记录不能代替本轮验证。

## 全局约束

- 基线：`8e318ea381ce80595fd9ba20dd7be97b6d53b810`；独立分支 `codex/roc18-context-residue-20261110`。
- 不修改轴映射、IO 映射、轴手动、IO 监控四个已保护页面及 MainWindow 的 XAML/code-behind，不扩展导航骨架。
- 每个新增/修改的类型、成员和方法提供中文注释，优先清楚的具体逻辑。
- 不新增控制器、FakeAxis 或内存运动模型。生命周期故障注入只说明内部逻辑；运动回归使用已有软件控制器 API。
- 工程应用当前要求安全断连并重启；任务 Apply 要求先断连。本轮不改成在线热切换。
- GitHub 独立分支、PR 和 Linear 更新沿用本会话既有授权，不直接推送或合并 master。

## 审查重点

- 同一 Runtime/同名工位但准备版本已换：旧错误不进入新范围，当前失败仍保留。
- 旧异步准备失败在换 Runtime、重连或换准备版本后返回：不把旧设备异常写到当前页，仍提示重新核对。
- 任务源在准备文件不变时被替换、增删或读取失败：旧检查结论失效，未保存编辑不丢失。
- 来源没有变化、人工检查刚完成、页面离开或工作区重载：不反复清空有效结论，不接纳旧轮询结果。
- 读取成本与来源语义：后台串行、适当节流；保存文件、已加载文件快照、草稿和当前执行会话分别表达。

## 任务 1：来源盘点与边界登记

**文件：** `docs/verification/2026-11-10-roc18-context-residue.md`。

- [x] 读取 ROC-18、最新 master、AGENTS.md 和既有 V1.8 记录。
- [x] 并行核对 14 页的来源、选择/缓存、换源入口和已存在的保护。
- [x] 汇总四类身份：编辑草稿、已保存文件、已载入会话、设备实际反馈。
- [x] 将具体缺口登记为 ROC-23、ROC-24、ROC-28；App 连接同步重入风险已在 ROC-19 记录，正式屏幕验证交由 ROC-13。

## 任务 2：任务文件变化使旧检查结论失效

**生产文件：** `src/MotionControl.TaskEngine.Wpf/Editing/TaskConfigurationView.Preparation.cs`、`TaskConfigurationView.xaml.cs`、`TaskEditorView.xaml.cs`；对应中文/英文资源；`src/MotionControl.TaskEngine/Editing/TaskWorkspaceStore.cs` 增加不占写权限的观察签名。

**回归文件：** `tools/MotionControl.TaskEditing.Checks/Roc18TaskSourceContextChecks.cs`、同项目 `Program.cs`；复用已有临时 Workspace。

**接口：** `TaskWorkspaceStore.ComputeSignature(string machineId)` 保留原严格读取，新增 `ComputeObservationSignature` 仅供后台观察；沿用 `ComputePreparationSignature` 和原 `RefreshPermission`/Loaded/Unloaded，不改变存储层 Apply 的冲突检查。

- [x] 在未修改生产实现的分支上，验证替换任务、改 TaskMachine、增删任务后旧结论仍残留的失败断言；同时覆盖文件读取失败、草稿保留及无变化正控。
- [x] 保留准备文件的即时观察；其他完整来源每约 2 秒在后台串行读取。完整来源变化或读失败/恢复时清旧检查，显示明确冲突/未知提示。
- [x] 每次人工检查、重载、同步、应用或离页使在途轮询失效；结果只有在同工作区、同观察代次下，经页面 Dispatcher 才能更新控件。
- [x] 原准备引用检查与新增回归全部通过；不增加后台设备读写。

## 任务 3：准备页错误绑定原设备与准备范围

**生产文件：** `src/MotionControl.Host.Wpf/Operation/EquipmentPreparationView.xaml.cs`。

**回归文件：** `tools/MotionControl.OperatorPages.Checks/PreparationContextResidualChecks.cs`、同项目 `Program.cs`。

**接口：** 使用已有 `MachineRuntime.ConnectionSessionId`、`ControlOwner`、`PreparationConfigurationVersion`；保留命令准入与安全观察版本校验。

- [x] 原实现失败回归：同 Runtime 新准备范围不能沿用旧错误；迟到失败不能污染新 Runtime、新连接、新准备版本。
- [x] 保留同上下文错误跨普通刷新；上下文实际改变时清旧摘要和诊断，保留 Runtime 报警/恢复信息。
- [x] 异步命令捕获原身份，返回时只显示当前身份的错误；过期操作保留 Trace 诊断输出并显示当前范围需重新核对的提示，不复用旧设备错误文字。
- [x] 覆盖 StopPreparation 的异常返回；保持现有“范围改变须重新选择”回归与按需装载准备行为。

## 任务 4：总览只展示当前有效发布的任务工位

**生产文件：** `src/MotionControl.Host.Wpf/MainWindow.Tasks.cs`、`Tasks/TaskMonitorLauncher.Overview.cs`、`Operation/EquipmentOverviewView.xaml.cs`；最后一项用于独立核对保留资源证据的来源。

**回归文件：** `tools/MotionControl.OperatorPages.Checks/BoundTaskLoadChecks.cs`，复用真实 Host 组合及已有准备换代流程。

- [x] 有效会话可展示工位；同 Runtime 准备换代后会话未确认，总览不得保留旧工位和定位入口。先观察原实现失败。
- [x] 总览身份提供器及工位快照使用 `CurrentPublishedTaskSession`，读取前后核对同一身份。
- [x] 不清除原任务的资源隔离、恢复与未确认安全诊断；不修改固定页面骨架。

**独立复核补充：** 为工位和保留资源证据分别提供身份；资源摘要须绑定当前总览 Runtime/连接，并在采样前后核对同一保留会话。不能因工位发布资格失效而隐藏仍需核查的警示，也不能把原会话摘要带到新连接。

## 任务 5：验证、复核与交付

- [x] 原基线 `python3 tools/Check-CommandBoundaries.py`：9 项通过。
- [x] 在同一独立分支通过 GitHub Windows CI 观察新增测试失败；记录对应 SHA、运行与具体失败原因。
- [x] 修复后运行 `Verify-ProtectedUi.ps1`、`Verify-V18.ps1` 和 `Verify-ControllerApi.ps1`，读取与最终代码相符的真实日志。
- [x] 独立代理复核完整差异，处理影响正确性的发现；`git diff --check` 通过。
- [x] 更新中文来源表、失败/成功证据、PR 说明、Linear TODO 与任务状态。
- [ ] 正式 Host 的 Windows 屏幕验收仍须可操作窗口和实际截图；本环境为 Linux，不能把 CI 控件测试记作目视验收。

## 裁定与进度记录

- 授权裁定：用户本轮要求继续执行 ROC-18，且此前已明确允许仓库修改、提交及 Linear 同步；不重复停下来请求流程确认。
- 范围裁定：本轮修复页级来源残留；App 连接创建/清理的并发重入属于 ROC-19，应记录证据而非同时扩展高风险设备生命周期重构。
- 验证裁定：本地 Linux 无正式 Windows WPF 桌面；使用现有 Windows CI 做实际构建及内部逻辑回归，正式屏幕与硬件验收继续单独标未完成。
- 复核裁定：后台观察改用允许读写/原子替换的专用读取共享方式，人工检查、同步、应用的严格签名入口保持原语义；专用观察与原签名在稳定文件下必须等价。
- 复核裁定：准备错误的四项归属使用命令捕获的原参数，不能在写入摘要后从当前环境重新采集身份；增加确定性 Message 读取换代回归。
- 验证闭合：代码提交 `793159aa818c5e7d442cf3c1f4025191933a6729` 的 [Windows 运行 42746247040](https://github.com/hefan1/MotionControlFramework/actions/runs/42746247040) 成功；Host、保护布局 5/5、内部项目 15/15、任务编辑 35/35、操作页面 69/69 及软件控制器 API 通过。
- 交付状态：[PR #12](https://github.com/hefan1/MotionControlFramework/pull/12) 已转待审查，ROC-18/23/24/28 已更新 In Review；尚未合并 master。正式屏幕走查仍留在 ROC-13。
