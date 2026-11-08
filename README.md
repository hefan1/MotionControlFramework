# MotionControlFramework

面向不同非标设备的运动控制上位机框架，提供 WPF 操作界面、C# 工位流程和任务列表两种流程入口。

## 当前范围

- 目标运行时：.NET Framework 4.8。
- 桌面界面：WPF。
- 流程方式：支持设备项目编写 C# 工位流程，也提供任务列表配置和执行入口；同一时刻只允许一个入口持有设备控制权。
- 任务模块：提供任务定义、配置引用、保存读取、校验、步骤执行和设备级工位调度。正式 Host 已装配设备任务入口；`MoveAxisGroup` 多轴分别定位、有进入/退出条件的资源范围、`CoordinatedMove` 插补尚未接入正式任务设备，必须在前序动作及控制权接管前拒绝。普通并行单轴与多轴分别定位、插补分别判断。
- 任务运行监控：按工位显示步骤与详情；正式 Host 可加载已应用的任务，关联设备运行时、轴、IO、变量和报警。当前端到端设备循环及任务暂停停轴仍需验证。
- 任务配置与编辑初版：设置 → 任务配置，支持工位安排、任务库、分类步骤编辑、设备草稿、检查与带回滚的运行文件应用。独立预览参数为 `--task-editing-preview`，不创建控制卡服务。操作和文件说明见 [`Editing/README.md`](src/MotionControl.TaskEngine.Wpf/Editing/README.md)。
- 设备总览、任务配置、列表编辑和当前报警页面已接入；使用 `--ui-workspace-preview` 查看独立四页示例，说明见 [`Operation/README.md`](src/MotionControl.Host.Wpf/Operation/README.md)。
- 设备模块：由 Host 显式引用并组合；本骨架中的 Equipment.Template 仅作新设备项目的结构参考，Host 不引用它。
- 扩展模块：运动卡、通讯、存储和 MES 适配器按独立模块接入；雷赛、正运动和 ADLINK 原生 API 已拆成独立项目，正运动另有仅供 ZDevelop 仿真器使用的首版适配器。
- 通用手动调试：已提供逻辑轴、DI/DO 手动页；设备专属动作留在设备项目。

当前已建立第一版通用运动卡、逻辑轴、逻辑 DI/DO 契约，以及单机连接/复位/启动/暂停、并行工位步骤调度、设备报警和按设备实例隔离的 JSON 存储骨架。设备卡、轴、DI/DO 映射已有语义校验；Runtime 接入逻辑急停状态，启动前及运行期间监控并锁存停机报警。通用手动调试页已实现，提供轴状态、伺服、回零、报警复位、停止、点动、软限位内定位和逻辑 IO 操作。正运动适配器目前只连接 `127.0.0.1` 上身份为 `VPLC5xx-Simu` 的 ZDevelop 离线仿真器；实体卡、雷赛和 ADLINK 驱动尚未接入。MES 协议和客户 UI 模板留待后续沟通。

## 运动控制契约进度

- 已定义卡连接、轴使能/回零/绝对与相对运动/点动/停止/报警复位，以及轴状态读取。
- 已定义按逻辑名称读 DI、读 DO、写 DO 的接口；物理点位映射由设备硬件配置提供。
- Runtime 已增加多卡连接与逻辑轴注册入口，启动连接失败时会回收本次已连接的卡，并对重复控制卡标识/逻辑轴名称报错。
- Runtime 已增加设备状态入口：连接后必须复位再启动；暂停等待当前步骤停下并保留步骤，复位后才能再次启动；工位流程并行执行，未处理流程异常会暂停整机。
- Runtime 已增加停机/非停机报警服务：停机报警请求整机暂停，条件恢复后仍需复位；非停机报警在条件恢复时自动清除。急停状态仍需由独立安全回路接入。
- Runtime 已增加按 `MachineId` 隔离的 JSON 配置存储：首次启动复制缺失模板，读取 `schemaVersion`，保存时先备份并原子替换。具体参数类型、校验和版本转换由设备项目按明确版本编写。
- Runtime 模块提供设备硬件映射全量校验器，离线检查工具已接入：检查卡、轴、DI/DO 关联和重复、软限位范围及急停 DI。当前通用配置/手动调试 Host 允许逐步配置，尚未调用全量校验；设备项目启用生产流程前必须在启动链路调用。
- Runtime 必须接入 `IMachineSafetyStatus`：复位、启动前和运行期间检查急停输入；输入有效或读取失败时触发锁存停机报警，并阻止启动/复位，急停释放后仍须复位。该软件监控不代替硬件安全回路。
- 轴动作参数使用工程单位，厂商适配器负责与脉冲单位转换；急停仍由独立安全回路处理。
- 正运动已有 ZDevelop 仿真适配器，仍不代表已接入正运动实体卡；雷赛和 ADLINK 目前只有原生 API 声明。

