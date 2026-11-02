# 交接说明（设备任务与本机软件控制器）

本文件记录正式 Host 两种设备流程及其软件控制器接入情况。接手时先读 `AGENTS.md`，再读本文件和第 7 节。计划文档 `docs/plans/2026-10-31-task-device-binding.md` 的部分步骤已调整，以实际代码和本文件为准。

## 0. 2026-11-01 夹具焊接多工位与当前配置

本节记录当前配置；下面“历史四工位验证”保留先前测试证据，其入口和版本不代表当前状态。

- 当前 `Machine01/Parameters/Equipment.json` 为 `definition: WeldingExample`、`executionOwner: TaskEngine`，控制器后端为 Virtual。任务运行清单已安装夹具焊接 R4。切换代码流程前须安全关闭 Host，并将执行入口切换为 `StationFlows`；重新打开时先核对实际配置及恢复记录。按 `src/MotionControl.TaskEngine/Samples/ClipWelding/README.md` 核对映射和反馈；不要用旧草稿覆盖运行清单。
- 配置目录：`C:\Users\ASUS\AppData\Local\MotionControlFramework\Configurations\Machine01`。安装样例前备份在 `Backups/ClipWelding-20261101-185625-93c902bc39e847d289ceb69fb7200ad2`，更新反馈步骤前备份在 `Backups/ClipWelding-feedback-20261101-190230`，切换到代码示例前的设备入口在 `Backups/Equipment-before-ClipCSharp-20261101-192227.json`。备份不会自动恢复。
- 参考 BCW HIVE-BZ 的扫码、焊接、压合、复检及载具流转，分别实现任务列表和 C# 四工位示例，各站有单件缓存，下游堵塞逐站向上游传递。正式 Release Host 连接本机软件控制器：任务列表实测不同工位同时持有 A/B 测试件、堵料后计数停止；解除堵料、暂停复位及启动后复检计数从 24 增至 40。本轮任务列表的报警恢复由内存检查覆盖，未在正式 Host 注入报警。
- C# 入口在正式 Host 实测四站并行及不同产品/载具编号；注入 DI7 反馈卡住后复检传感器超时报停、上游逐站等待，恢复反馈、复位并启动后复检计数从 23 增至至少 31，报警清除。暂停后轴监控显示全部轴停止；结束时 DO0–DO3、DI4–DI7 已核对为无效并关闭本轮 Host 与软件控制器。重新启动软件控制器后须重新准备轴和测试反馈。
- 任务监控将等待状态整理成更直观的文字，原始诊断仍可查看。两个样例均在移动前与输出前检查旧完成反馈已复位，此为流程条件，不是原子联锁或安全回路。启动恢复重复提示已去重；没有续跑条件时仍禁止续跑。
- 测试轴与 IO 沿用既有 Machine01 名称，并非焊机、扫码枪或视觉的实际映射；任务列表的 A/B 标识循环复用，OK/NG 和去向仅为预设变量；C# 编号只在本次会话有效。新会话清空测试载具/握手状态，不能用于现场带料启动或断电恢复。实际焊接、识别、检测、分拣、共享空间、安全回路、真实卡与现场机械尚未验证。具体步骤和边界见两个样例目录的 README。

### 历史四工位验证（非当前入口）

- 当前 `Machine01` 使用任务列表入口和本机软件控制器。运行清单 `Parameters/TaskMachine.json` 是版本 R3 的“上料 → 压装 → 检测 → 下料”四工位，使用 `Tasks/FourInit.json`、`FourLoad.json`、`FourPress.json`、`FourInspect.json`、`FourUnload.json`。原有的 `Tasks/Task_05d26ead.json` 和 `Drafts/TaskWorkspace.json` 保留；原草稿仍是旧单工位，来源与当前运行文件不一致，不能直接把旧草稿应用覆盖四工位运行文件。
- 安装前的任务清单、变量和报警文件备份在 `C:\Users\ASUS\AppData\Local\MotionControlFramework\Configurations\Machine01\Backups\FourStation-20261101-160928-9ca2c6de5915454eb4bc788bee6b1bfd`。不要自动恢复这些文件；测试配置按 `AGENTS.md` 保留。
- 上料工位循环执行，压装、检测、下料工位按请求运行；每次交接按“请求置位 → 完成置位 → 请求复归 → 完成复归”进行。`FourInit` 在每个新任务会话的首次启动时清除六个交接变量，仅用于已确认没有在制品的测试现场；不能未经工艺确认用于实际断电恢复。
- 已修正 `MachineTaskDevice` 的交接适配：请求有效期间完成信号保持，请求清除后才能复归；设备变量存储在同一把锁内核对并更新握手值，未知值不放行。相关内部检查只能补充验证，不能代替正式 Host 实测。
- 正式 Host 连接本机软件控制器实测四工位运行至第 10 个周期时注入压装反馈故障，验证超时报警、暂停及恢复；恢复后完成次数继续增至 21。任务运行页显示五行，并实测从初始化行定位到压装故障步骤。结果只覆盖本机软件控制器和此测试配置；当前是单件串行交接，不代表多件并行、共享区域防碰撞、真实节拍或实物设备验收。真实运动卡、伺服、接线、安全回路与机械仍需单独验证。
- 任务运行页仍有偏工程化的条件和变量说明，操作员如何快速识别工位当前动作、等待原因和恢复步骤仍需继续优化。不要把内部测试结果表述为所有任务页面已完善。

