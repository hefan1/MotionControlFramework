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

- [ ] 针对已确认红态实现等待后的来源与代次核对，旧清理只针对捕获对象。
- [ ] 同一已发布有效连接复用原 Runtime；发布时确保任务入口、主窗口与变量/工位页面同源。
- [ ] 关闭后确认仍为本次已断开上下文；旧结果不关闭新设备。
- [ ] 执行完整 Windows 检查与 Virtual API 回归，解决实际失败，不重跑掩盖故障。

## 任务 3：集成与交接

- [ ] 独立差异复核并修正真实问题，确认上述五项复核重点。
- [ ] 中文提交、PR 与验证记录同步远端；合并前核对实际候选 SHA。
- [ ] 验证实际 master，再更新 ROC-19 清单；ROC-13 保留正式窗口待办和工程资产准备缺口。

## 当前进度

2026-11-11：已完成职责盘点；三个仅测试提交累计取得七条连接来源、两条关窗真实产品 RED。第三轮正常关窗通过，缺少断连保持能力的 fixture 前提已经修正；编译、保护布局和 Virtual API 通过。任务 1 完成，连接来源和在线关窗修复在本地实施中，尚未取得 GREEN。

独立静态评审要求补齐未发布 Runtime 停监控失败时的对象保留：先追加真实 finally 路径回归，再确保下一次同步或退出仍能找到具体 Runtime 和历史，监控未确认停止前不关闭其历史。该回归针对初版修复的清理路径，不与未修改生产源码的第三轮 RED 混淆。

正式 Windows 窗口验收仍由 ROC-13 单独记录。用户此前已授权继续修改、推送与合并，按既定范围执行，无需重复请求许可。
