# 任务模块：先从这些文件读起

当前完成阶段 1 的定义/存储/校验、阶段 2 的纯软件仿真执行器，以及阶段 3 的资源仲裁和显式互锁。
在此基础上已加入同卡轴组定位，各轴独立目标与速度，全部到位后继续。
设备级入口也已加入：任务清单、独立循环/触发工位、可选初始化、统一复位启停、故障当前步骤恢复和连续生产监控。
执行器只接收独立的 `SimulatedTaskDevice`，不连接控制器或 Host。编辑器页面尚未开发；定义校验通过不等于允许实际设备运行。

## 文件如何定位

| 文件夹 | 要找什么 |
|---|---|
| `Definitions` | 任务由哪些节点组成，每种节点有哪些参数 |
| `Catalog` | 现有轴、DI/DO、变量、示教点从哪里来 |
| `Validation` | 为什么任务不能保存，哪个节点或字段有问题 |
| `Configuration` | JSON 如何读取，版本如何检查，旧文件如何保留 |
| `Execution` | 顺序、并行、分支如何运行，怎样暂停/取消，错误在哪个节点 |
| `Simulation` | 纯内存的轴、DI/DO、变量；没有真实设备服务 |
| `Resources` | 整组申请、冲突顺位、持有/等待/待确认诊断 |
| `Machine` | 设备任务清单、独立工位循环、请求交接、整机复位启停和监控快照 |
| `Samples` | 可直接阅读的示例任务；不会自动写入现场目录 |

建议阅读顺序：`TaskDefinition` → `SequenceNode` → `MoveAxisNode` → `TaskResourceCatalogLoader.Load` → `TaskDefinitionValidator.Validate` → `TaskDefinitionStore.Save`。

调用关系只有一条明确主线：

```text
已有设备 JSON → TaskResourceCatalogLoader → 只读目录
任务定义 + 只读目录 → TaskDefinitionValidator → 中文错误/提示
通过定义检查 → TaskDefinitionStore → 现有 MachineConfigurationStore 原子保存
```

## 现有配置怎么复用

以下路径都位于 `MachineConfigurationStore.GetMachineDirectory(machineId)` 返回的设备目录内：

- `HardwareConfig/AxisParameters.json`：逻辑轴、所属卡、工程单位和行程；不猜测缺失单位。
- `HardwareConfig/IoMapping.json`：分别读取逻辑 DI 和 DO，不混用方向。
- `Parameters/DeviceVariables.json`：变量名称和类型；不读取或写入当前生产值。
- `Parameters/ManualAxisPositions.json`：按现有 `ControllerId.Axis轴号` 找到逻辑轴，再按点位 ID 引用。

前两个文件必须存在。后两个可选文件缺失时返回空列表并记录提示；存在但损坏时会报错。
旧示教点找不到对应物理轴键时只报告未匹配，不跨卡猜测替换关系。
目录是本次读取的只读副本。现场修改映射、点位或变量定义后，需要重新读取并重新校验；它不是实时设备状态快照。

## 最小使用方法

```csharp
// 只读加载现有设备配置，不连接控制器。
var configuration = new MachineConfigurationStore(configurationRoot);
var catalog = new TaskResourceCatalogLoader(configuration).Load("Machine01");
var tasks = new TaskDefinitionStore(configuration);

// 正常读取入口会检查当前目录中的引用。
TaskDefinition definition = tasks.Load("Machine01", "TransferCycle.json", catalog);

// 修改后先查看全部提示；Warning 也需要在后续执行阶段处理。
var issues = new TaskDefinitionValidator().Validate(definition, catalog);
foreach (TaskValidationIssue issue in issues)
{
    Console.WriteLine(issue.ToString());
}

// 修改已保存定义时递增修订号；有定义错误时 Save 不覆盖旧文件。
definition.Revision++;
tasks.Save("Machine01", "TransferCycle.json", definition, catalog);
```

