# 操作员界面优化：检查与待验收记录

日期：2026-11-04。此记录区分代码和内部检查、Sandbox 正式 Host 屏幕验收以及真实设备验收。

## 问题、修改和状态

| 问题 | 本轮修改 | 回归状态 |
| --- | --- | --- |
| 不在报警页时不易发现报警和恢复阻断；条件恢复后仍可能需要复位 | 底栏使用运行时报警、任务资源和连接状态的只读摘要，显示数量、待复位或未知状态；详情仍在相关页面 | 底栏格式检查 25/25；操作页面检查覆盖非报警页面、多个报警、待复位和断连。新版 Sandbox 屏幕待验收 |
| 任务结束但资源仍待确认，容易误以为设备已安全停止 | 总览和工位运行显示资源待核查、可能保持的输出、工位及后续方向；资源核查页区分命令、反馈与未知，未知时交维护人员处理 | 内部检查覆盖资源阻断、读取失败和只读导航；新版 Sandbox 屏幕待验收 |
| 设备准备缺少集中提示；查看页面不应改变任务会话 | 展示当前已知阻断和依赖前序动作的后续检查，打开页面只读，实际准备时按既有准入重新核对 | 准备内部检查 18 组通过，任务准备检查通过；尚未实现一次列全所有轴、IO 和依赖缺项，新版 Sandbox 屏幕待验收 |
| 条件恢复后报警仍需复位；夹持命令不等于夹具实际反馈 | 报警页和资源核查说明检查、复位和现场确认责任；未增加不存在的清料或恢复按钮 | 操作页面内部检查通过；新版 Sandbox 屏幕和真实现场流程待验收 |
| 独立工位已完成时可能错误显示“正在执行步骤” | 按工位自身状态选择步骤标签 | 内部检查通过；新版 Sandbox 屏幕待复验 |

受保护的轴映射、IO 映射、轴手动和 IO 监控页面未修改；顶部、左侧导航和底部状态栏沿用原有骨架。设备项目实际未配置或未通过互锁检查的清料动作不视为可执行。

## 内部检查

- `dotnet run --project tools/MotionControl.FooterSummary.Checks/MotionControl.FooterSummary.Checks.csproj -c Release`：31/31 通过。
- `dotnet run --project tools/MotionControl.Preparation.Checks/MotionControl.Preparation.Checks.csproj -c Release`：18 组通过。
- `dotnet run --project tools/MotionControl.TaskPreparation.Checks/MotionControl.TaskPreparation.Checks.csproj -c Release`：通过，覆盖顺序、占用、取消和重新核对。
- `dotnet run --project tools/MotionControl.TaskMonitor.Checks/MotionControl.TaskMonitor.Checks.csproj -c Release`：4 组通过。
- `dotnet run --project tools/MotionControl.OperatorPages.Checks/MotionControl.OperatorPages.Checks.csproj -c Release`：30 组通过，包含中英文离屏布局和 Launcher 超时回归。离屏图像属于内部检查，不是正式 Host 屏幕截图。
- 正式 Host Release 隔离输出构建：0 警告、0 错误。
- `dotnet run --project tools/MotionControl.TaskEngine.Checks/MotionControl.TaskEngine.Checks.csproj -c Release`：离线 23 组、执行仿真 58 组及设备级内存运行 28 组通过，包含暂停活动时间检查；不代替软件控制器或实际设备验收。

上述检查包含内存替身和离屏组件，不能证明 Sandbox 操作稳定性，也不能证明真实轴、IO、机械结构或安全回路通过。

## Sandbox 现场和待完成验收

之前在 Sandbox 运行的正式 Host 使用来宾软件控制器。原始测试记录和截图位于工作区外的 `F:\Work\运动控制框架\ui-pilot\winapp-sandbox\TEST-RECORD-2026-11-04.md`，其中 `focus-alarm-stop-click.png`、`focus-resource-read.png`、`focus-drain-outputs.png` 记录了缺料报警后停止失败、资源待确认以及 A 夹紧输出保持有效的当时现场。这些是旧版 Host 的证据，不是本轮新版界面截图。

本轮只读核查时旧 Host 仍显示报警；没有清除报警、删除恢复记录、伪造反馈或强制关闭保持输出。正式 Host 单实例和当前仅有的 Sandbox 被旧会话占用，尚未在同一环境启动新版正式 Host。对原有关闭收尾的代码复核还发现：关闭超时后，原后台续体可能迟到并清除仍需核查的任务会话；两种关闭入口缺少共同的成功终态。现已修改这两处，并补充内部检查；这些修复尚未进入正在运行的旧 Host，不能把直接关闭旧进程当作已验证的安全交接。

