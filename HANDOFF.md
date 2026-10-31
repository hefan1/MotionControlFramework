# 交接说明（任务执行接入真实设备）

本文件是"任务定义 → 真实运动控制卡"这条线的交接点。接手时先读 `AGENTS.md`，再读本文件，然后按第 7 节的第一件事开始。计划文档 `docs/plans/2026-10-31-task-device-binding.md` 的部分步骤已被调整，**以本文件为准**。

## 1. 当前状态（判据）

| 项目 | 值 |
|---|---|
| HEAD | `04de8a1 执行入口改为设备配置显式指定` |
| 工作区 | 干净，无未提交改动 |
| 构建 | Host 与全部检查工程 0 警告 0 错误 |
| 正式 Host | 进程运行中（启动时间见 `Get-Process MotionControl.Host.Wpf`），四工位、`控制卡 会话已建立`、等待复位、无报警 |
| 配置根 | `C:\Users\ASUS\AppData\Local\MotionControlFramework\Configurations\Machine01` |
| 执行入口 | `Parameters/Equipment.json` 的 `executionOwner` 当前为 `StationFlows` |

一句话：**任务执行已经能驱动设备，但正式 Host 还没装配任务执行页面**，所以现在把 `executionOwner` 改成 `TaskEngine` 只会禁止工位流程启动，任务不会自己跑。

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
- Host 装配点：`src/MotionControl.Host.Wpf/App.xaml.cs`（`ResolveEquipmentDefinition`、`ResolveExecutionOwner`、`CreateEquipmentStationFlows`）

## 3. 已定决策（不要推翻）

1. **两条执行路径并存，同一时刻只有一个能启动**；判据是"谁正在控制设备"，不是"配置了哪种入口"。入口由 `executionOwner` 显式指定。
2. **暂停一律立即受控停止当前轴**，不停在半路等步骤做完。
3. **真机轴组插补不支持时必须明确报错**，不许退化成逐轴顺序移动。
4. **真机适配器独立成工程**，设备定义与厂商无关。
5. **验收基准是 ZSimu**：Host → 正运动适配器 → SDK → VPLC5xx-Simu 与连实体卡是同一份代码，其结果就是设备验证结论。只有伺服动态、编码器、限位/急停回路、接线、机械负载、总线时序需要实物。
6. **不纠结 20009**：只在它直接阻碍当前验证时做最小确认并记录，不反复重连。

## 4. 未完成（阶段 C 剩余）

- **Host 任务执行页**：创建 `MachineTaskDevice`、加载任务、启动/暂停/停止、显示运行状态与报警。这是唯一挡住端到端验证的缺口。
- **暂停语义统一**：`TaskRun.Pause()` 现在只是冻结时钟，真机必须改成"取消当前节点 → 受控停止 → 等停稳 → 保留节点与固定终点 → 恢复时重进"，复用 `AcceptedAxisTargets` 与 `ResetTimeBudgetAsync`。
- **报警接线**：`TaskMachineController` 需注入 `MachineRuntime.AlarmService`，任务超时报警才会进"当前报警"页。
- **互锁节点**：`MachineTaskDevice.CheckInterlock` 现在抛 `NotSupportedException`。互锁是框架级安全语义，长期必须支持。

## 5. 已知坑（先读再动手）

1. **端口是同步的，设备是异步的**。`MachineTaskDevice` 用 `Wait()`（`GetAwaiter().GetResult()`）桥接，根因是 `TaskRun.StepAsync` 在持锁状态下执行同步动作。这发生在执行器后台线程，**不要在 UI 线程调用这些同步方法**。彻底解决要把端口改异步并改运行锁，风险大，需单独立项。
2. **轴未回零会被拒绝**：`BeginMove` 要求 `ServoEnabled` 且 `Homed`。当前 ZSimu 原点 DI（DI8–DI11）未触发，直接跑定位会报"轴尚未回零"。回零需要原点信号，涉及仿真输入，不能想当然。
3. **轴组/互锁是明确不支持**，不要在 Host 里把它们包装成能用。
4. **git 不在 PATH**：用 `C:\Users\ASUS\.cache\codex-runtimes\codex-primary-runtime\dependencies\native\git\cmd\git.exe`。
5. **配置根有两处**：带包名的容器上下文解析到 `...\Packages\OpenAI.Codex_2p2nqsd0c76g0\LocalCache\Local\...`，普通会话解析到 `C:\Users\ASUS\AppData\Local\...`。真实现场配置已迁到后者，前者是历史残留。
6. **运行中的 Host 锁 `bin\Release\net48`**：重建前先正常关闭；关闭被"设备流程仍在运行/报警处理中"拒绝时，按 `AGENTS.md` 的记录流程处理。

## 6. 验证命令与期望输出

```powershell
$env:DOTNET_CLI_HOME='F:\Work\运动控制框架\.dotnet-cli-home'
cd F:\Work\运动控制框架\MotionControlFramework
$dotnet='C:\Program Files\dotnet\dotnet.exe'

# 1) 真机任务设备检查：期望 7 组，失败 0
& $dotnet build tools/MotionControl.MachineDevice.Checks/MotionControl.MachineDevice.Checks.csproj -c Release -o tmp/check-md -v:q
& .\tmp\check-md\MotionControl.MachineDevice.Checks.exe

# 2) 任务引擎检查：期望 23 / 53 / 16 组，失败 0
& $dotnet build tools/MotionControl.TaskEngine.Checks/MotionControl.TaskEngine.Checks.csproj -c Release -o tmp/check-te -v:q
& .\tmp\check-te\MotionControl.TaskEngine.Checks.exe

# 3) Runtime 回归：期望 35 项全部通过（33 Runtime + 1 工位配置 + 1 ZMotion）
& $dotnet build tools/MotionControl.ZMotionSimulatorCheck/MotionControl.ZMotionSimulatorCheck.csproj -c Release -o tmp/check-rt -v:q
& .\tmp\check-rt\MotionControl.ZMotionSimulatorCheck.exe --runtime-regression

# 4) 操作员页面检查：期望 10 组，失败 0
& $dotnet build tools/MotionControl.OperatorPages.Checks/MotionControl.OperatorPages.Checks.csproj -c Release -o tmp/check-ops -v:q
& .\tmp\check-ops\MotionControl.OperatorPages.Checks.exe
```

任何一套数字变化，都说明行为被改动了，先查清再提交。

## 7. 第一件事

在正式 Host 里加任务执行入口：新建一个页面（或复用现有任务监视页），装配顺序是

```csharp
// 1. 目录：Host 已有 TaskResourceCatalogLoader
// 2. 设备：用已连接的 MotionCardGroup + ILogicalIo + DeviceVariableStore + MachineRuntime
var device = new MachineTaskDevice(catalog, motionCards, logicalIo, _deviceVariableStore, _machineRuntime);
// 3. 接管：device.ClaimMachine(owner) → 内部调用 MachineRuntime.AcquireTaskEngineControlAsync
// 4. 运行：TaskExecutionEngine.Start(definition, device, ...)
// 5. 收尾：device.ReleaseMachine(owner) → 入口自动回到 StationFlows
```

注意：`executionOwner` 要设成 `TaskEngine` 才符合"同一时刻一个入口"的语义；接管成功后工位流程的启动按钮应保持禁用并说明原因。

## 8. 提交约定

- 中文、简短、说明实际改动；一次一项。
- 提交前 `git diff --cached` 核对，只提交本次涉及的文件。
- 只提交本地，不推送远端。
- 每项改完跑第 6 节对应检查，数字必须与记录一致或在提交说明里解释差异。
