# 阶段 2：纯软件仿真执行引擎实施计划

**目标：** 在阶段 1 的定义、目录和校验基础上执行顺序、分支、并行与子任务，并验证暂停、取消、超时和故障传播。

**架构：** `TaskExecutionEngine` 只接收具体的 `SimulatedTaskDevice`，不接收控制卡或 Host 服务。定义在启动前校验并复制；每次运行单独保存状态、取消令牌、活动计时和节点快照。普通类型分支直接调用各动作方法。

**技术基线：** net48、C# 7.3；复用现有 `DeviceVariableStore`、任务校验与 JSON；不增加依赖。

## 已检查的现有实现

- `MachineFlowScheduler`：已有后台启动和取消收尾方式；本轮保留现有代码流程，不把新执行器接入 Host。
- `AxisResourceCoordinator`：已有轴组按序申请；它是 Runtime 内部类且没有机械区域退出语义。本轮仅在内存设备中阻止同轴同时动作，完整仲裁在阶段 3 复用/扩展评估。
- `DeviceVariableStore`：复用八种类型和快照；仿真设备自行创建独立实例，初始值均无效，不读取生产恢复文件。
- `TaskDefinitionValidator` 与 `TaskDefinitionJson`：启动前重复校验，深复制冻结当前任务及子任务。

## 实施顺序与文件

1. 新增 `Execution/TaskRunState.cs`、`TaskNodeSnapshot.cs`、`TaskRunSnapshot.cs`、`TaskExecutionException.cs`、`TaskRun.cs`：运行控制和只读诊断。
2. 新增 `Simulation/SimulatedTaskDevice*.cs`、`SimulatedAxisSnapshot.cs`：内存轴、DI、DO和变量；每个轴动作通过 finally 清理占用。
3. 新增 `Execution/TaskRuntimeValues.cs`、`TaskConditionEvaluator.cs`：显式类型解析、完整变量快照上的条件判断，无效数据不当作 false 或成功。
4. 新增 `TaskExecutionEngine.cs`、`TaskNodeExecutor*.cs`、`TaskExecutionScope.cs`：直接分发节点、并行全成功汇合、首个故障取消其余分支并等待退出。
5. 在 `tools/MotionControl.TaskEngine.Checks` 添加执行检查：使用已有检查入口和内存目录；保留阶段 1 全部回归。
6. 更新模块说明、验证记录；VS18 MSBuild 构建 Debug/Release，并重复离线检查，最后中文提交本地 Git。

## 语义和验收

- 暂停仅是内存仿真冻结，不代表实际运动卡暂停；暂停时间不计入节点时限，恢复不重发当前相对定位。取消仍能在暂停时完成。
- 节点时限包含其子节点；采用单调活动时钟与协作检查，等待循环约每 10ms 检查，不能承诺 Windows 实时性。
- 终态只在子分支均退出、内存轴占用清理后发布；失败保留任务/节点/本次执行编号和原始异常。
- 多次调用同一个子任务产生独立执行编号；调用堆栈、定义 ID 与运行状态分离。每次最多展开 10000 个节点，防止任务调用指数膨胀。
- 未提供输入/变量时保持未知；条件等待显示原因并超时，条件分支和运动目标拒绝无效数据。变量质量只覆盖 HasValue；请求周期/新鲜度属于通讯接入阶段。
- 同轴冲突明确失败，独立轴可并行。`ResourceScope` 和 `CoordinatedMove` 在执行前拒绝，不伪装完成。
- 单轴是匀速进度模型；回零是内存回零流程占位，不模拟传感器或厂商回零模式；停止节点只能确认未被其他动作占用的仿真轴。取消当前运行用于停止它持有的动作。
- 不开发 UI，不改现有生产配置、恢复文件、Host、Runtime 流程或厂商 API，不连接 ZDevelop 或任何控制器。

## 检查命令

```powershell
& 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe' tools/MotionControl.TaskEngine.Checks/MotionControl.TaskEngine.Checks.csproj /restore /t:Build /p:Configuration=Debug /nologo /verbosity:minimal
& tools/MotionControl.TaskEngine.Checks/bin/Debug/net48/MotionControl.TaskEngine.Checks.exe
```

Release 使用相同命令替换配置。预期所有检查通过，终态后没有仿真轴占用或继续写值。

## 完成记录

以上阶段 2 已实现；Debug/Release 构建成功，每种配置连续 3 轮检查全部通过。每轮包含阶段 1 的 22 组、阶段 2 的 21 组和运行后依赖边界检查。说明和证据见 `docs/verification/2026-10-23-task-execution-stage2.md`。
