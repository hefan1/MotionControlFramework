# 任务轴组定位实施计划

**目标：** 让同一卡上的多根逻辑轴按各自目标和速度执行定位，统一等待、暂停、取消和处理异常。

**架构：** 新增具体的 MoveAxisGroup 节点；每项目标复用已有坐标方式、数值来源和工艺参数。执行器一次申请完整轴组、一次解析目标快照；内存设备先检查整组再接受动作，各轴独立推进，全部到位后返回。仍只接收 SimulatedTaskDevice。

**技术：** .NET Framework 4.8 / C# 7.3；普通类、直接调用、中文注释；已有 Newtonsoft.Json 和检查程序，无新增依赖。

## 依据与确定的边界

- UAMP `Common/Manager/MotionManager.cs:2369` 的 RunToPos：检查整组状态、锁定轴组，逐轴发送 AbsMove，检查全部停止与到位；Z 抬升/下降属于它的具体动作安排。
- UAMP 同文件 `PTMove:386` 单独使用轨迹接口；POL `PolModuleStation.RunCNC:1197` 使用插补缓存。轴组定位与插补分别表达。
- 本框架的 ParallelGroup 表示独立流程，资源范围内禁止并行分支；新增具体轴组动作可以在范围内继承完整占用，无需放开这一限制。
- 当前优先单卡整机。本次组内要求同一 ControllerId；允许不同工程单位，因为每轴使用独立速度。轴数 2–64 仅为文件规模限制，不代表卡能力。
- 每项显式给出逻辑轴、Absolute/Relative、目标来源及速度/加减速度。示教点只能作所属轴的绝对位置。相对目标接受后固定，暂停恢复不重复叠加。
- 组名不隐含 Z 抬升或机械安全判断。安全顺序由普通 Sequence 写清楚，区域继续由 ResourceScope 管理。
- 本轮仅内存仿真；不连接厂商模拟器或实际卡，不接 Host/UI，不改厂商声明或真实运动契约。

## 实施顺序与文件

1. `Definitions/MoveAxisGroupNode.cs`：MoveAxisGroupNode 与 AxisPositionTarget，保持纯定义；`Configuration/TaskNodeJsonConverter.cs` 登记固定 kind 并要求每项 mode 等字段显式存在。
2. `Validation/TaskNodeValidator.AxisGroups.cs`：复用 CheckAxisTarget / CheckProfile；检查数量、重复、同卡、各轴参数；`TaskResourceUsageValidator.cs` 检查整组资源声明。
3. `Simulation/SimulatedTaskDevice.AxisGroups.cs` 与 `SimulatedAxisGroupMove.cs`：整组开始前检查、各轴独立时长、到位与报警检查、整组收尾；复用单轴状态和动作凭证。
4. `Execution/TaskNodeExecutor.AxisGroups.cs` 与直接分发入口：整组申请或继承范围、单份变量快照解析、目标固定、统一轮询与 finally 清理。某轴先到仍保留该轴资源，直至整组结束。
5. `ExecutionChecks.AxisGroups*.cs`：定义/JSON、整组前置检查、排队与目标读取、到位汇合、暂停相对目标、取消/超时/报警、范围异常隔离、子任务及轴组样例。
6. 新增 `Samples/AxisGroupPositioning.json`；更新模块说明、架构和验证记录；检查暂存差异后中文提交本地 Git。

## 验证方案

- 有意义的故障检查：第二根轴无效时第一根也不接受动作；任何轴报警后其余轴停止，后续输出不执行；退出资源范围仍需确认。
- 进度检查：快轴先到位时慢轴仍运动，后续节点不能提前执行，快轴也不能被其他运行取得；暂停后所有剩余动作冻结，恢复沿原终点继续。
- 一次完成 Debug / Release 构建和完整检查。只在新修改、失败或未解决疑问需要时补充重复，不机械循环已经通过的测试。

```powershell
& 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe' tools/MotionControl.TaskEngine.Checks/MotionControl.TaskEngine.Checks.csproj /restore /t:Build /p:Configuration=Debug /nologo /verbosity:minimal
& tools/MotionControl.TaskEngine.Checks/bin/Debug/net48/MotionControl.TaskEngine.Checks.exe
```

Release 替换命令中的两处 Debug。预期退出码为 0、所有组通过，取消或失败后本次内存动作均停止；范围异常保留 Blocked。

## 后续实际设备接入需要核实

内存整组检查通过后可在同一锁内接受全部轴，实际 SDK 逐轴发指令可能中途失败。后续接入时须停止已经接受命令的轴，并逐轴确认停止和到位；不能把本次内存原子性直接当成硬件保证。不承诺同时启动、同时到达或中间轨迹为直线。

## 完成记录

以上范围已实现，提供 AxisGroupPositioning.json 完整往返样例。Debug / Release 最终构建与检查均通过，每种配置 72 组，含本轮新增的 12 组。详细行为及未覆盖范围见 `docs/verification/2026-10-23-task-axis-group-positioning.md`。