上例类型分别位于 `MotionControl.Framework.Runtime.Configuration`、本项目的 `Catalog`、`Configuration`、`Definitions`、`Validation` 命名空间。
任务保存位置是设备目录下的 `Tasks/文件名.json`。不能用文件名跨目录写入。
覆盖成功时 `Save` 返回旧文件备份路径；首次保存或内容完全相同时返回 null。
旧文件损坏或版本不支持时拒绝覆盖，先人工核实备份后处理。

有子任务时，先用 `LoadForEditing` 读取完整任务库，再将其他任务作为 `dependencies` 传入 `Validate`、`Load` 或 `Save`。`LoadForEditing` 只检查 JSON 格式，不能代替引用校验。主任务不要再次放入 dependencies。

## 节点规则

- 一个任务有一个 `Sequence` 根节点。节点 ID 不随名称和排序改变。
- `ParallelGroup` 至少两个 `Sequence` 分支，含义固定为全部成功后汇合；它不是插补。
- `CoordinatedMove` 只定义同卡、统一 mm 单位的多轴绝对直线运动。卡能力、最大轴数、组完成和组停止尚未验证，因此总是给出待核实提示。
- `MoveAxis` 必须明确 Absolute 或 Relative，并填写工艺速度/加减速度。不能把手动页速度暗中当作生产速度。
- 位置可来自常量、变量或同一轴的示教点；相对距离不能引用绝对示教点。
- 条件只有 Compare、All、Any、Not；不允许任意脚本。字符串和布尔只支持相等/不等。
- 数值常量使用不受界面语言影响的小数点格式。DateTime 常量使用 ISO 8601 往返格式，例如 `2026-10-22T10:30:00.0000000+08:00`。
- 变量类型沿用已有配置名称：Boolean、Integer、Number、String、Byte、Character、DateTime、UInt32；后续 UI 可显示 bool、int、double、string、byte、char、DateTime、uint。
- 赋值仅允许同类型或当前整数类型到 double；不把浮点数隐式截断为整数。
- 轴资源名称为 `Axis:逻辑轴名`，输出为 `Output:逻辑输出名`，均由目录自动登记。区域等命名资源由调用方明确传入；自定义名称不能使用这两个保留前缀。
- JSON 拒绝未知节点、未知字段、数字枚举、重复字段和 `$type` 等类型元数据。

## 阶段 2：先这样阅读执行器

推荐顺序：`TaskExecutionEngine.Start` → `TaskNodeExecutor.ExecuteAsync` → `ExecuteBodyAsync`，再按具体功能查看 `Flow` 或 `Motion` 文件。

```text
Start：校验定义 → 深复制冻结 → 检查本阶段能力 → 后台开始
ExecuteAsync：进入节点 → 直接调用具体动作 → 等待清理 → 记录结果
ParallelAsync：启动独立分支 → 全部等待 → 任一失败取消其余分支 → 等全部退出
TaskRun：Pause / Resume / Cancel / GetSnapshot / Completion
```

没有新的服务容器、反射分发或动态脚本；`TaskNodeExecutor` 按职责分文件，类型判断后直接调用方法。现有 `IStationFlow` 代码流程和 Host 运行方式不变。

### 怎样启动内存仿真

下面代码放在自己的异步测试方法中；`catalog`、`definition` 使用上文加载方法取得，示例假设逻辑轴名为 X、Y 且行程包含 0。测试场景必须明确给定起始状态，仿真器不会读取现场状态或生产恢复文件。

```csharp
// 只创建内存模型；ConfigureAxis 不会向真实伺服写任何指令。
var device = new SimulatedTaskDevice(catalog);
device.ConfigureAxis("X", position: 0, enabled: true, homed: true);
device.ConfigureAxis("Y", position: 0, enabled: true, homed: true);

// 输入和变量默认无效；按本次测试场景提供明确的值。
device.SetInput("PartReady", true);
device.SetVariable("Vision.OffsetX", "12.5");

var engine = new TaskExecutionEngine();
TaskRun run = engine.Start(definition, device);
// 其他调用入口可使用 run.Pause()、run.Resume() 或 run.Cancel()。
TaskRunSnapshot result = await run.Completion;
if (result.State == TaskRunState.Failed)
    Console.WriteLine(result.Error.ToString());
```

