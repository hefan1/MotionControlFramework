# UI 前设备级多工位运行验证记录

日期：2026-10-23。

## 本轮完成范围

在现有任务定义、单次执行、资源仲裁和轴组定位之上，补齐设备任务清单、可选初始化、独立循环/请求触发工位、统一复位启动暂停、故障当前节点恢复、监控快照和连续生产样例。

只创建独立 `SimulatedTaskDevice`。未连接 ZDevelop/ZSimu、SDK 或实际卡，未启动 Host/WPF，未执行真实伺服使能、运动或物理 IO。本记录属于纯内存软件验证。

## 参考与实现定位

- 参考 UAMP 的 `Common/Manager/TaskManager.cs`：统一 Run/Pause/ClearErr/Continue；`Common/TaskBase.cs`：独立循环和请求/完成交接。本次没有复制 Thread.Abort。
- 现有 `Framework.Runtime/FlowScheduling/MachineFlowScheduler.cs` 执行 C# 工位，保持原依赖方向；新控制器在 TaskEngine 内组合已有单次执行器。
- 清单：`Machine/TaskMachineConfiguration.cs`、`TaskMachineConfigurationValidator.cs` 和 `Configuration/TaskMachineConfigurationStore.cs`。
- 控制：`Machine/TaskMachineController*.cs`，按控制命令、工位循环、交接分文件；内部工位记录在 `StationExecutionControl.cs`。
- 监控：`Machine/TaskMachineSnapshot.cs`，不向 UI 暴露可变 TaskRun。
- 节点恢复：`Execution/TaskNodeExecutor.cs`、`TaskExecutionScope.cs`；运动文件缓存已接受绝对终点；范围互锁文件保留进入/正文/退出阶段。
- 所有权与信号：`Simulation/SimulatedTaskDevice.Runs.cs`、`SimulatedTaskDevice.Signals.cs`。
- 检查与样例：`tools/MotionControl.TaskEngine.Checks/MachineChecks*.cs`、`Samples/ProductionDemo/*.json`。

## 构建与实际结果

- 使用 Visual Studio 18 Community MSBuild，目标 `MotionControl.TaskEngine.Checks.csproj` 及 Contracts、Runtime、TaskEngine 依赖；net48 / C# 7.3，没有新包。
- Debug、Release 构建及完整检查均通过，退出码 0。
- 每种配置 84 组：原定义/配置 22 组、原单次执行/资源/轴组 49 组、新设备级 12 组、执行后依赖 1 组。
- 独立 `--production-demo` 通过：每个工位 30 周期，逐件核对产品编号，X=0、Y=0、R=30，资源数 0；X/Y 各 60 次动作、R 共 30 次，无重复或漏执行。
- NETSDK1057 是本机 .NET 预览 SDK 的环境提示，目标运行时未改变。

| 新设备级检查组 | 实际验证内容 |
|---|---|
| 清单校验与持久化 | 合法引用、重复工位、缺失任务、非 bool 信号、设备目录隔离、修订递增、备份、失败不覆盖、未知/损坏 JSON、缺少 mode/enabled 拒绝 |
| 初始化与定义冻结 | 初始化成功前自动工位不动作；外部修改清单/任务无效；暂停复位启动不重复初始化和 Once 工位 |
| 独立循环与统一暂停 | 快工位完成多个周期时慢工位仍在首周期；Once 一次、禁用项零次；暂停后周期不增长；必须复位再启动；旧快照不被改写 |
| 旧完成与未知信号 | 未知不作 false；等待请求不消耗节点时限；旧完成先复归；请求持续 true 只执行一次；大小写不同的合法变量引用仍工作 |
| 三工位三十周期 | 读取真实样例 JSON，用请求/完成驱动上料→检测→下料，逐件核对各工位编号、周期、位置和动作计数 |
| 故障暂停与当前步骤 | 变量无效故障定位到任务/节点；整机暂停后其他轴不再推进；修复复位启动后继续，前面成功的相对动作不重做 |
| 相对终点 | 单轴/轴组运动中途注入报警；先收尾再锁存；有效报警阻止复位；解除后按原绝对终点完成，修改目标变量也不追加位移 |
| 超时与区域退出 | 等待输入超时后保持原调用编号；修复后继续；范围退出超时仍 Held，复位只重新核对退出，不重做正文 |
| 父时限与规模超限 | 父步骤时限到期后先前动作不重放；10000 调用上限使运行 Failed，设备拒绝复位续跑父任务 |
| 外部停机条件 | 条件有效时不能复位；条件恢复仍锁存；复位只到 Ready，启动才继续 |
| 提前撤销请求 | 已完成动作后交接异常锁存；修复请求只发布完成，不重做动作；请求方清请求后完成复归 |
| 所有权和终止关闭 | 整机期间拒绝独立入口和第二控制器；终止等待动作停下、区域 Blocked；旧会话不可续跑；重复关闭安全，显式确认区域后可另开独立运行 |

## 检查期间发现并处理的问题

1. 交接检查最初按小写文本比较 bool，但已有变量存储标准化为 `True` / `False`。检查改为按 bool 值比较，保留未知值检查，未更改存储和交接协议。
2. 父时限可能在新子节点首次登记前到期。受控入口会在当前子节点记录故障等待，不将错误抛到父节点重放前面动作。
3. 调用数量超限不属于可恢复动作故障。整次运行明确标记超限并沿父链退出，设备阻止复位续跑。

## 验收边界

- 本轮可以支持第一版仿真任务编辑器使用清单、定义保存/校验、统一控制和监控接口；UI 本身尚未开发或验证。
- 现有 C# 工位入口与新任务入口仍独立；没有将 Host 的物理按钮或实际卡服务接入新控制器。
- 同卡轴组是各轴定位后汇合，尚无同步路径速度、直线/圆弧插补。CoordinatedMove 仍在任何动作之前拒绝。
- 资源互斥与显式进入/退出条件不等于连续几何避碰；未验证真实机械区域清空、伺服动力学或硬件安全回路。
- 暂停和可恢复故障保持同一进程内的调用进度。退出软件后的任务检查点、在制产品、资源占用恢复尚未实现；不能用设备变量恢复推断任务进度恢复。
- 等待上游请求无动作超时；正文中的信号互等依靠具体等待节点时限定位，没有业务死锁自动检测或信号所有者权限系统。
- 快照分别复制整机记录、各运行和资源，不保证多工位与硬件采样时刻原子一致。
- Blocked 在关闭控制器后保留于同一个内存设备；新建内存设备或重启进程不恢复此状态。

## 重复方式

```powershell
& 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe' tools/MotionControl.TaskEngine.Checks/MotionControl.TaskEngine.Checks.csproj /restore /t:Build /p:Configuration=Debug /nologo /verbosity:minimal
& tools/MotionControl.TaskEngine.Checks/bin/Debug/net48/MotionControl.TaskEngine.Checks.exe
& tools/MotionControl.TaskEngine.Checks/bin/Debug/net48/MotionControl.TaskEngine.Checks.exe --production-demo
```

Release 将命令中的 Debug 换为 Release。配置写入检查仅使用自建随机临时目录并清理，不覆盖现场配置。源码按具体职责拆分，新增/修改手写类型、成员和方法有中文注释；厂商原生 API 未改动。
