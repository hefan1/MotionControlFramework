# PDF 四个工作区的落地版本

依据 2026-10-24 的 UI 设计 PDF 第 6、8、10、17 页实现。已有外壳、轴、IO 和变量页布局保留，关联查看时只在外壳标题区临时显示“返回操作页”。

## 正常 Host

| 页面 | 入口 | 数据及操作 |
| --- | --- | --- |
| 设备总览 | 首页 → 设备总览 | 读取当前传统 `MachineRuntime`，按配置顺序显示所有工位和当前动作；工位数量不固定。顶部状态给出下一步提示，查看流程可定位同名工位；工位明确等待 DI 时可直接跳转并选中该输入。 |
| 任务配置 | 设置 → 任务配置 | 工位安排、任务库、已应用文件版本和草稿状态；行内编辑进入该工位指定任务。草稿、检查和应用继续使用现有存储保护。 |
| 任务编辑 | 从任务配置进入 | 分栏步骤树与当前类型表单；多轴目标摘要表、选中轴的独立参数、工程单位和原配置关联。保存不执行动作。 |
| 当前报警 | 报警 → 当前报警 | 搜索工位/代码/内容，发生时间带年月日，保留选择。四块指导说明发生位置、当前条件、检查内容和后续操作，工程诊断默认折叠。 |

总览和报警页跟随 `MainWindow.SetMachineRuntime` 更换来源。未连接/没有 Runtime 时显示未知，不能显示就绪或零报警。读取的是软件状态，页面采样时间不代表轴、IO 的硬件采集时间。传统 `IStationFlow` 没有独立状态时，总览不从“等待”字样猜测资源持有人或运行状态；实现循环统计或 `IStationInputWaitState` 的工位会显示自己的计数和当前等待 DI。卡片状态明确标为整机状态，暂停时保留动作文字。

停机报警条件解除后仍显示等待复位，使用原顶部复位和启动流程。非停机提醒由现有报警服务随条件恢复移除。新页没有确认清除、报警复位、使能、点动或输出入口，查看与刷新不改变报警。

报警定义目前没有稳定的关联轴/IO 字段。因此“定位工位流程”仅在来源确实匹配工位时可用；“查看 IO 监控”和“进入轴手动”只打开原页面，不根据故障文字猜测轴或物理通道。工程诊断中的最近整机异常明确提示可能不属于选中报警。历史记录按钮禁用并解释历史持久化未接入。

正式操作页只显示传统 Runtime 状态，不再提供 TaskEngine 内存会话的入口；顶部启停按钮语义不变。TaskEngine 的执行工作台仍可用于开发检查，但尚未接入设备运行。

## 独立四页预览

```powershell
& './src/MotionControl.Host.Wpf/bin/Debug/net48/MotionControl.Host.Wpf.exe' --ui-workspace-preview
```

预览窗口有四个页面按钮和中英文切换；标明“离线页面示例”。配置存放在 `%LOCALAPPDATA%/MotionControlFramework/FourWorkspacesPreview/TaskEditorPreview`，首次创建示例后不覆盖已有文件。任务页仍使用真实编辑器、草稿存储与校验器；不创建控制卡、传统 Runtime 或现场恢复服务。总览和报警只是独立示例快照，其产量 128、周期 12.4 等不能视为生产数据。预览里的关联按钮只在底栏显示目标。

## 文件定位

- `EquipmentOverviewView`：状态横幅、工位卡片、摘要、只读查看原因。
- `CurrentAlarmsView`：查询、选择、处理说明、只读导航。
- `OperationSnapshot`：从当前 Runtime 复制软件状态，不携带任何控制入口。
- `OperationUi` / `Localization`：明确的语言与状态颜色。
- `FourWorkspacesPreviewWindow`：独立示例和四页入口。
- `MainWindow.OperationPages.cs`：正常 Host 的组合、关联导航和返回。
- `TaskEngine.Wpf/Editing/TaskLibraryRow`：任务库只读版本行。
- `TaskEditorView.Motion.cs`：多轴目标表与每轴参数。
- `tools/MotionControl.OperatorPages.Checks`：六组真实控件、临时文件和内存服务检查；导出图为离屏布局，不是桌面截图。

Windows Computer Use 当前初始化失败，实际鼠标、焦点和弹窗的屏幕验收仍需补做；不要用内部事件测试替代屏幕结论。验证记录见 `docs/verification/2026-10-24-four-workspaces.md`。