再次验证时按照 `src/MotionControl.TaskEngine/Samples/FourStation/README.md` 使用 `tools/MotionControl.VirtualController/StartFourStation.ps1`。脚本启动本机控制器和正式 Host，配置上料及三组反馈规则，不会自动使能、回零或启动任务。在正式 Host 核对连接、四轴使能与回零，再读取任务、复位并启动。脚本或控制器重启会重置轴、IO 和反馈状态；不要重复运行安装脚本覆盖已有配置。结束时在正式 Host 停止任务，确认四轴停止、相关输出和输入处于安全状态，并按样例说明停用供料和反馈规则。

## 1. 当前状态（判据）

| 项目 | 值 |
|---|---|
| 基线提交 | 以 `git log` 为准；本轮软件控制器相关改动和提交状态以 `git status --short` 为准 |
| 工作区 | 正式 Host 具备正运动及独立软件控制器两种后端；精确状态以 `git status --short` 为准 |
| 构建 | MachineDevice 检查为 24 组、OperatorPages 检查为 19 组；TaskEngine 23/53/16 组、Runtime 38 项；具体范围见第 6 节 |
| 正式 Host | 新一轮软件控制器测试前重新核对连接、轴和 IO；每次控制器进程重启后轴、IO 与供料场景状态均重置 |
| 配置根 | `C:\Users\ASUS\AppData\Local\MotionControlFramework\Configurations\Machine01` |
| 执行入口 | 当前 `Parameters/Equipment.json` 选择 `WeldingExample` / `TaskEngine`；切换前须安全停机并按第 0 节核对 |
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
- **任务互锁**：设备侧互锁条件暂不支持并明确拒绝；现有同步读卡可能阻塞任务运行锁，不能据此宣称暂停实时可靠。须改造状态读取和任务锁、验证厂商调用超时与停轴后，再接入设备互锁；硬件安全回路须独立。资源隔离尚未跨进程持久化。

## 5. 已知坑（先读再动手）

1. **端口是同步的，设备是异步的**。`MachineTaskDevice` 用 `Wait()`（`GetAwaiter().GetResult()`）桥接，根因是 `TaskRun.StepAsync` 在持锁状态下执行同步动作。这发生在执行器后台线程，**不要在 UI 线程调用这些同步方法**。彻底解决要把端口改异步并改运行锁，风险大，需单独立项。
2. **轴未回零会被拒绝**：`BeginMove` 要求 `ServoEnabled` 且 `Homed`。软件控制器的回零是软件零位；正运动 VPLC 历史 DATUM 模式 3 的方向与输入有效状态尚未确认，切回后须先查清，再发回零或循环指令。
3. **轴组插补及设备侧互锁条件明确不支持**；任务内存模型的互锁能力不代表实际设备支持，禁止包装成连续安全监控。
4. **git 不在 PATH**：用 `C:\Users\ASUS\.cache\codex-runtimes\codex-primary-runtime\dependencies\native\git\cmd\git.exe`。
5. **配置根有两处**：带包名的容器上下文解析到 `...\Packages\OpenAI.Codex_2p2nqsd0c76g0\LocalCache\Local\...`，普通会话解析到 `C:\Users\ASUS\AppData\Local\...`。真实现场配置已迁到后者，前者是历史残留。
6. **运行中的 Host 锁 `bin\Release\net48`**：重建前先正常关闭；关闭被"设备流程仍在运行/报警处理中"拒绝时，按 `AGENTS.md` 的记录流程处理。

## 6. 验证命令与期望输出