新增命名空间为 `MotionControl.TaskEngine.Execution`、`MotionControl.TaskEngine.Simulation`。示例逻辑名称必须在自己的目录中存在。主任务有子任务时，仍将其他定义作为 `Start` 的 `dependencies` 参数。

### 暂停、超时和取消

- `Pause()` 冻结内存动作提交和活动时钟；`Resume()` 从内存冻结点继续。已接受的相对移动保留原来的绝对终点，修改目标变量不会改变这次动作。
- 这是阶段 2 的内存冻结语义，不能直接用作实际卡的暂停，也没有替换 Host 的“暂停后复位再启动”规则。
- 每个节点的 `TimeoutMs` 包含它的子动作；暂停时间不计入。等待通过异步轮询检查约每 10ms 的状态，不能承诺 Windows 的实时调度精度。延时应给总时限留出调度余量。
- `Cancel()` 只是发出请求；必须等待 `Completion` 才表示分支全部退出、内存轴占用清理完毕。暂停期间同样可取消。
- 失败优先保留触发并行取消的原始故障；因它退出的其他节点标为 Cancelled，主运行标为 Failed。
- 完成、失败或取消后的运行不能恢复。需要新的运行时重新调用 Start；本模块尚无跨进程检查点恢复，不读取现有设备恢复文件。
- 内存 DO 和变量保留已经执行的值，不做未定义的自动回滚或全部清零；取消后不执行后续节点。

### 当前能执行什么

| 节点 | 当前内存执行行为 |
|---|---|
| Sequence、ParallelGroup | 顺序执行；独立分支全部成功后汇合 |
| ConditionBranch、CallTask | 单次快照判断分支；子任务正常返回，调用状态独立 |
| Delay、WaitInput、WaitCondition | 可暂停/取消、有时限、有等待对象说明 |
| SetVariable、SetOutput | 写独立内存变量或 DO；变量类型复用现有实现 |
| MoveAxis | 匀速单轴模型，检查使能、回零参考、报警和动态终点范围 |
| MoveAxisGroup | 同卡轴组各自定位，统一等待、暂停与异常收尾，全部到位后完成 |
| AxisCommand.Home | 100ms 内存回零占位，成功建立仿真参考；不模拟任何厂商回零模式 |
| AxisCommand.Stop | 只确认闲置轴；不夺取其他动作控制权，停止自己的动作应取消所属运行 |
| ResourceScope | 整组申请 → 进入检查 → 顺序正文 → 退出确认 → 释放；异常正文转为待确认 |
| CoordinatedMove | 启动前明确拒绝，包括未选中的分支和加载的子任务 |

Move/Home 自动申请单根轴，MoveAxisGroup 一次申请整组轴，SetOutput 自动申请该输出；冲突会排队，独立资源可以并行。排队取得轴后才读取目标；接受运动后继续固定终点。并行单轴和轴组定位均不代表协调插补。

变量默认无效，等待条件可以等到其有效；分支选择、变量赋值来源或运动目标无效则失败。All/Any/Not 中遇到未知子项也保持未知。一次条件求值使用同一份变量快照。有时区的 DateTime 按 UTC 时刻比较；不能混用有时区和没有时区的日期。

运行快照保留 `RunId`、`TaskId`、`NodeId`、独立 `ExecutionId` 和 `ParentExecutionId`，同一子任务被重复调用也不会覆盖状态。`Nodes` 只包含已进入的调用，不包含尚未进入或未选择的定义分支。全局暂停看 `TaskRunSnapshot.State`；节点保留其暂停前的等待说明。每次最多展开 10000 个节点调用。

## 阶段 3：资源范围怎样使用

阅读顺序：`ResourceScopeNode` → `TaskNodeExecutor.Resources.cs` → `TaskResourceCoordinator` → `SimulatedTaskDevice.Interlocks.cs` / `SimulatedTaskDevice.Resources.cs`。

```text
登记完整资源组 → 等待冲突者退出 → 一次取得整组
    → 等待进入条件 → 执行顺序正文（含子任务）
    → 等待退出确认 → 释放整组

正文开始前失败/取消 → 删除申请
正文开始后失败/取消 → 停下自己的内存动作 → 整组 Blocked（待确认）
```