## 项目

| 项目 | 职责 |
| --- | --- |
| `MotionControl.Framework.Contracts` | 框架与设备、驱动模块之间的稳定契约所在位置 |
| `MotionControl.Framework.Runtime` | 单机运行状态、工位流程调度、报警、配置和追溯等通用运行时 |
| `MotionControl.Framework.Wpf` | 通用 WPF 页面与控件，包括手动调试页面 |
| `MotionControl.TaskEngine` | 任务定义、配置目录、校验、执行、资源协调及多工位调度，通过设备接口访问轴、IO 和变量 |
| `MotionControl.TaskEngine.MachineDevice` | 将任务设备接口接到当前运动卡组、逻辑 IO、变量和设备运行时；不支持的操作明确报错 |
| `MotionControl.TaskEngine.Wpf` | 任务运行监控、分类详情、列表编辑、工位配置、对象导航与独立预览；复用已有 WPF 滚动条资源 |
| `MotionControl.TaskEngine.Checks` | 任务模块离线检查，不引用 Host、WPF 或厂商适配器 |
| `MotionControl.TaskMonitor.Checks` | 任务监控树关联、内存仿真和 WPF 控件构造检查，不连接控制器 |
| `MotionControl.TaskEditing.Checks` | 草稿隔离、版本冲突、应用回滚、中断恢复与分类编辑控件检查，不连接控制器 |
| `MotionControl.Host.Wpf` | WPF 应用入口、主窗口和模块组合根 |
| `MotionControl.Equipment.Template` | 新设备项目的目录模板；不自动接入 Host |
| `MotionControl.VendorApi.*` | 各厂商的低层原生 API 声明；不被框架核心直接引用 |
| `MotionControl.Adapter.MotionCard.*` | 各厂商运动卡适配器；当前正运动实现仅支持 ZDevelop 仿真器 |

开发时遵循根目录的 [`AGENTS.md`](AGENTS.md) 项目规则，尤其是“直白优先，默认不抽象”。详细依赖和模块边界见 [`docs/architecture.md`](docs/architecture.md)；厂商 API 来源及部署依赖见 [`docs/vendor-motion-card-apis.md`](docs/vendor-motion-card-apis.md)。

任务模块的阅读顺序、使用示例和本阶段边界见 [`src/MotionControl.TaskEngine/README.md`](src/MotionControl.TaskEngine/README.md)。

## V1.8 手册对应改动

按《运动控制框架·现场交互与配置应用手册 V1.8》中的源码对照项，完善任务能力准入、准备任务引用、结构化等待诊断、数值单位与来源、示教点归属和任务应用恢复。每项修改的原因、代码入口、兼容行为和验证范围记录在 [V1.8 改动与验证说明](docs/verification/2026-11-08-v18-implementation.md)。

新增或维护任务步骤时，按 [任务节点支持矩阵与修改清单](docs/task-node-support.md) 同时核对定义、校验、执行、编辑和监控。能编辑或通过定义校验，均不能单独证明当前设备后端可执行；内存设备的检查结果也不能证明正式 Host 或实物控制器已经验收。

## 两条流程路径和维护入口

| 要做的事情 | 从哪里开始 |
| --- | --- |
| 用 C# 增加工位流程 | 先看 [`DemoStationFlow.cs`](src/MotionControl.Equipment.Demo/DemoStationFlow.cs) 和 [`DemoEquipmentDefinition.cs`](src/MotionControl.Equipment.Demo/DemoEquipmentDefinition.cs)；设备项目负责工艺步骤和设备专属配置，Host 在 [`HostEquipmentSelection.cs`](src/MotionControl.Host.Wpf/HostEquipmentSelection.cs) 显式登记设备定义 |
| 用任务列表配置流程 | 在 Host 的任务配置页安排工位和编辑任务；先保存草稿、检查，再更新任务文件。运行入口读取已应用的任务和工位配置；当前控制卡会话连接时不允许更新任务文件 |
| 理解任务如何调用设备 | 从 [`TaskMonitorLauncher.cs`](src/MotionControl.Host.Wpf/Tasks/TaskMonitorLauncher.cs) 看加载与启停；[`HostTaskSessionLoader.cs`](src/MotionControl.Host.Wpf/Tasks/HostTaskSessionLoader.cs) 负责装配设备任务会话，再看 [`ITaskDevice`](src/MotionControl.TaskEngine/Device/ITaskDevice.cs) 和 [`MachineTaskDevice`](src/MotionControl.TaskEngine.MachineDevice/MachineTaskDevice.cs) |
| 修改公共轴、IO 或报警行为 | 先找 `Framework.Contracts` 中的契约，再看 `Framework.Runtime` 对应功能；确认两条流程路径的影响 |
| 修改正运动卡专有行为 | 在 `Adapters/MotionCards/ZMotion` 查找适配器和厂商 API；设备工艺使用逻辑名称与工程单位 |
| 修改现场界面 | 先找 `Framework.Wpf` 或 `TaskEngine.Wpf` 的对应页面，再看 Host 的装配和导航；任务入口中的报警表格显示见 [`TaskAlarmGrid.cs`](src/MotionControl.Host.Wpf/Tasks/TaskAlarmGrid.cs) |

