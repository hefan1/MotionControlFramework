# ROC-13 正式 Host 跨设备与工艺验收交接

日期：2026-11-11。本次完成仓库只读准备盘点，未操作 Windows Host，未修改设备配置或产品代码。

## 当前结论与基线

- 已确认的代码基线为 `be17f5c063604b06a9019dbced09a71b7ce2dea6`；该提交的 [Windows 自动检查 42769800848](https://github.com/hefan1/MotionControlFramework/actions/runs/42769800848) 成功。自动构建、内部检查和软件控制器 API 检查不代替正式 Host 的页面操作与屏幕证据。
- 当前会话的执行环境为 Linux，Computer Use 目标和 Sky 应用均属于 Linux，未取得可操作的用户 Windows Host 画面。只能记录“本会话无法观察 Windows 桌面”，不能据此判断用户的 Host、控制器或工程是否存在、运行或连接。
- **ROC-13 保持 1/15；其余 14 项正式屏幕验收全部未执行。** 本文是接手准备，不增加通过项，也不沿用旧版本截图作为本次证据。
- 验收对象仍为同一构建的 `src/MotionControl.Host.Wpf/bin/Release/net48/MotionControl.Host.Wpf.exe`，通过正常入口打开“运动控制上位机”。不得用预览窗口、内部检查器或其他 exe 替代。

## 已核实的后端与配置边界

仓库的 `MotionControl.VirtualController` 独立进程固定提供 **64 个物理轴、4096 路 DI、4096 路 DO**，编号分别为 0–63、0–4095。正式 Host 的 Virtual 连接和工程导入接受 1–64 个轴通道，因此 24/6 轴数量在其容量内；是否已具备可用工程须另行核对。

| 事实 | 可定位入口 |
|---|---|
| 控制器容量与一次性命令故障注入 | `tools/MotionControl.VirtualController/ControllerState.cs`、该目录 `README.md` |
| 当前 Host 只支持一张卡；有效逻辑轴来自已配置的轴映射行 | `src/MotionControl.Host.Wpf/VirtualAxisMappingCardConnection.cs` 的 `ConnectCoreAsync`、`LoadAxes` |
| Virtual 工程导入允许 1–64 个轴通道 | `src/MotionControl.Host.Wpf/HostEquipmentProjectCardValidation.cs` |
| Virtual 复用既有硬件映射元数据：正运动、总线型、`adapterType: ZMotion`；实际后端由 `ControllerBackend.json` 选择 | `VirtualAxisMappingCardConnection.ConnectCoreAsync` |
| Virtual 轴映射只读、IO 映射不支持保存 | `src/MotionControl.Host.Wpf/README.md`“运动卡连接位置” |
| 正式任务设备仍拒绝组定位及有条件的资源范围 | `src/MotionControl.TaskEngine.MachineDevice/MachineTaskDevice.cs` 的 `ExecutionCapabilities`；`docs/task-node-support.md` |

卡的通道容量与设备有效逻辑轴数量须分别记录。可使用同一 64 通道单卡配置，分别提供 24/6 个有效轴映射；只改容量数字不算完成对象数量切换。工程单位、数值含义、状态和操作目标须同时对应当前对象。

该后端经 `VirtualMotionCard` / `IAxis` 访问仓库软件控制器，不调用 ZMotion SDK。其结果不代表其他厂商、实体运动卡、机械和独立安全回路验收。

## 现有资产与缺口

本次检查本仓库文件（含忽略文件，排除 Git 内部及构建产物）后，**未发现完整的 A24/B6 `Machine01` 配置根目录或已保存的 `.mcpkg.json` 工程包**。此结论仅针对本次仓库，不推断用户 Windows 上没有相关资产。

| 资产 | 复用条件与限制 |
|---|---|
| `tools/MotionControl.VirtualController/NewRotaryTestConfiguration.ps1` | 复制已有完整 `Machine01`，安装十三轴任务；源须已有 `HardwareConfig`、`Parameters`、`Tasks`、十三个指定轴名/通道及若干既有 IO。不是从零生成器，也没有 24/6 参数。 |
| `tools/MotionControl.VirtualController/StartRotaryWelding.ps1` | 为既有十三轴隔离工程建立控制器模型；`-LaunchHost` 启动正式 Host。固定核对该样例轴与 IO，不能直接用于任意 A24/B6。 |
| `src/MotionControl.TaskEngine/Samples/RotaryMotionValidation/`、`RotaryWelding/` | 可复用任务、变量及准备定义，未提供完整硬件工程。前者为无扫码和逐件记录的运动样例。 |
| `src/MotionControl.Equipment.Demo/Samples/DemoStations.json` | 已有 C# 工位配置模式，可填写工位名、逻辑轴/IO 和动作参数；整体默认关闭，仍需有效映射及报警定义。 |
| `tools/Install-RecentSimulationProject.ps1` | 从已停止使用的完整快照创建新项目及默认入口；不补齐缺失轴/IO，不迁移旧 Runtime，也不解除旧报警与恢复门禁。 |
| `src/MotionControl.Equipment.Template/HardwareConfig/` | 空工程模板；不是可运行的 24/6 种子。ZMotion Adapter 的 `Samples/` 也只有两轴示例。 |

### 历史十三轴配置的定位线索

`docs/verification/2026-11-09-default-simulation-project.md` 记录当时默认项目为 `%LOCALAPPDATA%/MotionControlFramework/SimulationProjects/RecentSandboxProject`，源快照为 Windows 主工作区 `tmp/alarm-recovery-20261109/results/preserved-AlarmContinuityEarlyConfig`。当时对照了 35 个 JSON，包含 13 轴、88 DI、14 DO；原设备配置未覆盖，源报警待交接记录保留。

这些路径、数量和状态属于历史记录。接手 Windows 会话须重新确认实际文件与恢复状态，本次没有验证它们当前存在或可直接运行。不得删除待交接记录以获得干净启动。

### 两项准备限制

1. **MotionOnly 配置注册缺口：静态发现，待另项验证。** 已建立 [ROC-30](https://linear.app/roc123/issue/ROC-30) 跟踪。`NewRotaryTestConfiguration.ps1 -MotionOnly` 写入 `Parameters/RotaryMotionValidation.json`；当前 `MachineProjectPackageService.cs` 的参数登记清单没有该文件，`App.EquipmentProject.cs` 创建服务时未传额外登记项。按现有导出/导入校验路径，该文件会成为未登记参数。尚未在正式 Host 复现，不能写成通过，也不能删除模式标记绕过校验。选择该资产作为工程包来源前，应另项核实并处理。
2. **现有配置检查器并非通用六轴入口。** `tools/MotionControl.MachineProject.Checks/Program.cs` 的可选 `CheckExistingSample` 要求已命名轴数至少 13，测试卡容量选项仅为 8/16/32/64。不能把六轴配置交给此分支后的失败解释为正式 Virtual 不支持六轴。A/B 均须用正式 Host 工程预览核对实际能力。

## A24/B6 的最小准备要求

优先核对 Windows 已有资产，再补缺少的配置；不新增假轴或另一套场景框架。最少需要一个明确的隔离工作根、两份完整工程及一份预期差异表。

| 范围 | 必须具备的内容 |
|---|---|
| 硬件配置 | `HardwareConfig/MotionCards.json`、其引用的连接 JSON、`AxisParameters.json`、`IoMapping.json`、`SafetyInterlock.json`；单卡及引用一致。 |
| 轴与 IO | A24/B6 分别有 24/6 个有效轴；轴键、物理轴号、工程单位、换算、行程、速度及 IO 通道/极性明确。急停及所有流程引用指向当前映射。 |
| 后端与入口 | `Parameters/ControllerBackend.json` 明确 `Virtual` 和本机端口；`Equipment.json` 明确当前 Host 已登记的设备定义与 `TaskEngine` / `StationFlows` 入口。 |
| 任务与流程 | 任务入口具备 `TaskMachine.json`、被引用的 `Tasks/*.json`，使用准备功能时具备有效 `StationPreparation.json`；C# 入口使用已经安装的定义和对应工位配置。 |
| 关联内容 | 提供实际引用的变量、报警、手动点位和关联 IO 配置。A/B 的名称、单位、工位数量、步骤或条件确有差异，并列清预期来源和操作对象。 |

现 Host 已登记 `Demo`、`WeldingExample`、`RotaryWelding`、`RotaryMotionValidation`。工程包不安装新的 C# 程序；不能随意增加设备定义名称或配置字段来假定具备新执行能力。任务只使用当前后端已支持的节点；不支持项应验收明确拒绝，不改成其他动作冒充通过。

工程包由“设置 → 设备工程 → 导出当前工程”生成，扩展名为 `.mcpkg.json`，含格式版本与逐文件校验。两份工程均须在正式 Host“选择工程并预览”中成功校验。详见 `src/MotionControl.TaskEngine/Configuration/README.md`。

## 同一构建 A→B→A 的正式执行步骤

1. **核对环境和身份。** 在 Windows 会话安全结束确认过的旧 Host。按 `src/MotionControl.Host.Wpf/README.md` 的正式入口启动；显式隔离配置使用 `--simulation-config-root`，避免误用历史默认项目。记录完整 exe 路径、SHA-256、构建提交、窗口标题、配置根、控制器实际身份与端口。启动和构建方法见该 README 与 `tools/MotionControl.VirtualController/README.md`，本文不提供未核对本机路径的启动命令。
2. **核对 A24。** 确认当前生效工程，按 ROC-13 原检查清单逐页记录名称、对象集合、单位、状态、选择及操作目标；覆盖详情、提示和对话框。受保护的轴映射、IO 映射、轴手动、IO 监控正常参与验收，不改布局或代码。
3. **准备切换。** 保存编辑，停止设备，停止并关闭任务会话，确认本轮触及轴停止和 IO 安全状态，完成待确认恢复/资源收尾，然后安全断开控制器。保留未完成状态和证据，不通过删除文件绕过门禁。
4. **应用 B6。** “设置 → 设备工程 → 选择工程并预览”，核对差异、引用和保留文件，再“应用导入”。成功后正常关闭并重启同一个 exe，重新连接及准备；当前实现不支持带运动热切换。
5. **核对 B6，再返回 A24。** 复用原逐页检查，并核对旧选择、详情、缓存、订阅和导航目标。按相同步骤导入 A24、重启、再次核对，确认三次使用同一二进制哈希。
6. **补齐分支与两种入口。** 按原检查项覆盖任务关闭/重读、断开/重连、读取失败、迟到结果、未生效编辑、空或失效引用、切换中及切换失败。`TaskEngine` 与 `StationFlows` 分别记录真实执行来源，不能同时持有设备控制。持续悬停提示和页面刷新也须实际观察。
7. **安全收尾并保留配置。** 在正式画面确认轴停止；测试输入/输出按当前设备的安全值恢复。记录最后配置根、应用/加载状态、控制器状态及保留证据。测试配置、草稿和记录默认保留，不自动恢复备份或切回默认配置。

工程导入不覆盖 `Runtime`、`Drafts`、`Backups`，包外文件也会保留。每次预览必须核对“保留文件”；旧草稿可以保留，但不能显示或解释为新工程当前已应用/已加载的运行来源。门禁与重启入口见 `App.EquipmentProject.cs`、`Configuration/EquipmentProjectView.xaml.cs`。

### 故障场景复用入口

已有 `tools/MotionControl.VirtualController/ControllerCommand.ps1` 的 `SetCommandFault`、`CommandFaultStatus`、`ClearCommandFault` 可为指定轴/通道注入一次性拒绝或延迟。先核对当前映射和正式屏幕，再按其 README 使用；无需新增模拟设备。

单通道 `readInput` / `readOutput` 故障不影响批量 IO 读取，不能用它声称覆盖所有 IO 监控失败路径。故障匹配会被下一次请求消费，必须记录实际命中与画面反馈；没有观察到场景发生就记未执行或受阻。

## 接手会话的证据模板

每项沿用 ROC-13 的原编号和名称，不因本文另建通过清单：

| 字段 | 填写内容 |
|---|---|
| 检查项 | ROC-13 原编号、名称；本次状态：未执行 / 通过 / 未通过 / 受阻 |
| 版本与程序 | 提交、构建目录、Host 与控制器 SHA-256、进程完整路径、窗口标题 |
| 当前上下文 | A24 / B6 / 返回 A24；配置根及工程包校验值；实际后端、连接及任务会话来源 |
| 场景与前提 | 执行入口、有效轴/IO/工位数量、预期名称/单位/操作目标、设备初始状态 |
| 操作与观察 | 实际点击顺序；操作前后截图；关联详情/提示；实际反馈与预期差异 |
| 结果与边界 | 是否发生目标场景；失败或受阻原因；未覆盖内容；关联问题编号 |
| 收尾与保留 | 轴停止与 IO 状态、最后应用/加载状态、保留配置/草稿/恢复记录路径 |

当前模板未产生任何正式屏幕结果。后续只有在同一版本、真实场景和画面证据具备后，才更新对应验收项；此前 **ROC-13 仍为 1/15，14 项正式验收未执行**。
