# 任务执行接入真实设备（步骤 2）实施方案

目标：让"任务配置页里编辑的步骤"能够驱动真实的逻辑轴、DI/DO 和报警，并在 ZSimu + 正式 Host 上验收。内存替身降级为自动化防回归工具，不作为验收证据。

## 一、现状事实（代码依据）

| 事实 | 位置 |
|---|---|
| 执行引擎只接受具体类型，没有设备接口 | `TaskExecutionEngine.cs:19` `Start(TaskDefinition, SimulatedTaskDevice, ...)` |
| 执行器字段同样是具体类型 | `TaskNodeExecutor.cs:18` |
| 运动是"开始/推进/结束"轮询协议，由执行器按 `进度 = 已用活动时间 / 预计时长` 判定到位 | `TaskNodeExecutor.Motion.cs:41-66` |
| 活动计时是真实 Stopwatch，暂停时停表 | `TaskRun.cs:15`、`:48`、`:59-82` |
| 内存设备已有所有权机制 `ClaimMachine` / `ReleaseMachine` | `SimulatedTaskDevice.Runs.cs:15`、`:26` |
| 报警已走 `IMachineAlarmService`，支持批量与条件上下文 | `TaskNodeExecutor.Alarms.cs:56-84` |
| 任务资源目录能从真实 Machine01 配置加载（轴参数、IO 映射、变量、报警库） | `TaskResourceCatalogLoader.Load(machineId, ...)` |
| Runtime 侧执行入口互斥已就绪 | `MachineRuntime.ControlOwner`、`AcquireTaskEngineControlAsync` |

两条已知的语义冲突（必须在本步解决，不能绕过）：

1. **暂停**：内存模型是"冻结时钟与写值"，轴仍在原地不动也没人停它；真实轴不取消就会继续走。
2. **到位判定**：内存模型按"速度算出的时长"宣布到位；真实轴必须由反馈位置进入到位容差且停稳来宣布。

## 二、设计

### 2.1 新增端口 `ITaskDevice`（放在 `MotionControl.TaskEngine/Device/`）

执行器只依赖端口，端口保留现有轮询形状，避免重写 53 组节点检查：

```csharp
TaskResourceCatalog Catalog { get; }
bool? ReadInput(string name);
bool? ReadOutput(string name);
void WriteOutput(string name, bool value);
void WriteVariable(string name, object value);
IReadOnlyList<DeviceVariableSnapshot> ReadVariables();
TaskAxisState ReadAxis(string name);
bool CheckInterlock(TaskInterlock interlock, TaskCondition legacy, out string reason);
ITaskResourceHandle BeginMove(string axis, double target, bool relative, bool home);
bool AdvanceMove(ITaskAxisMove move, double activeMilliseconds);
Task EndMoveAsync(ITaskAxisMove move, bool completed, CancellationToken cancellationToken);
ITaskAxisGroupMove BeginGroupMove(IReadOnlyList<TaskAxisGroupTarget> targets);
bool AdvanceGroupMove(ITaskAxisGroupMove move, double activeMilliseconds, out string detail);
Task EndGroupMoveAsync(ITaskAxisGroupMove move, bool completed, CancellationToken cancellationToken);
Task ConfirmStoppedAsync(string axis, CancellationToken cancellationToken);
// 资源与所有权沿用现值：TryAcquireResources / ReadResources / TryRecoverResources /
// ClaimMachine / ReleaseMachine / RegisterRun / UnregisterRun
```

关键点：`AdvanceMove` 由**设备**返回是否完成，而不是由执行器按时长推断。仿真实现按 `elapsed/时长` 计算，真机实现按轴实际到位计算。

### 2.2 两个实现

- `SimulatedTaskDevice`：改为实现 `ITaskDevice`，行为保持不变（时长判定、内存位置、内存 IO/变量），仅把 `SimulatedMove` 改为实现 `ITaskAxisMove`。
- `MachineTaskDevice`（新增工程 `src/MotionControl.TaskEngine.Machine`，引用 TaskEngine + Framework.Runtime + Contracts）：真机实现，见下表。

