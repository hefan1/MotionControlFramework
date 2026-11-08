# 2026-11-08 代码与验证产物梳理

## 范围和处理

本轮从 `6af701a` 的独立工作区检查源码、项目引用和主仓库中被 Git 忽略的验证产物。源码按字节比较未发现可以直接合并的整份非空重复文件；这不代表语义相似或动态调用的代码已经全部排查。厂商接口、设备模板、任务样例、内部检查工具以及历史验证记录均有各自用途，不能只因文件大或名称相似就删除。

静态文本搜索发现 `src/MotionControl.Framework.Wpf/ManualDebug/ManualAxisControl.xaml.cs` 的私有方法 `GetBooleanText` 只有定义；该文件属于已确认的轴手动页面，本轮未修改。文本搜索也不能排除所有动态调用，后续确需处理时应结合页面限制与实际引用重新核对。

已清理上一轮由本任务生成的三份临时编译副本，合计 50 个文件、13,003,639 字节：

| 已删除目录 | 原内容和确认依据 |
| --- | --- |
| `tmp/cleanup-project-baseline` | 工程包检查的旧版编译输出，只有 EXE、DLL、PDB 和运行时配置。 |
| `tmp/cleanup-project-after` | 工程包检查的新版编译输出，只有 EXE、DLL、PDB 和运行时配置。 |
| `tmp/cleanup-operator-checks` | 操作页检查的编译输出；唯一 JSON 模板与 `src/MotionControl.Equipment.Demo/EquipmentTemplates/Parameters/DeviceVariables.json` 的 SHA-256 相同。 |

删除前核对了各目录路径、文件数量、文件类型、Git 忽略状态、目录链接及进程路径。检查日志和结果仍在 `tmp/cleanup-sandbox-70fff418`、`tmp/cleanup-ui-sandbox-a9ba2413`；上一轮保存的 Machine01 配置在后者的 `GuestConfigurationFinal/Machine01` 中。

## 后续候选

清理后，`tmp` 中还有 90 个目录仅包含扩展名为 `.dll`、`.exe`、`.pdb` 或 `.config` 的文件，共 925 个文件、约 112.13 MiB。这只是按扩展名得到的清单，不代表所有内容都可以直接删除；仍须逐目录核对运行引用、配置和验证用途。

其中以下 8 对目录的相对文件路径和每个文件的 SHA-256 均相同。本轮均予保留，后续处理前仍须核对历史路径是否被外部记录或脚本使用：

| 目录一 | 目录二 |
| --- | --- |
| `tmp/check-bcw-monitor` | `tmp/check-monitor-final` |
| `tmp/exit-virtual-adapter` | `tmp/project-final-adapter` |
| `tmp/clearance-executor-final2` | `tmp/final-executor` |
| `tmp/clearance-target-monitor` | `tmp/final4-executor` |
| `tmp/clearance-integrated-final` | `tmp/clearance-lifecycle-final` |
| `tmp/clearance-integrated-verified` | `tmp/clearance-settlement-fix` |
| `tmp/clearance-target-feedback` | `tmp/final4-device` |
| `tmp/clearance-profile-final2` | `tmp/clearance-profile-verified` |

`tmp/io-status-exact-tree` 含历史源码、配置和构建内容，不能按普通编译缓存处理。先前对 `.artifacts/footer-checks`、`.artifacts/footer-host`、`.artifacts/operator-ui-final` 和 `obj/CodexVerify` 的删除被自动审批以 `blocked by policy` 拒绝；本轮这些目录继续保留。

## 本轮验证