```powershell
$env:DOTNET_CLI_HOME='F:\Work\运动控制框架\.dotnet-cli-home'
cd F:\Work\运动控制框架\MotionControlFramework
$dotnet='C:\Program Files\dotnet\dotnet.exe'

# 1) 真机任务设备检查：期望 24 组，失败 0（使用假设备，不代表实体卡验收）
& $dotnet build tools/MotionControl.MachineDevice.Checks/MotionControl.MachineDevice.Checks.csproj -c Release -o tmp/check-md -v:q
& .\tmp\check-md\MotionControl.MachineDevice.Checks.exe

# 2) 任务引擎检查：期望 23 / 53 / 16 组，失败 0
& $dotnet build tools/MotionControl.TaskEngine.Checks/MotionControl.TaskEngine.Checks.csproj -c Release -o tmp/check-te -v:q
& .\tmp\check-te\MotionControl.TaskEngine.Checks.exe

# 3) Runtime 回归：期望 38 项全部通过（36 Runtime + 1 工位配置 + 1 ZMotion）
& $dotnet build tools/MotionControl.ZMotionSimulatorCheck/MotionControl.ZMotionSimulatorCheck.csproj -c Release -o tmp/check-rt -v:q
& .\tmp\check-rt\MotionControl.ZMotionSimulatorCheck.exe --runtime-regression

# 4) 操作员页面检查：期望 19 组，失败 0（含后端选择、断线和 IO 关联反馈检查）
& $dotnet build tools/MotionControl.OperatorPages.Checks/MotionControl.OperatorPages.Checks.csproj -c Release -o tmp/check-ops -v:q
& .\tmp\check-ops\MotionControl.OperatorPages.Checks.exe
```

任何一套数字变化，都说明行为被改动了，先查清再提交。

## 7. 第一件事

先核对第 0 节的 Machine01 夹具焊接配置、旧草稿边界、后端和执行入口，再按 `src/MotionControl.Equipment.Demo/WeldingExample/README.md` 或 `src/MotionControl.TaskEngine/Samples/ClipWelding/README.md` 启动和检查。进程重启后重新确认伺服、回零和供料及反馈规则；继续按具体需求扩展设备场景，核对暂停时轴、DO 和报警状态。测试配置按项目规则保留，结束时安全停止所有轴并处理测试 IO。

如本轮目标是正运动 SDK 而非软件控制器，先确认 `ControllerBackend.json` 的选择，再调查前述 Load_X 回零有效电平与历史方向异常；相关状态未查清前不发回零或循环命令。

## 8. 提交约定

- 中文、简短、说明实际改动；一次一项。
- 提交前 `git diff --cached` 核对，只提交本次涉及的文件。
- 只提交本地，不推送远端。
- 每项改完跑第 6 节对应检查，数字必须与记录一致或在提交说明里解释差异。

## 9. 2026-11-02 工位回零与设备准备验证

- 通用框架提供独立的设备准备入口和按依赖排序的工位准备协调；C# 流程与任务列表均使用同一正式 Host 操作页。焊接、压合的轴、IO 和工艺顺序属于设备示例，并非框架内置工艺。准备完成不自动启动生产，也不清除在制品；报警复位与工位回零是不同操作。
- 使用正式 Host 的旧构建及软件控制器验证 C# 路径：选中压合工位后先执行焊接前置准备，两工位均准备完成；启动后四工位各完成 6 次，暂停、复位并续跑至在制载具排空。
- 使用新构建的正式 Host 和现有 Machine01 任务入口时，恢复记录显示压合工位有料，准备被正确拦截；保留原有恢复数据，没有通过回零清除占料记录。
- 为验证无料条件下的任务列表路径，使用独立配置副本 `tmp/preparation-operator-20261102`，不复制原 `Runtime` 目录。正式 Host 在该副本中完成两个工位准备，四工位循环至各 12 次，并验证暂停、复位、续跑及排空。DI5 保持有效时等待可受控停止；再次等待超时产生报警；恢复 DI5、复位并重试后两个工位准备成功。
- 本轮退出前确认 DO0–3 和 DI4–7 为 false、供料关闭，并关闭 Host 与软件控制器。原 Machine01 当前保留任务列表入口及原恢复数据；上述隔离配置不是默认配置。
- 本轮验证使用软件模型，其软件零位、运动与反馈不能证明实体设备的原点输入、回零方向、限位、安全回路、伺服或现场工艺正确。任务生产初始化 `ClipInit` 会清理样例交接变量，不支持带料恢复；有料时应先按设备工艺处理并核对记录，不能以运行初始化替代清料确认。

## 10. 2026-11-02 运行入口、报警历史与恢复保护

