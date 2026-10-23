# 任务轴组定位验证记录

日期：2026-10-23。

## 行为与范围

新增 MoveAxisGroup：同卡多根轴各自指定坐标方式、目标和工艺参数；一次申请全部轴，取得资源后用同一份变量快照读取目标；整组检查通过后接受动作，各轴独立速度，全部到位才返回。

仍只操作独立的 SimulatedTaskDevice。没有连接 ZDevelop/ZSimu 或实际控制卡，没有执行真实运动、伺服使能或物理 IO，没有启动 Host/UI。

## 构建和检查结果

- 工具：`C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe`。
- 构建目标：`tools/MotionControl.TaskEngine.Checks/MotionControl.TaskEngine.Checks.csproj` 及 Contracts、Runtime、TaskEngine 依赖；net48 / C# 7.3，无新增包。
- 最终 Debug、Release 构建和完整检查均通过，程序退出码为 0。
- 每种配置包含 22 组定义/存储检查、49 组执行检查、1 组执行后依赖检查，共 72 组。原有 60 组继续通过，本轮新增 12 组。
- 初轮通过后补充极小速度导致时长溢出检查及 JSON 目标行定位说明，再对最终代码完成两种配置检查；没有把固定轮数重复作为验收要求。
- SDK 11 预览版本的 NETSDK1057 是环境信息提示；目标运行时保持 .NET Framework 4.8，未更改 SDK 设置。

## 新增检查的实际观察

| 检查组 | 结果 |
|---|---|
| 定义与范围声明 | 数量、重复轴、缺失轴、异卡、无效模式、行程、工艺参数及示教点归属均拦截；子任务内漏声明轴定位到外层范围与被调节点 |
| JSON 与备份 | 定位组字段往返保持一致；修订号和备份沿用现有存储；遗漏目标字段、数字/未知枚举、未知字段拒绝，并定位所属组与目标行 |
| 独立到位与汇合 | X 先到位、Y 仍运行时后续输出不执行；其他运行不能取得已到位的 X，独立 R 可运行；整组到位后才写输出 |
| 等待后解析目标 | 等待中的组不占住其余轴；取消等待组不影响拥有者；资源取得后使用更新的变量目标与当前相对起点 |
| 重叠定位组 | XY 占用期间，YR 整组等待，R 不提前接受动作；两组依次完成并释放 |
| 零距离与工程单位 | X 零距离立即到位，仍等待旋转 R 完成；没有混合单位的路径速度计算 |
| 整组前置检查 | Y 未使能、未回零、报警、变量无效、动态越界或相对越界时，X/Y 都不接受命令；时长溢出也在整组接受前拒绝 |
| 暂停相对目标 | 所有剩余动作和活动时钟冻结；暂停超过节点时限仍可恢复；外部修改变量、原定义目标和速度后仍按冻结终点完成，每轴只接受一次命令 |
| 取消与超时 | 暂停取消、组节点时限及父级时限均停止剩余轴，后续输出不执行；超时保留节点与组内进度；临时资源全部释放 |
| 运行报警 | 正在运动的 Y 报警以及已经先到位的 X 报警，均结束整组；原始原因、轴名和组节点保留，其余轴停止 |
| 子任务与区域恢复 | 子任务的组动作继承整个范围；正常退出条件通过后释放；取消后轴均停止但区域仍 Blocked，显式恢复后后继组才执行 |
| 完整中文样例 | AxisGroupPositioning.json 实际读取运行；XY 去拍照位再返回，最终各接受 2 次动作、回到 0、内存输出 OFF、无资源遗留 |

## 代码组织与复查

- 定义：`Definitions/MoveAxisGroupNode.cs`；校验：`Validation/TaskNodeValidator.AxisGroups.cs`。
- 执行：`Execution/TaskNodeExecutor.AxisGroups.cs`；仿真状态：`Simulation/SimulatedTaskDevice.AxisGroups.cs` 和 `SimulatedAxisGroupMove.cs`。
- 复用现有资源协调器、值绑定、条件、示教目录、变量快照、时限及暂停控制，没有增加服务定位器或厂商接口。
- 组内每轴动作收尾后才释放本组临时资源；范围占用由外层管理，异常不会借组收尾自动放行区域。
- 手写类型、字段、属性、构造方法和方法保留中文说明，普通分支与直接调用便于逐层跟踪。
- 构建检查确认无 WPF、Host 或厂商程序集依赖，新增源码没有原生 API 调用。

## 尚未覆盖的行为

1. 本次是匀速内存模型；加减速度经过参数校验，但没有仿真伺服动力学或加减速曲线。
2. 不提供同卡直线/圆弧插补、同步启动、同时到达或确定的中间路径；CoordinatedMove 仍在动作前明确拒绝。
3. 实际 SDK 逐轴发命令可能中途失败；后续硬件接入必须停止已接受命令的轴并逐轴确认停止，不能把内存整组原子性当作实体卡保证。
4. 当前要求同一 ControllerId，2–64 仅是定义规模限制；实际轴数和卡能力未据此验证。
5. 安全顺序由设备流程明确编写；区域与干涉仍使用显式资源和互锁，不根据轴名猜测 Z 抬升或机械避让。
6. Host 组合、任务编辑器、跨进程任务恢复和生产设备恢复尚未由本轮接入。

## 重复命令

```powershell
& 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe' tools/MotionControl.TaskEngine.Checks/MotionControl.TaskEngine.Checks.csproj /restore /t:Build /p:Configuration=Debug /nologo /verbosity:minimal
& tools/MotionControl.TaskEngine.Checks/bin/Debug/net48/MotionControl.TaskEngine.Checks.exe
```

Release 将两处 Debug 替换为 Release。文件检查只写自己创建的随机临时目录，不更改现场配置。
