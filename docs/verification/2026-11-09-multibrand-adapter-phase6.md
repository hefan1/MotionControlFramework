# 第六批：多品牌运动控制架构的统一验证基线

日期：2026-11-09
工作分支：`codex/multi-brand-adapter-validation-phase6-20261109`
上游：`codex/plcopen-refactor-phase5-20261109`（第五批最终提交 `71c1c0b090ededbaed8a4e7e774f1e5ae1a89f81`）

## 0. 本批验收前提

第五批 GitHub Actions [42642470281](https://github.com/hefan1/MotionControlFramework/actions/runs/42642470281) 最终为 **success**：正式 Host 编译、15 个内部检查、5 个保护布局文件的 Git Blob 检查、已有软件控制器 `VirtualMotionCard/IAxis` API 检查通过。本批新增的多控制器测试**仍须等待本批 Windows CI 的最终结果**，不能沿用第五批成功来宣称第六批已验证。

本批采用的设计目标是：**PLCopen 风格的通用运动语义 + `IAxis` / `IMotionCard` / 逻辑 IO + 每个厂商、卡型独立适配器 + 严格的软件控制器验证**。C# 工位流程、任务列表流程和 WPF 通用页面不持有 ZAux/雷赛/固高等厂商类型；新增卡型只在相应适配器、显式装配代码及设备硬件配置中修改。不要为“通用性”增加反射、动态扫描、第三套运动状态机或 FakeAxis。

## 1. 仓库已经具备的能力与真实限制

| 位置 | 源码现状 | 本阶段可否宣称 |
| --- | --- | --- |
| `Contracts/Motion/IAxis.cs`、`IMotionCard.cs` | 业务以逻辑轴名、工程单位和通用异步命令编写 | 已存在统一接口 |
| `Runtime/Machine/MotionCardGroup.cs` | 按 `ControllerId` 管多卡，按逻辑轴名路由，遇重复名称拒绝 | 具备同一进程中管理多卡的通用基础 |
| `Host/HostMotionCardBackendSelection.cs` | 当前按 `Parameters/ControllerBackend.json` **整个工程**选择 Virtual 或 ZMotion | 不支持声称“单工程中任意混合厂商自动装配” |
| `MotionCardSettingsDocument.MotionCards[]` | 有逐卡的 `ControllerId`、`AdapterType`、连接配置路径字段 | 具备未来逐卡装配的配置基础，但当前 Host 仍需实现相应接线 |
| `Host/VirtualAxisMappingCardConnection.cs` | 为保护原有设备映射，软件控制器连接仍接受保存的 ZMotion 卡型元信息 | 不能把 Virtual 后端显示成“ZMotion 原厂 SDK 已验证” |
| `ZMotionCard` | 使用厂商 ZAux API，已独立适配 | 只有经过 ZMotion SDK/兼容软件仿真器的实际连接验证才能标注 SDK 软件链通过 |
| 雷赛、固高及其他厂商 | 当前仓库未看到已接入的生产级适配器和相关 SDK 软件验证 | 必须标注“未接入/未验证”，不得生成空实现伪装支持 |

目前默认项目仍允许按原规则使用 ZMotion，显式 Virtual 仍需严格通过原控制器身份/端口核对。未注册的厂商/卡型应明确拒绝，不允许自动切换到一个“看上去可用”的其他后端。不同卡型的回零、停止、插补、限位与轴状态能力不能只因名称相同就当作语义一致。

## 2. 第六批新增的可执行多卡 API 验证

**不创建新的控制器或假轴模型**。重用仓库已有的 `MotionControl.VirtualController.exe`，在本机两个不同的隔离端口启动 **两份独立进程**，其运动命令仍由已有 `VirtualMotionCard` / `IAxis` 适配器发送。现有单控制器的 `--controller-api` 回归仍保留。

新入口：`tools/MotionControl.VirtualAdapter.Checks/Program.MultiController.cs` 中的 `--multi-controller-api <端口A> <端口B>`。通过 `MotionCardGroup` 检查：

1. **配置准入**：两张卡的 `ControllerId` 不能重复；跨卡逻辑轴名不允许重复；请求上一台设备中才有的轴名必须明确拒绝。发生冲突前不会连接或发指令。
2. **独立轴路由**：`ControllerA.Transfer_X` 与 `ControllerB.Clamp_Z` 分别通过各自的软件控制器回零和绝对定位；读取每台控制器的实际位置及停稳反馈，不交叉使用。
3. **同编号 IO 隔离**：两张卡分别使用通道 8；写第一张卡的逻辑 DO 不能改变第二张卡的同编号 DO。
4. **安全断卡门禁**：第二张卡点动中请求断开整个 `MotionCardGroup`，必须明确拒绝且不能先断开第一张；随后通过 `IAxis.StopAsync(Controlled)` 停稳。
5. **部分失联与重连**：仅断开第二张卡，整组 `IsConnected` 必须变为 false，第一张卡仍维持连接；再按已有 API 重连，核对完整连接及停稳反馈。
6. **退出核对**：每个轴分别完成受控停止，连接断开；PowerShell 再向两份控制器各自的 `readAxis` API 查询本轮使用的轴。所有异常路径不报告成功。

启动脚本：`tools/Verify-ControllerApi.ps1`，现有 Windows CI 的同一验证步骤会同时调用单控制器与双控制器检查；只关闭此脚本启动的两个 `VirtualController.exe` 进程，不访问现场设备、用户配置目录或真实驱动器。

**必须严格说明：**双控制器验证虽然证明了通用 `MotionCardGroup` 的多实例连接、轴路由和 IO 隔离，但**两份实例仍属于同一 Virtual 实现**，不等于 ZMotion + 雷赛等混合厂商已完成软件验证。

## 3. 原有 Host 连接器停稳门禁对齐

- `VirtualAxisMappingCardConnection.ConnectCoreAsync`：连接完成后接管轴状态，不再只看 `status.Moving`，必须 `status.HasStoppedMotionFeedback()`，否则拒绝接管并沿已有路径保留原失联安全处理。
- `VirtualAxisMappingCardConnection.DisconnectAsync`：必须每个逻辑轴反馈真正停稳才允许断开，避免卡切换时把未知速度当作停止。
- `ZMotionAxisMappingCardConnection.ReconnectAsync` 及在线 IO 配置变更：同样复用 `AxisStatus.HasStoppedMotionFeedback()` 判定，不再简单判断 `Moving == false`。这仅为反馈规则对齐，并**没有声称 ZMotion SDK 或 ZSimu 连接已由本批测试**。
- 没有修改受保护的轴映射、IO 映射、轴手动、IO 监控四个页面及其 `.xaml.cs`，也没有触碰 `MainWindow.xaml` 和后台导航/按钮。

## 4. 未来每接入一个新品牌的统一验收清单

该检查清单是**正式适配器开发准入条件**，不是当前已全部通过的测试：

1. **注册与配置**：明确厂商、型号、SDK 版本和 `AdapterType`；使用明确的 C# 创建入口。未知品牌/型号禁止“自动退回” Virtual 或 ZMotion。
2. **连接与身份**：通过目标厂商软件控制器真正调用其原厂 API，核对实际型号/版本/控制器 ID；失败不伪装连接成功；多卡同时连接时身份及端口保持隔离。
3. **单轴控制**：使能、回零、绝对/相对运动、点动和 Controlled 停止按该厂商实际 API 执行；位置/速度有限、在到位容差内、无驱动报警后才能报告 Done。Abort、特殊回零、插补或持续限位监控不支持时应明确报错，不偷偷降级。
4. **IO 与轴号**：轴、输入、输出必须按 `ControllerId` 和逻辑名称映射；两张卡拥有相同物理编号也不能串线。切换配置后 UI 必须来自新设备当前有效的快照。
5. **取消、报警及失败收尾**：命令可能已下发后断线、回读失败或任务取消均须尝试受控停止；停稳无法证实时保持故障与资源待核查，不能释放为 Ready。
6. **切换品牌/设备**：先终止本次生产并确认停止，保留需要人工核查的保持输出和资源记录，再关闭旧设备会话；创建全新连接 ID、重新读取工艺和硬件映射，不携带上一设备轴号、回零标记、报警、IO 或旧 UI 异步回调。
7. **差异能力**：记录厂商缺省支持/不支持的轴组、多轴插补、硬件原点、限位、输出保持、停止模式；不把软件控制器的理想化反馈当作物理制动与硬件安全回路验收。

## 5. 通过/待验证矩阵（本批完成 CI 后更新）

| 场景 | 验证入口 | 当前可归类的能力边界 |
| --- | --- | --- |
| 共用 `IMotionCard/IAxis` 单控制器动作、反馈和停止 | `Verify-ControllerApi.ps1` 原单控制器步骤 | 已有第五批真实软件控制器 API 验证 |
| `MotionCardGroup` 同时接两个独立控制器，隔离及重连 | 本批 `--multi-controller-api` | 本批源码已提交，是否通过以最新 CI 为准 |
| Virtual Host 接管、ZMotion Host 重新映射的停稳反馈一致性 | Windows Host 编译 + 已有相关检查 | 本批改动已提交，API/屏幕覆盖需分开报告 |
| ZMotion 原厂 SDK / ZSimu 软件控制器 | `ZMotionCard` + ZAux DLL + 兼容仿真控制器 | 本轮 **未执行**；Virtual 通过不能替代 |
| 雷赛、固高与其他厂商 | 对应厂商的独立适配器与软件控制器 | **尚未实现，不能宣称已兼容** |
| 正式 Host 操作员屏幕换设备/卡、实体安全回路 | 桌面操作与现场工程验收 | 本批 **未执行** |

本批交付保持草稿 PR 与独立分支，**未合并 master**。未来逐卡混合品牌的 Host 装配属于后续实现项目，需要单独验证其文件迁移、按钮、原工艺兼容及设备切换旧状态隔离后才能标注支持。
