# 交接说明（设备任务与本机软件控制器）

本文件记录正式 Host 两种设备流程及其软件控制器接入情况。接手时先读 `AGENTS.md`，再读本文件和第 7 节。计划文档 `docs/plans/2026-10-31-task-device-binding.md` 的部分步骤已调整，以实际代码和本文件为准。

## 1. 当前状态（判据）

| 项目 | 值 |
|---|---|
| 基线提交 | 以 `git log` 为准；本轮软件控制器相关改动和提交状态以 `git status --short` 为准 |
| 工作区 | 正式 Host 具备正运动及独立软件控制器两种后端；精确状态以 `git status --short` 为准 |
| 构建 | MachineDevice 检查为 9 组、OperatorPages 检查为 16 组；TaskEngine 23/53/16 组、Runtime 35 项为历史记录，须按第 6 节重新确认 |
| 正式 Host | 新一轮软件控制器测试前重新核对连接、轴和 IO；每次控制器进程重启后轴、IO 与供料场景状态均重置 |
| 配置根 | `C:\Users\ASUS\AppData\Local\MotionControlFramework\Configurations\Machine01` |
| 执行入口 | 2026-11-01 完成 C# 工位最终复测后，`Parameters/Equipment.json` 的 `executionOwner` 为 `StationFlows`；`DemoStations.json` 仅启用上料定位工位，其余三个工位保留配置但已禁用。两种流程的测试配置均保留，切换入口后需重新加载 |
| 控制器后端 | 当前 Machine01 的 `Parameters/ControllerBackend.json` 保留 `{"backend":"Virtual","port":18520}`；它不改写原有运动卡、轴和 IO 映射。未提供此文件时默认正运动 |

正式 Host 已使用协议 2 软件控制器复测上料工位两种流程的正常循环、暂停、缺料报警和恢复，以及 C# 流程断线状态。任务列表故障恢复后继续循环至少 19 次，C# 故障恢复后至少 85 次。一键启动脚本已从未运行状态启动控制器和正式 Host。这些结论仅适用于软件控制器，不代表正运动 SDK 或实体设备验收。历史正运动联调中 Load_X 回零方向与有效电平尚未查清；切回正运动后仍须独立核对，不能把软件控制器的回零结果当作厂商结果。

任务列表在 `DO0 夹具夹紧` 打开后的保持阶段暂停，实测 DO0 会保持当时的输出状态；任务步骤恢复时再继续处理。不能假设暂停或看门狗会自动关闭输出，也不能在缺乏设备工艺依据时统一强制释放夹具。运行前后应在正式 Host 查看输出，并按工艺判断安全处理方式。

## 2. 本轮已完成的改动

| 提交 | 内容 |
|---|---|
| `b70cb55` | 设备工位创建收敛为 `IEquipmentDefinition` 边界，Host 不再 `new DemoStationFlow` |
| `d6d2c3f` | 执行入口互斥（`MachineControlOwner`） |
| `cc2444a` | 抽出 `ITaskDevice` 端口，执行器不再绑定内存设备 |
| `3f8ea16` | 内存设备改名 `Device/InMemory`、`InMemoryTaskDevice`，加 README 说明边界 |
| `dbc9c35` | 新增 `MotionControl.TaskEngine.MachineDevice` 真机适配器 + 检查工程 |
| `4949d03` | 默认报警改由 `IEquipmentDefinition.CreateDefaultAlarms()` 提供，框架不再内置设备报警 |
| `cfd45f0` | 互斥判据从"是否配置工位流程"改为"工位流程是否正在运行" |
| `04de8a1` | `executionOwner` 配置项（StationFlows / TaskEngine / None / 留空自动） |

关键文件：

- 端口：`src/MotionControl.TaskEngine/Device/ITaskDevice.cs`（24 个成员）
- 真机实现：`src/MotionControl.TaskEngine.MachineDevice/MachineTaskDevice.cs`、`MachineTaskDevice.Motion.cs`、`MachineAxisMove.cs`
- 内存替身：`src/MotionControl.TaskEngine/Device/InMemory/`（只给离线检查和预览用，见其 README）
- 入口仲裁：`src/MotionControl.Framework.Runtime/Machine/MachineRuntime.cs`（`ControlOwner`、`AcquireTaskEngineControlAsync`、`ReleaseTaskEngineControl`）
- Host 装配点：`src/MotionControl.Host.Wpf/App.xaml.cs` 的 `CreateEquipmentStationFlows`；设备定义与执行入口的配置解析位于 `src/MotionControl.Host.Wpf/HostEquipmentSelection.cs`。
- 软件控制器装配：`src/MotionControl.Host.Wpf/HostMotionCardBackendSelection.cs` 读取独立后端配置；`VirtualAxisMappingCardConnection.cs` 使用现有设备映射连接 `src/Adapters/MotionCards/Virtual/Adapter`。独立进程及现场调试命令见 `tools/MotionControl.VirtualController/README.md`。

## 3. 已定决策（不要推翻）

1. **两条执行路径并存，同一时刻只有一个能启动**；判据是"谁正在控制设备"，不是"配置了哪种入口"。入口由 `executionOwner` 显式指定。
2. **暂停一律立即受控停止当前轴**，不停在半路等步骤做完。
3. **真机轴组插补不支持时必须明确报错**，不许退化成逐轴顺序移动。
4. **真机适配器独立成工程**，设备定义与厂商无关。
5. **正式 Host 是界面验收对象，控制器验证分别记录**：软件控制器覆盖设备流程和操作；正运动 VPLC 覆盖对应原厂 SDK 与仿真适配器。当前正运动适配器只接受本机 VPLC，不支持实体卡；任何软件结果均不能代替实物接线、伺服、安全回路和机械验证。
6. **不纠结 20009**：只在它直接阻碍当前验证时做最小确认并记录，不反复重连。