### 声明范围

`Samples/ResourceTransfer.json` 是可运行的内存例子：申请 `Zone:Camera`、`Axis:X`、`Output:Vacuum`，等待 PartReady 且 X 在 0，往返 10 → 0，确认 X 停在 0 后释放。例子假定目录行程包含这些位置，区域名称由场景明确登记；0 并不天然代表真实设备安全位。

- `Resources` 一次列齐正文、所有被调用子任务以及互锁使用的轴/输出。漏填会在启动前定位到范围和实际使用节点。
- 同一区域可能涉及不同的轴：把相同的区域名写入各自范围，才能互相排队。只申请不同轴不会产生区域互斥。
- 范围内禁止再嵌套 `ResourceScope` 或 `ParallelGroup`，包括子任务内部。并行组织方式是 `ParallelGroup → 各分支自己的 ResourceScope → 顺序正文`。
- 范围内的子任务继承凭证，不重复申请；范围外的 Move/Home/SetOutput 自动申请单项资源。
- `AxisCommand.Stop` 只确认停止，不申请或抢占其他动作；需要停止某运行时取消它并等待 Completion。

### 进入和退出条件

`EntryInterlock` / `ExitInterlock` 均为可选对象，可同时配置以下条件，全部满足才通过：

| 字段 | 要求 |
|---|---|
| `Axes` | 每项明确 AxisName、MinimumPosition、MaximumPosition；包含端点。必须停止、使能、无报警；RequireHomed 默认 true |
| `Inputs` | 每项明确 InputName 和 Expected；只接受逻辑 DI，未知不等于 false |
| `Condition` | 复用已有变量 Compare/All/Any/Not；无效值继续等待 |

互锁中的轴同样纳入本范围资源，避免核对位置时被其他任务移动。进入条件是在正文前检查一次；不会在整个动作期间连续检查。变量和 DI 可能随后变化，本功能不代替持续安全监控。

旧 `ExitCondition` 可继续读取和执行，但不能同时填写 `ExitInterlock`。省略退出条件代表作者允许正文正常完成即让出；框架不会自行推断区域清空。定义包含互锁对象时不能为空。

### 等待、暂停与诊断

每个 `SimulatedTaskDevice` 有独立协调器；操作同一个实例的运行共用队列。完整组要么一起取得、要么全部等待；相冲突的较早等待者优先，互不冲突的申请可并行。暂停的等待者保留顺位，相冲突的后来者不能插队；取消或超时会移除等待记录。

`device.ReadResources()` 返回只读副本：RequestId、RunId、TaskId、NodeId、ExecutionId、资源组、Waiting/Held/Blocked 和原因。任务节点的等待详情及超时错误同样保留互锁条件或阻塞者身份。

这些规则避免“占有一组再申请另一组”的资源锁死锁。等待外部 DI/变量造成的工艺互等仍可能发生，应使用有界超时定位，不能宣称已经自动检测所有业务死锁。

### 异常后恢复

正文异常退出时，资源会保留 Blocked；已写输出和变量保留当时值，不自动回滚。`Completion` 表示动作已经收尾，**不表示区域已让出**。

```csharp
// requestId 来自 ReadResources() 中需要核实的 Blocked 记录。
// 调用方已检查场景后才传入确认说明；返回 false 时显示 reason，不能当作恢复成功。
string reason;
bool recovered = device.TryRecoverResources(requestId, "已检查工件和区域状态", out reason);
Console.WriteLine(reason);
```

恢复会再次核对整组轴已停止、无报警及原退出条件，不允许空确认或旧申请编号。原本没有配置退出条件时，确认说明代表调用方已人工核实；它不是权限认证。恢复只释放该记录，不复位报警、不改轴位置、不改输出，也不重启旧任务。

若实际位置不满足退出条件，本阶段会继续保留 Blocked；尚未提供占用状态下的人工点动撤离入口。`ConfigureAxis` 也不能覆盖已占用或待确认轴来绕过检查。后续实际设备接入需要单独实现权限、撤离操作、停止确认和恢复审计。