- 正式 Host 顶部控制兼容 C# 流程和任务列表；顶部停止是受控暂停并保留工步，任务运行页“停止并关闭”才结束任务会话。两种入口仍使用通用框架和各自设备定义，不把焊接工艺写入框架核心。
- 增加报警发生、条件恢复和复位的历史记录及查询页面。历史目录位于各设备配置的 `Runtime/AlarmHistory`；写入失败会提示、保留进程内待写项并在重连时重试。磁盘持续不可用或程序突然退出时，内存中的未写记录仍可能丢失，不能当作可靠审计数据库。
- 自动生产和设备准备开始前创建持久标记；准备安全收尾或受控暂停并成功保存后才移除。重启发现遗留标记、损坏或不完整恢复文件时，保留原记录并阻止准备和生产。当前没有经现场核实、权限控制和审计的恢复入口；不能靠直接删除标记或覆盖快照假装完成现场恢复。
- 资源隔离在当前任务会话内阻止归还控制权，恢复检查要求持有控制权；正式 Host 尚无解除隔离入口，隔离记录也没有跨进程持久化。设备侧有条件互锁暂不支持，加载时提前拒绝；同步读卡和任务锁可能拖延暂停，需单独改造和验证，硬件安全链必须独立。
- 最新 Release 正式 Host 使用 `tmp/operator-unified-20261102` 隔离副本和软件控制器：焊接、压合准备完成，四工位各完成 20 个循环后受控暂停；运行中标记存在、成功暂停后消除。较早构建同场景各完成 24 次并检查报警和恢复。正式 Host 的历史报警页显示发生、条件恢复和复位，并可查看输入与详情。
- 使用另外两份隔离副本验证恢复文件损坏和遗留运行标记：设备准备和生产被拦截、原因显示在界面，恢复文件哈希前后不变；标记也保留。隔离测试不修改默认 Machine01 的配置或恢复记录。结束时在 Host 确认轴停止、DI4–7 与 DO0–3 无效，已关闭本轮 Host 和软件控制器。
- 内部检查：MachineDevice 16 组、TaskEngine 23/53/16 组、Runtime 37 项、OperatorPages 18 组、设备准备 11 组及报警历史检查通过。构建的 NuGet 漏洞数据查询有 SSL 警告。内部假设备和软件控制器的结果不代表真实控制卡、电气、机械或现场安全验收。

## 11. 2026-11-03 资源恢复与暂停并发边界

- 任务资源仍被持有、隔离或无法确认时，保留运行标记及原恢复快照，不能通过关闭任务会话遗失当前隔离。正常持有资源的暂停可在同一会话继续；隔离或未知状态禁止重启生产和设备准备。重启后的标记只提供整机拦截，不含具体资源名称与退出条件，正式 Host 尚无现场解除隔离或经身份认证的标记处理入口。
- 设备准备页可展开只读维护诊断，显示实际设备配置下的恢复快照和运行标记路径，并提示维护人员核对现场；不会改变原始记录或解除运行限制。
- 等待 DI 的同步读卡仍可能持有任务运行锁，卡住时会拖延整机暂停。曾试验移出锁，但并发审查发现迟到读卡故障可能发生在暂停保存与标记清除之后，故该试验未保留。需要先建立在途调用收尾与暂停的明确屏障，再重新验证；厂商原生锁、永久阻塞时超时及实体安全链仍需单独解决。
- 暂停并发复核还发现：同步设备调用异常释放运行锁后、故障上报之前，暂停可能先保存检查点；轴完成确认和资源清理也有锁外收尾，外部设备若自行实现轴组，目前没有对应暂停停轴登记。完整修复需让动作、在途调用、故障和暂停保存使用可验证的收尾协议，并处理厂商驱动锁；这些均不能仅用超时包装或假设备结果代替现场验证。
- 已修复此前停止路径先读状态、收尾失败仍释放临时轴的问题：优先请求受控停止，失败保留隔离，动作与停止调用未收尾时禁止释放控制权；动作凭证去重并防止过期调用影响新动作，同轴预留覆盖开始前读状态。正式 Host 配合软件控制器复测四工位各 18 次，结束后轴及测试 IO 均已复位。持有资源时保留运行标记只能提供重启拦截，不能证明当前轴已停稳；其他设备调用的暂停完成与故障上报竞态、厂商 SDK 内锁及真实机械行为仍待闭环。
- Runtime 暂停收尾已避免覆盖此前已经进入的报警状态；但报警在状态检查之后到达、设备故障迟到及恢复记录保存之间仍可能发生竞态。完整修复仍需逐调用的在途屏障与故障同步，不能把这一项局部修复当成暂停安全闭环。