`Parameters/Equipment.json` 可指定 `StationFlows`、`TaskEngine` 或 `None` 作为执行入口；任务会话申请控制权时，运行中的 C# 工位会阻止接管，关闭会话后归还接管前的入口。两条路径的流程定义各自维护，不会自动互相转换。配置入口本身不代表设备正在运行。

代码工位示例当前是“定位 → 等待 DI → 输出脉冲 → 等待 DI 复位”；任务列表可以配置另一种步骤顺序。更改工艺时应先确认实际设备要求，不能将这两个示例当成完全相同的生产流程。

正式 Host 的任务读取和未回零复位拦截已做窗口核对；任务驱动轴、DI、报警到连续循环，以及任务暂停时真实轴停稳，尚未完成端到端验证。正运动 Host 当前只连接 `VPLC5xx-Simu`，并不等于实体卡已经接入或设备完成验收。后续扩展优先围绕具体功能逐步调整，记录修改入口、跨模块影响和对应验证，避免仅为减少文件数而合并职责。

## 新工程师：从一个上料流程开始

先确认要维护哪一种流程：手写工位看设备项目；任务列表先看上位机里的任务配置和已应用版本。下面两个例子用于定位代码，不能直接当作设备的机械动作要求。

### 路径一：C# 手写工位

1. 打开 [`DemoStationFlow.cs`](src/MotionControl.Equipment.Demo/DemoStationFlow.cs)，从 `ExecuteCurrentStepAsync` 的步骤分派开始，顺着步骤 10、20、30、40 看定位、等待输入、输出脉冲和等待信号复位。关注每步完成后在哪里设置下一步，以及取消时输出怎样收尾。
2. 看 [`DemoEquipmentDefinition.cs`](src/MotionControl.Equipment.Demo/DemoEquipmentDefinition.cs)，了解工位怎样根据设备配置创建、取得逻辑轴和 IO，以及检查引用。样例配置在 [`DemoStations.json`](src/MotionControl.Equipment.Demo/Samples/DemoStations.json)。
3. 需要修改工艺时，在对应设备项目中修改工位及其设备参数；确认动作的超时、报警、暂停后重试和输出收尾。不要把具体设备的轴号或厂商 API 写进通用 Runtime。
4. 查设备何时创建工位：从 [`App.xaml.cs`](src/MotionControl.Host.Wpf/App.xaml.cs) 的 `CreateEquipmentStationFlows` 跟到设备定义，再查 [`IStationFlow`](src/MotionControl.Framework.Contracts/Flow/IStationFlow.cs) 和 Runtime 的工位调度。`Equipment.Template` 是参考模板，不会自动成为当前设备。

### 路径二：任务列表

1. 在正式 Host 的“设置 → 任务配置”查看工位安排和任务步骤。保存草稿后先检查，再按页面条件更新任务文件；连接控制卡时目前不能更新运行文件，草稿可以保存。
2. 在“监控 → 任务接入状态”读取已应用任务。读取不自动启动；随后分别执行任务复位和启动。关闭任务会话会归还该会话取得的设备控制权。
3. 追踪代码：[`TaskConfigurationView.xaml.cs`](src/MotionControl.TaskEngine.Wpf/Editing/TaskConfigurationView.xaml.cs) 负责配置页操作，[`TaskWorkspaceStore.Apply.cs`](src/MotionControl.TaskEngine/Editing/TaskWorkspaceStore.Apply.cs) 负责应用过程；[`TaskMonitorLauncher.cs`](src/MotionControl.Host.Wpf/Tasks/TaskMonitorLauncher.cs) 负责正式 Host 的任务会话入口。
4. 找步骤执行逻辑时，从 [`TaskNodeExecutor.cs`](src/MotionControl.TaskEngine/Execution/TaskNodeExecutor.cs) 按动作类型进入 `TaskNodeExecutor.Motion.cs`、`TaskNodeExecutor.Flow.cs` 或报警处理文件；设备动作经过 [`ITaskDevice.cs`](src/MotionControl.TaskEngine/Device/ITaskDevice.cs) 进入 `MachineTaskDevice`。仅修改编辑器不会自动实现一个新动作的执行。