- 独立工作区的正式 Host Release 构建成功；由于该工作区未复制被忽略的正运动原生 DLL，构建有 1 条厂商运行库缺失警告。本轮未验证正运动 SDK 或实卡。
- 软件控制器和虚拟卡适配器检查程序的 Release 构建均为 0 警告、0 错误。
- Windows Sandbox 中使用独立软件控制器进程及正式虚拟卡适配器运行已有往返检查，退出码为 0；控制器身份为 `MotionControl.VirtualController`、协议版本 2。检查涉及模拟轴动作和 IO，结束时读回测试轴停止、物理通道 DO0 电平为 false，随后控制器进程退出。结果和日志在 `tmp/cleanup-followup-20261108/adapter-check-result.json`、`adapter-check.log`。
- 检查将输出配置为低有效，因此 DO0 物理电平 false 对应逻辑有效，不能称为输出关闭。检查还注入了模拟物料输入；控制器退出前没有逐项确认模拟输入、输出恢复，其内存状态随进程退出消失。上述测试不等同正式 Host 页面验收或现场硬件验证。
- 新建沙盒没有活动的来宾用户会话，`wsb exec --run-as ExistingLogin` 返回 `0x80070520`。为避免远程连接抢占宿主焦点，没有启动本轮正式 Host 界面，因此本轮 Host 界面连接及页面核对未完成。沙盒进程检查确认本轮 Host、控制器和检查程序均未继续运行，随后关闭本轮沙盒。
- 本轮未启动 Host 来宾配置，也未修改保存的 Machine01 配置或恢复记录。

## 追加：恢复沙盒来宾界面验证

- 同一新建 Sandbox 中，WinApp 0.7.1 使用默认状态目录时立即返回 `sandbox_agent_busy`；默认状态记录的实例和地址属于旧沙盒。为本次沙盒指定新的 `WINAPP_TARGET_STATE_ROOT` 后，WinApp 成功建立来宾会话并执行命令。这个对照说明问题与默认状态有关，但未逐项确认旧状态中导致错误的具体字段；旧状态文件没有删除或覆盖。
- 后续复用时，每个新沙盒使用自己的 WinApp 状态目录，同一会话的各条 WinApp 命令保持使用同一目录。先通过 `winapp target snapshot sandbox --json` 确认当前沙盒和来宾桌面可用，再在来宾中启动控制器与正式 Host。冷连接可能短暂切换宿主窗口焦点；页面操作使用 `--on sandbox` 的 UIA `invoke` 或 `select`，不向宿主发送鼠标或键盘输入，并在操作后检查实际页面与截图。
- 正式 Host 的 Release 构建和软件控制器构建均为 0 警告、0 错误。来宾中的 Host 可执行文件与构建输出的 SHA-256 一致，窗口标题为“运动控制上位机 — 隔离测试配置”；软件控制器实际返回类型 `MotionControl.VirtualController`、协议版本 2，Host 界面显示“软件控制器 已连接”。
- 通过来宾 UIA `InvokePattern` 验证“设备总览 → 工位运行 → 设备准备 → 工位运行”，并进入轴监控、IO 监控；通过 `SelectionItemPattern` 切换 IO 输出页。每次核对页面标题及截图，没有启动生产、回零、轴运动或切换输出。截图保存在 `tmp/sandbox-ui-background-probe-20261108` 下的 `host-overview.png`、`host-station.png`、`host-preparation.png`、`host-axis-status.png`、`host-io-status.png` 和 `host-output-status.png`。
- 验证中一次调用短暂返回 `sandbox_no_interactive_session`；随后只读快照显示会话已就绪，重新识别目标并重试成功。此次证明所列页面操作可用，尚不能证明沙盒通道的长期稳定性，也没有独立监测宿主指针的物理移动。
- 画面显示两根测试轴已确认停止且未使能，DI0 与 DO0 均未触发；关闭 Host 前后，控制器只读应答再次确认两轴停止、DI0/DO0 物理电平为 false，且测试映射均为高有效。Host 正常退出后保存配置，随后停止本轮软件控制器并关闭本轮沙盒。配置和恢复记录保存在 `tmp/sandbox-ui-background-probe-20261108/GuestConfigurationFinal/Machine01`；收尾结果为 `host-close.json`、`state-after.json` 和 `controller-close.json`。
- 本次为新的独立控制器会话，不改变或补证上一节中已经退出、未逐项复位的模拟 IO 会话，也不代表厂商 SDK 或现场实物通过验证。