## 轴组定位怎样使用

阅读顺序：`Definitions/MoveAxisGroupNode.cs` → `TaskNodeExecutor.AxisGroups.cs` → `SimulatedTaskDevice.AxisGroups.cs`。相关方法分别负责定义、资源申请/目标解析，以及整组检查/逐轴推进/收尾。

```csharp
// 每根轴明确指定自己的目标和工艺参数；此定义交给现有 TaskExecutionEngine。
var move = new MoveAxisGroupNode
{
    Id = "toCamera", Name = "XY 定位到拍照位置", TimeoutMs = 5000,
    Targets = new List<AxisPositionTarget>
    {
        new AxisPositionTarget
        {
            AxisName = "X", Mode = TaskMoveMode.Absolute,
            Target = new TaskValueBinding { Source = TaskValueSource.Constant,
                DataType = DeviceVariableType.Number, Constant = "10" },
            Profile = new TaskMotionProfile { Speed = 50, Acceleration = 100, Deceleration = 100 }
        },
        new AxisPositionTarget
        {
            AxisName = "Y", Mode = TaskMoveMode.Absolute,
            Target = new TaskValueBinding { Source = TaskValueSource.TeachPoint,
                AxisName = "Y", PointId = "P001" },
            Profile = new TaskMotionProfile { Speed = 25, Acceleration = 50, Deceleration = 50 }
        }
    }
};
```

例子需要 `System.Collections.Generic`、`MotionControl.TaskEngine.Definitions` 和 `MotionControl.Framework.Contracts.Variables` 命名空间。X/Y、Y 的 P001 示教点及行程必须在当前目录中存在。完整 JSON 往返样例是 `Samples/AxisGroupPositioning.json`，检查程序会实际读取并执行它。

- 首版支持同一 ControllerId 的 2–64 根不重复逻辑轴；上限仅限制文件规模，实际卡轴数还须核实。直线轴和旋转轴可组合，每轴使用自己的工程单位和速度，没有混合单位的路径速度。
- 每项的 Mode、Target、Profile 必须明确。常量、变量和示教点沿用单轴规则；相对距离不能引用绝对示教点。
- 整组取得资源后，使用同一份变量快照读取目标，并在同一设备锁内检查所有起点、状态、终点和有限运动时长；任一项不合法，组内任一轴都不接受动作。单卡 SDK 的真实逐轴发指令是否能这样安排，仍须后续接入核实。
- 各轴独立速度和进度；快轴先到位时也仍保留该轴资源，等待慢轴结束。暂停冻结剩余内存进度，恢复保留原终点，不重新读取目标或叠加相对距离。
- 任一轴报警，包括先到位的轴报警，都让本组失败；取消和超时也会先停下本组未完成动作。诊断保留轴名、当前/目标位置、到位状态及原始原因，后续节点不继续执行。
- 组外动作自动申请整组；放入 ResourceScope 时，范围必须列齐组内全部 Axis 资源，子任务中的组动作也继承同一凭证。异常范围仍遵循 Blocked/显式恢复规则。
- `先抬 Z → XY 轴组定位 → 再降 Z` 应写成清楚的顺序节点；框架不通过轴名猜测安全顺序。需要控制中间轨迹时使用独立的 CoordinatedMove 能力，当前执行入口仍拒绝该未实现能力。

内存模型继续按匀速模拟，Profile 中的加减速度经过校验但尚不模拟伺服加减速；本功能不保证真实卡同时启动或同时到达，不提供组内任一成员单独回零/停止指令。

## 设备级运行：多个工位各自生产

`ParallelGroup` 是一个任务内部的并行汇合。整机生产用 `TaskMachineController`：每个工位独立执行自己的任务周期，不用等待所有工位一起完成才开始下一周期。

### 清单与正文分开

