# 交接说明（任务执行接入真实设备）

本文件是"任务定义 → 真实运动控制卡"这条线的交接点。接手时先读 `AGENTS.md`，再读本文件，然后按第 7 节的第一件事开始。计划文档 `docs/plans/2026-10-31-task-device-binding.md` 的部分步骤已被调整，**以本文件为准**。

## 1. 当前状态（判据）

| 项目 | 值 |
|---|---|
| 基线提交 | `ffd587e`（本轮实现的提交前基线） |
| 工作区 | 本轮包含任务执行接入、真实轴暂停/报警处理和复位预检查；精确状态以 `git status --short` 为准 |
| 构建 | 正式 Host Release 0 警告、0 错误；四套基线检查全部通过：MachineDevice 8 组、TaskEngine 23/53/16 组、Runtime 35 项、OperatorPages 10 组 |
| 正式 Host | 目标 Release exe 正在运行，`VPLC5xx-Simu` 会话已建立；设备等待复位。Load_X 停在 -90 mm、已停止、未回零；本轮任务会话已关闭 |
| 配置根 | `C:\Users\ASUS\AppData\Local\MotionControlFramework\Configurations\Machine01` |
| 执行入口 | `Parameters/Equipment.json` 的 `executionOwner` 当前为 `TaskEngine` |

一句话：**Host 任务执行入口已装配，正式窗口也验证了未回零轴会被拦截；生产循环尚未端到端验证。** Load_X 配置为 DATUM 模式 3、原点 DI8，但回零搜索实际向负方向移动；DI8 目前未触发，且本轮屏幕操作未能切换该输入。方向和输入切换问题查清前，不再发回零或循环命令。

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

- **正式 Host 生产循环**：任务入口、任务装配、受控暂停/停止和共享报警接线已在当前工作区实现，软件回归通过；正式 Host 已验证任务复位会在 Runtime 复位前拦截未回零定位轴。仍未完成“任务 → 轴动 → DI 变化 → 报警进页面 → 连续循环”的端到端运行。
- **Load_X 回零方向与 DI8 输入**：模式 3 按厂商手册应先正向快找，但 Host 实际观察到轴从 0 向负方向运行并在 -90 mm 受控停止；Host 与 ZDevelop 在线表都显示轴 1 停在 -90、DATUM_IN=8，DI8 未触发。ZSimu 画面切换 DI8 的屏幕操作没有产生可见变化。不要为了继续流程重复回零；先确认仿真输入切换是否生效及控制器轴方向来源。
- **互锁节点**：`MachineTaskDevice.CheckInterlock` 仍抛 `NotSupportedException`，需要设计并实现框架级安全语义。

## 5. 已知坑（先读再动手）

1. **端口是同步的，设备是异步的**。`MachineTaskDevice` 用 `Wait()`（`GetAwaiter().GetResult()`）桥接，根因是 `TaskRun.StepAsync` 在持锁状态下执行同步动作。这发生在执行器后台线程，**不要在 UI 线程调用这些同步方法**。彻底解决要把端口改异步并改运行锁，风险大，需单独立项。
2. **轴未回零会被拒绝**：`BeginMove` 要求 `ServoEnabled` 且 `Homed`。正式 Host 当前 Load_X 未回零，DI8 未触发。已观察到 DATUM 模式 3 朝负方向搜索，与厂商手册描述不符；不允许继续回零或开始任务，直到方向和输入状态问题确认。
3. **轴组/互锁是明确不支持**，不要在 Host 里把它们包装成能用。
4. **git 不在 PATH**：用 `C:\Users\ASUS\.cache\codex-runtimes\codex-primary-runtime\dependencies\native\git\cmd\git.exe`。
5. **配置根有两处**：带包名的容器上下文解析到 `...\Packages\OpenAI.Codex_2p2nqsd0c76g0\LocalCache\Local\...`，普通会话解析到 `C:\Users\ASUS\AppData\Local\...`。真实现场配置已迁到后者，前者是历史残留。
6. **运行中的 Host 锁 `bin\Release\net48`**：重建前先正常关闭；关闭被"设备流程仍在运行/报警处理中"拒绝时，按 `AGENTS.md` 的记录流程处理。

## 6. 验证命令与期望输出

```powershell
$env:DOTNET_CLI_HOME='F:\Work\运动控制框架\.dotnet-cli-home'
cd F:\Work\运动控制框架\MotionControlFramework
$dotnet='C:\Program Files\dotnet\dotnet.exe'

# 1) 真机任务设备检查：期望 8 组，失败 0
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

先解决 Load_X 回零方向与 DI8 输入的验证阻塞。厂商 RTBasic 手册说明 DATUM 模式 3 正向快找、反向慢速复找；本轮正式 Host 上观察到模式 3 朝负向运动。ZDevelop 在线轴表只读显示轴 1 的 `DPOS=-90`、`MPOS=-90`、`DATUM_IN=8`，ZSimu 当前输入仍未触发。先确认 DI8 能否在 ZSimu 中切换并映射到 Host 的 ORG 状态，再查清轴方向参数来源；在此之前保持 Load_X 停止，不运行回零和任务循环。

阻塞排除后，再通过正式 Host 完成 Load_X 回零、任务复位、启动/暂停/恢复/停止和连续周期验证，并逐页核对 DI/DO 与共享报警。配置目录中的测试配置按项目规则保留；所有轴停止，测试输入输出恢复安全值。

## 8. 提交约定

- 中文、简短、说明实际改动；一次一项。
- 提交前 `git diff --cached` 核对，只提交本次涉及的文件。
- 只提交本地，不推送远端。
- 每项改完跑第 6 节对应检查，数字必须与记录一致或在提交说明里解释差异。
