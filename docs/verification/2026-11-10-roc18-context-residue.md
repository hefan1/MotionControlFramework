# ROC-18：设备与工艺切换后页面旧信息残留排查

## 范围与基线

- 核对日期：2026-11-10（北京时间）。基线 `8e318ea381ce80595fd9ba20dd7be97b6d53b810`。
- Linear 主任务 ROC-18；具体修复由 ROC-23（任务来源检查）、ROC-24（准备页错误来源）与 ROC-28（总览任务身份）跟踪。
- 本轮检查源码、正式页面控件的内部逻辑及 Windows CI。没有访问用户 Windows 桌面或实体设备，不能据此勾选 ROC-13 的正式屏幕验收。
- 设备工程应用现有规则是安全断卡、锁定导航与重连、重启读取；任务文件应用要求先断卡。本轮不把“通用”改写成未经支持的在线热切换。

## 四种数据身份

| 身份 | 当前实现与正确含义 | 不能据此推断 |
| --- | --- | --- |
| 编辑草稿 | TaskWorkspaceDocument / TaskEditorView 独立副本，可含未保存编辑；保存到 Drafts 不改运行文件 | 已检查、已应用或设备已采用 |
| 已保存文件 | TaskWorkspaceStore 当前 Tasks/TaskMachine/引用目录等；来源签名用于判断基准冲突 | 当前执行器已经读取这些字节 |
| 已载入会话 | TaskMonitorSession 的冻结目录、任务修订、独立会话标识、加载时间；由有效发布资格决定是否展示 | 磁盘后续改动已经更新这个会话 |
| 实际设备反馈 | 当前 Runtime、连接会话与既有控制卡 API；未知和读取失败明确区分 | 配置名称、草稿、离屏测试或 CI 等于现场状态 |

任务配置的 `_activeWorkspace` 本来就标注“已载入文件快照”，不能把它误报为冒充实时文件。真正缺口是后续任务文件变动未使“最近检查”和来源一致性提示及时失效。

## 14 个主页面来源表

以下为基线源码审查；“已有保护”不表示已完成本轮屏幕走查。路径前缀：H=`src/MotionControl.Host.Wpf`，W=`src/MotionControl.Framework.Wpf`，T=`src/MotionControl.TaskEngine.Wpf`。

| 页面 | 数据来源与保留内容 | 主要换源入口 / 结论 |
| --- | --- | --- |
| 设备总览 | `H/Operation/EquipmentOverviewView.xaml.cs`：当前 Runtime、任务身份、工位快照和选中键 | 页面内部已按身份清旧行和选择；Host 提供的原始会话宽于有效发布身份，准备换代时的组合缺口见 ROC-28 |
| 设备准备 | `H/Operation/EquipmentPreparationView.xaml.cs`：Runtime.PreparationStations、准备版本、选中工位、页面错误 | RefreshCore 已清旧行；页面 `_error/_diagnostic` 及异步错误缺少完整归属，见 ROC-24 |
| 工位运行 | `H/MainWindow.OperationPages.cs` 根据 ControlOwner 选择 C# FlowStatusView 或 TaskMonitorLauncher | C# 页绑定原 Runtime/连接并在离页退订；任务页使用有效发布会话及冻结目录，不能自动改用磁盘配置 |
| 轴监控 | `W/Monitoring/AxisMonitorView.xaml.cs`：卡组、轴定义副本、连接会话和显示代次 | RefreshAxisStatusesAsync 读前读后核对会话，离页拒绝迟到结果；App 映射应用/重连时重建 |
| IO 监控（保护） | `W/ManualDebug/IoMonitorView.xaml.cs`：当前 Runtime、逻辑/物理 IO、映射副本、页签和反馈选择 | App.RefreshIoMonitorPage 重建；反馈关联有代次校验。原始通道迟到读取的视图边界仍需正式场景验证，不擅改 |
| 当前报警 | `H/Operation/CurrentAlarmsView.xaml.cs`：Runtime 报警、工位来源、搜索和记录选择 | SetRuntime 清选择，失败清列表并禁定位；断连时明确标识的旧报警是保留证据，不能为“清残留”删除 |
| 轴手动（保护） | `W/ManualDebug/ManualMotionDebugView.xaml.cs`：固定 Runtime/卡组/逻辑 IO/轴定义，卡片持轴对象 | App.RefreshManualDebugPages 重建；离页停止轮询并停止点动。单实例迟到读数需正式场景核对，不擅改 |
| 轴映射（保护） | `W/Configuration/AxisMappingConfigurationView*`：固定配置目录、卡连接、轴草稿与读回值 | 读取/失败清集合，取消重读；保存后 App 刷新监控/手动页。切页保留未保存草稿属于正确行为 |
| IO 映射（保护） | `W/Configuration/IoMappingConfigurationView*`：配置目录、DI/DO 草稿、卡能力 | 读取/失败清集合、Loaded 更新能力，保存后刷新安全 DI / 手动 / 监控；不应无条件丢弃编辑 |
| 安全联锁 | `W/Configuration/SafetyInterlockConfigurationView*`：配置目录、输入草稿及 IO 地址缓存 | RefreshDigitalInputNames 重读来源而保留编辑值；失读禁保存，映射失效有提示；工程切换通过重启隔离 |
| 任务配置 | `T/Editing/TaskConfigurationView*`：工作区草稿、已载入文件快照、运行会话提供器、子编辑器 | LoadWorkspace 与 RefreshPermission；准备文件以外的来源变动未及时失效旧检查，见 ROC-23 |
| 设备变量 | `W/Variables/DeviceVariableView.xaml.cs`：固定变量 Store、定义、选中名称、待写文本及当前 Runtime | 定义含义变化清待写文本，删除清失效行；SetMachineRuntime 退旧订新并更新写入许可；工程变换依赖重启 |
| 报警库 | `H/Configuration/AlarmCatalogConfigurationView.xaml.cs`：文件定义副本、选择与未保存状态 | LoadAlarms 成功/失败均替换集合并清选择；工程应用检查未保存状态，保存文件不自动替换已运行报警定义 |
| 设备工程 | `H/Configuration/EquipmentProjectView*`：候选包预览、差异、应用服务 | Import 失败清候选；应用成功清预览并 RequireRestart，工程门拒旧内存重连；已有明确边界 |

