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
- 首次交付时的状态：[PR #12](https://github.com/hefan1/MotionControlFramework/pull/12) 转待审查，ROC-18/23/24/28 更新 In Review；当时尚未合并 master。正式屏幕走查仍留在 ROC-13。合并后的继续处理见下文。

## 合并后追加：来源观察检查的间歇性停顿

PR #12 已合并为 `1b432afe`。合并前集成提交 `a353c2cf` 的 PR/push 两轮完整通过，但 [master 运行 42757120979](https://github.com/hefan1/MotionControlFramework/actions/runs/42757120979) 中任务编辑第 35 项停顿并被原 180 秒守护记录为失败；其余 14 个项目、操作页面 69 组及软件控制器 API 通过。ROC-18、ROC-23 已重新进入进行中，不能沿用之前的最终全绿结论。

### 诊断任务：分清原计时器、同步刷新和 Dispatcher 的停点

**范围：** 只改 `Roc18TaskSourceContextChecks.cs` 的离线观察设施和 `Program.cs` 的定向重复入口；产品文件、原文件共享方式、原 25ms Background 计时器、2.5/5 秒条件和所有业务断言保持不变。

- [x] 读失败日志及产品/测试调用链：正常后台读取没有被 UI 同步等待；恒真正控也不等待后台结果，不能把挂起直接归因于文件共享。
- [x] 分别记录计时器启动、进入消息泵、轮询进入、同步刷新返回、退出请求及 Dispatcher 操作起止。
- [x] 用独立后台诊断线程在原期限失守后先打印被动计数，再投递一次 Send/Background 探针；被探针唤醒必须仍记失败。
- [x] 在同一进程保留原 35 项顺序，再只重复已出问题的可写句柄场景 10 轮；现有 180 秒单进程守护保持。
- [ ] 根据 Windows 日志定位具体停点后制定最小修正，不增加等待时间或依靠重跑覆盖失败。

诊断只操作本次离线检查线程，不暂停线程、不控制正式 Host，不把内部窗口当作正式屏幕验收。诊断代码在证据明确后整理或移除；正式修复仍需独立复核、完整 Windows 回归及中文记录。

### 第一轮取证结果与下一项判别

诊断提交 `bf4cc2c6` 的 [push 运行 42759238126](https://github.com/hefan1/MotionControlFramework/actions/runs/42759238126) 全部通过，但 [PR 运行 42759257852](https://github.com/hefan1/MotionControlFramework/actions/runs/42759257852) 再次在原第 35 项停顿。轮询进入、同步刷新返回、轮询退出均为 32 次；调度开始/结束为 112/112。期限失守后 Send 与 Background 探针均立即执行，计数变为 114/114，但原轮询不再触发，最终由原 180 秒守护记录失败。

这说明取样时没有卡在同步页面刷新内，也不支持持续高优先级队列饥饿。下一诊断仅在 UI 线程读取测试/页面计时器的启用状态、待执行操作、到期值、Dispatcher 登记集合与缓存到期值，同时记录实际 WPF 文件版本和 Tooltip 默认时长。首次期限失守后的调度操作开始时先取一份快照，再在 Send 委托内取第二份，避免把探针执行前的到期扫描遗漏掉。私有字段不存在时明确记录，不推断为零，不调用任何框架私有方法。

待验证候选包括：计时器被停用、重新登记遗漏，以及极长 Tooltip 计时器与短轮询到期比较发生有符号回绕。当前没有快照支持其中任何一个结论；不据此修改生产逻辑或宣称 WPF 框架缺陷已经证实。

### 第二轮取证后：用可控前提验证候选

提交 `972d9fd6` 的 [PR 运行 42760946373](https://github.com/hefan1/MotionControlFramework/actions/runs/42760946373) 和 [push 运行 42760939422](https://github.com/hefan1/MotionControlFramework/actions/runs/42760939422) 都通过，没有捕获新的停顿。日志确认 WindowsBase / PresentationFramework 文件版本为 `4.8.9363.0`，来源说明控件的实际 Tooltip 时长为 `2147483647ms`；正常关闭上一窗口时确有 `PopupControlService` 的短时清理计时器。一次偶发通过不排除已有失败，也尚不能证明原停顿由 Tooltip 造成。

下一提交先补两项会失败的检查，不加生产修复：

1. 在进程最前使用真实 DispatcherTimer，先使 25ms 短计时器逾期，再登记由实际控件 Tooltip 时长生成的 Normal 计时器。短 Tick 必须在两秒内执行，独立线程池守护只能退出并记录失败；清理全部测试计时器和迟到探针，避免污染后续检查。这验证极值到期调度机制，不声称已经复现正式 Host 的鼠标交互。
2. 仅在一个新页面注入观察运行中状态，使本次实际后台读取不启动；原固定等待入口必须拒绝通过。只接受明确的“未发布观察结果”断言，其他异常不能算负控通过。此时保留旧入口以先取得缺口的失败证据，再实现两轮同工作区、同代次、无读取错误的 UI 发布见证。

### 已确认根因后的实施与验证

`c3ada3dd` 的 push 运行 `42761942117` 中两项新增回归明确失败，其余 45 项通过。PR 运行 `42761950372` 又在原来源未变场景停顿，现场同时取得已逾期的测试/页面计时器、极长 Tooltip 计时器的 `PopupControlService.OnShowDurationTimerExpired` 回调，以及指向该极长计时器的 Dispatcher 缓存。详细数值保存在验证记录；这次现场已把原问题与极值到期机制连接起来。

- [x] 独立源码核对与真实 Windows 红态确认极值调度机制和固定等待误通过。
- [x] 按 `docs/superpowers/specs/2026-11-10-roc18-timer-compatibility-design.md`，用共享启动入口修正极大默认时长；Host 实例构造和两个检查入口在控件创建前调用。
- [x] 两项来源正控必须见证本窗口两次合格 UI 发布；负控只接受精确的缺失发布异常，Loaded 采集异常单独报错。
- [x] 清理临时私有 WPF 快照、调度钩子与十轮重复，保留明确的极值行为回归、显式样式保护和独立八秒失败退出；普通五秒等待不变。
- [x] 生产修复 `14e5e279` 的 PR/push 两轮 Windows 回归通过：任务编辑 38/38、操作页面 69/69、内部项目 15/15、Host、保护布局及 Virtual API。
- [x] 独立整份差异复核未发现生产阻塞项；对五秒后才见证的结果可能被接受这一检查边界，增加期限前后核对及 5200ms 延迟轮询回归，组数为 39。
- 最终候选复核、39 项 Windows 结果、合并后实际主分支验证及任务关闭状态，统一跟踪在 [PR #13](https://github.com/hefan1/MotionControlFramework/pull/13) 与 ROC-18/ROC-23 最后 TODO，避免把本文提交时的阶段记录当作后来已完成的验证。

实施裁定：兼容方法通过公开元数据 API 修改系统极大默认值，目标为一天；保留旧有限默认值和显式样式。本项会影响默认 Tooltip 的持续显示上限，不宣称所有提示行为完全不变，正式画面留在 ROC-13。共享入口不接触运动、连接生命周期或已有报警恢复证据。


### 最终集成追加：任务监控独立入口

`5a54c2af` 的 PR Windows 已完整通过 39/39、69/69、15/15；同 SHA 的 push 在 TaskEditing 39/39 通过后停于 TaskMonitor 的操作按钮场景，实时日志末尾为等待诊断会话已关闭。没有该停点的堆栈证据，不把它直接归因为同一 Tooltip 问题。

追加范围为 TaskMonitor 检查 Main、现有场景阶段日志与 Verify-V18 的原单 PID 180 秒守护。独立程序必须在控件前显式调用与 Host 相同的兼容入口；该程序集未构造 Host.App。现有业务断言、内部等待期限及产品源码保持，不扩展到其他未经证实的设备流程。

- 检查入口补齐由独立实施代理负责；检查脚本和中文证据由总协调维护，避免并发暂存。
- 小差异单独复核；结果待新候选 Windows 执行，不能沿用前一个 SHA 的通过状态。
- 完成后仍以实际 master 工作流和 Linear 最后 TODO 为交付关闭依据。


### ROC-29：整套回归的临时记录收尾

PR #13 已合并为 `23cbeb8`。最终候选两轮全绿；实际主分支的任务编辑 39/39、任务监控 5/5、操作页面 69/69 和 Virtual API 继续通过，但 MachineDevice 清料用例的 finally 删除临时记录遇到占用，整套结果为 14/15。

后续范围限定于 `PendingClearanceCannotRecoverAsync` 的 5 秒测试预算、显式停止超时见证、成功/失败目录收尾及真实文件句柄异常保留回归。原 100ms 同时限制启动持久化，finally 可覆盖首因；生产默认 30 秒和产品资源恢复逻辑保持。复用既有检查设施，不新增轴替身。

独立实施代理只改 `ClearanceLeaseChecks.cs`，总协调维护中文证据与集成。先保留实际失败证据，再复核小差异、执行新候选完整 Windows 回归并合并；实际 master 通过后关闭 [ROC-29](https://linear.app/roc123/issue/ROC-29) 和 ROC-18 最后 TODO。不得沿用 `23cbeb8` 的 14/15 结果作全绿交付。