- 任务正文仍是 `Tasks/*.json`，保存步骤与参数。
- 设备清单固定是 `Parameters/TaskMachine.json`，由 `TaskMachineConfigurationStore` 保存。它记录设备标识、修订号、可选 `initializationTaskId` 和 `stations`。
- 一个工位明确填写 `stationId`、`name`、`taskId`、`enabled` 和 `mode`。`mode` 为 `Once`（会话内一次）、`Cyclic`（独立循环）或 `OnRequest`（请求触发）。不同工位可以引用同一份任务，各有自己的运行实例。
- `cycleIntervalMs` 默认 20，允许 1–60000 毫秒；是周期之间的轮询间隔，不是轴动作周期，也不提供实时调度保证。
- OnRequest 还需 `requestVariable` / `completedVariable`，引用两个不同的已有 bool 变量。各触发工位的交接变量不能重复分配；其他模式不填写这两项。
- 初始化任务不能同时作为自动工位任务。工位、任务定义在创建控制器时冻结，修改配置不会偷偷改变运行中的步骤。
- 保存继续检查设备目录、版本、引用和修订备份；缺少 enabled/mode、未知字段或枚举、损坏文件会被拒绝。最多 128 份任务、128 个工位属于文件规模限制，不代表实体设备能力。

传入控制器的是本次执行库。库中全部定义都要合法且已实现，包含未调用分支中的未支持动作也会在启动前拒绝；编辑器可保留草稿，但不能把草稿直接加入执行库。

### UI 将来调用哪个入口

```csharp
// configurationRoot、machineId、definitions、catalog 由调用方明确提供。
// definitions 包含本次各工位及它们调用的子任务，不重复任务标识。
var store = new TaskMachineConfigurationStore(new MachineConfigurationStore(configurationRoot));
var configuration = store.Load(machineId, definitions, catalog);
var device = new SimulatedTaskDevice(catalog);

// 本例假定 X 在目录中；其他轴和信号也必须按测试场景明确配置。
device.ConfigureAxis("X", position: 0, enabled: true, homed: true);
var machine = new TaskMachineController(configuration, definitions, device);
try
{
    machine.Reset();  // 检查条件，通过后是 Ready，未开始动作。
    machine.Start();  // 首次先执行可选初始化，然后各工位独立运行。
    TaskMachineSnapshot snapshot = machine.GetSnapshot();
    Console.WriteLine(snapshot.State);
    machine.Pause();  // 普通停止按钮映射到这里，保留当前步骤。
    machine.Reset();
    machine.Start();  // 从当前中断处继续。
}
finally
{
    await machine.CloseAsync(); // 先等待所有运行收尾，再归还内存设备。
}
```

新增命名空间 `MotionControl.TaskEngine.Machine`。上例仅说明组合和控制顺序，立刻暂停不代表产品已完成；实际测试可按快照等待指定周期数。目录内信号默认未知，OnRequest 场景需给出明确初值。

| 操作 | 实际行为 |
|---|---|
| Reset | 首次/暂停/故障后检查有效停机条件、目录内轴报警、Blocked 区域和不可恢复执行器；通过后清锁存到 Ready，不清轴报警、不使能、不回零、不推进工位 |
| Start | 只接受 Ready；首次创建后台工位，后续放行原运行，不重复启动线程或重复初始化 |
| Pause | 冻结各运行的内存动作、活动计时及周期/交接提交；之后必须 Reset 再 Start |
| SetStopAlarm | active=true 锁存并暂停整机；active=false 只解除条件，仍须复位启动 |
| AbortAsync | 明确终止，取消并等待全部工位/动作收尾；旧会话不能复位续跑；不擅自复位输出和区域 |
| CloseAsync | 等待终止收尾后释放设备所有权；重复调用等待同一关闭任务 |

Reset 不统一要求轴已经使能或回零，免回零场景可不配置初始化；具体动作仍按其前置条件检查。初始化内存 Home 也只是生命周期模拟。实际设备回零方式应由后续运动适配层明确实现。

### 请求与完成如何交接

```text
请求方：Request=true ──等待 Complete=true──> Request=false ──等待 Complete=false──> 下一件
完成方：等 Request=true 且 Complete=false ──执行一次任务──> Complete=true
        └──────────────────等 Request=false 后清 Complete=false──────────────────┘
```

