# 任务模块：先从这些文件读起

当前完成阶段 1 的定义/存储/校验，以及阶段 2 的纯软件仿真执行器。
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
- 轴资源名称为 `Axis:逻辑轴名`；区域等命名资源由 Host 明确传给目录。完整资源占用调度和自动避让留在后续阶段；当前内存设备只拒绝同轴同时动作。
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

| 节点 | 阶段 2 的行为 |
|---|---|
| Sequence、ParallelGroup | 顺序执行；独立分支全部成功后汇合 |
| ConditionBranch、CallTask | 单次快照判断分支；子任务正常返回，调用状态独立 |
| Delay、WaitInput、WaitCondition | 可暂停/取消、有时限、有等待对象说明 |
| SetVariable、SetOutput | 写独立内存变量或 DO；变量类型复用现有实现 |
| MoveAxis | 匀速单轴模型，检查使能、回零参考、报警和动态终点范围 |
| AxisCommand.Home | 100ms 内存回零占位，成功建立仿真参考；不模拟任何厂商回零模式 |
| AxisCommand.Stop | 只确认闲置轴；不夺取其他动作控制权，停止自己的动作应取消所属运行 |
| ResourceScope、CoordinatedMove | 启动前明确拒绝，包括未选中的分支和加载的子任务 |

同轴同时使用会明确失败，独立轴可以并行；这只是最小冲突拦截，还没有等待队列、区域占用、退出确认或死锁处理。并行单轴不是协调插补。

变量默认无效，等待条件可以等到其有效；分支选择、变量赋值来源或运动目标无效则失败。All/Any/Not 中遇到未知子项也保持未知。一次条件求值使用同一份变量快照。有时区的 DateTime 按 UTC 时刻比较；不能混用有时区和没有时区的日期。

运行快照保留 `RunId`、`TaskId`、`NodeId`、独立 `ExecutionId` 和 `ParentExecutionId`，同一子任务被重复调用也不会覆盖状态。`Nodes` 只包含已进入的调用，不包含尚未进入或未选择的定义分支。全局暂停看 `TaskRunSnapshot.State`；节点保留其暂停前的等待说明。每次最多展开 10000 个节点调用。

## 后续阶段仍要做什么

实际控制器的回零/位置参考、伺服、报警、限位、运动到位、停止确认，仍需设备适配层接入；内存模型通过不代表这些硬件行为已验证。
变量质量目前只覆盖已有 HasValue，采集周期/请求关联和数据新鲜度尚未接入。区域清空、资源仲裁、死锁诊断、相对定位重启恢复和并行分支检查点属于后续阶段。
通讯、计算动作、子任务参数绑定暂不接受对应 kind，避免保存一个会被默默跳过的动作。

## 离线检查

使用已安装的 Visual Studio 18 MSBuild：

```powershell
& 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe' tools/MotionControl.TaskEngine.Checks/MotionControl.TaskEngine.Checks.csproj /restore /t:Build /p:Configuration=Debug /nologo
& tools/MotionControl.TaskEngine.Checks/bin/Debug/net48/MotionControl.TaskEngine.Checks.exe
```

将 Debug 改成 Release 可重复检查发布构建。检查只在自己的随机临时目录写文件并清理，不使用现场配置。
也可传 `--catalog 配置根目录 MachineId`，只读查看现有配置是否能形成共享目录，不写文件。
