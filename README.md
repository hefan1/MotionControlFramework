# MotionControlFramework

通用运动控制上位机框架的第一期工程骨架。

## 当前范围

- 目标运行时：.NET Framework 4.8。
- 桌面界面：WPF。
- 流程方式：设备项目使用 C# 编写工位流程步。
- 设备模块：由 Host 显式引用并组合；本骨架中的 Equipment.Template 仅作新设备项目的结构参考，Host 不引用它。
- 扩展模块：运动卡、通讯、存储和 MES 适配器按独立模块接入；目前已把参考工程中的雷赛、正运动和 ADLINK 原生 API 声明拆成独立项目。
- 通用手动调试：框架预留轴和逻辑 IO 操作能力；设备专属动作留在设备项目。

当前已接入厂商原生 API 声明，并已建立第一版通用运动卡、逻辑轴和逻辑 DI/DO 契约。完整骨架仍在完善：厂商驱动适配器、单机/工位运行协调、报警运行规则、硬件配置加载及通用手动调试页面尚未实现。MES 协议和客户 UI 模板留待后续沟通。

## 运动控制契约进度

- 已定义卡连接、轴使能/回零/绝对与相对运动/点动/停止/报警复位，以及轴状态读取。
- 已定义按逻辑名称读 DI、读 DO、写 DO 的接口；物理点位映射由设备硬件配置提供。
- Runtime 已增加多卡连接与逻辑轴注册入口，启动连接失败时会回收本次已连接的卡，并对重复控制卡标识/逻辑轴名称报错。
- 轴动作参数使用工程单位，厂商适配器负责与脉冲单位转换；急停仍由独立安全回路处理。
- 尚未把雷赛、正运动或 ADLINK 的原生 API 封装成可运行的通用驱动。原生 API 声明不等于已接入设备。

## 项目

| 项目 | 职责 |
| --- | --- |
| `MotionControl.Framework.Contracts` | 框架与设备、驱动模块之间的稳定契约所在位置 |
| `MotionControl.Framework.Runtime` | 单机运行状态、工位流程调度、报警、配置和追溯等通用运行时 |
| `MotionControl.Framework.Wpf` | 通用 WPF 页面与控件，包括手动调试页面 |
| `MotionControl.Host.Wpf` | WPF 应用入口、主窗口和模块组合根 |
| `MotionControl.Equipment.Template` | 新设备项目的目录模板；不自动接入 Host |
| `MotionControl.VendorApi.*` | 各厂商的低层原生 API 声明；不被框架核心直接引用 |

开发时遵循根目录的 [`AGENTS.md`](AGENTS.md) 项目规则，尤其是“直白优先，默认不抽象”。详细依赖和模块边界见 [`docs/architecture.md`](docs/architecture.md)；厂商 API 来源及部署依赖见 [`docs/vendor-motion-card-apis.md`](docs/vendor-motion-card-apis.md)。