## 已定位缺口与最小修复范围

### ROC-23：任务来源变动后的旧检查结论

原 `RefreshPreparationSource` 在准备文件字节未变时直接返回，未察觉任务、设备任务清单和引用目录变动。父页保留检查结论和“最近检查未报告基准不一致”；子编辑器还有独立的问题表及检查结果文字。

复现采用隔离目录和真实 WPF 页面：先检查，再单独替换 Tasks/Load.json、TaskMachine.json 或增删任务文件，准备文件保持原样。还覆盖文件被独占导致无法读取、恢复读取、未保存输入保留以及无变化正控。存储层 Apply 已有冲突核对，本问题不是绕过运行准入。

修复原则：继续保留准备来源即时检查；其余完整来源异步串行观察，限制频率，不在每个 500ms UI tick 读取整套文件。只在同工作区、同观察代次下提交结果；实际变化、失读及恢复使父页和子编辑器旧检查失效，保留用户输入和草稿。

### ROC-24：准备页错误归属

原 RefreshCore 在换准备版本时清行和选择，却未清 `_error/_diagnostic`；PrepareAsync/StopPreparation_Click 捕获异常后也未核对归属。正式任务关闭与重新载入会在同 Runtime 更换准备版本，故该问题不依赖未支持的在线工程切换。

回归从已有配置读取委托注入延迟/异常，页面和 Runtime 使用正式实现，复用无轴检查设施。断言普通同上下文刷新仍保留真实错误；换准备版本、Runtime 或连接后，旧错误标记不能显示在新页面。允许提示当前范围已经变化、需重新核对。

修复只针对页面提示。Runtime 报警、资源、恢复文件以及真实设备状态不得被清除。

### ROC-28：总览的任务会话资格宽于任务运行页

`MainWindow.SetTaskMonitorPage` 给总览提供原始 `CurrentTaskSession`；`TaskMonitorLauncher.ReadStationOverview` 仅检查连接、控制入口及非空会话。任务运行页使用的 `CurrentPublishedTaskSession` 还检查关闭状态和准备配置代次。因此准备换代时，总览的内部身份检查可能仍接受旧会话。

回归复用真实 Host/Launcher 绑定，在正常发布有工位的前提下更换同 Runtime 的准备定义，检查失效会话的旧行、选择和定位被撤销。该测试属于正式控件的离线组合检查，不能称为正式 Host 屏幕验收。资源安全摘要仍须保留原记录供核查，不能随工位展示一起删除。

独立复核发现：总览原本在工位身份未确认时隐藏任务安全摘要，改用严格发布身份后，会连带隐藏同连接保留会话的资源警示。因此还须分别核对工位发布身份和资源证据身份：准备换代可撤下工位，但仍应保留绑定当前连接的待核查警示；实际切换 Runtime、连接或任务会话时必须拒绝旧摘要。已补真实 ResourceScope 终止生成 Blocked 的回归，当前候选用于观察该边界失败，尚未宣称修复。

## 交给相邻任务的范围

- ROC-19：`App.SynchronizeCardConnectionAsync` 的断连清理和连接创建包含多个 await，需针对并发重入固定原 Runtime/连接身份、核对迟到发布与清理。此为源码风险，尚未宣称已有正式窗口复现。
- ROC-13：正式 Host 中 24轴→6轴→24轴、任务关闭/重读、断开/重连、旧请求返回、两种执行入口的真实屏幕验收。保护页的原始 IO/轴卡片异步观察需在该流程中核对。
- 不新增对象模型、通用事件服务、其他厂商驱动或工程热切换机制；沿用原 V1.8 未完成范围。