### 常见故障怎样找

| 现象 | 先确认什么 | 代码入口 |
| --- | --- | --- |
| 任务检查提示引用不存在 | 当前设备目录是否已有同名逻辑轴、DI、DO、变量或报警；查看完整检查信息 | [`TaskDefinitionValidator.cs`](src/MotionControl.TaskEngine/Validation/TaskDefinitionValidator.cs)、[`TaskResourceCatalogLoader.cs`](src/MotionControl.TaskEngine/Catalog/TaskResourceCatalogLoader.cs) |
| 修改步骤后运行结果未变 | 页面是否只保存了草稿、是否更新任务文件、任务会话是否重新读取 | [`TaskWorkspaceStore.cs`](src/MotionControl.TaskEngine/Editing/TaskWorkspaceStore.cs)、[`TaskMonitorSession.cs`](src/MotionControl.TaskEngine.Wpf/Simulation/TaskMonitorSession.cs) |
| 不能启动、手动操作被拒绝 | 先看界面提示、当前执行入口及任务会话、报警和轴状态；未回零或读取失败应先处理 | [`MachineRuntime.cs`](src/MotionControl.Framework.Runtime/Machine/MachineRuntime.cs)、[`TaskMachineController.AxisReadiness.cs`](src/MotionControl.TaskEngine/Machine/TaskMachineController.AxisReadiness.cs) |
| 原点、限位或轴动作异常 | 对照逻辑轴映射、控制器状态和报警；保留轴名、动作与原始错误码 | [`ZMotionCard.AxisOperations.cs`](src/Adapters/MotionCards/ZMotion/Adapter/ZMotionCard.AxisOperations.cs)、[`ZMotionCard.AxisStatus.cs`](src/Adapters/MotionCards/ZMotion/Adapter/ZMotionCard.AxisStatus.cs) |
| 等待超时却没有预期报警 | 核对等待步骤关联、报警库和上层步骤时限，再查当前报警状态 | [`TaskNodeExecutor.Alarms.cs`](src/MotionControl.TaskEngine/Execution/TaskNodeExecutor.Alarms.cs)、[`MachineAlarmService.cs`](src/MotionControl.Framework.Runtime/Alarms/MachineAlarmService.cs) |

## 构建和验证的边界

Host 为 .NET Framework 4.8、WPF 项目，默认目标为 x64。Windows 开发环境需要能构建 .NET Framework 4.8 的工具链；正运动原生运行库的本机放置位置及位数要求见[适配器说明](src/Adapters/MotionCards/ZMotion/Adapter/README.md)。在仓库根目录构建正式 Host：

```powershell
dotnet build src/MotionControl.Host.Wpf/MotionControl.Host.Wpf.csproj -c Release
```

在 Windows 上统一构建正式 Host 并运行本次涉及的独立检查：

```powershell
./tools/Verify-V18.ps1
```

同一脚本已接入仓库的 Windows 自动检查。各项目独立输出，构建失败的项目不会运行旧程序；任意一项失败，整体检查仍标记失败并汇总原因。

目标为 `src/MotionControl.Host.Wpf/bin/Release/net48/MotionControl.Host.Wpf.exe`。若文件被运行中的 Host 占用，先确认窗口和进程路径，再按项目规则安全退出该实例。不要通过改写用户的 Machine01 配置来解决构建问题。

选择与改动相关的检查：任务定义或执行看 `tools/MotionControl.TaskEngine.Checks`，设备任务接入看 `tools/MotionControl.MachineDevice.Checks`，Runtime 看 `tools/MotionControl.ZMotionSimulatorCheck --runtime-regression`，操作页逻辑看 `tools/MotionControl.OperatorPages.Checks`。具体构建和运行命令可从 [`HANDOFF.md`](HANDOFF.md) 第 6 节查找；其历史结果和设备状态需要重新核对，不能直接当成当前状态。

内存检查只证明检查覆盖的代码行为；正式界面需在正式 Host 实际操作并观察，控制器动作需核对所用控制器和信号状态。测试后保留 Machine01 的测试配置，停止轴并将本轮改动过的虚拟 IO 恢复安全值。现场硬件、接线和机械行为需要在对应设备上单独验证。
