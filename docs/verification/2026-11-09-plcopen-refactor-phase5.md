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
- `ZMotionAxis.WaitForCompletionAndStopOnFailureAsync`：此前只对取消/超时执行受控停轴。由于运动命令可能已被控制器接受，改为对完成等待期间的异常（含报警、状态读取失败和无效反馈）都尝试受控停轴；若停止本身失败，以 `AggregateException` 保留最初运动错误和停止错误，不能把后台运动当作已安全停止。

### 兼容性

- 原 `IAxis`/ `AxisStatus` 构造签名和已有页面未变；仅增加一个短小、可直接查阅的反馈方法。
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
