# 交接说明（任务执行接入真实设备）

本文件是"任务定义 → 真实运动控制卡"这条线的交接点。接手时先读 `AGENTS.md`，再读本文件，然后按第 7 节的第一件事开始。计划文档 `docs/plans/2026-10-31-task-device-binding.md` 的部分步骤已被调整，**以本文件为准**。

## 1. 当前状态（判据）

| 项目 | 值 |
|---|---|
| 基线提交 | `ffd587e`（本轮实现的提交前基线） |
| 工作区 | 本轮包含任务执行接入、真实轴暂停/报警处理和复位预检查；精确状态以 `git status --short` 为准 |
| 构建 | MachineDevice 检查现为 9 组、OperatorPages 检查现为 15 组；TaskEngine 23/53/16 组、Runtime 35 项为历史记录，构建与检查结果需按第 6 节重新确认 |
| 正式 Host | 历史联调中 Load_X 在 -90 mm 停止且未回零；控制器会话与轴位置可能变化，继续测试前必须在正式 Host 重新核对 |
| 配置根 | `C:\Users\ASUS\AppData\Local\MotionControlFramework\Configurations\Machine01` |
| 执行入口 | 2026-11-01 验证任务页面后，`Parameters/Equipment.json` 的 `executionOwner` 为 `TaskEngine`；`DemoStations.json` 仅启用上料定位工位，其余三个工位保留配置但已禁用。两种流程的测试配置均保留，切换入口后需重新加载 |

一句话：**正式 Host 中，任务列表和 C# 上料工位均已验证未回零轴会被拦截；生产循环尚未端到端验证。** 历史联调中 Load_X 配置为 DATUM 模式 3、原点 DI8，但回零搜索实际向负方向移动。2026-11-01 已确认 DI8 可以切换，且 Host 原点显示随之变化；控制器回零有效电平和当时负向动作原因仍未证实，查清前不再发回零或循环命令。

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

## 3. 已定决策（不要推翻）

1. **两条执行路径并存，同一时刻只有一个能启动**；判据是"谁正在控制设备"，不是"配置了哪种入口"。入口由 `executionOwner` 显式指定。
2. **暂停一律立即受控停止当前轴**，不停在半路等步骤做完。
3. **真机轴组插补不支持时必须明确报错**，不许退化成逐轴顺序移动。
4. **真机适配器独立成工程**，设备定义与厂商无关。
5. **验收基准是 ZSimu**：Host → 正运动适配器 → SDK → VPLC5xx-Simu 与连实体卡是同一份代码，其结果就是设备验证结论。只有伺服动态、编码器、限位/急停回路、接线、机械负载、总线时序需要实物。
6. **不纠结 20009**：只在它直接阻碍当前验证时做最小确认并记录，不反复重连。

## 4. 未完成（阶段 C 剩余）

- **正式 Host 生产循环**：任务入口、任务装配、受控暂停/停止和共享报警接线已在当前工作区实现，软件回归通过；正式 Host 已验证任务复位会在 Runtime 复位前拦截未回零定位轴。仍未完成“任务 → 轴动 → DI 变化 → 报警进页面 → 连续循环”的端到端运行。
- **Load_X 回零方向与有效电平**：历史模式 3 运行时从 0 向负方向移动并在 -90 mm 受控停止。2026-11-01 重启控制器后，Host 显示轴 1 在 0 mm 且未回零，DI8 切换可使 Host 原点状态变化并已恢复；控制器输入表显示 DI8 为 `datum_in(1)` 且输入反转关闭。厂商手册对不同控制器族的有效电平有区别，历史负向动作是否为反找尚未确认。不要为了继续流程重复回零；先核实该控制器的原点有效状态和模式行为。
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

# 1) 真机任务设备检查：期望 9 组，失败 0
& $dotnet build tools/MotionControl.MachineDevice.Checks/MotionControl.MachineDevice.Checks.csproj -c Release -o tmp/check-md -v:q
& .\tmp\check-md\MotionControl.MachineDevice.Checks.exe

# 2) 任务引擎检查：期望 23 / 53 / 16 组，失败 0
& $dotnet build tools/MotionControl.TaskEngine.Checks/MotionControl.TaskEngine.Checks.csproj -c Release -o tmp/check-te -v:q
& .\tmp\check-te\MotionControl.TaskEngine.Checks.exe

# 3) Runtime 回归：期望 35 项全部通过（33 Runtime + 1 工位配置 + 1 ZMotion）
& $dotnet build tools/MotionControl.ZMotionSimulatorCheck/MotionControl.ZMotionSimulatorCheck.csproj -c Release -o tmp/check-rt -v:q
& .\tmp\check-rt\MotionControl.ZMotionSimulatorCheck.exe --runtime-regression

# 4) 操作员页面检查：期望 15 组，失败 0（含任务状态和 IO 关联反馈检查）
& $dotnet build tools/MotionControl.OperatorPages.Checks/MotionControl.OperatorPages.Checks.csproj -c Release -o tmp/check-ops -v:q
& .\tmp\check-ops\MotionControl.OperatorPages.Checks.exe
```

任何一套数字变化，都说明行为被改动了，先查清再提交。

## 7. 第一件事

先解决 Load_X 回零有效电平与历史方向异常的验证阻塞。2026-11-01 已在正式 Host 验证 DI8 切换与原点显示联动，并恢复原值；两种执行入口均因 Load_X 未回零而被拦截。须确认控制器实际原点有效电平、反转参数与初始原点有效时的回零行为；在此之前保持 Load_X 停止，不运行回零和任务循环。

阻塞排除后，再通过正式 Host 完成 Load_X 回零、任务复位、启动/暂停/恢复/停止和连续周期验证，并逐页核对 DI/DO 与共享报警。配置目录中的测试配置按项目规则保留；所有轴停止，测试输入输出恢复安全值。

## 8. 提交约定

- 中文、简短、说明实际改动；一次一项。
- 提交前 `git diff --cached` 核对，只提交本次涉及的文件。
- 只提交本地，不推送远端。
- 每项改完跑第 6 节对应检查，数字必须与记录一致或在提交说明里解释差异。
