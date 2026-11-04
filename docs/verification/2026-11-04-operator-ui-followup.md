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

- `dotnet run --project tools/MotionControl.FooterSummary.Checks/MotionControl.FooterSummary.Checks.csproj -c Release`：25/25 通过。
- `dotnet run --project tools/MotionControl.Preparation.Checks/MotionControl.Preparation.Checks.csproj -c Release`：18 组通过。
- `dotnet run --project tools/MotionControl.TaskPreparation.Checks/MotionControl.TaskPreparation.Checks.csproj -c Release`：通过，覆盖顺序、占用、取消和重新核对。
- `dotnet run --project tools/MotionControl.TaskMonitor.Checks/MotionControl.TaskMonitor.Checks.csproj -c Release`：4 组通过。
- `dotnet run --project tools/MotionControl.OperatorPages.Checks/MotionControl.OperatorPages.Checks.csproj -c Release`：28 组通过，包含中英文离屏布局。离屏图像属于内部检查，不是正式 Host 屏幕截图。
- 正式 Host Release 隔离输出构建：0 警告、0 错误。

上述检查包含内存替身和离屏组件，不能证明 Sandbox 操作稳定性，也不能证明真实轴、IO、机械结构或安全回路通过。

## Sandbox 现场和待完成验收

之前在 Sandbox 运行的正式 Host 使用来宾软件控制器。原始测试记录和截图位于工作区外的 `F:\Work\运动控制框架\ui-pilot\winapp-sandbox\TEST-RECORD-2026-11-04.md`，其中 `focus-alarm-stop-click.png`、`focus-resource-read.png`、`focus-drain-outputs.png` 记录了缺料报警后停止失败、资源待确认以及 A 夹紧输出保持有效的当时现场。这些是旧版 Host 的证据，不是本轮新版界面截图。

本轮只读核查时旧 Host 仍显示报警；没有清除报警、删除恢复记录、伪造反馈或强制关闭保持输出。正式 Host 单实例和当前仅有的 Sandbox 被旧会话占用，尚未在同一环境启动新版正式 Host。对原有关闭收尾的代码复核还发现：部分失败或超时路径可能在后续关停核对完成前丢失任务会话信息；尝试修复时发现资源隔离场景仍不完整，该尝试未保留。不能把直接关闭旧进程当作已验证的安全交接。

因此，下列均**未通过验收**：新版正式 Host 的 Sandbox 截图和操作流程；C# 与任务列表在正式 Host 的分别屏幕回归；新的多报警、待复位、断连及读取失败屏幕对照；停止失败、保持输出和资源待确认的新版画面；完整清料和故障恢复；真实设备及安全回路。此前旧版软件控制器测试已覆盖准备、回零、等待步骤暂停继续及缺料报警，但不能替代新版回归，也不能外推到所有动作场景。

继续时应先只读复核并保存现有隔离现场，再完善和验证正常关闭、超时和资源保持情况下的安全交接；只有确认不会丢失待核查状态或改变保持输出后，才启动新版正式 Host 并逐项补充截图和屏幕结果。真实设备验证单独报告。