只有请求方写 Request，完成方的控制器写 Complete；任务正文可以请求下游工位，但不要自行写本工位 Complete。完成信号未知时不猜 false；旧 Complete=true 时先等请求复归；请求保持 true 不会重复执行。任务成功前请求被撤销会锁存交接故障，恢复请求、复位启动后只发布完成，不重做产品动作。

等待上游请求、初始化和周期间隔在单次 TaskRun 外面进行，不消耗动作节点时限。当前交接不含产品缓存或信号依赖图；业务互等靠明确步骤时限和等待详情定位。

### 故障后究竟重做哪一步

设备控制器管理的节点故障会先局部收尾，再暂停整机并保留当前异步调用进度。修复条件后 Reset、Start，只重进失败节点；已成功的顺序动作、已完成并行分支、已选中的分支不会从任务根重放。当前调用链重新获得节点时限。

- MoveAxis/MoveAxisGroup 在接受动作时保存绝对终点，中途故障恢复不重新叠加相对位移，变量目标后续改变也不覆盖已接受终点。
- 故障前尚未接受动作时，修复后重新读取当时目标与轴状态。
- ResourceScope 中可恢复步骤仍留在原范围；退出条件超时只重查退出，正文不再执行；明确终止才展开清理，正文已进入的范围保留 Blocked。
- 展开超过每周期 10000 个节点调用、执行器异常退出等情况禁止复位续跑，必须终止并核实状态，避免整段重放。
- 原独立 `TaskExecutionEngine.Start` 仍然失败即终态；上述等待复位行为只属于设备控制器入口。

整机控制器持有内存设备期间，禁止另一个控制器或独立 Start 绕过整机许可。场景的变量/输入故障注入仍可用，这些入口用于测试，不是现场手动控制权限。

### 监控和连续生产样例

`GetSnapshot()` 返回整机状态、初始化状态、工位阶段/成功周期数/当前或最近运行、锁存故障，以及共享资源 Waiting/Held/Blocked。节点含任务与调用编号、中文步骤和等待原因。旧快照不随运行改写，UI 无须保存可变 TaskRun。

`Samples/ProductionDemo` 有上料、检测、下料三份任务正文和设备清单。检查命令实际读取这些 JSON，逐件传递编号、核对 30 件产品及最终动作次数，不是用 C# 循环替代任务正文：

```powershell
& tools/MotionControl.TaskEngine.Checks/bin/Debug/net48/MotionControl.TaskEngine.Checks.exe --production-demo
```

预期每工位成功 30 周期，X/Y 回到 0，旋转 R=30；X/Y 各 60 次、R 共 30 次动作，无遗留资源。它只操作独立内存设备，没有使用 ZDevelop、原生 SDK、Host 或实体 IO。

本会话暂停恢复不等于软件重启恢复。清单持久化不保存异步调用栈、产品在制信息或资源占用；已有 Host 设备变量恢复不能自动恢复这些任务执行进度。

## 后续阶段仍要做什么

实际控制器的回零/位置参考、伺服、报警、限位、运动到位、停止确认，仍需设备适配层接入；内存模型通过不代表这些硬件行为已验证。
变量质量目前只覆盖已有 HasValue，采集周期/请求关联和数据新鲜度尚未接入。机械几何避让、实际区域清空验证、业务信号依赖图、相对定位重启恢复和并行分支检查点属于后续阶段。资源状态仅保留在当前内存设备内，重新创建设备或重启程序不会恢复 Blocked；禁止将此行为直接用于生产恢复。
通讯、计算动作、子任务参数绑定暂不接受对应 kind，避免保存一个会被默默跳过的动作。

## 离线检查

使用已安装的 Visual Studio 18 MSBuild：

```powershell
& 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe' tools/MotionControl.TaskEngine.Checks/MotionControl.TaskEngine.Checks.csproj /restore /t:Build /p:Configuration=Debug /nologo
& tools/MotionControl.TaskEngine.Checks/bin/Debug/net48/MotionControl.TaskEngine.Checks.exe
```

将 Debug 改成 Release 可重复检查发布构建。检查只在自己的随机临时目录写文件并清理，不使用现场配置。
也可传 `--catalog 配置根目录 MachineId`，只读查看现有配置是否能形成共享目录，不写文件。
