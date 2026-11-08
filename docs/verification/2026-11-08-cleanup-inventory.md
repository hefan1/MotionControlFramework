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
