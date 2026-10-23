# UI 前的设备级多工位运行实施计划

**目标：** 在现有任务引擎上提供可被后续 UI 直接调用的设备任务清单、独立持续运行、请求交接、统一复位启停、故障恢复和一致监控入口，并用连续生产场景验证。

**架构：** 具体的 TaskMachineController 管理同一 SimulatedTaskDevice 上的多个工位，各工位独立执行自己的周期。复用单次执行器、变量、资源和配置服务；有恢复需求时保存当前异步调用进度，故障在当前节点等待复位，不从任务根重放。仍保持纯内存执行，不接 Host/实际卡。

**技术：** net48 / C# 7.3；普通类、直接调用、中文注释，不引入容器、反射编排或新包。

## 参考和固定语义

- UAMP TaskManager.Run/Pause/ClearErr/Continue 分别管理已配置任务；TaskBase.RunLoop 独立循环，TaskBegin/TaskFinish/TakingFinish 承担交接。本次采用异步等待，不采用 Thread.Abort。
- 原 Runtime 的 MachineFlowScheduler 已有 IStationFlow 并行调度，但它操作实际运动契约，不能把内存 TaskRun 当作实体工位接入。新模块先提供独立组合入口，依赖不反转。
- 清单支持 Once、Cyclic、OnRequest，工位标识独立于任务定义标识；可选初始化任务成功后才放行自动工位。
- 请求触发采用明确的两个 bool 变量：请求方保持 Request=true 直到 Complete=true，再置 Request=false；完成方收到 false 才清 Complete。初值未知时等待，不猜默认状态；旧 Complete=true 不重复执行。
- 普通停止按钮语义使用 Pause；所有工位冻结，Reset 验证条件后 Ready，Start 才继续。取消终止采用单独的 AbortAsync，终止后不恢复旧任务。
- 故障暂停整机；已完成节点不重做，失败节点在 Reset/Start 后重进。已接受相对运动保存绝对终点，重新进入也不叠加位移。范围仍持有资源，退出条件故障只重查退出，不重做正文；终止才展开清理并保留 Blocked。
- 工位等待请求、等待初始化和周期间隔不消耗动作节点时限；每周期有自己的 TaskRun 和节点数量上限，不用设备运行时间充当单次动作时限。
- 一台内存设备只允许一个设备运行控制器持有，避免独立运行绕过整机暂停；关闭前等待全部动作收尾。输入/变量注入仍供明确的仿真场景使用。

## 实施文件

1. `Machine/TaskMachineConfiguration.cs`、`TaskMachineConfigurationValidator.cs`、`Configuration/TaskMachineConfigurationStore.cs`：清单、固定设备文件、修订备份、任务/信号引用校验。
2. `Machine/TaskMachineController*.cs`、`StationExecutionControl.cs`、`TaskMachineSnapshot.cs`：生命周期、独立工位循环、四阶段交接、可选初始化、报警/节点故障与监控。
3. `Execution/TaskExecutionEngine.cs`、`TaskNodeExecutor*.cs`、`TaskExecutionScope.cs`：增加受设备控制的内部入口、节点恢复和固定终点，保留原单次执行接口行为。
4. `Simulation/SimulatedTaskDevice.Runs.cs`：设备所有权及活动运行登记，禁止混用独立入口。
5. `tools/MotionControl.TaskEngine.Checks/MachineChecks*.cs`：多周期流水交接、快慢工位独立推进、复位门禁、并行故障暂停/当前节点恢复、相对位移、范围退出、不重复启动、关闭/终止清理、配置和监控。
6. 使用说明和可执行场景，更新验证记录；Debug/Release 检查后按项目规则中文提交本地 Git。

## 完成审查

必须证明：任务清单可按设备保存读取；初始化可选且有先后；工位独立循环和触发执行；整机暂停/复位/启动及故障联动；中断步骤与相对终点保留；连续多周期交接不沿用旧完成信号；独立工位不受慢工位的全局汇合限制；资源与所有权清理；快照可供未来 UI 读取。仅给出单次任务通过或说明文档，不算以上链路完成。

## 完成记录

以上文件和链路已实现；Debug / Release 每种配置 84 组完整检查通过，独立三工位 30 周期样例通过。结果和限制见 `docs/verification/2026-10-23-task-machine-running.md`。本轮仅完成第一版仿真 UI 的后台准备，尚未接入 Host、实际卡、协调插补或跨进程生产恢复。