因此，下列均**未通过验收**：新版正式 Host 的 Sandbox 截图和操作流程；C# 与任务列表在正式 Host 的分别屏幕回归；新的多报警、待复位、断连及读取失败屏幕对照；停止失败、保持输出和资源待确认的新版画面；完整清料和故障恢复；真实设备及安全回路。此前旧版软件控制器测试已覆盖准备、回零、等待步骤暂停继续及缺料报警，但不能替代新版回归，也不能外推到所有动作场景。

关闭修复的补充检查：`dotnet run --project tools/MotionControl.MachineDevice.Checks/MotionControl.MachineDevice.Checks.csproj -c Release` 39 组通过，覆盖设备侧退出交接、隔离保持、输出读取迟到及两种关闭入口的顺序和并发；Launcher 的超时和迟到完成检查通过注入可控关闭任务验证界面会话保留与重试，该检查不覆盖真实设备关闭。此处均为内部检查，不等于旧版 Host 或 Sandbox 的关闭验收。

继续时应先只读复核并保存现有隔离现场，再验证正常关闭、超时和资源保持情况下的安全交接；只有确认不会丢失待核查状态或改变保持输出后，才启动新版正式 Host 并逐项补充截图和屏幕结果。真实设备验证单独报告。

## 追加：保留原现场的交接与 Sandbox 回归

- 旧 Host 关闭前在资源现场核查页面只读采样：13 轴均报告停止，A 夹紧输出有效且 A 夹紧到位输入有效；上料与双夹位切换所占资源仍待确认。当前会话有 1 条停机报警，条件显示等待复位。现场截图和文本保存在 `F:\Work\运动控制框架\ui-pilot\winapp-sandbox\handoff-current-resources-20261104.png` 与同目录 `handoff-current-resource-sample-20261104.txt`。
- 通过旧正式 Host 主窗口正常关窗，未使用停止并关闭、解除隔离或强制结束进程。退出后软件控制器仍运行；独立只读协议采样确认 13 轴不运动、DO41 仍为 true。退出前后 Runtime 共 25 个文件的路径及 SHA-256 哈希一致，包含 `AutomaticRun.pending` 和 20 个资源保护文件。这是保留隔离的退出交接，不是清料或恢复通过。
- 新版正式 Host 部署到单独目录，接回原配置及原端口 19584 软件控制器。启动时出现恢复未确认弹窗，启动被禁用；控制器仍是 13 轴不运动、DO41 有效；再次复制的 25 个 Runtime 文件哈希不变。
- 初次新版屏幕回归发现：总览的“等待复位”和通用顶部按钮提示掩盖恢复阻断，底栏仅显示“启动受阻”；报警页空列表显示“当前无报警”且没有说明旧现场。随后代码增加恢复待确认的总览、底栏及报警页提示，任务页避免建议直接读取后控制；总览保留运行、准备、清料和报警的当前状态，读取失败显示未知。页面不新增解除隔离或虚构清料按钮。
- 修正版正式 Host 在同一原现场复测：总览显示“恢复信息未确认”，底栏显示“恢复待确认”；恢复诊断按钮进入设备准备页，回零按钮禁用；报警页区分“当前会话未记录报警”和恢复仍未确认；工位页提示先核查现场和恢复诊断。截图分别是 `fixedhost-recovery-overview-20261104.png`、`fixedhost-recovery-alarms-20261104.png`、`fixedhost-recovery-stations-20261104.png`，均位于上述 `ui-pilot\winapp-sandbox` 目录。
- 该轮又发现恢复提示使用绿色正常样式、工位空状态仍引导读取任务，以及清料文案过于绝对；已按设备配置、现场检查和维护责任修改。最后构建的正式 Host 再次连接原现场，屏幕截图 `finalhost-recovery-overview-20261104.png`、`finalhost-recovery-alarms-20261104.png` 验证恢复摘要、灰色提醒、工位未知提示和报警空状态。报警历史在进程重启后并非当前会话报警列表，不能把零条当前报警解释为历史故障已解除。

## 追加：独立控制器任务列表流程

- 新建独立配置和软件控制器 PID 9960，端口 19585；未复制原现场 Runtime，也未将新配置连接原控制器。原 19584 控制器 PID 9452 持续运行，最后核对 DO41 仍有效、轴不运动。新场景中模型反馈按独立仿真配置建立，不修改原场景输入。
- 13 轴未使能时点全部回零：页面明确指出 Feeder_Z 未使能，但会先装载任务并占用控制权，需要到工位页停止关闭会话后再到轴手动使能。截图 `independent-preflight-axis-disabled-20261104.png`。尝试优化装载前检查时发现采样挂起和停止并发的安全问题，未合入该尝试；减少此往返仍待解决。
- 通过正式 Host 轴手动页面依次使能 13 轴并再次准备，界面显示配置工位已准备；软件控制器协议读回 13 轴已使能、已回零且停止，所有映射输出无效。截图 `independent-prepared-20261104.png`，采样 `independent-after-home-20261104.json`。
- 复位并启动后，在等待上料的步骤暂停；页面显示任务和设备已暂停，工位仍等待上料到位。复位和启动后出现上料等待 30 秒超时：报警页显示 1 条停机报警、等待复位，非报警的设备准备页底栏显示“报警 1/待复位 1”。截图 `independent-paused-20261104.png`、`independent-alarm-footer-20261104.png`、`independent-alarm-page-20261104.png`。暂停前已消耗的等待时间未采集，不能据恢复后短时间报警推断暂停时间被计入超时；源码审查及 TaskEngine 内部检查显示暂停活动时钟冻结。
- 报警后只读采样确认独立控制器 13 轴停止、映射输出均无效，Runtime 证据已复制至 `Independent-AlarmEvidence-20261104`。没有将这一结果记为完整清料、故障恢复或所有动作场景下暂停继续通过。

