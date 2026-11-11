# ROC-19 连接来源隔离实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox syntax for tracking.

**Goal:** 让连接同步、任务上下文和关闭收尾在等待期间来源变化时拒绝旧结果。

**Architecture:** 保留 App、TaskMonitorLauncher 和 HostShutdownOperation 的职责。采用具体对象快照、代次与等待后核对，不新增通用协调器或改变设备准入。

**Tech Stack:** C#、.NET Framework 4.8、WPF、既有 Windows CI 与 Virtual 软件控制器 API。

**Spec:** `docs/superpowers/specs/2026-11-11-roc19-connection-context-design.md`

## 全局约束

- 从 master be17f5c 的干净独立分支执行；原工作区 0320fe0 保持。
- 方法与新增成员写中文注释，保持初级 C# 工程师可跟踪的具体判断。
- 不改主窗口骨架、四个保护页面布局、厂商 SDK、资源恢复许可或生产停止期限。
- 离线屏障只控制等待；必须调用真实生产入口并检查实际页面绑定/Runtime身份。
- 当前没有可访问的正式 Windows Host；ROC-13 仍单独记录未执行。

## 复核重点

1. 心跳失联不进入同步入口：不能只检查同步代次。
2. 同卡重复通知：不丢任务、不重复创建 Runtime 或监控。
3. 旧失败清理：不能清空新报警服务或关闭新历史日志。
4. 清理失败仍有资源待核查：不能默认收尾成功或丢弃证据。
5. 正常关窗清空 Runtime 与新连接替换要正确区分。

## 任务 1：固定先后顺序的回归

文件：新增 `tools/MotionControl.OperatorPages.Checks/ConnectionContextChecks.cs`，修改同项目 `Program.cs` 注册；复用 `BoundTaskLoadChecks` 现有无轴设施。

接口：检查入口为 `BoundTaskLoadChecks.RunConnectionContextChecksAsync()`，不增加生产测试开关。

- [x] 写出旧上下文 setter 被断连撤销、旧断连等待后不得清空新 Runtime、同一连接通知幂等的行为断言；补齐能可靠固定先后顺序的其他入口。
- [x] 在生产源码未修改时执行 Windows 回归，保存具体失败标记；测试配置或编译错误不算红态。
- [x] 测试无论成功失败都释放自己的等待、等待真实收尾并保留原始错误。

## 任务 2：最小来源隔离修复

文件：`App.xaml.cs` 的连接同步区域、`App.Shutdown.cs`、`Tasks/TaskMonitorLauncher.cs`；若职责需要，可把连接同步原样移入 `App.Connection.cs`，不迁移其他职责。

接口：保持 `SynchronizeCardConnectionAsync()`、`SetMachineContextAsync(...)` 和现有退出公开/内部接口；新增核对方法为具体私有方法。

- [x] 针对已确认红态实现等待后的来源与代次核对，旧清理只针对捕获对象。
- [x] 同一已发布有效连接复用原 Runtime；发布时确保任务入口、主窗口与变量/工位页面同源。
- [x] 关闭后确认仍为本次已断开上下文；旧结果不关闭新设备。
- [x] 执行完整 Windows 检查与 Virtual API 回归，解决实际失败，不重跑掩盖故障。

## 任务 3：集成与交接

- [ ] 独立差异复核并修正真实问题，确认上述五项复核重点。
- [ ] 中文提交、PR 与验证记录同步远端；合并前核对实际候选 SHA。
- [ ] 验证实际 master，再更新 ROC-19 清单；ROC-13 保留正式窗口待办和工程资产准备缺口。

## 评审修正：未发布运行时的失败收尾

- [x] 独立审阅确认取消回调可在监控等待前抛错；新增真实 App finally 回归并完成静态测试评审。
- [x] 在初版候选实际运行该回归，区分产品断言失败与编译/场景配置错误。
- [x] 保留具体 Runtime、对应历史和原始停止 Task；停止成功后才移除并结束历史，失败后仍可重试。
- [x] 下一次同步在同源幂等返回前等待待收尾对象，并重新核对来源；正常关闭异步重试且不沿用等待前的设备，强制退出只同步等待原始 Runtime 停止任务，不等待 UI 续体。
- [x] 未完成对象的历史不进入已关闭历史队列，退出的全部正常交接许可均要求待收尾集合为空；保留原始错误和报警连续性限制。
- [x] Windows 全套与既有九条来源回归通过，完整差异独立复核关闭该 finding。

## 评审修正：真实 Closing 中的同步完成

- [x] 用实际窗口 Close 触发已绑定的 App Closing，证明 pending 重试同步完成时原实现会重入关闭；直接反射调用不作为该窗口事件的证据。
- [ ] 在重新 Close 前让原 Closing 返回，再复核来源；验证未处理异常消失且新连接仍不受旧关闭请求影响。
- [ ] 独立复核最小修改，取得完整 Windows GREEN 后进入最终集成。

## 当前进度

### 验证中发现的既有检查对象竞争

真实 Closing 的 RED 运行同时暴露 `MachineDevice.Checks` 的清料反馈超时。只读核查确认本检查点相关源码未改，并找到既有检查对象先发布 Moving、后创建完成任务的丢信号窗口。后续仅修正该检查对象的状态发布和本次完成任务捕获，使用原有 43 组及完整 Windows 检查验证；不以延长预算代替修正，也不把静态竞争认定为已凭日志证明本次超时的唯一原因。

2026-11-11 提交时快照：原九条来源与新增未发布监控都已完成真实红→绿验证。`a0d83fe1` / run `42943872813` 的 Host、15/15 内部项目、70/70 操作页面、布局与 Virtual/多控制器 API 全部通过。`b0bb763` / run `42945053437` 已通过真实 WPF Closing 复现重入异常；最小 Yield 修正已写入，最终候选与 master 验证待执行。本轮还出现一条未改动的清料检查反馈超时，原证据单独排查，不忽略失败。最终完成记录以 [ROC-19](https://linear.app/roc123/issue/ROC-19) 和关联 PR 为准。

独立静态评审要求补齐未发布 Runtime 停监控失败时的对象保留：先追加真实 finally 路径回归，再确保下一次同步或退出仍能找到具体 Runtime 和历史，监控未确认停止前不关闭其历史。该回归针对初版修复的清理路径，不与未修改生产源码的第三轮 RED 混淆。

正式 Windows 窗口验收仍由 ROC-13 单独记录。用户此前已授权继续修改、推送与合并，按既定范围执行，无需重复请求许可。
