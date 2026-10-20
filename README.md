# MotionControlFramework

通用运动控制上位机框架的第一期工程骨架。

## 当前范围

- 目标运行时：.NET Framework 4.8。
- 桌面界面：WPF。
- 流程方式：设备项目使用 C# 编写工位流程步。
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
| `MotionControl.Host.Wpf` | WPF 应用入口、主窗口和模块组合根 |
| `MotionControl.Equipment.Template` | 新设备项目的目录模板；不自动接入 Host |
| `MotionControl.VendorApi.*` | 各厂商的低层原生 API 声明；不被框架核心直接引用 |
| `MotionControl.Adapter.MotionCard.*` | 各厂商运动卡适配器；当前正运动实现仅支持 ZDevelop 仿真器 |

开发时遵循根目录的 [`AGENTS.md`](AGENTS.md) 项目规则，尤其是“直白优先，默认不抽象”。详细依赖和模块边界见 [`docs/architecture.md`](docs/architecture.md)；厂商 API 来源及部署依赖见 [`docs/vendor-motion-card-apis.md`](docs/vendor-motion-card-apis.md)。
