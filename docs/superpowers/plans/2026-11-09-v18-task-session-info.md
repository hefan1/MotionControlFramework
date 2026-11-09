# V1.8 任务会话来源实施计划

> 执行方式：在隔离工作区逐项实现；测试和正式 Host 界面验证使用 Windows Sandbox 软件控制器。

**目标：** 修复交接报告 UI-04，让已成功发布的任务会话具有可追溯的会话标识、加载时间和冻结任务修订。

**依据：** `F:\Work\运动控制框架\MotionControlFramework\output\pdf\MotionControlFramework-V1.8-UI-audit-handoff-2026-11-09.pdf` 的 UI-04，V1.8 手册第 319、561 页。

**架构：** 任务监控会话在 Host 完成连接与准备配置核对后生成一次不可变元数据；任务配置页通过 Host 提供的当前有效会话读取该元数据，任务运行页使用同一会话对象。工程整体版本没有来源时继续显示未提供，不把任务修订、磁盘最新文件或连接会话号冒充工程版本。

**全局约束：** 保留顶部/左侧导航框架、底栏及轴映射、IO 映射、轴手动、IO 监控四个受保护页面；不改变启动、复位、运动、报警和资源门禁；所有新增成员和方法写中文说明。

**重点回归：** 未发布/装载失败不显示有效会话；连接丢失或准备配置换代不复用旧会话；重新读取获得新标识；磁盘或可变任务对象变更不改写已冻结修订；中英文与长任务清单可读；只读刷新不触发设备命令。

### 任务 1：先增加失败检查

- 修改 `tools/MotionControl.TaskEditing.Checks/Program.cs`，增加会话元数据检查组；新增 `TaskSessionMetadataChecks.cs`。
- 修改 `tools/MotionControl.OperatorPages.Checks/BoundTaskLoadChecks.cs`，覆盖发布成功、装载失败、断连失效和重新读取的会话身份。
- 在 `TaskSessionMetadataChecks.cs` 覆盖真实会话、快照故障和未知/失效会话的配置页摘要。
- 在沙盒运行对应检查，确认因元数据或入口缺失而失败。

### 任务 2：冻结并发布元数据

- 新增 `src/MotionControl.TaskEngine.Wpf/Simulation/TaskSessionMetadata.cs`：不可变保存 `SessionId`、`PublishedAtUtc`、`MachineId`、`Source` 及任务 ID 到修订号的只读副本。
- 修改 `TaskMonitorSession`：提供 `PublishedMetadata` 和 `Publish(string machineId)`；仅允许发布一次，修订取自执行器构造时同源的任务定义与设备任务清单快照。
- 修改 `TaskMonitorLauncher.LoadTaskSessionAsync`：现有连接及准备配置检查均成功后、发布会话前调用 `Publish`；提供只返回当前有效已发布任务会话的只读入口。

### 任务 3：展示真实会话

- `TaskConfigurationView.SetCurrentTaskSessionProvider(Func<TaskMonitorSession>)` 接入 Host 的有效会话；摘要和来源详情显示该会话 ID、加载时间、来源、任务快照数及实际修订。
- `TaskMonitorView` 使用相同元数据；引用详情不再声称已提供的任务会话标识缺失。无提供方、无有效会话、读取失败分别保守显示。
- 在 `App.xaml.cs` 接入提供方；不改变任务读取、应用或设备动作。

### 任务 4：回归与交付

- 在隔离沙盒运行任务编辑、任务监控和操作员页面检查；保持原有状态门禁回归通过。
- 构建 Release 正式 Host，使用软件控制器在沙盒读取流程，截图核对任务配置与工位运行；验证断连/重连后旧会话不再显示为当前会话。
- 更新中文验证记录，检查暂存差异并以中文提交本地 Git；不推送远端。