## 验证记录

- 基线本地结构检查：9/9 通过。
- 第一批测试提交：`f15ef52e04c7aec6f967671f5a34bcac14a6f470`，未修改生产代码。[Windows 失败运行](https://github.com/hefan1/MotionControlFramework/actions/runs/42740007278)，任务 `129578435534`。
  - 正式 Host 构建、受保护 UI 检查与原有独立软件控制器 API 检查通过。
  - 任务编辑 34 组中 7 组失败：任务替换、清单变化、增删任务、读取失败、编辑器来源提示、子编辑器旧检查。来源不变正控通过。
  - 操作页面 68 组中 2 组失败。总览换代后 `工位与选择已清=False；旧按钮仍导航=True；旧工位仍可定位=True；安全收尾会话保留=True`。准备页聚合用例的 4 个来源变化分支失败，同来源错误保留正控通过。
  - 其余 13 个内部检查项目通过；失败均为行为断言，不是构建或测试准备错误。
- 第二批测试提交：`f454d717be0d85f268d322ed773d314a65f33058`，补真实无轴 Runtime 的准备→停止等待→换 Runtime→旧异常返回，并断言旧 Runtime 停机报警和 LastError 保留。[运行 42740726289](https://github.com/hefan1/MotionControlFramework/actions/runs/42740726289) 长时间未结束，后被新候选取消。取消后的完整日志显示它停在任务编辑项目：任务替换、清单替换和新增三项断言失败后未再产生结果，尚未进入操作页面或停止测试。不能把本轮作为停止行为红灯，也不能据此判断产品停止功能死锁；新候选继续复核，并为停止专项补有界等待与逐阶段日志。
- 首轮修复及复核回归提交：`8a67d783f8f60103eb084bacec687eb3ba6265d7`，[运行 42743094391](https://github.com/hefan1/MotionControlFramework/actions/runs/42743094391)，任务 `129588718213` 正常结束。
  - 原任务来源的 8 项回归通过；总览旧工位回归通过；准备来源的原 5 项分支通过。
  - 新补共享可写句柄回归失败，证实后台严格读会把允许共享且未改字节的文件误标为失读。
  - 新补资源回归确认：旧工位已撤下、真实隔离仍保留，但原连接警示丢失且另一 Runtime 可能接收旧摘要。
  - 错误发布期间更换准备定义、旧停止异常迟到两个分支失败；停止专项的真实取消、异常返回及清理阶段均正常结束，失败来自旧异常污染新页面，不是测试等待失败。
  - 原布局用例另有 1 组失败：它在未触发 Loaded 的页面上反射同步刷新，期待完整来源立即恢复，已不适用于新的后台观察。只把两处完整来源观察改为真实 Loaded 与有界调度等待；恢复提示、故障文件路径和按钮门禁原断言均保留，明确操作的同步断言不变。
  - 正式 Host 构建、受保护 UI 和软件控制器 API 检查通过；两个受影响项目以外的 13 个内部检查项目通过。
- 复核修正：后台专用签名允许读写和原子替换，手工检查/应用仍保留原严格签名；准备错误保留命令捕获的原四项身份；总览分别核对工位发布身份与绑定当前 Runtime/连接的资源证据身份。
- `684a01a6` 的 PR 与 push 运行均在任务编辑最后一项长期未返回，取消后日志确认前 34 项通过，尚未进入操作页面。后续增加检查组与窗口阶段日志；原停滞具体原因没有取到线程栈，不能认定为文件锁或设备停止问题。
- 诊断提交 `cbb2d5d20b90b4b904a013e68ed547eb08f0dc9a` 的 [运行 42745069483](https://github.com/hefan1/MotionControlFramework/actions/runs/42745069483) 正常结束：任务编辑 35/35、保留资源警示、准备来源 7 个分支均通过。随后操作页面检查捕获到明确失败：`ObserveTaskSources` 的续体在 ThreadPool 调用 `RecordSourceReadFailure`，跨线程更新 WPF 控件，导致该项目退出；总计 14/15 项目通过，Host、保护布局及软件控制器 API 通过。
  - 修正为后台读取后显式经控件 Dispatcher 发布结果；单飞标记、工作区/观察代次/Loaded 校验和控件更新全部在 UI 线程内完成，不依赖调用时是否存在同步上下文。Dispatcher 退出后不再发布。
  - `Verify-V18.ps1` 只为已发生停滞的任务编辑离线检查增加 180 秒进程保护。超时只停止本次构建产物对应的检查 PID，再有界确认退出，并以失败记录继续其他项目；不改变其余 14 项入口、不触碰正式 Host 或现场设备。超时不代表清理成功，可能留下该次隔离临时文件。
- 修复后 Windows 结果仍待核验，不能将上述红灯记为已完成修复。
- 本地环境为 Linux，未运行正式 Windows Host 窗口；现有截图属于历史记录，不能作为本轮验收。