## 追加：C# 入口及轴使能语义

- 第三套独立配置使用 `StationFlows`、`RotaryMotionValidation` 和软件控制器 19586。正式 Host 总览显示五个 C# 工位，设备准备页显示“双夹位设备准备”；先后验证准备、启动、等待上料时暂停、复位后继续至同一等待步骤及缺料报警。报警后协议采样 13 轴停止、映射输出均无效；截图 `csharp-overview-20261104.png`、`csharp-preparation-20261104.png`、`csharp-paused-20261104.png`、`csharp-alarm-20261104.png`。这组测试中的 C# 示例仍有自动使能行为。
- 发现相同“工位回零”操作下，任务列表要求先手动使能，而两个 C# 设备示例会自动使能。已修改 `RotaryPreparation` 和 `WeldingStationPreparation`：仅检查本准备项涉及的轴，未使能时在动作前报出轴名；不再由准备过程自动使能，回零步骤前保留状态复核。两个设备示例的 Release 内部检查通过，包含最后一轴未使能时全部轴不回零、回零过程中后续轴失能后不继续，以及无关工位轴不受影响。
- 第四套独立配置和软件控制器 19587 使用修复后正式 Host：13 轴初始未使能、未回零；点击准备被拒绝并提示 Feeder_Z 未使能，协议读回 13 轴仍未使能、未回零、未运动，输出均无效。随后通过轴手动页逐轴使能，重新准备成功，协议确认 13 轴已使能、已回零、停止且输出均无效。截图 `csharp-fix-servo-refused-20261104.png`、`csharp-fix-prepared-20261104.png`；读回 `csharp-fix-before-20261104.json`、`csharp-fix-after-refused-20261104.json`、`csharp-fix-after-home-20261104.json`。
- 回归完成后正常关闭该正式 Host；原 19584 软件控制器仍运行，最后读回 13 轴停止、DO41 A 夹紧有效。新示例回零门禁通过不代表完整清料、实机安全回路或全部设备项目已经验证。
- 发现额外恢复生命周期问题：19587 场景准备成功、未启动生产且正常关闭后，重启仍因 `AutomaticRun.pending` 报恢复未确认。只读审查确认当前准备标记无事务身份，安全退出时也未结算；不能直接删除该文件，否则可能误删其他事务或丢失异常证据。需要具备身份核对、可靠落盘和完整关闭成功证明的事务结算机制；该问题未修复，现场标记保留。

## 追加：Sandbox 内 Appium WinCore 非鼠标试点

- 在当前 Sandbox 独立目录运行项目已有的 Node、Appium 3.9.0 和 WinCore 3.3.2；Appium 仅监听来宾 `127.0.0.1:4727`。附加已核实的正式 Host 窗口，并设置会话结束不关闭上位机。
- `pilot.cjs` 在来宾通过 WinCore 连续 10 次读取 WPF 控件树及元素，每次找到 144 个；一次树读取加全元素查询约 1.3–1.6 秒。结果 `wincore-guest-results-20261104.json` 和首轮树 `wincore-guest-source-20261104.xml` 位于 `ui-pilot\winapp-sandbox`。
- `sandbox-uia-navigation.cjs` 只调用 `windows: invoke` UIA 模式，真实页面从“设备准备”切换至“工位运行”，结束会话后正式 Host 仍在。该操作没有调用鼠标模拟或宿主窗口输入；尚未对宿主指针做独立仪器监测，也不能代表所有控件和坐标拖动都可用 UIA 替代。
- 发现导航 ToggleButton 的 UIA Toggle 可能只改变选中状态，不能据返回成功就认定页面切换；必须验证页面实际结果。禁止自动降级为物理鼠标、`windows: click` 或键盘真实输入。宿主窗口激活曾占用本机鼠标，已停止该做法。

以上仅为 Sandbox 软件控制器的原现场保留与部分 UI 回归。完整清料、故障恢复、不同入口的完整操作流程、厂商 SDK 和真实设备仍须独立验证；不得为补齐测试删除原现场记录或强制改变 DO41。
