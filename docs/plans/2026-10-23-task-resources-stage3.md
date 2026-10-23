# 阶段 3：任务资源仲裁与显式互锁实施计划

**目标：** 在内存仿真执行器内支持共享轴、逻辑输出及命名区域的互斥等待、进入检查、退出确认和异常后的待确认状态。

**架构：** 同一个 `SimulatedTaskDevice` 持有一个具体的 `TaskResourceCoordinator`。申请的一组资源要么全部取得，要么一项也不占用；等待不持锁。动作继续使用逻辑名和工程单位，无厂商 SDK。复用现有条件、目录、校验器和运行控制。

**技术：** net48 / C# 7.3；普通类、直接调用、中文注释；本轮不接入 Host 或实际控制器。

## 参考依据与取舍

- UAMP `Common/Manager/MotionManager.cs:969`：先在锁内检查整组轴是否被占用，再登记整组占用。
- UAMP `ProjectCustom/Task/S4/MeasureTask.cs:754`：复位流程显式检查前后工位安全信号，条件不满足或读取失败会返回原因。
- POL `Leadshine.CNC.POL.Forms/DebugForm.cs:594`：回安全位前检查本工位回零、气缸及另一工位 Y 轴安全位置。
- 本框架 `AxisResourceCoordinator` 仍供原有 C# 工位使用。它是内部轴专用信号量协调器，不提供区域隔离、退出确认及等待者诊断；本轮不改其语义，新任务模块采用一次性全量登记和独立状态快照。

## 本轮固定规则

1. Move/Home 在无资源范围时自动排队申请 `Axis:轴名`；SetOutput 申请 `Output:输出名`。独立资源仍可并行；确认停止节点只读确认，不夺取动作。
2. ResourceScope 必须显式列出正文以及子任务使用的全部轴/输出。区内动作继承本范围的占用；区内禁止再嵌套 ResourceScope，也禁止 ParallelGroup。需要并行时，把各自的 ResourceScope 放到外层 ParallelGroup 中。
3. 以上限制配合一次取得完整资源组，避免先持有 A 再等待 B 的资源锁死锁；子任务间隐藏的嵌套和遗漏也在启动前检查。变量/外部信号的业务互等仍通过有界超时和等待原因定位，不宣称自动识别所有工艺死锁。
4. 新增可选 `EntryInterlock` / `ExitInterlock`：明确轴位置区间（同时要求停止、使能、无报警及可选已回零）、逻辑 DI 和已有变量条件。互锁引用的轴也必须纳入本范围资源。原 `ExitCondition` 保留，两者同时设置时明确拒绝。
5. 先取得完整资源组，再等待进入条件，随后执行正文，最后等待退出条件。进入条件是一次性前置检查，不是持续安全监控；框架不猜测机械碰撞路径。
6. 正文开始前取消/失败可直接退还资源；正文开始后若未完成退出确认就失败或取消，把整组标成 Blocked。它不会因清理 finally 自动放行。
7. 恢复需要显式调用内存设备恢复入口，填写确认说明；重新核对配置的退出条件及所有占用轴的停止/报警状态，通过才清除 Blocked。不会自动重启旧任务。没有退出条件时，确认说明代表调用方已人工核实；未来 UI 权限和实际卡停稳需要另行接入。
8. 资源快照包含资源名、持有/等待/待确认状态、运行/任务/节点/调用编号及阻塞原因。取消等待者会清理队列；暂停等待者保留顺位，相冲突的后来者不能插队。

## 文件范围与步骤

- `Definitions/TaskInterlock.cs` 与 `ResourceScopeNode.cs`：明确互锁定义，保持现有 JSON 可读。
- `Catalog/TaskResourceCatalog.cs`：登记逻辑输出资源，继续复用原配置。
- `Validation/TaskNodeValidator.Interlocks.cs`、`TaskResourceUsageValidator.cs`：字段、类型、范围、子任务资源使用与死锁结构检查。
- `Resources/TaskResourceCoordinator*.cs`、`TaskResourceRequest.cs`、`TaskResourceSnapshot.cs`：原子全量申请、冲突顺位、释放/隔离、诊断。
- `Simulation/SimulatedTaskDevice.Resources.cs`、`SimulatedTaskDevice.Interlocks.cs`：同一设备锁内检查场景状态与退出恢复；变量条件复用已有求值器，原运动方法继续作为底层冲突保护。
- `Execution/TaskNodeExecutor.Resources.cs`、执行上下文与动作入口：明确持有范围及 finally 收尾，不新增通用分发框架。
- `tools/MotionControl.TaskEngine.Checks`：更新同轴冲突预期为等待；新增资源/互锁/恢复检查，保留既有回归。
- 更新 README/架构和验证记录；Debug/Release 重复检查后中文提交本地 Git。

## 验收重点

完整组原子性、同轴串行/独立轴并行、队列顺位、跨运行隔离、取消/超时清理、暂停不推进、子任务继承、静态拒绝嵌套/遗漏/区内并行、输入和位置进入条件、退出等待、异常隔离、恢复前置条件、过期恢复 ID、JSON 往返以及无厂商依赖。

## 完成记录

阶段 3 已按上述范围实现，并提供 ResourceTransfer.json 可运行样例。最终 Debug / Release 各连续 3 轮检查，每轮 60 组全部通过。验证记录及未覆盖边界见 `docs/verification/2026-10-23-task-resources-stage3.md`。
