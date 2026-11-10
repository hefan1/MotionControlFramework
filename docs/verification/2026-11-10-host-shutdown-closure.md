# Host 退出职责归位与问题关闭记录

基线：`e9912e32de74399e828c95e336a4a09f0514c8e2`（PR #10）。
本批：[PR #11](https://github.com/hefan1/MotionControlFramework/pull/11)，独立分支 `codex/host-shutdown-lifecycle-20261110`，不合并 master。

## 维护入口

| 文件 | 职责 |
| --- | --- |
| `src/MotionControl.Host.Wpf/App.xaml.cs` | 启动、连接和页面组合；断卡前核对原任务入口及连接归属 |
| `src/MotionControl.Host.Wpf/App.Shutdown.cs` | 关窗/退出事件、未保存内容确认、工程门、失败提示和退出字段；同一个 App 的 partial 文件，不是新执行层 |
| `src/MotionControl.Host.Wpf/HostShutdownOperation.cs` | 原设备退出门禁、准备/任务/工位停止、输出核验和恢复保存；失败只尝试撤回原 Runtime 的退出保护 |
| `src/MotionControl.Host.Wpf/Tasks/TaskMonitorLauncher.cs` | 识别未完成装载、已发布任务和关闭；专用退出复用原关闭交接，正常任务才请求终止 |

本轮不改 `IAxis` / `IMotionCard`、厂商 API、工程单位和运动完成判断，不增加程序集、抽象基类、泛型管线或反射生产装配。

## 关闭矩阵

| 编号 | 原问题与处理 | 验证证据 / 范围 |
| --- | --- | --- |
| H01 | 专用退出只判断 `_session`，后台尚未发布的装载可能被遗漏。现先走原 `CloseTaskSessionAsync` 等待同一次装载及回滚 | 旧实现检出 `PENDING_LOAD_CLOSE_RETURNED_EARLY`；修改后普通关闭、上下文切换和专用退出三种场景通过 |
| H02 | 原关闭失败后操作入口已撤销，专用退出重试却再次调用 Abort。现按 `_sessionClosePreservesBlocked` 重试原关闭模式 | 旧实现检出 `SHUTDOWN_RETRY_REENTERED_INVALID_OPERATION`；修改后实际释放原控制权，不用假成功替代关闭 |
| H03 | 关窗事件在多次 await 后继续读取可变 Runtime。现由具体退出对象保存原 Runtime、连接、卡实例与连接代次 | 延迟原退出准入并替换 App 上下文，旧处理准确拒绝；取消只解除原 Runtime，新 Runtime 仍保持退出保护 |
| H04 | 停止、输出核查、保存和窗口提示混在一起。现设备步骤由 `HostShutdownOperation` 承担，窗口和工程门留在 App | 边界检查禁止事件直接调用设备退出步骤；未停步及未知连接均不能核验保存 |
| H05 | App 退出及报警交接不能只看已发布会话。现统一使用 `HasUnsettledTaskSession` | 结构检查覆盖关窗、OnExit 和专用退出；行为测试覆盖在途装载、关闭失败与成功后清除 |
| H06 | 大型 App 文件混入退出方法和字段。现四个方法、五个字段原样归位 `App.Shutdown.cs` | 结构先失败再通过；完整方法块逐字比对、编译与三个专项再次通过 |

## 可核对的执行记录

1. [原生产源码红灯 42726513984](https://github.com/hefan1/MotionControlFramework/actions/runs/42726513984)：编译成功，准确观察前两个缺陷及独立协调类缺失。运行绿色是检查器确认了预期失败，**不是旧功能通过**。
2. [职责迁移完整验证 42727197451](https://github.com/hefan1/MotionControlFramework/actions/runs/42727197451)：三个专项通过；正式 Host 编译、15/15 内部检查、单/双独立软件控制器及任务操作协调 API 回归通过。结果源码提交 `031053421e68a21b9713411ee1f890062ee15b0f`。
3. [退出文件归位 42727934282](https://github.com/hefan1/MotionControlFramework/actions/runs/42727934282)：新增结构回归先失败；文件归位后 9/9 结构检查、Host 引用构建、三个专项及十个保护文件检查通过。提交 `9a8d3af031f4993bb6ddc37785a07f347bcd9d2c`。
4. 临时工具清理后的**最终常规 Windows CI**必须另外核对最终 SHA；最新 SHA、运行链接和完成结论记录在 PR #11，不能用上面候选运行代替。

操作页完整检查由 65 增至 67 组，新增两组为退出重试与原上下文保护；在途装载组增加专用退出场景。新增生命周期检查复用既有无轴设施，只证明 Host 软件时序，不声称控制器运动、厂商 SDK 或屏幕验收。运动回归仍调用仓库已有独立软件控制器，未增加 FakeAxis 或内存运动引擎。

## 保护与代码量

四个锁定页面与 MainWindow 的 XAML/code-behind 共十个文件相对基线不变。`App.xaml.cs` 在本批净减少 221 行；新 `App.Shutdown.cs` 为 262 行，具体 `HostShutdownOperation.cs` 为 131 行。主要收益是职责和查找入口清楚，不是全仓库总代码量减少。

临时接线/迁移/整理脚本用后删除，临时写入工作流完成后删除；只保留既有常规只读 Windows CI。正式设备配置未改，未连接用户桌面或现场控制器。仅进行了主代理源码复核与自动化检查，没有独立第三方审查声明。

## 仍需后续处理

- 启动和连接装配的整体职责整理、连接同步重入及异常断连策略。
- Host 按每张控制卡选择适配器、逐卡混合品牌装配与配置迁移。
- ZMotion 原厂 SDK 软件调用链以及未接入的其他品牌；既有原生 DLL 缺失警告仍应如实记录。
- 正式 Host 屏幕操作走查与实体限位、制动、安全回路；源码不变和离屏检查不是这类验收。
- OnExit 原有同步收尾策略只迁入专用文件；本轮没有证明强制终止、断电等情况下所有动作均能完成。