## 4. 未完成（阶段 C 剩余）

- **协议 2 正式 Host 回归**：两种路径的正常循环、暂停与缺料报警恢复，以及 C# 断线状态已有屏幕验证。其他机型、现场硬件和故障场景仍需独立验证。
- **Load_X 回零方向与有效电平**：历史模式 3 运行时从 0 向负方向移动并在 -90 mm 受控停止。2026-11-01 重启控制器后，Host 显示轴 1 在 0 mm 且未回零，DI8 切换可使 Host 原点状态变化并已恢复；控制器输入表显示 DI8 为 `datum_in(1)` 且输入反转关闭。厂商手册对不同控制器族的有效电平有区别，历史负向动作是否为反找尚未确认。不要为了继续流程重复回零；先核实该控制器的原点有效状态和模式行为。
- **互锁节点**：`MachineTaskDevice.CheckInterlock` 仍抛 `NotSupportedException`，需要设计并实现框架级安全语义。

## 5. 已知坑（先读再动手）

1. **端口是同步的，设备是异步的**。`MachineTaskDevice` 用 `Wait()`（`GetAwaiter().GetResult()`）桥接，根因是 `TaskRun.StepAsync` 在持锁状态下执行同步动作。这发生在执行器后台线程，**不要在 UI 线程调用这些同步方法**。彻底解决要把端口改异步并改运行锁，风险大，需单独立项。
2. **轴未回零会被拒绝**：`BeginMove` 要求 `ServoEnabled` 且 `Homed`。软件控制器的回零是软件零位；正运动 VPLC 历史 DATUM 模式 3 的方向与输入有效状态尚未确认，切回后须先查清，再发回零或循环指令。
3. **轴组/互锁是明确不支持**，不要在 Host 里把它们包装成能用。
4. **git 不在 PATH**：用 `C:\Users\ASUS\.cache\codex-runtimes\codex-primary-runtime\dependencies\native\git\cmd\git.exe`。
5. **配置根有两处**：带包名的容器上下文解析到 `...\Packages\OpenAI.Codex_2p2nqsd0c76g0\LocalCache\Local\...`，普通会话解析到 `C:\Users\ASUS\AppData\Local\...`。真实现场配置已迁到后者，前者是历史残留。
6. **运行中的 Host 锁 `bin\Release\net48`**：重建前先正常关闭；关闭被"设备流程仍在运行/报警处理中"拒绝时，按 `AGENTS.md` 的记录流程处理。

## 6. 验证命令与期望输出

```powershell
$env:DOTNET_CLI_HOME='F:\Work\运动控制框架\.dotnet-cli-home'
cd F:\Work\运动控制框架\MotionControlFramework
$dotnet='C:\Program Files\dotnet\dotnet.exe'

# 1) 真机任务设备检查：期望 9 组，失败 0
& $dotnet build tools/MotionControl.MachineDevice.Checks/MotionControl.MachineDevice.Checks.csproj -c Release -o tmp/check-md -v:q
& .\tmp\check-md\MotionControl.MachineDevice.Checks.exe

# 2) 任务引擎检查：期望 23 / 53 / 16 组，失败 0
& $dotnet build tools/MotionControl.TaskEngine.Checks/MotionControl.TaskEngine.Checks.csproj -c Release -o tmp/check-te -v:q
& .\tmp\check-te\MotionControl.TaskEngine.Checks.exe

# 3) Runtime 回归：期望 35 项全部通过（33 Runtime + 1 工位配置 + 1 ZMotion）
& $dotnet build tools/MotionControl.ZMotionSimulatorCheck/MotionControl.ZMotionSimulatorCheck.csproj -c Release -o tmp/check-rt -v:q
& .\tmp\check-rt\MotionControl.ZMotionSimulatorCheck.exe --runtime-regression

# 4) 操作员页面检查：期望 16 组，失败 0（含后端选择、断线和 IO 关联反馈检查）
& $dotnet build tools/MotionControl.OperatorPages.Checks/MotionControl.OperatorPages.Checks.csproj -c Release -o tmp/check-ops -v:q
& .\tmp\check-ops\MotionControl.OperatorPages.Checks.exe
```

任何一套数字变化，都说明行为被改动了，先查清再提交。

## 7. 第一件事

先核对当前 Machine01 的后端和执行入口，再按 `tools/MotionControl.VirtualController/README.md` 启动独立控制器和正式 Host。进程重启后重新确认伺服、回零和供料规则；继续按具体需求扩展设备场景，核对暂停时轴、DO 和报警状态。测试配置按项目规则保留，结束时安全停止所有轴并处理测试 IO。

如本轮目标是正运动 SDK 而非软件控制器，先确认 `ControllerBackend.json` 的选择，再调查前述 Load_X 回零有效电平与历史方向异常；相关状态未查清前不发回零或循环命令。

## 8. 提交约定

- 中文、简短、说明实际改动；一次一项。
- 提交前 `git diff --cached` 核对，只提交本次涉及的文件。
- 只提交本地，不推送远端。
- 每项改完跑第 6 节对应检查，数字必须与记录一致或在提交说明里解释差异。
