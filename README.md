# MotionControlFramework

通用运动控制上位机框架的第一期工程骨架。

## 当前范围

- 目标运行时：.NET Framework 4.8。
- 桌面界面：WPF。
- 流程方式：设备项目使用 C# 编写工位流程步。
- 可选任务模块：已完成任务定义、配置引用、JSON 保存读取、离线校验和纯软件仿真执行；支持顺序、分支、并行汇合、单轴/同卡轴组定位，以及共享轴/输出/区域的资源排队、显式互锁和异常待确认恢复。设备级入口已支持可选初始化、独立循环/请求触发工位、统一复位启动暂停、故障当前步骤恢复和监控快照，提供三工位三十周期连续交接样例。协调插补、实际卡接入后续分阶段实现。
- 任务运行监控：已加入独立 WPF 项目，按工位显示任务步骤树与分类详情；轴、IO、变量详情可定位到现有映射/手动/变量页的具体对象。当前执行工作台仅运行内存仿真，实时整机变量共享、真实任务执行另行开发。
- 任务配置与编辑初版：设置 → 任务配置，支持工位安排、任务库、分类步骤编辑、设备草稿、检查与带回滚的运行文件应用。独立预览参数为 `--task-editing-preview`，不创建控制卡服务。操作和文件说明见 [`Editing/README.md`](src/MotionControl.TaskEngine.Wpf/Editing/README.md)。
- PDF 四个工作区已接入：设备总览、任务配置、分栏列表编辑、当前报警。总览与报警观察传统 Runtime；列表任务的运行入口仍为独立内存会话。使用 `--ui-workspace-preview` 查看独立四页示例，说明见 [`Operation/README.md`](src/MotionControl.Host.Wpf/Operation/README.md)。
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
| `MotionControl.TaskEngine` | 独立任务定义、配置目录、校验、内存执行、资源协调及设备级多工位调度；目前仅操作内存仿真设备 |
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