| 端口 | 真机实现 |
|---|---|
| `BeginMove` | 启动 `IAxis.MoveAbsoluteAsync/MoveRelativeAsync/HomeAsync`，持有 CTS 与任务 |
| `AdvanceMove` | 轴任务未完成返回 false；故障时抛出原始驱动异常 |
| `EndMoveAsync` | 未完成则 `StopAsync(ControlledStop)` 并等待停稳；完成则只回收 |
| `ReadAxis` | `IAxis.ReadStatusAsync` 映射为 `TaskAxisState`（位置、速度、运动中、报警、回零） |
| `ReadInput/WriteOutput` | `ILogicalIo`，名称缺失或读取失败必须抛出，不返回 false |
| `WriteVariable` | 设备变量存储（`IDeviceVariableStore`） |
| `ClaimMachine` | `MachineRuntime.AcquireTaskEngineControlAsync` |
| `ReleaseMachine` | 归还执行入口，失败必须报告 |

`ReadAxis` 返回框架自有类型 `TaskAxisState`，不暴露 `SimulatedAxisSnapshot`。

### 2.3 暂停与停止语义（本步最高风险）

- 真机模式下"暂停"= 取消当前节点动作 → 受控停止 → 等待停稳 → 保留节点、保留已接受的绝对终点。
- 恢复时重进当前节点，复用现有 `AcceptedAxisTargets` 与 `ResetTimeBudgetAsync`，相对位移不重复叠加。
- 停机报警导致的暂停同样走这条路径：先停轴，再锁存报警。

### 2.4 超时

活动计时已是真实时间，保持不变。新增要求：节点超时时**先停轴并确认停稳**，再上报超时报警，避免报警后轴仍在运动。

### 2.5 报警归属

`TaskMachineController` 注入 `MachineRuntime.AlarmService`，任务超时报警直接进入正式 Host 的"当前报警"页；停机级别沿用现有锁存与复位规则。

### 2.6 轴组与插补

`IAxis` 没有轴组运动契约，正运动适配器的插补能力尚未验证。真机模式下轴组节点**明确报"当前适配器未提供轴组插补"并拒绝执行**，不退化成分步移动假装成功。

## 三、分三阶段交付

| 阶段 | 内容 | 通过标准 | 风险 |
|---|---|---|---|
| A | 抽出 `ITaskDevice`，`SimulatedTaskDevice` 实现它，执行器与控制器改依赖端口 | 现有回归 35 + 53 + 16 组结果完全不变；构建 0/0 | 低 |
| B | `MachineTaskDevice` 单轴/IO/变量/等待/报警 + 所有权接管 | 用假 `IAxis` 检查：移动被等待、取消触发受控停止、IO 读写失败抛出、接管被拒场景 | 中 |
| C | 暂停/停止语义统一、Host 接线、ZSimu 端到端 | 见第四节验收脚本 | 高 |

每阶段独立构建、跑检查、提交；不做"一次性大改"。

## 四、ZSimu + 正式 Host 验收脚本

1. ZDevelop 连接 `VPLC5xx-Simu`，确认轴参数页可读（DPOS/MPOS、SPEED、限位、DATUM_IN）。
2. 正式 Host 启动，确认窗口路径与标题、轴映射 12/64、`控制卡 会话已建立`。
3. 任务库新建单工位任务：`Load_X` 定位到 1 mm → 等待 DI8 → DO0 置位。检查通过后应用。
4. 启动任务入口，屏幕核对：轴位置变化、DI8 状态、DO0 状态、步骤推进、周期计数。
5. 运动中暂停 → 轴受控停止、位置保持；恢复 → 从固定终点继续，位置不叠加。
6. 制造超时（DI8 不触发）→ 等待超时报警出现在"当前报警"页且为停机级别 → 复位后可再次启动。
7. 结束时轴全停、DO 恢复安全值、记录未验证项。

## 五、明确不做

- 不做运行时插件扫描或热加载设备定义（沿用显式登记）。
- 不做轴组插补的真实实现（适配器能力未验证，先明确报错）。
- 不改任务定义 JSON 格式，不改现有页面交互。
- 不把内存检查结果当作设备验收结论。

## 六、需要你决定

1. **任务运行中按暂停**：立即受控停止当前轴（推荐），还是等当前步骤做完再停？
2. **轴组节点在真机不支持时**：明确报错拒绝（推荐），还是退化为逐轴顺序移动？
3. **`MachineTaskDevice` 放哪**：新工程（推荐，便于其他设备复用），还是直接放 Host？
