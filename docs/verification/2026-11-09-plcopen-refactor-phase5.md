# PLCopen 重构第五批：轴反馈可信度、命令完成与异常停止

日期：2026-11-09  
上游：`codex/plcopen-refactor-phase4-20261109`，基线 `2d8b000d6a9714055b35ca53d9edfb283381e686`  
本批工作分支：`codex/plcopen-refactor-phase5-20261109`

## 前置结果

第四批 Windows CI：[42630493355](https://github.com/hefan1/MotionControlFramework/actions/runs/42630493355) **Success**，受保护 UI 检查、正式 Host 构建、15 个内部检查项目和使用仓库已有独立软件控制器的 API 联调均通过。此结果不代表正运动 SDK、实体电机或安全回路验证。

## PLCopen 依据与本批边界

参考 PLCopen Motion Control Part 1 V2.0（官方原文：<https://www.plcopen.org/system/files/downloads/plcopen_motion_control_part_1_version_2.0.pdf>），运动功能块需要区分命令完成、被中止和错误；轴速度为零也不等于整个功能块执行成功。**本项目 C# 的 `Task` 返回语义与 PLCopen FB 并非一一对应，本轮没有增加 MC_* 接口，也不宣称已经实现完整 PLCopen 轴状态机。**

## R09：统一停稳反馈事实来源

- **原现象**：Runtime 有 `AxisStopFeedback.IsConfirmedStopped`（必须同时满足 `Moving=false` 和有限近零速度），但 Virtual 与 ZMotion 适配器部分等待路径只判断 `!status.Moving`，可能误把不可信速度认作停稳。
- **实现**：在 `AxisStatus` 增加 `HasStoppedMotionFeedback()`，使用项目既有阈值 **0.001 轴工程单位/秒**；校验状态未运动、速度不是 NaN/Infinity 且绝对值不超过阈值。
- `Runtime.AxisStopFeedback` 保留旧公开方法供已有业务调用，内部委托 `AxisStatus.HasStoppedMotionFeedback`，删除第二份重复判定规则。Virtual/ZMotion 适配器直接使用同一入口，不反向依赖 Runtime。
- **含义**：这只是软件看到可信停稳反馈，不证明 PLCopen Standstill、伺服安全、机械制动、消除报警或紧急停止完成。

## R10：运动命令完成时拒绝无效反馈和异常状态

- `VirtualAxis.StopAsync`：Controlled 停止发送后，不再只看 `Moving=false`，还必须得到有限近零速度；否则明确失败。
- `VirtualAxis.WaitUntilStoppedAsync`：定位和回零完成前要求有效速度、停止标记、有效位置及伺服使能，并继续核对目标容差、回零标记和已有报警处理；NaN/Infinity 不能因数值比较的特殊规则被误判 Done。任何反馈失败继续沿原有异常分支尝试使用独立令牌停车。
- `ZMotionCard.WaitForAxisMoveCompletedAsync`：成功到位同时满足真实停稳反馈、指令位置和实际位置容差；报警、伺服断使能、无效速度/位置/指令位置立即报错，绝不报告 Done；停稳标志和速度不一致时不会先判 Done。
- `ZMotionCard.WaitForAxisIdleAsync`：只有有限近零速度与停止标志一致才能确认停稳。未知反馈保持等待直到超时，不当作成功。
- `ZMotionCard.WaitForHomingAsync`：控制器 HOMESTATUS 报告成功且 idle 之后，还必须读取实际轴反馈，确认停稳速度有效、伺服仍使能且无报警，才允许标记回零成功；校验失败继续走现有回零停车收尾。
- `ZMotionAxis.WaitForCompletionAndStopOnFailureAsync`：此前只对取消/超时执行受控停轴。由于运动命令可能已被控制器接受，改为对完成等待期间的异常（含报警、状态读取失败和无效反馈）都尝试受控停轴；若停止本身失败，以 `AggregateException` 保留最初运动错误和停止错误，不能把后台运动当作已安全停止。
- `MachineTaskDevice.IsMoveComplete`：任务层在沿用原有报警、位置、速度与回零核对的同时，新增伺服使能检查。否则控制器报告“轴已停止且位置在容差内”时，可能把失能的轴错误记成任务 Done。

### 兼容性

- 原 `IAxis`/ `AxisStatus` 构造签名和已有页面未变；仅增加一个短小、可直接查阅的反馈方法。
- 任务设备原有公开方法签名不变，只增加一项有意义的动作完成保护检查。
- 此次故意收紧成功条件。原本可能将异常反馈误认作成功的调用，现在返回具体异常；业务不应依赖原错误行为。
- `AxisStopMode.Abort` 不等同 PLCopen MC_Stop，也不替代硬件急停。

## R11：使用已有软件控制器 API 验证反馈失败，不创建模拟轴

在现有 `MotionControl.VirtualAdapter.Checks` 的 `--controller-api` 分支增加：

1. 通过真正的 `VirtualMotionCard/IAxis` 进行回零与定位，从控制器读取 `AxisStatus`，确认停止时 `HasStoppedMotionFeedback` 返回真，点动时返回假。
2. 使用**已有独立软件控制器协议的单次 `setCommandFault`** 注入 `readAxis` 被拒绝：定位开始后读取失败时应报告错误并尝试停轴，随后从控制器 API 再次确认停稳。
3. 再次点动后，停止命令已发出但 `readAxis` 回读被拒绝时，`StopAsync` 不得报告确认成功；随后的控制器真实回读必须能确认已停稳。
4. 复用第四批的软件控制器连接/重连及 Runtime 持权检查，不新增 FakeAxis、内存模拟轴、第二个控制器或虚假的运动状态。

测试期结束仍须逐轴确认已停稳并释放本轮控制器进程；故障注入仅在隔离测试控制器上执行，不得向现场实卡发送。

## UI 保护与验收边界

- 四个锁定页面：轴映射、IO 映射、轴手动、IO 监控，其 XAML 和后台事件不变。
- 主窗口顶部、左侧、底部布局固定，`MainWindow.xaml` 不改。
- CI 静态 Blob SHA 检查继续有效；正式 Host 屏幕交互验收和厂商 SDK / 实卡检查仍未由本批执行。
- 第五批尚未取得 Windows CI 结果时只能标记**已提交待验证**，不能写作全部通过或合并 `master`。
- 若后续需完整映射 Homing、DiscreteMotion、ContinuousMotion、Stopping、ErrorStop 等 PLCopen 轴状态，必须先核对每个控制器能可靠提供的状态来源及正在执行的命令凭证；不靠 `Moving` 猜出一套状态机。

## 首轮 Windows CI 实际结果及回归诊断修正

[第五批首轮运行 42632995273](https://github.com/hefan1/MotionControlFramework/actions/runs/42632995273) 的结果为 **failure，不能声称整批通过**。

- 正式 Host 构建成功；受保护 UI 基线检查成功；现有软件控制器的 `--controller-api` 实际完成定位、点动、停止、一次性 `readAxis` 故障注入、反馈拒绝和停稳复核，**该 API 步骤成功**。
- 15 个内部检查项目中 14 个通过；`MotionControl.TaskEngine.Checks` 的既有 `ResourceInterlocks` 用例在暂停后检查资源占用时出现 `NullReferenceException`，堆栈位于 `ExecutionChecks.Interlocks.cs`。这是内存任务逻辑的并发/时序验证，不涉及实际控制卡；**尚不能把它判定为框架实际资源被错误释放，也不能当作无关小错误跳过**。
- 为明确原因，测试现等待“正文完成且退出互锁进入 Waiting”后再核对资源。暂停后仍要求资源记录存在且 `State == Held`；异常时显示运行状态和资源条数，不再因为访问空记录丢失根因。此改动没有新增内存轴/模拟控制器，也没有放宽资源互锁或生产安全代码。
- 本批代码是否可以合并，仍以更新后的完整 Windows CI 为准；即使 CI 通过，也不替代正式 Host 屏幕、真实 ZMotion SDK 或实物安全验收。


## 后续完整回归与测试收尾修复

[复验 42634159699](https://github.com/hefan1/MotionControlFramework/actions/runs/42634159699) **未完成而取消**：受保护 UI、正式 Host 构建、任务引擎（23+58+28 组）、真机任务设备（43 组离线检查）、任务编辑（26 组）等已完成的检查中未再出现资源互锁空引用；但 `TaskMonitor.Checks` 在执行“等待诊断的中英文说明”时卡住，最后达到 30 分钟限制取消。故不得将整批报告为通过，新增控制器 API 步骤在该次运行中并未执行。

此次只修改 `tools/MotionControl.TaskMonitor.Checks/Program.cs` 内部测试：`CloseTestSession` 将 `machine.CloseAsync()` 的调用本身移到线程池，WPF STA 线程用有界 Dispatcher 消息泵等待其完成（15 秒，超时明确失败）；解决关闭操作在第一次异步等待前同步阻塞时无法进入超时循环的问题。同时为双工位等待、未知采样、英文显示、请求复位和会话关闭增加输出阶段标记。如果再次出现挂起，按最后打印的阶段定位，不跳过任何断言；**此次改动尚待新 Windows CI 证实有效**。

## 关于软件控制器替代实体 ZMotion 验证的决定

用户明确要求本阶段以软件控制器代替实体运动卡做开发和测试；接受此工作流，**当前不要求提供 ZMotion 实卡**。仓库已有的 `MotionControl.VirtualController` 通过 `VirtualMotionCard` API 覆盖软件联调；如需验证 `ZMotionCard` 的 SDK 调用链，应使用能够由原厂 ZAux API 连接的 ZMotion 软件控制器/仿真运行时，验证的是“原厂 SDK 软件调用链”，不是物理卡。若没有相同 API 的仿真运行时或相应原生 DLL，则必须如实标注“ZMotion SDK 调用链未验证”，不能拿 Virtual 适配器的成功冒充 ZMotion 适配器成功。机械、电机、现场安全回路等实体风险仍不属于软件控制器覆盖范围。
